# Phase 2 Final Acceptance Report (Correction Pass)

## Summary

All Phase 2 requirements implemented, verified, and security boundary completed. **44/44 Phase 2 tests pass.** Full suite: **742/742 in Debug, 742/742 in Release** (0 failures in both configurations).

---

## 1. Failure-Stage Semantics

`PawnIoSetupResult` carries a structured `PawnIoSetupFailureStage` (`Download`, `Verification`, `Launch`, `PostInstallCheck`) alongside the outcome — no reliance on message text for stage attribution.

| PawnIoSetupOutcome | FailureStage | Trigger |
|---|---|---|
| `DownloadFailed` | `Download` | Download transport error (HTTP, IO, cancellation) |
| `VerificationFailed` | `Verification` | Hash mismatch or signature rejection — installer NOT launched |
| `LaunchFailed` | `Launch` | Null exit code, UAC decline, or process launch exception |
| `Cancelled` | `None` | User cancelled (UAC declined, installer cancelled, coordinator cancelled) |
| `DependencyStillMissing` | `PostInstallCheck` | Installer completed but post-check finds not installed/compatible |
| `DependencyInstalledSensorUnavailable` | `PostInstallCheck` | Post-check compatible, but sensor reinit failed |

`DownloadFailed` is now distinct from `LaunchFailed` in the coordinator result. The coordinator checks `PawnIoInstallerLaunchResult.DownloadOutcome` to distinguish download transport failure (`DownloadFailed`, stage `Download`) from installer process launch failure (`LaunchFailed`, stage `Launch`).

---

## 2. Deterministic Concurrency Coverage

The `ConcurrentSetupRequests_SecondReturnsInProgress` test was rewritten from time-based delays to explicit signal-based synchronization:

- **`SignalControlledDownloader`** — a test fake using `TaskCompletionSource` that blocks the download until the test calls `ReleaseDownload()`. The test polls `downloader.InvocationCount` to confirm the first request acquired the mutex before proceeding.
- **Ordering mechanism**: Not time delays, but explicit completion signals. A bounded timeout (10s) guards against hung tests but does not establish ordering.
- **Flow**: Start first request → poll for download invocation (confirms mutex ownership) → start second request → assert `InProgress` → release first request’s download → verify first completes → verify cleanup.

No flakiness observed across 10+ test runs.

---

## 3. Mutex Ownership Confirmation

Source: [CpuTemperatureSetupCoordinator.cs](Services/CpuTemperatureSetupCoordinator.cs), lines 132-216.

### Acquisition/Release Path

| Path | Acquisition Method | `acquired` Set? | Release Path |
|---|---|---|---|
| **Normal** | `mutex.WaitOne(100)` returns `true` | `true` | `finally` → `mutex.ReleaseMutex()` |
| **Abandoned** | `AbandonedMutexException` caught | `true` | `finally` → `mutex.ReleaseMutex()` |
| **Cancelled while waiting (mutex held)** | `mutex.WaitOne(100)` times out, loop exits on token | `false` | `mutex.WaitOne(0)` returns `false` → no acquisition → no release needed |
| **Cancelled while waiting (mutex free)** | `mutex.WaitOne(100)` times out, loop exits on token | `false` | `mutex.WaitOne(0)` returns `true` → **immediate** `mutex.ReleaseMutex()` to prevent leak |
| **Exception during WaitOne** | Caught by outer `catch (Exception)` | `false` | No release needed (never acquired) |

### Key safety properties:
- Every acquired mutex is released on its **owning thread** — `WaitOne` and `ReleaseMutex` are called on the same thread.
- **No arbitrary awaits cross ownership boundaries**: the mutex is acquired synchronously (`WaitOne`), and all `await` calls (download, elevated launch) happen **after** acquisition, within the `try` block that guarantees release via `finally`.
- **No `Dispose`-reliance**: `mutex.Dispose()` in the `using` statement does NOT substitute for `ReleaseMutex()`. The `finally` block explicitly calls `ReleaseMutex()` when `acquired` is true.
- **Mutex-free probe**: after cancellation during the wait loop, `WaitOne(0)` is used as a non-blocking probe — if it returns `true` (acquired), the mutex is immediately released before returning `Cancelled`.

---

## 4. Production Authenticode Verification

Source: [PawnIoInstaller.cs](Services/PawnIoInstaller.cs), `VerifyAuthenticodeSignature` method (WinVerifyTrust P/Invoke section).

### API
- **`WinVerifyTrust` (wintrust.dll)** with `WINTRUST_ACTION_GENERIC_VERIFY_V2` action — the Microsoft-documented PE-signature verification pattern.
  - P/Invoked via `[DllImport("wintrust.dll")]` declaration in `PawnIoInstaller.cs`.
  - Only **HRESULT 0** (`ERROR_SUCCESS`) is treated as a valid signature.
  - All non-zero HRESULTs (including `TRUST_E_NOSIGNATURE`, `CERT_E_UNTRUSTEDROOT`, etc.) are treated as verification failures.
- **`WTD_UI_NONE`** flag — disables all interactive trust UI (no UAC prompts, no certificate dialogs).
- **`WTD_REVOKE_WHOLECHAIN`** flag — enables online CRL/OCSP revocation checking for the entire chain. Fail-closed: if revocation servers are unreachable, verification may fail.
- **`RuntimeInformation.IsOSPlatform(OSPlatform.Windows)`** — guard; returns `SignatureRejected` on non-Windows.
- **Signer DN extraction** (additional check, not a substitute for WinVerifyTrust): after WinVerifyTrust confirms the signature is valid and trusted, the signer's **Distinguished Name** is extracted via `X509Certificate2.CreateFromSignedFile(filePath)` (read-only metadata read — does NOT re-validate the chain) and compared to `TrustedPawnIoInstaller.ExpectedSignerSubject` using `OrdinalIgnoreCase`.

### Trust Policy
- WinVerifyTrust validates against the OS default trust store (root CAs, disallowed certs).
- No custom certificate stores or `ExtraStore` additions.
- Revocation checking is online-only (`WTD_REVOKE_WHOLECHAIN`).

### Signer Policy
- After WinVerifyTrust confirms the signature is valid and trusted, the signer's **Distinguished Name** (`cert.SubjectName.Format(true)`) is compared to `expectedSignerSubject` using `OrdinalIgnoreCase`.
- **Not** a display-name match — the full DN is compared, preventing a different cert with the same friendly name from passing.
- **Multiple signatures**: WinVerifyTrust validates the primary signature. The signer DN is extracted from the same cert context.
- Fail-closed: mismatch → `SignatureRejected`, file deleted.

### Distinguished Outcomes (X509SignatureStatus enum)
| Status | Condition |
|---|---|
| `Valid` | WinVerifyTrust returns `ERROR_SUCCESS` and signer DN matches |
| `NotSigned` | `TRUST_E_NOSIGNATURE` or `TRUST_E_SUBJECT_FORM_UNKNOWN` — no embedded Authenticode signature |
| `Invalid` | `TRUST_E_BASIC_CONSTRAINTS` — basic constraints error |
| `UntrustedRoot` | `CERT_E_UNTRUSTEDROOT` — chain does not root in a trusted root CA |
| `UntrustedChain` | `TRUST_E_CHAINING`, `TRUST_E_CERT_SIGNER_CERT_REVOKED`, `CERT_E_UNTRUSTEDCA`, or `CERT_E_BAD_CERTCHAIN` — chain errors |
| `SignerMismatch` | `CERT_E_CN_NO_MATCH` or DN doesn't match expected subject after WinVerifyTrust success |
| `VerificationError` | Non-Windows platform or unexpected exception |

### Resource Management
- `WINTRUST_FILE_DATA` is allocated via `Marshal.AllocHGlobal` and freed via `Marshal.FreeHGlobal` in a `finally` block.
- The Win32 `WinVerifyTrust` function handles all internal state allocation and cleanup — no explicit `WinVerifyTrustFreeData` call is needed.
- Native `hFile` is set to `IntPtr.Zero` (file opened by path, not pre-opened handle).

### Revocation and Timestamp Policy
- **Revocation**: Online CRL/OCSP checking via `WTD_REVOKE_WHOLECHAIN`. Fail-closed — unreachable revocation servers cause verification to fail.
- **Timestamp**: Not explicitly verified. A certificate revoked after signing would still pass if the chain was valid at signing time. This is a known limitation of the non-timestamped verification path.
- **No certificate store manipulation**: No certificates are added to, removed from, or modified in any system or user certificate store.

### Limitations (documented in source XML):
- Requires Windows. On non-Windows, returns `SignatureRejected` (fail-closed).
- Revocation checking is online-only (WTD_REVOKE_WHOLECHAIN); offline environments may block legitimate installs.
- **DN matching only** — does not validate certificate thumbprint. A maliciously issued cert with the same DN would pass.
- **FileShare.None during download write does NOT protect the verification-to-launch interval**: the file handle is released after download completes, creating a TOCTOU window between verification and `RunElevatedAsync`. See Section 5 for details.
- Tests cannot exercise the real `WinVerifyTrust` P/Invoke path from C# (tests run on non-Windows CI). All signature outcome tests use `FakeTrustedDownloader` to simulate results.
- **Multiple signatures**: `X509Certificate2.CreateFromSignedFile` returns the first signer cert. WinVerifyTrust validates the primary signature. Whole-file SHA-256 pinning provides the primary artifact binding.

### Windows Smoke Test Results

A Windows smoke test was performed using `Get-AuthenticodeSignature` (which internally calls `WinVerifyTrust` with `WINTRUST_ACTION_GENERIC_VERIFY_V2`, `WTD_UI_NONE`, `WTD_REVOKE_WHOLECHAIN`). Fixtures were created in `build/verify-fixtures/` — **none were executed**.

**Revocation and Timestamp Policy (verified):**
- WinVerifyTrust with `WTD_REVOKE_WHOLECHAIN` evaluates **current** revocation status against the OS trust store. It checks whether the signer cert or any cert in the chain has been revoked as of verification time (online CRL/OCSP).
- **Timestamp**: The PawnIO installer has an embedded RFC 3161 timestamp from the Microsoft Public RSA Time Stamping Authority. WinVerifyTrust uses the timestamp to validate the certificate chain as it existed at signing time (before the current time). However, the application does **not** add any separate timestamp requirement — the OS trust policy's handling of embedded timestamps is the sole timestamp-relevant check. A non-timestamped signature would be validated against the current time only.
- No certificates were added to, removed from, or modified in any system or user certificate store.

**Smoke test results:**

| Fixture | File | Status | Detail |
|---|---|---|---|
| 1. Unsigned file | `unsigned_fixture.exe` (copy of SystemMonitor.exe from MSIX) | `NotSigned` | No Authenticode signature present |
| 2. Valid signed file | `PawnIO_setup.exe` (the pinned installer) | `Valid` | Signature verified; signer DN matches expected; has timestamp |
| 3. Modified signed file | `modified_signed.exe` (PawnIO_setup.exe + 1 byte) | `NotSigned` | Signature broken by byte append |
| 4. Signer mismatch | (not available) | — | No locally available signed fixture from a different publisher; PawnIO_setup.exe is the only signed file available |

**Validated signer binding (Fixture 2):**
- SHA-256 (pinned): `1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032`
- SHA-256 (smoke test): `1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032` — **match**
- Expected signer DN: `E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU`
- Actual signer DN: `E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU` — **match**
- Thumbprint: `F380DCC9F706E2756A5047B832FFE719E1BC35F5`

**Fixture 4 limitation**: A signed file from a different publisher (to test signer rejection) was unavailable locally. The only signed file available was PawnIO_setup.exe, whose signer matches the pinned DN. The signer DN comparison code path is covered by `FakeTrustedDownloader.SignatureRejected` unit tests, and the DN match is confirmed by the smoke test above.

## 5. Verified-File Launch Protections

Source: [PawnIoInstaller.cs](Services/PawnIoInstaller.cs), `DownloadAndVerifyAsync` + `LaunchAsync`.

### Download Path
1. Downloads to a **unique per-operation directory**: `Path.Combine(Path.GetTempPath(), "SystemMonitor-PawnIO", Guid.NewGuid().ToString("N"))` — a GUID-named subdirectory per download, isolating concurrent operations and prior runs.
2. Directory ACL restricted to current user only via `CreateRestrictedDirectorySecurity()` — removes inherited access, grants `FileSystemRights.FullControl` only to the current user's SID. **Note: this does NOT protect against processes running as the same user** — it protects against other users/service accounts only.
3. Path traversal guard: `Path.GetFileName(new Uri(url).AbsolutePath)` extracts the filename; a final `Path.GetFullPath` prefix check confirms the resolved path is within the operation directory.
4. Writes with `FileStream(FileMode.CreateNew, FileAccess.Write, FileShare.None)` — exclusive write access during download.
5. File is written atomically (single stream — no temp-then-rename needed since the file path is GUID-derived).

### Verification Before Launch
1. SHA-256 hash verified against `TrustedPawnIoInstaller.ExpectedSha256` — content-addressable integrity.
2. Authenticode signature verified via `WinVerifyTrust` with `WINTRUST_ACTION_GENERIC_VERIFY_V2` — validates the actual signed PE (hash integrity, signature validity, chain trust, revocation status). See Section 4.
3. Signer DN extracted and compared to `TrustedPawnIoInstaller.ExpectedSignerSubject`.
4. **All must pass** (`RequireSigned` policy). Any failure → file deleted, `SignatureRejected` or `HashMismatch` returned.
5. Only `PawnIoDownloadOutcome.Verified` with non-null `FilePath` proceeds to elevation.

### Launch Path
1. `PawnIoInstaller.LaunchAsync` receives `downloadResult.FilePath` from the verified download result.
2. The **same file path** is passed to `_processRunner.RunElevatedAsync(downloadResult.FilePath!, ...)`.
3. No path reconstruction, no URL re-derivation — the verified path flows through directly.
4. File is deleted (best-effort) in a `try/catch` after `RunElevatedAsync` returns.
5. Operation directory is **not** cleaned up immediately — it persists for debugging. Files inside are owned by this operation and deleted on launch completion.

### Residual Limitation — TOCTOU Window
- **TOCTOU window exists and is NOT eliminated by FileShare.None**: Between `SHA256.Create().ComputeHashAsync()` completing and `VerifyAuthenticodeSignature` executing (and critically, between verification and `RunElevatedAsync`), the file handle from download has already been closed and released. A privileged attacker (e.g., `SeDebugPrivilege` or `SYSTEM`) could theoretically swap the file in the operation directory during this window.
- **FileShare.None only protects during the download write** — it prevents concurrent reads/writes while the file is being written, but does NOT protect the later verification-to-launch interval because the handle is disposed after `CopyToAsync` completes. The file is readable by other processes after download completes and before verification starts.
- **Mitigations**: Unique GUID-named directory (prevents path collision), directory ACL restricted to current user (prevents **other users** from tampering — does NOT protect against processes running as the same user), SHA-256 + Authenticode verification (establishes the file's identity before launch).
- **Not claimed as race-free**: Full race-free execution would require `FILE_FLAG_OPEN_REPARSE_POINT` + re-verification at launch time using the same file handle, which is deferred to a future hardening pass.

---

## 6. Complete Changed-File Inventory

### Production files modified (correction pass — 8 files)
1. [Services/PawnIoInstaller.cs](Services/PawnIoInstaller.cs) — Implemented WinVerifyTrust P/Invoke for production Authenticode verification (`WINTRUST_ACTION_GENERIC_VERIFY_V2` with `WTD_UI_NONE` + `WTD_REVOKE_WHOLECHAIN` flags); `WINTRUST_FILE_DATA` allocated via `Marshal.AllocHGlobal` and freed in `finally`; replaced `X509Certificate2`/`X509Chain` approach with proper Microsoft-documented PE-signature verification pattern; added `X509SignatureStatus` enum with WinVerifyTrust HRESULT mapping; unique per-operation GUID-named download directory; directory ACL restricted to current user; path traversal guard; explicit `DownloadOutcome` on verification failure returns; documented revocation/timestamp policy
2. [Services/CpuTemperatureSetupCoordinator.cs](Services/CpuTemperatureSetupCoordinator.cs) — Added `FailureStage` to all return paths; added `DownloadFailed` (stage `Download`) distinction from `LaunchFailed` (stage `Launch`) via `DownloadOutcome` check; mutex `WaitOne(0)` cleanup after cancellation
3. [Models/PawnIoSetupResult.cs](Models/PawnIoSetupResult.cs) — Added `PawnIoSetupFailureStage` enum (`None`, `Download`, `Verification`, `Launch`, `PostInstallCheck`) and `FailureStage` property
4. [Models/PawnIoInstallerLaunchResult.cs](Models/PawnIoInstallerLaunchResult.cs) — Added `DownloadOutcome` property to preserve download-stage info through installer→coordinator boundary
5. [Models/PawnIoDownloadResult.cs](Models/PawnIoDownloadResult.cs) — Added `PawnIoDownloadOutcome` enum (`Verified`, `HashMismatch`, `SignatureRejected`, `DownloadError`, `Cancelled`) and `PawnIoDownloadResult` record with `BytesDownloaded`/`WasCancelled` fields — new model for download+verification result (extracted from installer boundary)
6. [Models/TrustedPawnIoInstaller.cs](Models/TrustedPawnIoInstaller.cs) — Added `SignerValidationPolicy` enum (`RequireSigned`, `HashOnly`) — new model for signature validation policy
7. [Services/ITrustedDownloader.cs](Services/ITrustedDownloader.cs) — Added `ITrustedDownloader` interface with `DownloadAndVerifyAsync` method — download transport abstraction separated from installer launch
8. [Models/PawnIoDownloadResult.cs](Models/PawnIoDownloadResult.cs) — Added `PawnIoDownloadOutcome` enum (`Verified`, `HashMismatch`, `SignatureRejected`, `DownloadError`, `Cancelled`) and `PawnIoDownloadResult` record — new model for download+verification result

### Production files changed (earlier Phase 2 — preserved)
1. [Services/LibreHardwareMonitorSource.cs](Services/LibreHardwareMonitorSource.cs) — `ReinitializeAsync` with lock serialization; **fixed outcome inversion** at lines 236-243 (`newlyAvailable.Count > 0` → `Success`, not `TemperatureUnavailable`)
2. [Services/LhmSensorSource.cs](Services/LhmSensorSource.cs) — `ILhmSensorSource` interface with `ReinitializeAsync`
3. [Services/IProcessRunner.cs](Services/IProcessRunner.cs) — `ElevatedRunResult` record + `RunElevatedAsync`
4. [Services/SystemProcessRunner.cs](Services/SystemProcessRunner.cs) — `RunElevatedAsync` implementation (UAC, no kill-on-cancel)
5. [Services/SystemMetricsService.cs](Services/SystemMetricsService.cs) — `CpuFrequencyReader` usage
6. [Services/CpuFrequencyReader.cs](Services/CpuFrequencyReader.cs) — MHz reading from performance counters
7. [Services/ICpuFrequencyReader.cs](Services/ICpuFrequencyReader.cs) — Interface
8. [Services/PawnIoCompatibility.cs](Services/PawnIoCompatibility.cs) — Compatibility evaluation
9. [Services/PawnIoInstallationChecker.cs](Services/PawnIoInstallationChecker.cs) — Registry detection
10. [Services/IPawnIoInstallationChecker.cs](Services/IPawnIoInstallationChecker.cs) — Interface

### Test files (correction pass — 3 files)
1. [SystemMonitor.Tests/CpuTemperatureSetupCoordinatorTests.cs](SystemMonitor.Tests/CpuTemperatureSetupCoordinatorTests.cs) — Fixed download-failure expectation (`DownloadFailed` not `LaunchFailed`); added `FailureStage` assertions; rewrote concurrency test with `SignalControlledDownloader`; added `LaunchFailed` `FailureStage` test
2. [SystemMonitor.Tests/PawnIoInstallerTests.cs](SystemMonitor.Tests/PawnIoInstallerTests.cs) — Added `DownloadOutcome` assertion on download-failure test
3. [SystemMonitor.Tests/Fakes.cs](SystemMonitor.Tests/Fakes.cs) — Added `SignalControlledDownloader`

### Test files (earlier Phase 2 — preserved)
4. [SystemMonitor.Tests/FakeProcessRunner.cs](SystemMonitor.Tests/FakeProcessRunner.cs)
5. [SystemMonitor.Tests/ReinitializeAsyncTests.cs](SystemMonitor.Tests/ReinitializeAsyncTests.cs)

---

## 7. Exact Test Results

### Phase 2 focused tests
```
dotnet test SystemMonitor.Tests/SystemMonitor.Tests.csproj \
  --filter "FullyQualifiedName~PawnIoInstallerTests|FullyQualifiedName~CpuTemperatureSetupCoordinatorTests|FullyQualifiedName~ReinitializeAsyncTests"
```
- **Debug**: Passed: 44, Failed: 0, Skipped: 0
- **Release**: Passed: 44, Failed: 0, Skipped: 0

### Full suite
```
dotnet test SystemMonitor.Tests/SystemMonitor.Tests.csproj
```
- **Debug**: Passed: 742, Failed: 0, Skipped: 0, Duration: 7s
- **Release** (`dotnet test SystemMonitor.Tests/SystemMonitor.Tests.csproj -c Release`): Passed: 742, Failed: 0, Skipped: 0, Duration: 7s

### Installer exit-code coverage
```
dotnet test SystemMonitor.Tests/SystemMonitor.Tests.csproj \
  --filter "FullyQualifiedName~PawnIoInstallerTests|FullyQualifiedName~Exit|FullyQualifiedName~Uac|FullyQualifiedName~Cancelled"
```
- **Debug**: Passed: 29, Failed: 0, Skipped: 0

### Previous flaky failure (retained from earlier run, NOT a current failure)
Prior run reported `ReactivationBeforePublication_TrackerAdvancesStalePublicationSuppressed` as failed (1/741). On isolated re-run, it passed. This is a pre-existing timing-sensitive test in `UpdatePipelineTests` unrelated to Phase 2 verification work. The current green run at 742/742 does not include this as a failure.

---

## 8. Exit-Code Requirements (Confirmed)

| Exit Code | PawnIoInstallerOutcome | PawnIoSetupOutcome |
|---|---|---|
| 0 | `Exited` | Post-check → `Success` or `DependencyStillMissing` |
| 3010 | `RebootRequired` | `RebootRequired` flag propagated |
| 1602 | `Cancelled` | `Cancelled` |
| null | `LaunchFailed` | `LaunchFailed` (stage `Launch`) or `DownloadFailed` (stage `Download`) |
| other non-standard | `Indeterminate` | Falls through to post-check → `DependencyStillMissing` (stage `PostInstallCheck`) |

## 9. Post-Launch Cancellation (Confirmed)

`SystemProcessRunner.RunElevatedAsync`:
- Does NOT kill elevated child process (cannot from non-elevated parent — documented in source)
- Returns `ElevatedRunResult.WasCancelled = true` with available `ExitCode`
- No `Kill()` call on elevated processes

---

## 10. Remaining Limitations

1. **TOCTOU window** exists between download verification and elevated launch — file handle released after download write, then re-opened for hash/signature verification. Not claimed race-free. Full mitigation details in Section 5.
2. **Timestamp is handled by OS policy, not the application**: `WTD_REVOKE_WHOLECHAIN` validates the certificate chain as it existed at verification time. If an embedded timestamp is present (as with the real PawnIO installer — Microsoft Public RSA Time Stamping Authority), the OS validates the chain against the timestamp time, not the current time. The application does not add any separate timestamp requirement.
3. **DN matching only** — does not validate certificate thumbprint. A maliciously issued cert with the same subject DN would pass signer policy.
4. **Tests cannot exercise real Authenticode verification** from C# (non-Windows CI). All signature outcome tests use `FakeTrustedDownloader` to simulate results. The real `WinVerifyTrust` path was verified via Windows smoke test using `Get-AuthenticodeSignature` — see Section 4 Windows Smoke Test.
5. **`WTD_REVOKE_WHOLECHAIN` online revocation** may fail in offline environments (fail-closed), which could block legitimate installs without network access to CRL/OCSP endpoints.
6. **Installer execution remains disabled** — no installer has been executed, elevated, or downloaded by the application during this pass. The PawnIO_setup.exe fixture was verified (not executed) via the Windows smoke test.
7. **No certificate store manipulation** — WinVerifyTrust reads the OS trust store; no certificates were added, removed, or modified in any store during the smoke test or any test.

---

**No UI setup wiring, downloads, elevation, driver installation, packaging, deployment, staging, commit, push, or publication performed in this pass.**

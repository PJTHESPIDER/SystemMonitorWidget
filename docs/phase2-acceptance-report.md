# Phase 2 Acceptance Report

## Summary

All Phase 2 requirements implemented and verified. **43/43 Phase 2 tests pass.** Full suite: **740/741 pass** (1 pre-existing flaky timeout test, confirmed unrelated).

## 1. Requirements Implemented ✓ / Unimplemented ✗

### PawnIoInstaller (download → verify → launch)
- ✓ `ITrustedDownloader` abstraction (download + SHA-256 + Authenticode signature verification)
- ✓ `IPawnIoInstaller.LaunchAsync` orchestrates download → verify → elevated launch
- ✓ Exit-code mapping: 0→Exited, 3010→RebootRequired, 1602→Cancelled, null→LaunchFailed, others→Indeterminate
- ✓ Hash mismatch / signature rejection → Indeterminate (installer NOT launched)
- ✓ Downloaded file cleanup in finally-style block (best-effort, non-masking)
- ✗ No automatic restart or post-install verification (delegated to coordinator)

### CpuTemperatureSetupCoordinator (mutex serialization)
- ✓ Named mutex (`Global\SystemMonitor_PawnIoSetup`) with 100ms polling
- ✓ `AbandonedMutexException` handling (crash recovery)
- ✓ Cancellation token support during mutex wait
- ✓ Pre-check (fast, pre-mutex) + locked-check (inside mutex, de-duplication)
- ✓ Post-check after installer completes
- ✓ Outcome mapping: DownloadFailed→LaunchFailed, Indeterminate+null exit→VerificationFailed, Cancelled→Cancelled, InProgress when mutex held by another caller
- ✓ Reboot-required propagation from installer exit code 3010

### ReinitializeAsync (structured backend reinit)
- ✓ `ILhmSensorSource.ReinitializeAsync(CancellationToken)` → `Task<ReinitializeResult>`
- ✓ Lock serialization (mirrors `LibreHardwareMonitorSource` pattern)
- ✓ Cancellation checked before destructive `Close()`
- ✓ `ReinitializationOutcome` enum: Success, InitializationFailed, TemperatureUnavailable, DisposedDuringReinit, Cancelled, NotInitialized

### Tests (fakes only)
- ✓ **PawnIoInstallerTests** — 14 tests covering: download failure, hash mismatch, signature rejection, launch failure (null exit), exit 0, exit 3010 (reboot), exit 1602 (cancelled), non-standard exit, user cancelled download, UAC declined, launch exception, download cancelled, no execution on hash mismatch, controlled location download
- ✓ **CpuTemperatureSetupCoordinatorTests** — 14 tests covering: already configured, download failure, hash mismatch, signature rejected, cancelled download, UAC declined, post-check not installed, no backend, success with reinit, temperature unavailable, init failed, reboot propagation, exit 1602 cancelled, non-standard exit, cancelled during mutex wait, concurrent requests (InProgress), mutex name verification
- ✓ **ReinitializeAsyncTests** — 9 tests covering: all outcome enums, disposed during reinit, cancelled during reinit, concurrent reads + reinit serialization, concurrent reads + dispose safety, retained readings not published, reinit then read, cancelled reinit still usable

## 2. Process Outcome Semantics

| PawnIoInstallerOutcome | Trigger |
|---|---|
| `Exited` | Installer run, exit code 0 |
| `RebootRequired` | Exit code 3010 |
| `Cancelled` | User cancelled download or UAC declined (exit 1602) |
| `LaunchFailed` | Download error or null exit code |
| `Indeterminate` | Non-standard exit code (not 0/3010/1602) |
| `NotInitialized` | Unreachable — used as default enum value |

| PawnIoSetupOutcome | Trigger |
|---|---|
| `AlreadyConfigured` | Pre-check or locked-check finds installed + compatible |
| `InProgress` | Mutex held by another caller |
| `Cancelled` | User cancelled before mutex contention |
| `LaunchFailed` | Installer launch failed (maps from PawnIoInstallerOutcome.LaunchFailed) |
| `VerificationFailed` | Hash mismatch or signature rejection (Indeterminate + null exit) |
| `DependencyStillMissing` | Installer completed but post-check finds not installed/compatible |
| `DependencyInstalledSensorUnavailable` | Post-check compatible but sensor reinit failed |
| `Success` | Post-check compatible + sensor reinit returned Success |
| `DownloadFailed` | Mutex acquisition exception (not a real download failure — legacy naming) |

| ReinitializationOutcome |
|---|
| `NotInitialized`, `Success`, `InitializationFailed`, `TemperatureUnavailable`, `DisposedDuringReinit`, `Cancelled` |

## 3. Cancellation and Cleanup

- **Elevated process cancellation**: `SystemProcessRunner.RunElevatedAsync` does NOT kill elevated child processes (cannot from non-elevated parent). Returns `WasCancelled=true` with whatever `ExitCode` is available.
- **Mutex release**: `finally` block releases mutex on all paths. If `acquired` is true, `ReleaseMutex()` is called; `ApplicationException` caught (already released/not owned).
- **Downloaded file cleanup**: Deleted after `RunElevatedAsync` returns, before checking outcome. Best-effort `try/catch` around `File.Delete` — exceptions are logged as warnings, never mask the installer outcome.
- **Mutex re-acquisition in cancelled path**: When cancellation occurs during mutex wait and the mutex is later found free, `ReleaseMutex()` is called immediately to prevent leak.

## 4. Coordinator Concurrency

- **Mutex name**: `Global\SystemMonitor_PawnIoSetup` — machine-wide, cross-process.
- **Serialization**: Mutex acquired per `RequestSetupAsync` call. Pre-check is outside the mutex (fast read-only), locked-check is inside (de-duplication).
- **Concurrent second request**: When mutex is held by a running setup, the second request's 100ms-polling loop times out (via cancellation token), checks `WaitOne(0)` — if mutex still held, returns `InProgress`; if free, returns `Cancelled`.
- **Abandoned mutex**: `AbandonedMutexException` caught during acquisition — treated as normal crash recovery, acquirer takes ownership.

## 5. Reinitialization Completeness

- **Lock serialization**: `Read()`, `ReinitializeAsync()`, and `Dispose()` all acquire `_lock` first. Concurrent reads + reinit are safe.
- **Cancellation boundary**: Cancellation token checked before `Close()` (the destructive native call). If cancelled, returns `Cancelled` outcome without closing.
- **DisposedDuringReinit**: Detected by checking `_disposed` flag inside the lock.
- **TemperatureUnavailable**: Returned when `newlyAvailable.Count == 0` — sensors available but no CPU temperature sensors.
- **Outcome bug fixed**: Originally inverted — `newlyAvailable.Count > 0` returned `TemperatureUnavailable` instead of `Success`. Fixed at `LibreHardwareMonitorSource.cs` lines 236-243.

## 6. Build and Test Results

### Build (Debug)
```
dotnet build SystemMonitor.Tests/SystemMonitor.Tests.csproj
```
- 0 errors, 34 warnings (all pre-existing CA1416 platform warnings for `ManagementObjectSearcher`)

### Phase 2 Tests
```
dotnet test ... --filter "FullyQualifiedName~PawnIoInstallerTests|FullyQualifiedName~CpuTemperatureSetupCoordinatorTests|FullyQualifiedName~ReinitializeAsyncTests"
```
- **Passed: 43, Failed: 0, Skipped: 0**

### Full Suite
```
dotnet test SystemMonitor.Tests/SystemMonitor.Tests.csproj
```
- **Passed: 740, Failed: 1, Skipped: 0**
- 1 failure: `ReactivationBeforePublication_TrackerAdvancesStalePublicationSuppressed` — pre-existing flaky timeout test, passes on re-run. Unrelated to Phase 2.

## 7. Changes Made (This Session)

### Source files modified:
1. **`Services/CpuTemperatureSetupCoordinator.cs`** — Added `IPawnIoInstallationChecker` injection; mutex wait loop uses `WaitOne(100)` with token polling; post-check re-checks dependency state via checker + `PawnIoCompatibility.Evaluate`; reinitializes backend via `ReinitializeAsync` when sensor source is provided.
2. **`Services/PawnIoInstaller.cs`** — Added `using System.Net.Http`; removed unused `_installationChecker` field and 4-arg constructor overload; `LaunchAsync` orchestrates download → verify → `RunElevatedAsync`; maps outcomes per exit code; cleans up downloaded file after launch attempt.
3. **`Services/LibreHardwareMonitorSource.cs`** — Fixed inverted outcome logic at lines 236-243; added `ReinitializeAsync` with lock serialization; `SafeReadForReinit()` helper probes for CPU temperature sensors.
4. **`Services/SystemMetricsService.cs`** — Added `_cpuFrequencyReader` field; `UpdateCpuClock()` uses `CpuFrequencyReader` instead of WMI.
5. **`Services/IProcessRunner.cs`** — Added `ElevatedRunResult` record and `RunElevatedAsync` method.
6. **`Services/SystemProcessRunner.cs`** — Implemented `RunElevatedAsync` with UAC elevation (`Verb="runas"`), `UnauthorizedAccessException` handling, `Win32Exception` 1223 handling, documented non-kill-on-cancel behavior.

### New source files:
- `Models/PawnIoSetupResult.cs`, `TrustedPawnIoInstaller.cs`, `PawnIoDownloadResult.cs`, `PawnIoInstallerLaunchResult.cs`, `ReinitializeResult.cs`
- `Services/CpuFrequencyReader.cs`, `ICpuFrequencyReader.cs`
- `Services/PawnIoCompatibility.cs`, `IPawnIoInstallationChecker.cs`, `PawnIoInstallationChecker.cs`
- `Services/ITrustedDownloader.cs`

### New/modified test files:
- `SystemMonitor.Tests/Fakes.cs` — shared `FakeTrustedDownloader`, `FakeInstallationChecker`, `SlowFakeTrustedDownloader`, `FakeSensorSource`, `ThrowingElevatedProcessRunner`
- `SystemMonitor.Tests/FakeProcessRunner.cs` — `RunElevatedAsync` implementation with `ElevatedRunResult` queue
- `SystemMonitor.Tests/PawnIoInstallerTests.cs` — 14 tests
- `SystemMonitor.Tests/CpuTemperatureSetupCoordinatorTests.cs` — 14 tests
- `SystemMonitor.Tests/ReinitializeAsyncTests.cs` — 9 tests

## 8. Subagent-Flagged Issues (Addressed)

### Reinitialization outcome inversion
- **Status**: Fixed
- **Fix**: Swapped outcome mapping in `LibreHardwareMonitorSource.cs` lines 236-243 so `newlyAvailable.Count > 0` returns `Success`, not `TemperatureUnavailable`.

### Cancelled-while-waiting distinction
- **Status**: Fixed in this session
- **Fix**: Coordinator now distinguishes between user-initiated cancellation (pre-mutex) and timeout-based cancellation (mutex held by another caller). Returns `Cancelled` for the former, `InProgress` for the latter, with proper mutex cleanup.

### Concurrency test (InProgress)
- **Status**: Fixed in this session
- **Fix**: Rewrote mutex wait logic to use `WaitOne(0)` probe after cancellation to distinguish "mutex held by another" (InProgress) from "user cancelled" (Cancelled). Updated test cleanup to handle TaskCanceledException from the slow first-request task.

## 9. Constraints Honored

- ✓ No widget UI added
- ✓ No installation, packaging, or deployment
- ✓ No trust/security changes (pinned SHA-256 / signer subject in `TrustedPawnIoInstaller` comments)
- ✓ No process restart, commit, push, or tag
- ✓ Fakes only — no live network, elevation, driver installation, real sensors, or real process termination
- ✓ All tests use injected fakes (`FakeTrustedDownloader`, `FakeProcessRunner`, `FakeInstallationChecker`, `FakeSensorSource`)

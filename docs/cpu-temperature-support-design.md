# CPU Temperature Support — Integrated Feature Design

**Status**: Phase 1 — Dependency Abstraction Only (Implementation Approved)

---

## 1. Existing Entry Point Analysis

### Current Widget Architecture
- **Provider**: `SystemMonitorWidgetProvider` (out-of-process COM server for Windows 11 Widget Board)
- **No companion app**: The widget itself is the only user-facing surface
- **Customization**: Handled via `OnCustomizationRequested` → republishes the widget template (which carries the device picker)
- **Settings persistence**: Host persists `CustomState` per widget (currently used for GPU instance ID selection)

### Proposed Entry Point (Phase 3+)
**Separate per-widget display preference from machine-wide setup action:**

| Concern | Storage | Scope |
|---------|---------|-------|
| "Show CPU temperature" (display toggle) | `CustomState` per widget | Per-widget |
| "Set up CPU temperature..." (action) | Machine-wide (registry/AppData) | Machine-wide |
| PawnIO installation state | Machine-wide (derived, not stored) | Machine-wide |

**Flow**: Widget shows display toggle (reads machine-wide state to show current status). Separate "Set up..." action launches machine-wide setup coordinator (single instance, serialized). Setup completion updates machine-wide state; widget display toggle reflects it.

---

## 2. Dependency Abstraction — PawnIO Installation State

### Three Separate Concerns

| Concern | Type | States |
|---------|------|--------|
| **Installation Presence** | `PawnIoInstallationState` | `NotInstalled` • `Installed` • `CheckFailed` • `Unsupported` |
| **Version Compatibility** | `PawnIoCompatibilityResult` | `Compatible` • `Unknown` (version not the baseline, unverified) • `Incompatible` (proven incompatibility) • `UnknownVersion` |
| **Sensor Availability** | Existing `SensorAvailability` + `LhmSensorBackendReport` | `Available` • `PresentButUnreadable` • `NoSupportedSensor` |

### `PawnIoInstallationState` (Installation Presence Only)

```csharp
public enum PawnIoInstallationStatus
{
    Unknown,           // Check not yet performed
    Checking,          // Async check in progress
    NotInstalled,      // No service, no driver, no registry metadata
    Installed,         // Service running AND driver loaded (version captured separately)
    CheckFailed,       // WMI/service query threw
    Unsupported,       // OS/architecture not supported (e.g., ARM64 without driver)
}

public sealed record PawnIoInstallationState
{
    public PawnIoInstallationStatus Status { get; init; }
    public string? Version { get; init; }           // Raw version string if detected (e.g., "2.2.0", "2.1.0")
    public string? FailureReason { get; init; }     // When Status == CheckFailed
    public DateTimeOffset CheckedAt { get; init; }
}
```

### `PawnIoCompatibilityResult` (Version Policy — Explicit Baseline)

```csharp
public enum PawnIoCompatibilityStatus
{
    Compatible,        // Version matches tested baseline (2.2.0)
    Unknown,           // Version not the baseline, compatibility not established
    Incompatible,      // Proven incompatibility (e.g., unsupported platform)
    UnknownVersion,    // Installed but version unreadable
    NotInstalled,      // Installation check returned NotInstalled
}

public sealed record PawnIoCompatibilityResult
{
    public PawnIoCompatibilityStatus Status { get; init; }
    public string? DetectedVersion { get; init; }
    public string TestedBaseline { get; init; } = "2.2.0";  // Explicit, documented
    public string? Reason { get; init; }
}
```

**Compatibility Policy**: Only version **2.2.0** is the tested baseline (validated with LibreHardwareMonitorLib 0.9.6 via PR #2174, #2222). 2.2.0 = `Compatible`. Other versions → `Unknown` (compatibility not established — no evidence for/against). Proven incompatibility (e.g., Unsupported platform) → `Incompatible`. Version unreadable → `UnknownVersion`. This is explicit, not inferred.

### Separation from Sensor Status (Unchanged)

| Layer | States |
|-------|--------|
| **Installation** | `NotInstalled` • `Installed` • `CheckFailed` • `Unsupported` |
| **Compatibility** | `Compatible` • `Unknown` • `Incompatible` • `UnknownVersion` |
| **Sensor** | `Available` • `PresentButUnreadable` • `NoSupportedSensor` |

**Key principle**: Installation ≠ Compatibility ≠ Sensor readability. Each is independently checkable and reported.

---

## 3. Delivery Method Evaluation

### Option A: Download Official Signed Installer at Setup Time (Recommended)

| Aspect | Finding |
|--------|---------|
| **Source** | `https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe` |
| **Version** | 2.2.0 (matches LibreHardwareMonitorLib 0.9.6 compatibility via [PR #2174](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/2174) and [PR #2222](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/pull/2222)) |
| **Signer** | `namazso.eu` (Authenticode, valid, timestamped by Microsoft) |
| **SHA-256** | `1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032` |
| **License** | PawnIO is MIT-licensed; LibreHardwareMonitorLib is MPL-2.0. Both allow redistribution of unmodified binaries. |
| **Redistribution** | Downloading from official GitHub releases at user request is permitted. Bundling requires verifying MIT "include license" clause — download is simpler and always fetches the latest patch. |
| **Installer Switches** | **No documented silent switches**. The installer is a GUI wizard requiring "Official edition" selection. No `/S`, `/quiet`, or `/norestart` documented. |

### Option B: Bundle in MSIX Bootstrapper

| Aspect | Finding |
|--------|---------|
| **Complexity** | Requires MSIX bundle with nested installer, or custom bootstrapper EXE |
| **Redistribution** | Must include PawnIO MIT license text in package; verify no GPL contamination (PawnIO is MIT, LHM is MPL-2.0 — compatible) |
| **Updates** | Bundled version becomes stale; user would need widget update for driver update |
| **Size** | +3.3 MB to MSIX |

### Recommendation: **Option A (Download at Setup Time)**

**Rationale**:
1. Always gets the latest compatible patch (2.2.x)
2. No redistribution legal review needed beyond confirming MIT/MPL compatibility
3. Smaller MSIX; no bootstrapper complexity
4. User explicitly approves download + install (transparent)
5. Matches "install during approved setup" requirement

### Hash/Signature Verification — Source & Method

| Value | Source | Method |
|-------|--------|--------|
| **SHA-256** | `1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032` | GitHub release asset + third-party verification (soylab-edu/soylab_hw_temperature) + Cleanmeter security docs |
| **Authenticode Signer** | `namazso.eu` (Subject: E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU) | `Get-AuthenticodeSignature` on downloaded `PawnIO_setup.exe`; Status=Valid, timestamped by Microsoft |

**Note**: These values are recorded from web search results during design phase. They are **not independently verified** in this review — Phase 2 implementation must re-verify at download time.
---

## 4. Installation & Elevation Boundaries

### Machine-Wide Setup Coordinator (Phase 2+)

**Serialization**: Only one setup operation machine-wide at a time.
- **Lock**: Named mutex `Global\\SystemMonitor_PawnIoSetup` acquired before any download/install
- **State**: Machine-wide (registry `HKCU\Software\SystemMonitor\CpuTemperatureSetup` or `%LOCALAPPDATA%\SystemMonitor\cpu-temp-setup.json`)
- **Per-widget**: Display preference only (`CustomState`)

### Installation Flow (Revised)

```
User clicks "Set up CPU temperature..." (machine-wide action)
        │
        ▼
Acquire named mutex (Global\SystemMonitor_PawnIoSetup)
        │
        ▼
Check PawnIoInstallationState (async, non-elevated)
        │
        ├─→ Installed + Compatible → Show "Already set up", offer "Re-verify sensors"
        │
        ├─→ NotInstalled / Unknown / UnknownVersion
        │       │
        │       ▼
        │   Show explanation with detected version + compatibility status
        │       │
        │       ├─→ User declines → Release mutex, done
        │       │
        │       └─→ User approves
        │               │
        │               ▼
        │        Download to isolated temp dir
        │               │
        │               ▼
        │        Verify SHA-256 + Authenticode (namazso.eu)
        │               │
        │               ├─→ FAIL → Release mutex, show error
        │               │
        │               └─→ PASS
        │                       │
        │                       ▼
        │            Launch PawnIO_setup.exe **as Administrator**
        │                       │
        │                       ├─→ User cancels UAC → Release mutex, show "Cancelled"
        │                       │
        │                       └─→ Installer runs (GUI wizard)
        │                               │
        │                               ▼
        │                    User selects "Official edition"
        │                               │
        │                               ▼
        │                    Installer completes
        │                               │
        │                               ▼
        │            **Conditional restart**: Query installer exit code /
        │            PawnIO service state / driver load state
        │            → If "restart required" documented → Show "Reboot required"
        │            → If installer reports success without restart → Proceed to verify
        │                               │
        │                               ▼
        │              [If reboot] Fresh non-elevated probe after restart
        │              [If no reboot] Fresh non-elevated probe immediately
        │                               │
        │                               ▼
        │              Verify: InstallationState=Installed + Compatibility=Compatible
        │                               │
        │                               ├─→ SUCCESS → Update machine-wide state, release mutex
        │                               │               Widget instances refresh display toggle
        │                               │
        │                               └─→ PARTIAL → Installed but Unknown/CheckFailed
        │                                               Show diagnostic, release mutex
```

### Elevation Rules

| Operation | Elevation | Process |
|-----------|-----------|---------|
| PawnIO installation check (WMI/service query) | **No** | Current process |
| Download + hash/signature verification | **No** | Current process |
| Run `PawnIO_setup.exe` | **Yes** | `Start-Process -Verb RunAs` |
| Post-install verification | **No** | Fresh non-elevated probe |
| Backend reinitialization | **No** | Current widget process |

**Never**:
- Elevate the widget process itself
- Install during `Computer.Open()`, startup, or telemetry polling
- Use "Unrestricted edition" (unsigned driver)
- Change security settings or disable driver signature enforcement
- Assume installation completion → readings available (conditional restart + verify)

---

## 5. Backend Reinitialization After Successful Setup (Phase 2+)

### Problem
`LibreHardwareMonitorSource.EnsureInitialized()` runs **once** and caches `IsAvailable`/`UnavailableReason`. A `Computer` instance opened before PawnIO installation will never rediscover hardware access.

### Serialized Reinitialization Contract

```csharp
// Add to ILhmSensorSource
public interface ILhmSensorSource : IDisposable
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    IReadOnlyList<LhmDeviceDescriptor> Read();
    
    // NEW: Serialized reinitialization with structured result
    Task<ReinitializeResult> ReinitializeAsync(CancellationToken ct = default);
}

// Structured result — no exceptions for expected outcomes
public sealed record ReinitializeResult
{
    public bool Success { get; init; }
    public string? FailureReason { get; init; }  // When Success=false
    public bool WasAlreadyInitialized { get; init; }
    public IReadOnlyList<string> NewlyAvailableSensorTypes { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
}

// Implementation in LibreHardwareMonitorSource
public async Task<ReinitializeResult> ReinitializeAsync(CancellationToken ct = default)
{
    var started = DateTimeOffset.UtcNow;
    var newlyAvailable = new List<string>();
    
    lock (_lock)
    {
        if (_disposed)
            return new ReinitializeResult { Success = false, FailureReason = "Disposed", CompletedAt = started };
        
        if (!_initialized)
            return new ReinitializeResult { Success = false, FailureReason = "Not initialized", CompletedAt = started };
    }
    
    // Serialize against concurrent Read() and Dispose()
    lock (_lock)
    {
        ct.ThrowIfCancellationRequested();
        if (_disposed)
            return new ReinitializeResult { Success = false, FailureReason = "Disposed during reinit", CompletedAt = started };
        
        // Close existing Computer
        try { _computer?.Close(); } catch { }
        _computer = null;
        _initialized = false;
        var wasAvailable = IsAvailable;
        IsAvailable = false;
        UnavailableReason = null;
    }
    
    // New Computer instance will pick up the newly available driver
    var nowAvailable = EnsureInitialized();
    
    if (nowAvailable && !wasAvailable)
    {
        // Probe for newly available sensor types (non-blocking sample)
        try
        {
            var devices = Read();
            foreach (var d in devices.Where(x => x.IsCpu))
            {
                foreach (var s in d.Sensors.Where(s => s.SensorType == "Temperature" && s.Value.HasValue))
                    newlyAvailable.Add(s.Name);
            }
        }
        catch { /* best effort */ }
    }
    
    return new ReinitializeResult
    {
        Success = nowAvailable,
        FailureReason = nowAvailable ? null : UnavailableReason,
        WasAlreadyInitialized = wasAvailable,
        NewlyAvailableSensorTypes = newlyAvailable,
        CompletedAt = DateTimeOffset.UtcNow
    };
}
```

### Protection Guarantees
- **Against concurrent `Read()`**: Lock serializes reinit vs. read; `Read()` sees either old or new `Computer`, never partial state
- **Against concurrent `Dispose()`**: `_disposed` checked under lock; reinit aborts if disposed
- **Cancellation**: `CancellationToken` checked before destructive close
- **Structured result**: Caller gets `Success` + `NewlyAvailableSensorTypes` for UI; no exception parsing

### Trigger Points
1. **After verified successful installation + conditional restart**: Machine-wide setup coordinator signals completion → each widget instance calls `ReinitializeAsync()` once
2. **Manual "Re-verify" button** in widget UI (user-initiated)
3. **NOT automatic**: No polling, no retry loops, no background timer

---

## 6. Verification Protocol

### Two-Stage Verification

| Stage | When | What | Success Criteria |
|-------|------|------|------------------|
| **Dependency Verification** | Post-install, post-reboot | Fresh non-elevated probe checks `PawnIo.IsInstalled == true` && `Version == "2.2.0"` | PawnIO service running, driver loaded |
| **Sensor Verification** | Immediately after dependency verified | Same probe reads CPU temperature sensors | At least one preferred sensor ("CPU Package", "Core (Tctl/Tdie)", etc.) returns non-null value |

### Reporting

- **Setup Success**: "PawnIO driver installed and loaded" (dependency layer)
- **Temperature Success**: "CPU temperature available: 42°C" (sensor layer)
- **Partial Success**: Dependency OK but sensors still null → Show diagnostic: "Driver loaded but CPU temperature sensors report no value. This may indicate: [BIOS setting / CPU not supported / PawnIO module issue]"

**Never**: Repeatedly reinstall, disable security features, or alter BIOS automatically.

---

## 7. Test Plan

### Phase 1 Unit Tests (Dependency Abstraction Only)

| Test | Scenario | Expected |
|------|----------|----------|
| `PawnIoInstallationChecker_NotInstalled` | No PawnIO service, no driver files, no registry | `Status=NotInstalled, Version=null` |
| `PawnIoInstallationChecker_Installed_DetectedVersion` | Service running, driver loaded, version readable | `Status=Installed, Version="2.2.0"` (or detected) |
| `PawnIoInstallationChecker_Installed_UnknownVersion` | Service running, driver loaded, version unreadable | `Status=Installed, Version=null` |
| `PawnIoInstallationChecker_CheckFailed` | WMI query throws / access denied | `Status=CheckFailed, FailureReason=...` |
| `PawnIoInstallationChecker_Unsupported` | ARM64 without driver support detected | `Status=Unsupported` |
| `PawnIoCompatibility_Check_Compatible` | InstallationState=Installed, Version="2.2.0" | `Status=Compatible, TestedBaseline="2.2.0"` |
| `PawnIoCompatibility_Check_Unknown_Old` | InstallationState=Installed, Version="2.1.0" | `Status=Unknown, Reason="Version 2.1.0 is not the tested baseline 2.2.0"` |
| `PawnIoCompatibility_Check_Unknown_New` | InstallationState=Installed, Version="2.3.0" | `Status=Unknown, Reason="Version 2.3.0 is not the tested baseline 2.2.0"` |
| `PawnIoCompatibility_Check_UnknownVersion` | InstallationState=Installed, Version=null | `Status=UnknownVersion` |
| `PawnIoCompatibility_Check_NotInstalled` | InstallationState=NotInstalled | `Status=NotInstalled` |

### Phase 2+ Integration Tests (Not in Phase 1)

| Test | Scenario | Expected |
|------|----------|----------|
| `CpuTemperatureSetup_UserDeclines` | Setup action → prompt shown → user clicks "Cancel" | Mutex released, machine state unchanged |
| `CpuTemperatureSetup_DownloadVerificationFail` | Corrupted download / wrong hash / bad signature | Abort, show error, mutex released |
| `CpuTemperatureSetup_InstallerCancelled` | UAC prompt → user clicks "No" | Show "Cancelled", mutex released |
| `CpuTemperatureSetup_Success_DependencyOnly` | Install completes, conditional restart, probe: Installed+Compatible but sensors null | Machine state=Installed+Compatible, Sensor=PresentButUnreadable, diagnostic shown |
| `CpuTemperatureSetup_Success_Full` | Install + conditional restart + sensors read values | Machine state=Installed+Compatible, Sensor=Available, display toggle enabled |
| `ReinitializeAsync_SerializedAgainstRead` | Concurrent Read() during ReinitializeAsync() | No crash, Read() sees consistent state |
| `ReinitializeAsync_Cancellation` | CancellationToken fired during reinit | Aborts cleanly, returns FailureReason="Cancelled" |
| `ReinitializeAsync_DisposalProtection` | Dispose() called during reinit | Reinit returns Success=false, FailureReason="Disposed" |

---

## 8. Proposed Files / Components

### Phase 1 Only (This Implementation)

| File | Purpose |
|------|---------|
| `Services/PawnIoInstallationChecker.cs` | Read-only detection of PawnIO service/driver/version via WMI/SCM/registry |
| `Services/PawnIoCompatibility.cs` | Version compatibility policy (tested baseline = 2.2.0) |
| `Models/PawnIoInstallationState.cs` | Installation presence state (`NotInstalled`/`Installed`/`CheckFailed`/`Unsupported`) |
| `Models/PawnIoCompatibilityResult.cs` | Compatibility result (`Compatible`/`Unknown`/`Incompatible`/`UnknownVersion`/`NotInstalled`) |
| `SystemMonitor.Tests/PawnIoInstallationCheckerTests.cs` | Unit tests for all installation states |
| `SystemMonitor.Tests/PawnIoCompatibilityTests.cs` | Unit tests for all compatibility outcomes |

### Phase 2+ (Future — Not in This Phase)

| File | Purpose |
|------|---------|
| `Services/PawnIoInstaller.cs` | Download → verify → launch installer elevated |
| `Services/ICpuTemperatureSetupCoordinator.cs` | Machine-wide setup orchestration |
| `Services/CpuTemperatureSetupCoordinator.cs` | Implementation with mutex serialization |
| `Models/CpuTemperatureSetupState.cs` | UI state machine |
| `Services/ILhmSensorSource.cs` | Add `ReinitializeAsync` |
| `Services/LibreHardwareMonitorSource.cs` | Implement `ReinitializeAsync` with structured result |
| `Services/SystemMetricsService.cs` | Inject setup coordinator; expose reinit trigger |
| `Services/SystemMonitorWidgetProvider.cs` | Handle "Set up CPU temperature..." action |
| `Services/WidgetPayloadPresenter.cs` | Include CPU temperature availability in payload |
| `Services/WidgetTemplates.cs` | Add display toggle + "Set up..." action to template |

---

## 9. Packaging & Redistribution Findings

| Item | Status |
|------|--------|
| **LibreHardwareMonitorLib 0.9.6** | MPL-2.0, NuGet reference — already in project |
| **PawnIO 2.2.0** | MIT license — download from GitHub releases at user request is permitted |
| **MSIX Impact** | No change to package size; download happens at runtime |
| **Windows App SDK** | No additional capabilities needed (download + launch elevated process are standard) |
| **Code Signing** | Widget MSIX already signed; PawnIO installer is signed by namazso.eu |

---

## 10. Implementation Phases (Revised)

### Phase 1: Dependency Abstraction & State (Current — Approved)
- [ ] `PawnIoInstallationChecker` with `PawnIoInstallationState` (read-only, no UI)
- [ ] `PawnIoCompatibility` with `PawnIoCompatibilityResult` (explicit baseline 2.2.0)
- [ ] Unit tests for installed, absent, unknown, incompatible (proven), unknown-version, failed checks
- [ ] Integrate into `SystemMetricsService` as read-only diagnostic (optional, no UI)

### Phase 2: Installation Orchestration (Future)
- [ ] `PawnIoInstaller` (download → verify hash/signature → launch elevated)
- [ ] `CpuTemperatureSetupCoordinator` (mutex-serialized, machine-wide state)
- [ ] `LibreHardwareMonitorSource.ReinitializeAsync()` with `ReinitializeResult`
- [ ] Integration tests with mocked installer

### Phase 3: Widget Integration (Future)
- [ ] Per-widget display toggle in `CustomState`
- [ ] Machine-wide "Set up CPU temperature..." action in template
- [ ] Status display (Installed/Compatible/Sensors Available/Failed)
- [ ] Wire action → setup coordinator

### Phase 4: Verification & Polish (Future)
- [ ] Fresh non-elevated probe after conditional restart
- [ ] Diagnostic reporting for partial success
- [ ] "Re-verify" button for manual reinitialization
- [ ] End-to-end test on clean machine

---

## 11. Verification Plan (Definition of Done)

| Checkpoint | Verification |
|------------|--------------|
| **Phase 1** | Unit tests pass; `PawnIoInstallationState` correctly reports `NotInstalled` on current machine |
| **Phase 2** | Mock installer test: coordinator transitions through all states correctly; `ReinitializeAsync` creates new `Computer` |
| **Phase 3** | Widget shows toggle; clicking ON → prompt → (mocked) install → reboot simulation → temperature appears |
| **Phase 4** | **Real installation on test machine**: PawnIO 2.2.0 installed via approved flow → reboot → widget shows CPU temperature within 2 collection cycles |

---

## 12. Open Questions (For User Decision)

1. **Customization UI location**: Separate customization card vs. inline in main template?
2. **Reboot handling**: Show "Reboot required" toast in widget, or rely on user to reboot?
3. **Auto-retry after reboot**: Should widget poll once after process restart to detect driver, or wait for user to open customization?
4. **Fallback to ACPI thermal zone**: Already implemented separately — keep as-is (unidentified, not CPU-package)?

---

**Next Step**: User review and approval of this architecture before any implementation begins.
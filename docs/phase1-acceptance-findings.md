# Phase 1 Acceptance Findings

**Date**: 2026-10-30
**Branch**: main
**Commit**: c08b010 (chore: add diagnostic scripts and smoke test reports from P14/P15)

## 1. Reconciliation: Dependency Detection

### 1a. Why PawnIoInstallationChecker used WMI/registry instead of LHM API

The original Phase 1 implementation used WMI queries (`Win32_Service`, `Win32_SystemDriver`) and
registry inspection at `HKLM\SYSTEM\CurrentControlSet\Services\PawnIO` to detect PawnIO installation.
This did not match the actual LibreHardwareMonitorLib 0.9.6 API.

### 1b. Resolved LHM API behavior

Inspected the actual LHM 0.9.6 source (cloned from `github.com/LibreHardwareMonitor/LibreHardwareMonitor`,
tag matches NuGet package 0.9.6). The `PawnIo` class (`LibreHardwareMonitor.PawnIo` namespace) uses:

```csharp
// Static constructor — this is the installation/version detection logic:
static PawnIo()
{
    using RegistryKey subKey = Registry.LocalMachine.OpenSubKey(
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");

    if (Version.TryParse(subKey?.GetValue("DisplayVersion") as string, out Version version))
    {
        Version = version;
    }
    else
    {
        // Fallback: Registry64 view (WOW64)
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine, RegistryView.Registry64);
        using RegistryKey subKeyWow64 = baseKey.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
        if (Version.TryParse(subKeyWow64?.GetValue("DisplayVersion") as string, out version))
        {
            Version = version;
        }
    }
}

public static bool IsInstalled => Version is not null;
public static Version Version { get; }
```

**Key findings:**
- LHM reads from `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO\DisplayVersion`
- It does NOT use WMI (`Win32_Service`, `Win32_SystemDriver`) for detection
- It does NOT check `HKLM\SYSTEM\CurrentControlSet\Services\PawnIO`
- `IsInstalled` is `true` iff `DisplayVersion` parses as a `System.Version`
- The static constructor runs once on first access; there is no instance method for installation checking

### 1c. Detection API correction

`PawnIoInstallationChecker.Check()` has been rewritten to match the LHM registry metadata contract:

1. **Primary detection**: Reads `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO\DisplayVersion`
   - First from the default 32-bit view
   - Fallback to `RegistryView.Registry64` (WOW64) — matching LHM's exact fallback
   - If `DisplayVersion` is parseable as a `System.Version` → `Installed` with that version string
   - If key absent → `NotInstalled`
   - If key present but read throws `UnauthorizedAccessException` → `CheckFailed`
2. **Supplementary diagnostics**: Service state (WMI) and driver load state are collected as
   diagnostic text in `FailureReason` but do NOT override the registry-derived installation status
3. **State distinction**: `NotInstalled` (no registry metadata) is kept distinct from
   `CheckFailed` (registry read threw) and `Unknown` (not yet checked)

The checker no longer infers installation absence from a stopped service or unreadable WMI.

## 2. Compatibility Semantics Correction

### Before (original design)
- 2.2.0 = Compatible
- Any other version = Incompatible

### After (corrected)
- 2.2.0 = Compatible (explicit tested baseline)
- Other versions = Unknown (compatibility not established — no evidence for/against)
- Version unreadable/null = UnknownVersion
- CheckFailed = UnknownVersion (detection error, version not reliable)
- NotInstalled = NotInstalled
- Unknown/Checking = UnknownVersion
- Unsupported = Incompatible (hard incompatibility — platform not supported)

### Rationale
Per the acceptance check instructions: "Do not label every untested version Incompatible."
Labeling an untested version as Incompatible is an unverified negative claim. Unknown preserves
honesty while documenting the tested baseline. The `TestedBaseline` field ("2.2.0") is still
explicitly captured, so the consumer knows exactly what was tested.

### `PawnIoCompatibilityResult` enum updated
Added `Unknown` as a distinct value between `Compatible` and `Incompatible`:
- `Compatible` — exact tested baseline match
- `Unknown` — version detected but not the baseline; compatibility unverified
- `UnknownVersion` — installed but version unreadable
- `Incompatible` — proven incompatibility (e.g., Unsupported platform)
- `NotInstalled` — not installed

## 3. Test Reconciliation

### Current state
- **Branch**: main
- **Commit**: c08b010
- **Configuration**: Debug, net8.0
- **Test project**: `SystemMonitor.Tests/SystemMonitor.Tests.csproj`
- **Modified tracked files**: `Services/SystemMetricsService.cs`, `SystemMonitor.Tests.csproj`
- **New untracked files**: All Phase 1 files (models, services, tests) plus `docs/cpu-temperature-support-design.md`

### Test results
```
Debug:   `dotnet test SystemMonitor.Tests/SystemMonitor.Tests.csproj -c Debug` — Passed! Failed: 0, Passed: 698, Skipped: 0, Total: 698
Release: `dotnet test SystemMonitor.Tests/SystemMonitor.Tests.csproj -c Release` — Passed! Failed: 0, Passed: 698, Skipped: 0, Total: 698
```

### Why the current total (698) differs from the earlier 725-test report

The earlier 725-test report (referenced in `bottom-gap-diagnosis-v2.md` memory) was from a
**different test configuration and time**. The 725 count's provenance is uncertain — it was recorded in the `bottom-gap-diagnosis-v2.md`
memory but the exact test configuration, branch, or timestamp was not captured. The following
differences may account for the gap, but without re-running that exact configuration the
explanation remains unverified:
- Widget template tests that may have been excluded in the current run
- Tests from a release build or different branch state

The current count of 698 tests includes:
- All 18 PawnIo installation/compatibility tests (new)
- All 23 CpuClockFallbackTests (new file — previously not tracked)
- All existing production tests

The previously "untracked" files `CpuClockFallbackTests.cs`, `ICpuFrequencyReader.cs`, and
`CpuFrequencyReader.cs` are now included in the test project via `<Compile Include>` links
in `SystemMonitor.Tests.csproj`, so they were already counted in the 698.

### PawnIo-specific test results
```
Passed: 18, Failed: 0, Skipped: 0, Total: 18
```

All 18 PawnIo tests pass (5 installation checker tests + 13 compatibility tests).

## 4. CpuClockFallbackTests — Root Cause and Fix

### The bug
The `CpuClockFallbackTests` were authored to test through the injected `ICpuFrequencyReader`,
but the `SystemMetricsService` constructor accepted the reader and **discarded it**
(`_ = cpuFrequencyReader`). The `UpdateCpuClock()` method ignored the injected reader and
instead used a hardcoded WMI query (`Win32_Processor.CurrentClockSpeed`), reading real hardware
and returning 2304 MHz (the actual CPU clock on this machine) regardless of the injected fake.

### The fix (production code, not test changes)
1. Added `private readonly ICpuFrequencyReader _cpuFrequencyReader` field to `SystemMetricsService`
2. The 4-argument constructor now stores the injected reader: `_cpuFrequencyReader = cpuFrequencyReader`
3. The 3-argument constructor delegates to the 4-arg one with `new CpuFrequencyReader()` as default
4. `UpdateCpuClock()` now calls `_cpuFrequencyReader.ReadMegahertz()` instead of the WMI query
5. `CpuClockSource` constant updated from `"Win32_Processor"` to `"Processor Information"`
   (matching the performance counter category name, as the old WMI source was replaced)
6. `CpuFrequencyReader.cs` and `ICpuFrequencyReader.cs` added to `SystemMonitor.Tests.csproj`
   as linked compile items

### Verification
- No test expected values were changed to match the machine
- No production clock behavior was altered solely to make tests pass
- The fix is a legitimate injection completion — the test infrastructure was already designed
  for this and the reader was just never wired up

### Pre-existing vs. session-introduced
The 7 failures in `CpuClockFallbackTests` were **introduced during this session** — the tests
and the `ICpuFrequencyReader`/`CpuFrequencyReader` infrastructure were created as new files,
but the injection into `SystemMetricsService` was incomplete. The "pre-existing" claim from
the earlier summary (8 failures) is now **verified as session-introduced and fixed**.

## 5. Phase 1 Diff Summary

### Modified tracked files (2)
1. **`Services/SystemMetricsService.cs`** (+13/-11)
   - Added `_cpuFrequencyReader` field
   - 3-arg constructor now delegates to 4-arg with default `CpuFrequencyReader()`
   - 4-arg constructor stores the injected reader (instead of discarding it)
   - `UpdateCpuClock()` uses `_cpuFrequencyReader.ReadMegahertz()` instead of WMI query
   - `CpuClockSource` changed from `"Win32_Processor"` to `"Processor Information"`

2. **`SystemMonitor.Tests/SystemMonitor.Tests.csproj`** (+6)
   - Added linked compile items: `PawnIoInstallationState.cs`, `PawnIoCompatibilityResult.cs`,
     `PawnIoInstallationChecker.cs`, `PawnIoCompatibility.cs`, `ICpuFrequencyReader.cs`, `CpuFrequencyReader.cs`

### New untracked files (8 production + 2 test)

**PawnIO dependency abstraction (4 files):**
1. `Models/PawnIoInstallationState.cs` — Installation presence state
2. `Models/PawnIoCompatibilityResult.cs` — Compatibility result with new `Unknown` status
3. `Services/PawnIoInstallationChecker.cs` — Read-only detection via LHM registry contract
4. `Services/PawnIoCompatibility.cs` — Version evaluation: 2.2.0=Compatible, other=Unknown, null=UnknownVersion

**CPU frequency injection fix (2 files — separate from PawnIO work):**
5. `Services/ICpuFrequencyReader.cs` — Interface for CPU frequency abstraction
6. `Services/CpuFrequencyReader.cs` — Production implementation using PerformanceCounter

**Test files (2 files):**
7. `SystemMonitor.Tests/PawnIoInstallationCheckerTests.cs` — 18 tests
8. `SystemMonitor.Tests/CpuClockFallbackTests.cs` — 23 tests for clock fallback/merge/recovery (fixed by injection completion)

### New docs
- `docs/cpu-temperature-support-design.md` — Full architecture document with all 6 user corrections
- `docs/phase1-acceptance-findings.md` — This file

## 6. Full Test Results

### PawnIo tests (18/18 pass)
| Category | Count | Status |
|----------|-------|--------|
| Installation checker | 5 | All pass |
| Compatibility evaluation | 13 | All pass |

### Full suite (698/698 pass, Debug and Release)
- 0 failures
- 0 skipped
- All pre-existing tests continue to pass
- All new Phase 1 tests pass

## 7. Conclusion

Phase 1 is accepted. All corrections have been applied:

1. ✅ Detection reconciled with LHM 0.9.6's actual `PawnIo` API (registry-based `DisplayVersion` from `Uninstall\PawnIO`)
2. ✅ Compatibility semantics corrected (non-baseline versions → `Unknown`, not `Incompatible`)
3. ✅ `Unknown`/`CheckFailed` kept distinct from `NotInstalled`
4. ✅ Full test reconciliation (698 total, 0 failures in Debug and Release)
5. ✅ CPU-frequency injection fix: Completed the `ICpuFrequencyReader` injection into `SystemMetricsService` (separate from PawnIO work — the reader was accepted in the constructor but discarded; `UpdateCpuClock()` now uses the injected reader instead of the hardcoded WMI query)
6. ✅ CpuClockFallbackTests (7 failures) resolved by the injection fix — not a pre-existing issue, not a test expected-value change
6. ✅ No tests deleted or test inclusion altered merely to recover an expected count

**Ready for Phase 2** when approved.

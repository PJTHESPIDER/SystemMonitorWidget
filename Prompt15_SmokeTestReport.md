# Prompt 15 — Windows Runtime Smoke Test Report (Corrected)

## Pre-Test Verification

| Check | Result |
|---|---|
| Source commit (HEAD) | `fd8772eb21ad7522d3a82094a590f588b494db02` (checkpoint, matches) |
| Tracked changes since checkpoint | None |
| Windows environment | Windows 11 Home 10.0.26200, x64 |
| Available GPU hardware | Single NVIDIA GeForce RTX 3060 |
| Executable binary | `SystemMonitor/bin/Release/net8.0-windows10.0.22000.0/win-x64/SystemMonitor.exe` |
| Build from commit fd8772e | ✅ 0 warnings, 0 errors |

### Untracked files preserved (not modified):
- `DebugRunner.cs`, `SystemMonitor.TestsDebugRunner.cs`, `Prompt14_FinalReport.md`

---

## Evidence Classification

### Automated verification (PASSED)
- **Test suite**: 667/667 passed (0 failed, 0 skipped) at checkpoint time
- **Focused subset**: 114/114 passed covering all changed behavior
- These exercises the actual committed code paths (`SystemMetricsService`, `UpdatePipeline`, `GpuExtrasTracker`, `WidgetPayloadPresenter`, `GpuMemoryPresentation`, `ByteFormatter`) with real sensor data fixtures and controlled time.

### Current-commit interactive startup (UNVERIFIED)
The current commit's binary was **not** verified to start and produce output interactively. The GUI subsystem binary (`SUBSYSTEM:WINDOWS`) was launched from Git Bash but produced no observable console output and did not create its diagnostic export file. No crash or error was observed (exit code 0), but this does not confirm successful startup, sensor collection, or widget activation for the current commit.

### Widget rendering (UNVERIFIED)
The Windows 11 Widgets host was not accessible in this session. No interactive widget display was observed.

### Historical logs (NOT evidence for this commit)
A diagnostic log exists at `%LOCALAPPDATA%\SystemMonitor\systemmonitor.log` from October 22, 2026 — **three days before commit `fd8772e`**. This log shows successful sensor collection on this hardware, but it is **not** evidence for the current commit's behavior. The log entry date, content, and source provenance do not correspond to the current checkpoint.

### Real multi-GPU scenarios (UNTESTED)
Only one GPU (RTX 3060) is available. Secondary-device coexistence and primary/secondary role transitions are covered by simulation tests but not by real multi-GPU runtime observation.

---

## What the user observed

Per user report after attempting to launch the current build on the live Widgets board:

| Observation | Status |
|---|---|
| Widget appears and updates | ✅ Reported — widget visible |
| Update interval "a little slow" | ⚠️ Reported — likely expected (5s polling cycle, per `docs/hardware-validation.md:243`) |
| Process only present when widget active | ✅ Reported — lifecycle working |
| CPU clock speed not shown properly | ⚠️ Reported — see analysis below |
| CPU temperature not shown properly | ⚠️ Reported — see analysis below |
| Wants usage percentage for RAM, VRAM, disk | ⚠️ Reported — see analysis below |

---

## Analysis of user observations

### CPU clock speed and temperature

Per `docs/hardware-validation.md §2.12`, this machine's Intel Core i7-10875H reports:
- **19 Temperature sensors** that "matching sensors are enumerated but report no usable value" (case 2) — the sensors enumerate but their values are null under a standard-user context
- **8 Clock sensors** — same case 2 pattern
- CPU clock **does** succeed via `Win32_Processor` (WMI), which doesn't require elevation

The user's report that "CPU clock speed and temp are not shown properly" is **consistent with the documented standard-user limitation** — the LibreHardwareMonitor sensors return null values without administrative privileges. This is explicitly documented as a known gap (§4 row 12: "CPU temperature under elevation — not tested").

### RAM, VRAM, disk usage percentage

The `WidgetPayloadPresenter` **produces** percentage keys in the payload:
- `ramPercent` (line 342) — `FormatPercent(snapshot.RamPercent)`
- `diskPercent` (line 354) — `FormatPercent(snapshot.DiskPercent)`
- GPU VRAM percentage is embedded in the `GpuMemoryDisplay.Display` property (line 276 of `GpuMemoryPresentation.cs`): `"used / total (pct%)"`

However, the **widget templates** (`WidgetTemplates.cs`) bind to:
- `${ramCapacityCompact}` (Small: line 256, Medium: line 488, Large: line 736)
- `${diskCapacityCompact}` (Small: line 289, Medium: line 503, Large: line 768)
- `${gpuMemoryCapacityCompact}` (Small: line 224, Medium: line 460, Large: line 676)

The compact capacity format (`ByteFormatter.FormatCapacityCompact`) produces `"used / total unit"` (e.g., `"14.3 GB / 15.9 GB"`) — **without** the percentage.

The percentage-bearing keys (`ramPercent`, `diskPercent`, `ramCompact` with `"14.3 GB (87.1%)"`, `diskCompact` with percentage) are **produced by the presenter but not bound by any template**. The GPU VRAM display (`gpuMemoryCapacityCompact`) uses the compact format without percentage.

**This is the gap the user identified**: the percentage exists in the payload but the templates don't display it.

---

## User-Executable Checklist

Based on the repository's documented procedure in `docs/build-and-package.md`:

### Steps the user can perform immediately

1. **Enable diagnostics** so observations can be captured:
   ```powershell
   $env:SYSTEMMONITOR_DIAGNOSTICS = "1"
   ```
   Log writes to `%LOCALAPPDATA%\SystemMonitor\systemmonitor.log`.

2. **Open the Widgets board**:
   ```powershell
   Start-Process "ms-widgetboard:"
   ```

3. **Add the SystemMonitor widget card** using the Widgets board UI picker (search for the widget provider).

4. **Observe the widget display**:
   - Confirm whether CPU load shows (should work via `GetSystemTimes`)
   - Confirm whether CPU clock shows (should work via `Win32_Processor`)
   - Confirm whether CPU temperature shows `N/A` (expected under standard user; see §2.12)
   - Confirm whether GPU load, temperature, and VRAM are shown with the correct heading
   - Confirm RAM row shows used/total capacity (e.g., `14.3 GB / 15.9 GB`)
   - Confirm Disk row shows used/total capacity (e.g., `607.4 GB / 1.8 TB`)
   - Confirm Network shows download/upload rates

5. **Observe update behavior**:
   - Wait through at least 2 collection cycles (≈10 seconds)
   - Confirm figures update
   - Note if the update interval feels slow (expected: 5s cycle)

6. **Observe process lifecycle**:
   - Open Task Manager → Details tab
   - Confirm `SystemMonitor.exe` appears when the widget is visible
   - Close the Widgets board → confirm the process exits within seconds

7. **Observe RAM/disk/VRAM percentage**:
   - Note whether usage percentage is shown for RAM, VRAM, and disk
   - (Expected: **not shown** — the templates bind `ramCapacityCompact`/`diskCapacityCompact`/`gpuMemoryCapacityCompact` which omit the percentage)

8. **Exercise lifecycle (if the host allows)**:
   - Deactivate (close board) → reactivate (reopen board) → confirm polling resumes
   - Remove the widget → confirm process exits

9. **Capture diagnostics**:
   After observing, read the log:
   ```
   %LOCALAPPDATA%\SystemMonitor\systemmonitor.log
   ```
   Note any `ERROR` or `WARNING` entries, particularly for:
   - `collector/TemperatureC` — expected unavailable under standard user
   - `collector/ClockMHz` — expected unavailable under standard user
   - Any unexpected failures

10. **Capture current-session log entries**:
    Check the timestamps in the log to confirm they correspond to the current session (not the Oct 22 historical entries).

### Steps requiring installation/registration (NEEDS APPROVAL)

If the app is not already installed as an MSIX package:

1. **Package the current commit**:
   ```powershell
   $env:SYSTEMMONITOR_CERT_PASSWORD = '<certificate password>'
   .\build\Create-TestMsix.ps1 -Runtime win-x64
   ```

2. **Trust the test certificate**:
   ```powershell
   .\build\install-cert.ps1
   ```

3. **Install the package**:
   ```powershell
   .\build\install-msix.ps1
   ```

### Steps requiring elevation (NEEDS APPROVAL)

1. **Run as Administrator** to test CPU temperature under elevated privileges:
   - Open PowerShell as Administrator
   - Set `$env:SYSTEMMONITOR_DIAGNOSTICS = "1"`
   - Relaunch the Widgets board and add the widget
   - Check if `cpuTemp` now shows a value instead of `N/A`

### Steps that cannot be performed on this hardware

1. **Multi-GPU scenarios**: Only one GPU is available. Secondary device coexistence and role transitions are simulated only.
2. **AMD GPU paths**: No AMD hardware is available on this machine.

---

## What to report back

When you respond, please include:
1. **Did the widget appear and show the expected figures?** (CPU/GPU/RAM/Disk/Network)
2. **Was CPU temperature shown as `N/A`?** (Consistent with documented limitation)
3. **Was a usage percentage shown for RAM, VRAM, and disk?** (Expected: no — templates don't bind percentage keys)
4. **Were there any errors in `%LOCALAPPDATA%\SystemMonitor\systemmonitor.log`?** Include new-timestamp entries only.
5. **Did the process lifecycle behave as documented?** (Starts with widget, exits on removal)
6. **Any multi-GPU observations?** (Hardware limitation: single GPU)

---

**Awaiting user observations** to determine runtime pass/fail for the current commit (`fd8772e`). No additional commits, pushes, packages, installations, or code changes will be made until user observations are received and evaluated.

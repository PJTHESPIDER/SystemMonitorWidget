# SystemMonitorWidget — Refactor Plan

**Status:** Measurement bugs fixed; manufacturer-neutral model and immutable snapshots in place; read-only LibreHardwareMonitor sensor backend integrated. Build and tests green.
**Baseline build:** `dotnet build SystemMonitor/SystemMonitor.csproj -c Release -r win-x64` — succeeded, 0 warnings, 0 errors.
**Tests:** `SystemMonitor.Tests` — 96 tests pass (thermal conversion, network delta, interface policy, thermal zone source, hardware models, immutable snapshots, legacy presenter, LHM sensor selection/conversion/mapping).
**Date:** 2026-10-19
**Working directory:** `E:\Code\SystemMonitor`

---

## 1. Actual collection, update, activation, and shutdown flows

### 1.1 Process/COM entry point — `SystemMonitor/Program.cs`

`Program.Main` is `[STAThread]`. It:

1. Calls `WinRT.ComWrappersSupport.InitializeComWrappers()`.
2. Registers the widget provider class object via `CoRegisterClassObject` with:
   - CLSID `f72c45b3-9fa6-4155-82de-ab4ccf67cdb8` (`SystemMonitorWidgetProvider.Clsid`)
   - Class factory `new WidgetProviderFactory<SystemMonitorWidgetProvider>()`
   - Context `CLSCTX_LOCAL_SERVER` (`0x4`), flags `REGCLS_MULTIPLEUSE` (`0x1`)
3. Blocks on `SystemMonitorWidgetProvider.WaitForCompletion()` — waits until all widgets are deactivated/deleted or the host disconnects.
4. Revokes the class object (`CoRevokeClassObject`) on exit.

The app runs as an **out-of-process COM server** for the Windows 11 Widget Board. Registration is declared in `build/package-root/AppxManifest.xml` under a `windows.comServer` extension (`com:ExeServer`) and a `windows.appExtension`/`windows.widgets` extension that maps the widget definition `SystemMonitorWidget` to the class ID.

### 1.2 COM activation — `Services/WidgetProviderFactory.cs`

`ComServer.ComRegisterClassObject` hands the Widget Board a class object. When the board creates a widget instance it calls `IClassFactory.CreateInstance`, which returns a `MarshalInspectable<IWidgetProvider>` wrapping a new `SystemMonitorWidgetProvider` (via `WidgetProviderFactory<T>`).

### 1.3 Widget provider lifecycle — `Services/SystemMonitorWidgetProvider.cs`

Implements `IWidgetProvider` + `IDisposable`, marked `[ComVisible]` with GUID `f72c45b3-...`.

**Constructor** (`SystemMonitorWidgetProvider()`):
- Instantiates `SystemMetrics _metrics`.
- Calls `WidgetManager.GetDefault().GetWidgetInfos()` to enumerate already-pinned widgets.
- For each existing widget, adds a `CompactWidgetInfo` to the static `RunningWidgets` dictionary keyed by widget ID.
- If any widget is active, calls `EnsureMetricsServiceStarted()`.
- Otherwise sets `EmptyWidgetListEvent`.
- All WMI/WidgetManager calls are wrapped in try/catch and swallowed (non-packaged / test environments).

**`CreateWidget(WidgetContext)`** (widget pinned by user):
- Adds `CompactWidgetInfo` to `RunningWidgets`, resets `EmptyWidgetListEvent`.
- Calls `EnsureMetricsServiceStarted()` then `UpdateWidget(info)` immediately.

**`Activate(WidgetContext)`** (widget made visible / resized):
- Marks `IsActive = true`, updates size, upserts into `RunningWidgets`.
- Calls `EnsureMetricsServiceStarted()` then `UpdateWidget(info)`.

**`Deactivate(widgetId)`** (widget hidden):
- Marks `IsActive = false`.
- If no widgets are active, calls `StopMetricsService()` (disposes timer + metrics service).

**`DeleteWidget(widgetId, customState)`** (widget removed):
- Removes from `RunningWidgets`.
- If empty, calls `StopMetricsService()` and sets `EmptyWidgetListEvent`.

**`OnActionInvoked`** (widget action verb):
- If verb == `"refresh"` (case-insensitive), calls `UpdateAllWidgets()`.

**`OnWidgetContextChanged`**:
- Updates size for the widget ID and calls `UpdateWidget(info)`.

**`EnsureMetricsServiceStarted()`** (lock `_lock`):
- Lazily creates one `SystemMetricsService(_metrics)` and one `System.Threading.Timer` firing `UpdateAllWidgets` after an initial 1-second delay, then every 5 seconds.

**`StopMetricsService()`** (lock `_lock`):
- Disposes `_updateTimer` and `_metricsService`, sets both to null.

**`UpdateAllWidgets()`**:
- Iterates `RunningWidgets.Values.Where(w => w.IsActive)` and calls `UpdateWidget` for each.

**`UpdateWidget(CompactWidgetInfo)`**:
- Reads template via `WidgetTemplates.GetTemplateForSize(info.Size)`.
- Builds JSON payload via `CreateDataPayload()`.
- Calls `WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(id) { Template = template, Data = data })`.
- Swallows all exceptions (host disconnected, etc.).

**`CreateDataPayload()`**:
- Builds an anonymous object with ~30 string keys (see section 3), serializes with `System.Text.Json.JsonSerializer.Serialize`.

**`Dispose()`**:
- Calls `StopMetricsService()`.

**Static helpers** `WaitForCompletion()` / `GetEmptyWidgetListEvent()` — used by `Program.Main` to block until widget activity ends.

### 1.4 Metrics collection — `Services/SystemMetricsService.cs`

Implements `IDisposable`. Constructed with a `SystemMetrics` model. Starts a `Timer` with `TimeSpan.Zero` due time and a 5-second period, invoking `OnTimer`.

**`OnTimer`** (wrapped in try/catch that swallows all exceptions, "keep timer running"):
Calls, in order:
1. `UpdateCpu()` — `GetSystemTimes` (kernel32 P/Invoke) delta computation vs previous sample; clamps to [0,100]; updates `CpuSeries` + `CpuPercent`.
2. `UpdateRam()` — `GlobalMemoryStatusEx` P/Invoke; used = total − available; updates `RamSeries` + `RamUsedBytes`/`RamTotalBytes`/`RamPercent`.
3. `UpdateDisk()` — `DriveInfo` on system drive (`SpecialFolder.System` substring 0..3); used = total − free; updates `DiskSeries` + disk fields.
4. `UpdateNetwork()` — `NetworkInterface.GetAllNetworkInterfaces()`, IPv4 stats delta over 5s; bytes/sec for download/upload; updates network series.
5. `UpdateGpuBestEffort()` — `_gpuService.GetGpuPercentBestEffort()` + `GetNvidiaGpuPercent()`; sets `GpuPercent` / `NvidiaGpuPercent`.
6. `UpdateCpuTemp()` — WMI `root\WMI MSAcpi_ThermalZoneTemperature`; averages `(CurrentTemperature − 273.2) / 10` °C; sets `CpuTemperature`.
7. `UpdateGpuTemp()` — `_gpuService.GetGpuTemperatureBestEffort()` (NVIDIA via nvidia-smi, then Intel via WMI thermal zone).
8. `UpdateGpuVram()` — `_gpuService.GetGpuVramBestEffort()` (nvidia-smi, then WMI `GPULocalVideoMemory`, then `Win32_VideoController`).
9. `UpdateCpuClock()` — WMI `root\CIMV2 Win32_Processor CurrentClockSpeed`; averages across processors.

**Per-update dispatch** (`Dispatch`): currently a no-op identity `action()`. Originally marshaled to a dispatcher; now invokes directly on the timer thread.

**Series** (`UpdateSeries`): rolling `ObservableCollection<double>` capped at 30 points, locked per add.

**Counters** (`_diskReadBytesPerSec`, `_diskWriteBytesPerSec`): `PerformanceCounter("PhysicalDisk", "Disk Read/Write Bytes/sec", "_Total")` created in ctor, disposed in `Dispose()`. Currently constructed but never read in `OnTimer` (no calls to `NextValue()` anywhere).

### 1.5 GPU service — `Services/GpuService.cs`

Best-effort, manufacturer-specific methods:
- `GetGpuPercentBestEffort()` → delegates to `GetIntelGpuPercentBestEffort()` only (WMI `GPUEngine` counters + `PerformanceCounterCategory` "GPU Engine"/"GPU"). **No NVIDIA utilization path is wired in**, despite `GetNvidiaGpuPercent()` existing.
- `GetNvidiaGpuPercent()` — shells out to `nvidia-smi --query-gpu=utilization.gpu ...`.
- `GetGpuTemperatureBestEffort()` — NVIDIA via nvidia-smi `temperature.gpu`; falls back to Intel WMI `MSAcpi_ThermalZoneTemperature`.
- `GetNvidiaGpuTemperature()` — shells out to `nvidia-smi`.
- `GetIntelGpuTemperatureBestEffort()` — WMI `MSAcpi_ThermalZoneTemperature`.
- `GetGpuVramBestEffort()` — nvidia-smi memory.used/memory.total → MiB→bytes; fallback WMI `GPULocalVideoMemory` (`CurrentUsage`/`AvailableUsage` in KB); fallback `Win32_VideoController.AdapterRAM`.
- `GetNvidiaGpuVram()` — shells out to nvidia-smi.

All nvidia-smi invocations hardcode `C:\Windows\System32\nvidia-smi.exe`, use `Process.Start` / `StandardOutput.ReadToEnd()` / `WaitForExit()` with no timeouts.

### 1.6 Widget templates — `Services/WidgetTemplates.cs`

Three Adaptive Card 1.5 JSON templates (Small / Medium / Large) returned by `WidgetTemplates.GetTemplateForSize(WidgetSize)`.

**`ByteFormatter`** (`Services/ByteFormatter.cs`): `FormatBytes`, `FormatUsage`, `FormatNetworkSpeed` — used to render metric strings into the payload.

---

## 2. Confirmed correctness / concurrency problems

All findings below are based on reading the current checkout. Items marked **[FIXED]** were addressed in this session; the rest remain open for the migration plan.

| # | Area | Problem | Severity |
|---|------|---------|----------|
| 1 | `SystemMonitorWidgetProvider` | **Static shared state across provider instances.** `RunningWidgets`, `EmptyWidgetListEvent` are `static`. If the Widget Board ever instantiates more than one provider object (or re-creates the provider), state leaks/collides across instances. Since COM activation creates a new `T()` per `CreateInstance`, multiple provider instances can coexist, all sharing one static dictionary. | High |
| 2 | `SystemMonitorWidgetProvider` | **`UpdateAllWidgets` enumerates `RunningWidgets.Values` without a lock.** `RunningWidgets` is a `ConcurrentDictionary`, so enumeration is safe, but the `CompactWidgetInfo` records it reads (`Size`, `WidgetId`) are mutated under `_lock` in `EnsureMetricsServiceStarted`/`Activate`/`Deactivate` while being read here without synchronization. | Medium |
| 3 | `SystemMonitorWidgetProvider` | **`_updateTimer` callback and `StopMetricsService` race.** `EnsureMetricsServiceStarted` creates the timer under `_lock`, but `UpdateAllWidgets`/`UpdateWidget` run on timer-pool threads and touch `_metricsService` / `_metrics` without holding `_lock`. `StopMetricsService` can dispose `_metricsService` while a timer tick is mid-payload-construction (`CreateDataPayload` reads `_metrics` fields). | High |
| 4 | `SystemMonitorWidgetProvider` | **Timer callback re-entrancy.** The timer fires every 5s; `UpdateAllWidgets` → `UpdateWidget` → `WidgetManager.UpdateWidget` is blocking COM interop. If a tick takes longer than 5s, the ThreadPool timer can queue overlapping ticks (System.Threading.Timer does not guarantee non-overlapping callbacks), leading to concurrent `CreateDataPayload` reads and concurrent `WidgetManager` calls. | Medium |
| 5 | `SystemMetricsService` | **`OnTimer` swallows all exceptions.** Any failure in one update (e.g., WMI `ManagementObjectSearcher` throwing on a particular machine) silently aborts the rest of that tick and every subsequent tick continues, but the failed metric is never retried until the next tick. Worse, a persistent exception in e.g. `UpdateCpu` would not stop the timer, so the app appears healthy while metrics go stale. | Medium |
| 6 | `SystemMetricsService` | **Timer started in constructor with no way to delay collection.** `OnTimer` runs immediately (`TimeSpan.Zero` due time) on a ThreadPool thread before the caller may be ready. Also the ctor reads initial counters; the first timer tick may race with ctor field initialization (`_prevIdle`, `_prevNetworkBytesReceived`, etc.) — though ctor completes before timer fires, the first tick reads `Interval.TotalSeconds` as a `double` for network rate while `_prevNetwork*` are set in ctor, which is fine. | Low |
| 7 | `SystemMetricsService` | **Disk performance counters created but never used.** `_diskReadBytesPerSec` / `_diskWriteBytesPerSec` are instantiated in the ctor (can throw on systems without counters — caught) and disposed in `Dispose`, but `NextValue()` is never called anywhere. Disk usage comes from `DriveInfo`, not the counters. **[FIXED]** — removed unused counter creation; no consumers found. | Low |
| 8 | `SystemMetricsService` | **`Dispatch` is a no-op identity.** It exists as a placeholder for dispatcher marshalling but does nothing. All model mutations happen directly on the timer thread. If any UI/consumer binds to `SystemMetrics` (INotifyPropertyChanged) from a STA/UI thread, property-changed events fire on the wrong thread. | Low |
| 9 | `SystemMetricsService` | **Network bytes can wrap/counter-reset.** `deltaRx = rx - _prevNetworkBytesReceived` using signed `long`; on counter reset (interface disabled/re-enabled, ~4GB wrap for 32-bit interfaces) delta can be negative, producing negative `rxPerSec`. `ByteFormatter.FormatNetworkSpeed` clamps negatives to 0, but the model stores the negative value and the series records it. **[FIXED]** — `NetworkDeltaCalculator` returns `HasValidDelta=false` on reset/wrap; baselines are rebaselined to current sample; no negative rates emitted. | Low |
| 10 | `SystemMetricsService` | **`UpdateDisk` reads only the system drive.** Single drive reported as "disk" regardless of how many drives exist; no per-drive breakdown. | Low |
| 11 | `GpuService` | **Hardcoded `C:\Windows\System32\nvidia-smi.exe`.** No PATH resolution, no presence check beyond try/catch. On systems without NVIDIA drivers or where nvidia-smi is elsewhere, every GPU call spawns and waits on a failing process. | Medium |
| 12 | `GpuService` | **`nvidia-smi` calls have no timeout.** `proc.StandardOutput.ReadToEnd()` then `WaitForExit()` with no `proc.StartInfo` timeout; a hung nvidia-smi blocks the collection thread indefinitely. | Medium |
| 13 | `GpuService` | **`GetGpuPercentBestEffort` ignores NVIDIA entirely.** It only calls `GetIntelGpuPercentBestEffort`; `GetNvidiaGpuPercent` exists but is never invoked from `GetGpuPercentBestEffort`. So on NVIDIA-only systems, GPU utilization is always "N/A" even though nvidia-smi could provide it. | Medium |
| 14 | `GpuService` | **`GetGpuTemperatureBestEffort` order biases NVIDIA.** It checks NVIDIA first, then Intel WMI; on hybrid systems the reported GPU temperature may be the dGPU while utilization/VRAM come from Intel paths, mixing manufacturers in one payload. | Medium |
| 15 | `SystemMonitorWidgetProvider` / `Program` | **Shutdown can block indefinitely.** `WaitForCompletion()` waits on `EmptyWidgetListEvent` (30s if empty, infinite otherwise). If the Widget Board never deactivates widgets and never disconnects, `Program.Main` never returns, `CoRevokeClassObject` is never called, and the process cannot exit cleanly. | Medium |
| 16 | `SystemMonitorWidgetProvider` | **Payload built per-widget, but template selected per-widget size.** `UpdateWidget` calls `GetTemplateForSize` then `CreateDataPayload` — payload creation doesn't depend on size, so all widgets of all sizes get the same full JSON, and small templates reference keys that may be absent/empty. Mismatch between template keys and payload keys is possible (see section 3). | Low |
| 17 | `SystemMonitorWidgetProvider` | **No cancellation / graceful shutdown of in-flight updates on `Dispose`.** `StopMetricsService` disposes the timer but does not wait for an in-flight `OnTimer`/`UpdateAllWidgets` to complete, so `Dispose` can race with a tick reading `_metrics` after disposal. | Medium |

### 2.1 Measurement fixes applied in this session

- **ACPI tenths-of-kelvin conversion**: `ThermalMath.TenthsKelvinToCelsius` now uses `raw / 10.0 - 273.15` (was `273.2`). Verified: raw 3000 → 26.85 °C.
- **ACPI thermal zones no longer labeled CPU/GPU**: `UpdateCpuTemp()` writes to the new `SystemMetrics.AcpiThermalZoneTemperatureC` field instead of `_model.CpuTemperature`. `GetIntelGpuTemperatureBestEffort()` returns `null` (ACPI zones are not GPU-specific). GPU temperature comes only from confirmed GPU sensors (NVIDIA via nvidia-smi).
- **Network throughput uses actual elapsed time**: `NetworkDeltaCalculator.Calculate` computes `(now - previous.Timestamp).TotalSeconds` instead of a hard-coded 5s interval. First sample establishes a baseline with no rate emitted.
- **Per-interface baselines by interface ID**: `SystemMetricsService` tracks baselines in a `ConcurrentDictionary<string, InterfaceSample>` keyed by `NetworkInterface.Id`. Disappearing interfaces are dropped from baselines; reappearing/reset interfaces are rebaselined (no spike, no negative rate).
- **Loopback/virtual adapter policy**: `NetworkInterfacePolicy.ShouldInclude` excludes loopback interfaces and down interfaces, but includes VPN/tunnel and other virtual adapters carrying legitimate traffic.
- **Consistent clamping**: `ThermalMath.ClampPercent` clamps to [0, 100] and handles NaN/Infinity; used for displayed values and series history equally (CPU, RAM, disk).
- **Unused disk throughput counters removed**: `_diskReadBytesPerSec` / `_diskWriteBytesPerSec` (never had `NextValue()` called, no consumers) removed from ctor and `Dispose`.

---

## 3. Current payload keys referenced by each widget template

All findings below are based on reading the current checkout. None have been modified.

| # | Area | Problem | Severity |
|---|------|---------|----------|
| 1 | `SystemMonitorWidgetProvider` | **Static shared state across provider instances.** `RunningWidgets`, `EmptyWidgetListEvent` are `static`. If the Widget Board ever instantiates more than one provider object (or re-creates the provider), state leaks/collides across instances. Since COM activation creates a new `T()` per `CreateInstance`, multiple provider instances can coexist, all sharing one static dictionary. | High |
| 2 | `SystemMonitorWidgetProvider` | **`UpdateAllWidgets` enumerates `RunningWidgets.Values` without a lock.** `RunningWidgets` is a `ConcurrentDictionary`, so enumeration is safe, but the `CompactWidgetInfo` records it reads (`Size`, `WidgetId`) are mutated under `_lock` in `EnsureMetricsServiceStarted`/`Activate`/`Deactivate` while being read here without synchronization. | Medium |
| 3 | `SystemMonitorWidgetProvider` | **`_updateTimer` callback and `StopMetricsService` race.** `EnsureMetricsServiceStarted` creates the timer under `_lock`, but `UpdateAllWidgets`/`UpdateWidget` run on timer-pool threads and touch `_metricsService` / `_metrics` without holding `_lock`. `StopMetricsService` can dispose `_metricsService` while a timer tick is mid-payload-construction (`CreateDataPayload` reads `_metrics` fields). | High |
| 4 | `SystemMonitorWidgetProvider` | **Timer callback re-entrancy.** The timer fires every 5s; `UpdateAllWidgets` → `UpdateWidget` → `WidgetManager.UpdateWidget` is blocking COM interop. If a tick takes longer than 5s, the ThreadPool timer can queue overlapping ticks (System.Threading.Timer does not guarantee non-overlapping callbacks), leading to concurrent `CreateDataPayload` reads and concurrent `WidgetManager` calls. | Medium |
| 5 | `SystemMetricsService` | **`OnTimer` swallows all exceptions.** Any failure in one update (e.g., WMI `ManagementObjectSearcher` throwing on a particular machine) silently aborts the rest of that tick and every subsequent tick continues, but the failed metric is never retried until the next tick. Worse, a persistent exception in e.g. `UpdateCpu` would not stop the timer, so the app appears healthy while metrics go stale. | Medium |
| 6 | `SystemMetricsService` | **Timer started in constructor with no way to delay collection.** `OnTimer` runs immediately (`TimeSpan.Zero` due time) on a ThreadPool thread before the caller may be ready. Also the ctor reads initial counters; the first timer tick may race with ctor field initialization (`_prevIdle`, `_prevNetworkBytesReceived`, etc.) — though ctor completes before timer fires, the first tick reads `Interval.TotalSeconds` as a `double` for network rate while `_prevNetwork*` are set in ctor, which is fine. | Low |
| 7 | `SystemMetricsService` | **Disk performance counters created but never used.** `_diskReadBytesPerSec` / `_diskWriteBytesPerSec` are instantiated in the ctor (can throw on systems without counters — caught) and disposed in `Dispose`, but `NextValue()` is never called anywhere. Disk usage comes from `DriveInfo`, not the counters. Dead object creation. | Low |
| 8 | `SystemMetricsService` | **`Dispatch` is a no-op identity.** It exists as a placeholder for dispatcher marshalling but does nothing. All model mutations happen directly on the timer thread. If any UI/consumer binds to `SystemMetrics` (INotifyPropertyChanged) from a STA/UI thread, property-changed events fire on the wrong thread. | Low |
| 9 | `SystemMetricsService` | **Network bytes can wrap/counter-reset.** `deltaRx = rx - _prevNetworkBytesReceived` using signed `long`; on counter reset (interface disabled/re-enabled, ~4GB wrap for 32-bit interfaces) delta can be negative, producing negative `rxPerSec`. `ByteFormatter.FormatNetworkSpeed` clamps negatives to 0, but the model stores the negative value and the series records it. | Low |
| 10 | `SystemMetricsService` | **`UpdateDisk` reads only the system drive.** Single drive reported as "disk" regardless of how many drives exist; no per-drive breakdown. | Low |
| 11 | `GpuService` | **Hardcoded `C:\Windows\System32\nvidia-smi.exe`.** No PATH resolution, no presence check beyond try/catch. On systems without NVIDIA drivers or where nvidia-smi is elsewhere, every GPU call spawns and waits on a failing process. | Medium |
| 12 | `GpuService` | **`nvidia-smi` calls have no timeout.** `proc.StandardOutput.ReadToEnd()` then `WaitForExit()` with no `proc.StartInfo` timeout; a hung nvidia-smi blocks the collection thread indefinitely. | Medium |
| 13 | `GpuService` | **`GetGpuPercentBestEffort` ignores NVIDIA entirely.** It only calls `GetIntelGpuPercentBestEffort`; `GetNvidiaGpuPercent` exists but is never invoked from `GetGpuPercentBestEffort`. So on NVIDIA-only systems, GPU utilization is always "N/A" even though nvidia-smi could provide it. | Medium |
| 14 | `GpuService` | **`GetGpuTemperatureBestEffort` order biases NVIDIA.** It checks NVIDIA first, then Intel WMI; on hybrid systems the reported GPU temperature may be the dGPU while utilization/VRAM come from Intel paths, mixing manufacturers in one payload. | Medium |
| 15 | `SystemMonitorWidgetProvider` / `Program` | **Shutdown can block indefinitely.** `WaitForCompletion()` waits on `EmptyWidgetListEvent` (30s if empty, infinite otherwise). If the Widget Board never deactivates widgets and never disconnects, `Program.Main` never returns, `CoRevokeClassObject` is never called, and the process cannot exit cleanly. | Medium |
| 16 | `SystemMonitorWidgetProvider` | **Payload built per-widget, but template selected per-widget size.** `UpdateWidget` calls `GetTemplateForSize` then `CreateDataPayload` — payload creation doesn't depend on size, so all widgets of all sizes get the same full JSON, and small templates reference keys that may be absent/empty. Mismatch between template keys and payload keys is possible (see section 3). | Low |
| 17 | `SystemMonitorWidgetProvider` | **No cancellation / graceful shutdown of in-flight updates on `Dispose`.** `StopMetricsService` disposes the timer but does not wait for an in-flight `OnTimer`/`UpdateAllWidgets` to complete, so `Dispose` can race with a tick reading `_metrics` after disposal. | Medium |
| 18 | `SystemMetricsService` | **`PerformanceCounter` category "PhysicalDisk" may require admin/elevated permissions on some SKUs.** Ctor catches the exception, so disk counters silently stay null; no surface tells the caller disk-counter data is unavailable. | Low |
| 19 | `SystemMonitorWidgetProvider` | **`DeleteWidget` / `Deactivate` stop the service when the last widget goes away, but `CreateWidget`/`Activate` restart it.** Frequent pin/unpin cycles repeatedly create/dispose `SystemMetricsService`, resetting counter baselines (`_prevIdle`, `_prevNetwork*`) each time, causing a spike of zero/invalid deltas on restart. | Low |
| 20 | `WidgetProviderFactory<T>` | **`CreateInstance` allocates a new `T()` twice in the fallback path** (once for the successful QI, then again in the fallback `MarshalInspectable<IWidgetProvider>.FromManaged(new T())` + `QueryInterface`). Wasteful and the second instance is leaked if `QueryInterface` fails. | Low |

---

## 3. Current payload keys referenced by each widget template

`CreateDataPayload()` (`SystemMonitorWidgetProvider.cs:272-301`) serializes an anonymous object with these keys:

| Key | Source | Notes |
|-----|--------|-------|
| `cpu` | `_metrics.CpuPercent:F1` + "%" | Always present |
| `cpuClock` | `_metrics.CpuClockDisplay` | "N/A" if no clock |
| `cpuDetailSmall` | `_metrics.CpuClockDisplay` + `cpuTempStr` | Small-only composite |
| `gpuDetailSmall` | `gpuTempStr` + `gpuVramUsed` | Small-only composite |
| `gpu` | `NvidiaGpuPercent` or `GpuPercent` or "N/A" | Best-effort |
| `igpu` | `_metrics.GpuPercent` or "N/A" | Intel path |
| `dgpu` | `_metrics.NvidiaGpuPercent` or "N/A" | NVIDIA path |
| `ram` | `_metrics.RamDisplay` | "used / total (pct)" |
| `ramUsed` | `ByteFormatter.FormatBytes(RamUsedBytes)` | |
| `ramTotal` | `ByteFormatter.FormatBytes(RamTotalBytes)` | |
| `ramCombined` | `"{ramUsed} / {ramTotal}"` | |
| `ramPercent` | `_metrics.RamPercent:F0` + "%" | |
| `ramCompact` | `"{ramUsed} ({RamPercent:F0}%)"` | |
| `disk` | `_metrics.DiskDisplay` | "used / total (pct)" |
| `diskUsed` | `FormatBytes(Math.Max(0, DiskUsedBytes))` | |
| `diskTotal` | `FormatBytes(Math.Max(0, DiskTotalBytes))` | |
| `diskCombined` | `"{diskUsed} / {diskTotal}"` | |
| `diskPercent` | `_metrics.DiskPercent:F0` + "%" | |
| `diskCompact` | `"{diskUsed} ({DiskPercent:F0}%)"` | |
| `netDown` | `FormatNetworkSpeed(NetworkDownloadBytesPerSec)` | |
| `netUp` | `FormatNetworkSpeed(NetworkUploadBytesPerSec)` | |
| `cpuTemp` | `CpuTemperature:F1` + "°C" or "N/A" | |
| `gpuTemp` | `GpuTemperature:F1` + "°C" or "N/A" | |
| `tempSummary` | combined cpu/gpu temp or "N/A" | |
| `gpuVram` | `GpuVramDisplay ?? "N/A"` | |
| `gpuVramUsed` | `FormatBytes(GpuVramUsedBytes)` or "N/A" | |
| `lastUpdated` | `DateTime.Now.ToString("h:mm:ss tt")` | Local time |

### Template → key mapping

**Small template** (`WidgetTemplates.cs:17-204`): `${cpu}`, `${cpuClock}`, `${cpuTemp}`, `${gpu}`, `${gpuTemp}`, `${gpuVram}`, `${ram}`, `${disk}`. Keys `cpuDetailSmall` / `gpuDetailSmall` are built in the payload but **not referenced by the small template** — dead payload fields for small.

**Medium template** (`WidgetTemplates.cs:205-417`): `${cpu}`, `${cpuClock}`, `${cpuTemp}`, `${gpu}`, `${gpuTemp}`, `${gpuVram}`, `${ram}`, `${disk}`. Does **not** use `netDown`/`netUp`, `ramUsed`/`ramTotal`/`ramCombined`/`ramPercent`/`ramCompact`, `diskUsed`/`diskTotal`/`diskCombined`/`diskPercent`/`diskCompact`, `igpu`/`dgpu`, `gpuVramUsed`, `tempSummary`, `lastUpdated`.

**Large template** (`WidgetTemplates.cs:419-691`): `${cpu}`, `${cpuClock}`, `${cpuTemp}`, `${gpu}`, `${igpu}`, `${dgpu}`, `${gpuTemp}`, `${gpuVram}`, `${ram}`, `${disk}`, `${netDown}`, `${netUp}`. Does **not** use `cpuDetailSmall`, `gpuDetailSmall`, `ramUsed`/`ramTotal`/`ramCombined`/`ramPercent`/`ramCompact`, `diskUsed`/`diskTotal`/`diskCombined`/`diskPercent`/`diskCompact`, `cpuTemp` (uses it — yes), `gpuVramUsed`, `tempSummary`, `lastUpdated`.

**Observation:** All templates reference a common core (`cpu`, `cpuClock`, `cpuTemp`, `gpu`, `gpuTemp`, `gpuVram`, `ram`, `disk`). Large adds `igpu`/`dgpu`/`netDown`/`netUp`. Medium and small reference only the core. A new `acpiThermalZoneTemp` key carries unidentified ACPI sensor readings separately. Many payload keys (`ramUsed`, `ramTotal`, `ramPercent`, `diskUsed`, `diskTotal`, `diskPercent`, `gpuVramUsed`, `tempSummary`, `lastUpdated`, `cpuDetailSmall`, `gpuDetailSmall`, `ramCompact`, `diskCompact`, `ramCombined`, `diskCombined`) are **never referenced by any template** — dead weight in every payload.

---

## 4. Supported target frameworks, architectures, and packaging path

### 4.1 Target framework

`SystemMonitor/SystemMonitor.csproj`:
- `TargetFramework`: `net8.0-windows10.0.22000.0`
- `TargetPlatformVersion`: `10.0.22000.0`
- `TargetPlatformMinVersion`: `10.0.22000.0`
- Output type: `WinExe`
- `UseWinUI`: `false`; `EnableMsixTooling`: `false`; `WindowsPackageType`: `None`
- `WindowsAppSDKSelfContained`: `true`; `WindowsAppSDKBootstrapInitialize`: `false`
- `GenerateAppxPackageOnBuild`: `false` (note: win-x64 pubxml overrides to `true`)
- Nullable: enabled

**Package references** (exact versions):
- `Microsoft.Windows.SDK.BuildTools` `10.0.28000.2705`
- `Microsoft.WindowsAppSDK` `2.5.1`
- `System.Management` `8.0.0`
- `System.Diagnostics.PerformanceCounter` `8.0.0`
- `System.Security.Permissions` `8.0.0`

Solution (`SystemMonitor.sln`) contains one project: `SystemMonitor`. No test project.

### 4.2 Architectures / runtimes

- `Platforms`: `x86; x64; ARM64`
- `RuntimeIdentifiers`: `win-x86; win-x64` (ARM64 not in `RuntimeIdentifiers`)
- Publish profiles:
  - `win-x86.pubxml`: `Platform=x86`, `RuntimeIdentifier=win-x86`, `SelfContained=true`, `PublishSingleFile=False`
  - `win-x64.pubxml`: `Platform=x64`, `RuntimeIdentifier=win-x64`, `SelfContained=true`, `PublishSingleFile=False`, `GenerateAppxPackageOnBuild=true`
  - `win-arm64.pubxml`: `Platform=x64` (mislabeled), `RuntimeIdentifier=win-arm64`, `SelfContained=true`, `TargetFramework=net8.0-windows10.0.19041.0` (differs from csproj `22000.0`), `PublishReadyToRun=false`, `Configuration=Debug`
- Build output present for `x64` (Debug/Release), `x86` implied by solution config, ARM64 obj folders exist but no `bin/x64/.../arm64` publish output observed.

**PublishReadyToRun:** `true` in Release, `false` in Debug (from csproj). `PublishTrimmed`: `false` everywhere.

### 4.3 Packaging path

1. **Build/publish:** `dotnet publish <csproj> -c Release -r win-x64 -o build/publish-temp` with `SelfContained=false` (per `build/install-msix.ps1` line 36 — note: this overrides the pubxml `SelfContained=true`).
2. **Package root:** copy publish output → `build/package-root`; copy `Package.appxmanifest` → `AppxManifest.xml`; copy `WidgetsManifest.xml`; copy assets; rename scaled asset files to un-scaled aliases (e.g. `Square44x44Logo.scale-200.png` → `Square44x44Logo.png`).
3. **Manifest patching:** `install-msix.ps1` rewrites `Identity Name`/`Publisher` (`com.test.systemmonitor` / `CN=Developer`), replaces `$targetnametoken$.exe` → `SystemMonitor.exe`, sets `ProcessorArchitecture` per runtime.
4. **MSIX:** `makeappx pack /d package-root /p msix-output/SystemMonitor.msix`; `signtool sign` with a self-signed code-signing cert (testcert.pfx).
5. **Install:** `build/install-msix.ps1` runs `Add-AppxPackage -Register` on `AppxManifest.xml`; `build/Create-TestMsix.ps1` removes/reinstalls the `com.test.systemmonitor` package.

**Manifest identities:**
- `Package.appxmanifest` (source): `Identity Name="com.yourcompany.systemmonitor" Publisher="CN=YourPublisher"` — placeholder, **must be replaced before real packaging**.
- `build/package-root/AppxManifest.xml` (generated): `Name="com.test.systemmonitor" Publisher="CN=Developer" Version="1.0.0.1" ProcessorArchitecture="x64"` — includes the `windows.comServer` COM registration (`Class Id="f72c45b3-..."`) and the `windows.widgets` app extension mapping `SystemMonitorWidget` → class ID, plus `TrustedPackageFamilyNames` for `Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe` and `runFullTrust` capability.

**COM activation GUID:** `f72c45b3-9fa6-4155-82de-ab4ccf67cdb8` — consistent across `SystemMonitorWidgetProvider.Clsid`, `WidgetProviderFactory`, `Guids`, `AppxManifest.xml` class registration, and widget activation `CreateInstance ClassId`.

---

## 5. Migration plan

Toward: **manufacturer-neutral hardware inventory**, **immutable snapshots**, **single collection/publishing loop**.

### Phase 0 — Baseline & tests (this task)
- [x] Audit complete; `docs/refactor-plan.md` written.
- [x] Test project added (`SystemMonitor.Tests`, xUnit) covering `ByteFormatter`, payload key presence, `SystemMetrics` clamping, and the update-pipeline lifecycle.
- [x] Build baseline recorded: Release/win-x64 succeeds, 0 warnings.

### Phase 1 — Manufacturer-neutral hardware inventory **[DONE]**
Implemented in `Models/HardwareModels.cs`:

- `HardwareDevice` — durable identity via `DeviceId` (never list position), with `DisplayName`, `Vendor`, `DeviceClass` (Cpu/Gpu/Unknown), and `GpuIntegration` (Integrated/Discrete/Unknown). Supports multiple GPUs from same or different vendors.
- `Measurement` — carries `Value`, `Unit`, `Source`, `MeasurementStatus` (Available/Unsupported/Failed/Stale), and `LastSuccessfulUpdate`. Zero is never substituted for unavailable data.
- `DeviceReading` — ties a `Measurement` to a `DeviceId` + `Metric` name.
- `MetricsSnapshot` — immutable snapshot with `Timestamp`, `Cpus`, `Gpus`, `Readings`, and system-level RAM/disk/network measurements. Collections are defensively copied to arrays via `MetricsSnapshot.Create(...)` so no mutable collection leaks.
- `LegacyPayloadPresenter` (`Services/LegacyPayloadPresenter.cs`) — compatibility shim that maps an immutable `MetricsSnapshot` to the legacy flat payload dictionary consumed by `WidgetTemplates`. Ambiguous legacy keys (single `gpu` across multiple GPUs, `igpu`/`dgpu`) are populated but marked for removal in the template-alignment phase.

No vendor-prefixed keys (IntelGpuPercent/NvidiaGpuPercent/AmdGpuPercent) are encoded anywhere in the model.

**Constraint:** Do not add automatic elevation, hardware tuning, clock/voltage writes, fan control, or automatic driver installation. Reads only.

### Phase 2 — Immutable snapshots **[DONE]**
`MetricsSnapshot` replaces mutable `SystemMetrics` as the published contract:

- Collections are `IReadOnlyList<>` backed by arrays — callers cannot mutate them.
- `MeasurementStatus` communicates Availability vs Unsupported/Failed/Stale rather than emitting 0 for missing data.
- `SystemMetrics` remains in place as the legacy mutable model consumed by `SystemMonitorWidgetProvider`; new code paths target `MetricsSnapshot`.

Next: connect hardware probes (WMI, nvidia-smi) to populate `HardwareDevice`/`DeviceReading` and emit `MetricsSnapshot` from `SystemMetricsService` instead of mutating `SystemMetrics`.

### Phase 3 — Single collection/publishing loop **[DONE]**

Today there were **two loops**: `SystemMetricsService.Timer` (collects into mutable `_metrics`) and `SystemMonitorWidgetProvider._updateTimer` (publishes from `_metrics`). Both are gone, replaced by one coordinator.

**`Services/UpdatePipeline.cs`** — `UpdatePipeline<TSize>`, the single coordinator:

- Each cycle: collect → build one payload from the completed snapshot → publish to every eligible (active) widget → wait for the next scheduled update. Collection never overlaps: a cycle is admitted only when the previous one has unwound.
- `IMetricsCollector` (`Services/IMetricsCollector.cs`) is the collector seam. `SystemMetricsService` implements it; `Collect()` runs every collector sequentially and returns one completed immutable `MetricsSnapshot`. Each collector is independently try/caught, so one failure does not abort the rest.
- `Services/WidgetRegistry.cs` — thread-safe widget registry. Entries are **immutable** (`WidgetEntry<TSize>`) and replaced atomically via compare-and-swap. A `ConcurrentDictionary` alone would only make the *collection* safe; mutable entry properties would still be read and written unsynchronized.
- `lastUpdated` uses the snapshot's **sample timestamp**, not the serialization time.

**Lifecycle contract**

| Concern | Mechanism |
|---|---|
| Activation | `Start()` creates a fresh *generation* with its own `CancellationTokenSource` and wakes the coordinator so it collects immediately — it never waits out an old polling delay. A cancelled generation is never reused. |
| Deactivation | `Stop()` transitions to inactive under the **same lock that admits a cycle**, so no new cycle can be admitted afterwards; then requests cancellation of the in-flight run. Cancellation is requested *outside* the lock (cancelling a token runs callbacks synchronously). |
| Quiescence | `DeactivateAsync()` awaits the deactivated generation's `Unwound` signal. It completes only after admitted collection and publishing work has finished or unwound. It is a task-completion signal — never a counter observation, never a sleep — and it never blocks the calling thread. |
| Reactivation race | A new activation is a *separate* generation with its own lifecycle and is not cancelled by the in-flight `DeactivateAsync()`. The new generation awaits the old collection to unwind before admitting its own cycle, so non-overlap holds across the boundary. |
| Refresh | `ScheduleRefresh()` coalesces: several requests during one collection collapse into a single additional collection. |
| Disposal | `DisposeAsync()` cancels, awaits the loop, and only then disposes the generation cancellation sources. The caller disposes the collector/backend afterwards, so the backend is never disposed while a collection is running. |

**Widget-host boundary:** a host update that has already been submitted to the widget host cannot be retracted. Only updates not yet submitted are suppressed when a generation is deactivated.

**Remaining blocking boundary (not eliminated):** `IMetricsCollector.Collect()` is synchronous. `GetSystemTimes`, `GlobalMemoryStatusEx`, WMI `ManagementObjectSearcher` queries, the `nvidia-smi` subprocess launch, and the LibreHardwareMonitor sensor reads are synchronous native/WMI calls. A cancellation request marks the token as cancelled and is observed at the next cycle boundary, but it **cannot forcibly stop a native call already in progress**. A deactivation therefore completes only once the in-flight collector returns on its own. This is a property of the platform.

**Provider wiring** (`Services/SystemMonitorWidgetProvider.cs`): `CreateWidget`/`Activate` start the pipeline and request an immediate sample; `Deactivate`/`DeleteWidget` request the pause synchronously (they are COM callbacks and must not block) when the last active widget goes away; `ShutdownAsync()` awaits quiescence and then disposes the backend. `Program.Main` calls `ShutdownAsync()` after `WaitForCompletion()`.

**Tests** (`SystemMonitor.Tests/UpdatePipelineTests.cs`): 10 deterministic tests. Virtual time via `FakeTimeProvider`; a controllable fake collector with explicit per-attempt `Started`/`Release` signals and separate counters for started / completed / failed / published; `TaskCompletionSource` with `RunContinuationsAsynchronously` throughout. No `Task.Delay` is used for synchronization — the only real-time wait is a deadlock watchdog on the final await. Covered: deactivate-while-waiting, deactivate-during-collection (non-cooperative blocked collector; quiescence provably incomplete until release), reactivate-during-old-collection (max concurrency 1, stale publication suppressed, new generation collects without advancing the old interval), reactivate-during-long-interval, failure recovery (attempt 2 throws, attempt 3 succeeds and publishes), and lifecycle stress including deleting the last active widget while inactive widgets remain.

Recorded: 106/106 tests pass; Release/win-x64 build 0 warnings, 0 errors.

### Phase 4 — Concurrency / correctness fixes (apply alongside Phase 3)
- Remove `static` from `RunningWidgets` / `EmptyWidgetListEvent` or guard with instance locks (fixes #1).
- Lock or snapshot-isolate `CompactWidgetInfo` reads in `UpdateAllWidgets` (fixes #2).
- Guard `_metricsService`/`_updateTimer` access with `_lock` in timer callbacks, or replace with the single-loop design so there is no shared mutable service (fixes #3, #4, #17).
- Add `CancellationTokenSource` + timeout to every `nvidia-smi` `Process` call (fixes #12).
- Wire `GetNvidiaGpuPercent` into `GetGpuPercentBestEffort` (fixes #13).
- Remove dead `PerformanceCounter` disk counter creation (fixes #7) or actually use it.
- Make `Dispatch` real (dispatch to UI thread) or remove it (fixes #8).
- Clamp network deltas to >= 0 before storing (fixes #9).

### Phase 5 — Payload/template alignment
- Derive the payload **from the template**, not the other way around: each template declares the keys it needs; the publisher supplies only those keys. Eliminates dead payload fields (`ramUsed`, `ramTotal`, `ramPercent`, `diskUsed`, `diskTotal`, `diskPercent`, `gpuVramUsed`, `tempSummary`, `lastUpdated`, `cpuDetailSmall`, `gpuDetailSmall`, `ramCompact`, `diskCompact`, `ramCombined`, `diskCombined`) and the risk of `${key}` references resolving to nothing.
- Consider generating payload keys strongly-typed (record per template size) so missing keys are compile-time errors.

### Phase 6 — Packaging / manifest hygiene
- Replace placeholder `Package.appxmanifest` identity (`com.yourcompany.systemmonitor` / `CN=YourPublisher`) before shipping; `install-msix.ps1` already patches at build time, but the source file should not ship a placeholder.
- Align `win-arm64.pubxml` `TargetFramework` (`19041.0`) with csproj (`22000.0`) and fix mislabeled `Platform=x64`.
- Decide self-contained vs framework-dependent: csproj does not set `SelfContained`; pubxmls set `true`; `install-msix.ps1` passes `/p:SelfContained=false`. Pick one and make it consistent.

---

## 5A. LibreHardwareMonitor read-only sensor backend **[DONE]**

### Dependency

| Item | Value |
|---|---|
| Package | `LibreHardwareMonitorLib` |
| **Exact version** | **0.9.6** |
| Source | https://github.com/LibreHardwareMonitor/LibreHardwareMonitor, commit `3d331e3370efb858411f19511373eff65a218701` |
| License | **MPL-2.0** (SPDX expression in the nuspec) |
| Target framework used | `net8.0` (the package also ships `net472`, `net9.0`, `net10.0`, `netstandard2.0`) |
| Native binaries | **None.** The package contains only managed assemblies — no `.sys`, `.drv`, or `.exe`, and no kernel driver is embedded. |
| Elevation required | **No.** The widget's `requestedExecutionLevel` is unchanged; the backend is read-only and never elevates. |

**License notice (MPL-2.0 requires the notice to be retained):** the `LibreHardwareMonitorLib`
package and its transitive dependencies are redistributed inside the MSIX package under
MPL-2.0 / MIT / Apache-2.0 terms respectively. The repository currently has **no LICENSE
file** — adding one (or a `THIRD-PARTY-NOTICES.md`) is required before shipping. This is
recorded as an open item, not as work completed.

**Transitive dependencies pulled into the package** (all must ship in the MSIX package root —
verified present in both the `dotnet publish` output and the packed `.msix`):

`DiskInfoToolkit 1.1.2`, `HidSharp 2.6.4`, `Mono.Posix.NETStandard 1.0.0`,
`RAMSPDToolkit-NDD 1.4.2`, `System.IO.Ports 10.0.3`, `System.Management 10.0.2`,
`System.Threading.AccessControl 10.0.3`, `BlackSharp.Core 1.0.7` (via `DiskInfoToolkit`),
`System.CodeDom 10.0.2`.

> **Dependency change to note:** the project previously pinned `System.Management 8.0.0`.
> `LibreHardwareMonitorLib 0.9.6` requires `System.Management >= 10.0.2`, and NuGet treats
> the resulting downgrade as an error (NU1605). The pin was raised to `10.0.2`. That package
> still ships a `net8.0` asset, so the project's target framework is unaffected.

### Architecture

```
LibreHardwareMonitorSource   (thin adapter — the ONLY file that touches the library)
        │  implements ILhmSensorSource, returns IReadOnlyList<LhmDeviceDescriptor>
        ▼
LhmSnapshotMapper             (pure — maps descriptors to HardwareDevice/DeviceReading/Measurement)
        ▼
LhmSensorSelection            (pure — sensor selection + unit conversion)
        ▼
MetricsSnapshot / HardwareDevice / DeviceReading / Measurement   (manufacturer-neutral model)
```

`LibreHardwareMonitorSource` is the only file that references `LibreHardwareMonitor.*` types.
It is deliberately **not** linked into the test project; the mapping and selection logic it
feeds is pure and is tested through fixtures.

### Implementation notes

1. **One managed lifetime.** A single `Computer` instance is created and opened once, on
   first `Read()`, and closed in `Dispose()`. It is never reopened.
2. **No repeated expensive initialization.** If `Computer.Open()` throws, the failure is
   recorded in `UnavailableReason` and the source stays unavailable for the rest of its
   life. Initialization is attempted exactly once, not once per poll.
3. **Recursive update.** `IHardware.Update()` is called on each hardware node and recursed
   into `SubHardware`, so sensors held by sub-hardware (for example a GPU's memory
   controller) are refreshed too. A failure updating one node is logged and does not abort
   the rest.
4. **Only CPU and GPU are enabled.** Motherboard, storage, network, controller, PSU,
   battery and power-monitor groups are disabled, so the library does not spend time
   enumerating devices the widget never reads.
5. **Read-only.** The adapter only ever reads `ISensor.Value`. It never writes an
   `IControl` value, never changes a hardware setting, and never requires elevation.
6. **Discovery covers** `HardwareType.Cpu` (Intel and AMD) and `HardwareType.GpuNvidia` /
   `GpuAmd` / `GpuIntel`.
7. **Cleanup on shutdown.** `SystemMetricsService.Dispose()` disposes the sensor source,
   which calls `Computer.Close()`.

### Device identity and vendor derivation

- **Identity** is `IHardware.Identifier` — the library's own hardware identifier. It is
  stable across collections, encodes vendor and instance, and is **never** the device's
  position in a list. Observed values: `/intelcpu/0`, `/amdcpu/0`, `/gpu-nvidia/0`,
  `/gpu-intel-integrated/<url-encoded PNP id>`, `/gpu-amd/0`.
- Devices are **never joined to another backend by display name**.
- **Vendor** is derived from `HardwareType` (authoritative) and, for CPUs, from the
  identifier prefix. `IHardware.Properties` is **empty in 0.9.6** and is therefore not
  used for identity or vendor.
- **Integration class** is `Integrated` only for the `/gpu-intel-integrated/` prefix and
  `Discrete` for `/gpu-intel/`. NVIDIA and AMD expose no integration information, so
  their class stays `Unknown` rather than being guessed.

### Sensor selection preference and fallback semantics

Sensors are matched on the **(name, sensor-type) pair, never on name alone**, because the
library reuses names across quantities: `CPU Core #1` exists as both a Temperature and a
Clock sensor, `CPU Core Max` is a *Load* sensor, and `GPU Memory` exists as both a Load
and a Clock sensor. The full table lives in `Services/LhmSensorSelection.cs`.

| Metric | Preference order (first match wins) |
|---|---|
| CPU temperature | `CPU Package` → `Core (Tctl/Tdie)` → `Core (Tdie)` → `Core Average` → `Core Max` → `CCDs Average (Tdie)` |
| CPU load | `CPU Total` → `Cores (Average)` → `Total` |
| CPU clock | `CPU Core #1` → `CPU Core #` (lowest index) → `Core Clock` |
| GPU temperature | `GPU Core` → `GPU Hot Spot` → `Core` → `Hot Spot` |
| GPU load | `GPU Core` → `GPU Total` → `D3D 3D` → `D3D ` |
| GPU clock | `GPU Core` → `GPU Memory` → `GPU SoC` |
| GPU memory used | `GPU Memory Used` → `D3D Dedicated Memory Used` → `VRAM Used` → `Memory Used` |
| GPU memory total | `GPU Memory Total` → `D3D Dedicated Memory Total` → `VRAM Total` → `Memory Total` |

Rules enforced by the implementation:

- **No averaging or merging.** Exactly one sensor supplies each metric. Package, core,
  hotspot and memory sensors are never combined into a synthetic value.
- **`D3D Shared Memory` is never used for VRAM.** Shared system memory is not VRAM.
- **Units are verified before conversion.** Temperature must be °C, clock must be MHz,
  load must be %, and memory must be MiB. A value whose unit does not match is rejected
  rather than silently reinterpreted. Memory is then converted MiB → bytes (× 1 048 576).
- **Missing, NaN and infinite readings are unavailable, never zero.**
- **A blocked sensor does not blank a working metric.** When the preferred sensor exists
  but cannot be read, a later candidate that *can* read is used; when none can read, the
  measurement is `Failed` and the legacy collector value (if any) still fills the payload.

### Measured behaviour on the development machine

The adapter was run against real hardware (Intel Core i7-10875H + NVIDIA RTX 2070 Super
+ Intel UHD Graphics). Observed results:

| Device | Metric | Result |
|---|---|---|
| Intel CPU | Load | **Available** — 13.6 % (`CPU Total`) |
| Intel CPU | Temperature | **Failed** — `CPU Package` present but unreadable |
| Intel CPU | Clock | **Failed** — `CPU Core #1` present but unreadable |
| NVIDIA GPU | Load / Temperature / Clock | **Available** — 10 % / 62 °C / 330 MHz |
| NVIDIA GPU | VRAM used / total | **Available** — 1 407 188 992 B / 8 589 934 592 B |
| Intel iGPU | Load | **Available** — 0 % (`D3D 3D`) |
| Intel iGPU | Temperature / Clock / VRAM | **Unsupported** — no such sensor exists |

The CPU temperature and clock failures are the **expected** consequence of LibreHardwareMonitor
reading CPU sensors through model-specific registers, which normally requires an elevated
process. The widget does not elevate, so those sensors exist but report no value. This is
the designed behaviour: the metric shows as unavailable rather than as zero, and the rest of
the widget continues to work. The NVIDIA GPU and Intel iGPU readings work without elevation.

### Integration with the vendor-neutral Windows collectors

`SystemMetricsService` now runs the vendor-neutral Windows collectors (CPU load via
`GetSystemTimes`, RAM via `GlobalMemoryStatusEx`, disk via `DriveInfo`, network via
`NetworkInterface`) **unchanged**, and adds the sensor backend on top. Merge precedence:

- The **backend wins** for CPU temperature, CPU clock, GPU temperature, GPU load and GPU
  VRAM, because it names the device and the sensor the value came from.
- The **legacy collectors fill in** when the backend cannot read the metric, so a blocked
  sensor never blanks a working value.
- When **neither** source can supply a metric, the model is set to `null` and the widget
  shows `N/A` — a stale value is never left on screen as if it were current.
- The **ACPI thermal-zone average** remains in its own `AcpiThermalZoneTemperatureC` /
  `acpiThermalZoneTemp` key. It is never presented as CPU or GPU temperature.

### Payload changes

- `gpu` now prefers the device-identified GPU load, falling back to the legacy
  engine-counter average.
- `igpu` / `dgpu` are now **integration-class** keys (the load of a GPU known to be
  integrated / discrete) rather than vendor keys. The large template's label was changed
  from `Intel: ${igpu} | NVIDIA: ${dgpu}` to `Integrated: ${igpu} | Discrete: ${dgpu}` to
  match. **This is a visible change to the large widget.** The legacy
  `NvidiaGpuPercent` model property is retained for the fallback path.

### Testing caveat

> **The backend tests are fixture tests, not physical-hardware certification.**
> `SystemMonitor.Tests/LhmFixtures.cs` contains hand-written sensor sets modelled on the
> library's naming scheme. They prove the mapping, selection, unit-conversion and status
> logic behave correctly for a given sensor set. They do **not** prove that a real AMD,
> Intel or NVIDIA machine produces those sensors. Only running the widget on physical
> hardware of each kind can establish that. The single machine available during
> development was Intel + NVIDIA + Intel iGPU; **no AMD machine was available**, so the
> AMD paths (`Core (Tctl/Tdie)`, `CCDs Average (Tdie)`, `/gpu-amd/0`, `/amdcpu/0`) are
> verified by fixture only.

### Known limitations

- CPU temperature and clock are unavailable without elevation, and the widget does not
  elevate by design.
- NVIDIA and AMD GPUs report `GpuIntegration.Unknown`; only Intel exposes the class.
- The Intel integrated GPU exposes no dedicated VRAM and no temperature.
- `LegacyPayloadPresenter` is not yet wired into the publishing path — the widget still
  builds its payload directly from `SystemMetrics`. It is exercised by tests and is
  intended to replace the direct payload construction in Phase 3/5.
- The `Computer` instance holds native handles for the lifetime of the service; it is
  created when the first widget activates and closed when the last one deactivates.

---

## Baseline record

- **Build:** `dotnet build SystemMonitor/SystemMonitor.csproj -c Release -r win-x64` → succeeded, 0 warnings, 0 errors (2026-10-19).
- **Tests:** `SystemMonitor.Tests` — 96 tests pass: thermal conversion, network delta, interface policy, thermal zone source, manufacturer-neutral hardware models, immutable snapshots, legacy payload presenter, LHM sensor selection / unit conversion / snapshot mapping / merge policy. Coverage: Intel CPU + AMD GPU; AMD CPU + NVIDIA GPU; Intel iGPU + NVIDIA dGPU; two GPUs with identical names; device with no readable sensors; unknown vendor/classification; legacy payload compatibility; missing sensors never reported as zero; blocked sensors reported as Failed rather than zero; partial permission failure leaving other metrics of the same device readable.
- **MSIX packaging:** `makeappx pack` verified to succeed with the new dependency present. `LibreHardwareMonitorLib.dll`, `DiskInfoToolkit.dll`, `HidSharp.dll`, `RAMSPDToolkit-NDD.dll`, `Mono.Posix.NETStandard.dll`, `System.IO.Ports.dll`, `System.Management.dll` and `System.Threading.AccessControl.dll` are all confirmed inside the packed `.msix` (364 entries).
- **Hardware validation:** the adapter was run against a real machine (Intel i7-10875H + NVIDIA RTX 2070 Super + Intel UHD Graphics). GPU load/temperature/clock/VRAM read correctly; CPU temperature and clock correctly reported as `Failed` because MSR access requires elevation the widget deliberately does not request. **No AMD machine was available** — the AMD code paths are fixture-verified only.
- **Tracked artifacts modified in working tree (git status snapshot):** `build/msix-output/SystemMonitor.msix`, `build/package-root/SystemMonitor.dll`, `build/package-root/SystemMonitor.exe`, `build/publish-temp/SystemMonitor.dll`, `build/publish-temp/SystemMonitor.exe` — all build outputs, no source changes pending.
- **Most recent source commit:** `a5c7b59` "Remove settings system; fix refresh interval at 5 seconds" — removed `WidgetSettings`/`SettingsStore`, hardcoded 5s interval, removed per-metric toggle gates.

---

## Changes applied in this session

**New files:**
- `Services/ThermalMath.cs` — pure `TenthsKelvinToCelsius` and `ClampPercent` helpers.
- `Services/NetworkDeltaCalculator.cs` — pure per-second rate calculation using actual elapsed time; detects counter resets.
- `Services/NetworkInterfacePolicy.cs` — loopback excluded; VPN/tunnel included; down interfaces excluded.
- `Services/ThermalZoneReading.cs` — typed thermal-zone reading with explicit source (`AcpiUnidentified` / `NvidiaSmiGpu`).
- `SystemMonitor.Tests/SystemMonitor.Tests.csproj` — test project linking only pure logic files.
- `SystemMonitor.Tests/ThermalMathTests.cs` — 8 tests.
- `SystemMonitor.Tests/NetworkDeltaCalculatorTests.cs` — 8 tests.
- `SystemMonitor.Tests/NetworkInterfacePolicyTests.cs` — 5 tests.
- `SystemMonitor.Tests/ThermalZoneSourceTests.cs` — 3 tests.

**Modified files:**
- `Models/SystemMetrics.cs` — added `AcpiThermalZoneTemperatureC` property (unidentified ACPI sensors, separate from CPU/GPU).
- `Services/SystemMetricsService.cs` — removed unused `PerformanceCounter` disk counters; ACPI temps converted with corrected formula and written to `AcpiThermalZoneTemperatureC` (not `CpuTemperature`); network now uses per-interface baselines by ID with actual elapsed time; counter reset/wrap produces no negative rate; loopback excluded via `NetworkInterfacePolicy`; clamping consistent via `ThermalMath.ClampPercent`.
- `Services/GpuService.cs` — `GetIntelGpuTemperatureBestEffort()` returns `null` (ACPI zones not GPU-specific).
- `Services/SystemMonitorWidgetProvider.cs` — payload now includes `acpiThermalZoneTemp` key.
- `SystemMonitor.sln` — added `SystemMonitor.Tests` project.

**New files:**
- `Models/HardwareModels.cs` — `HardwareDevice`, `DeviceClass`, `GpuIntegration`, `Measurement`, `MeasurementStatus`, `DeviceReading`, `MetricsSnapshot` (immutable), `MetricsSnapshot.Create` factory.
- `Services/LegacyPayloadPresenter.cs` — compatibility presenter mapping `MetricsSnapshot` → legacy flat payload dictionary for `WidgetTemplates`.
- `Services/ThermalMath.cs` — pure `TenthsKelvinToCelsius` and `ClampPercent` helpers.
- `Services/NetworkDeltaCalculator.cs` — pure per-second rate calculation using actual elapsed time; detects counter resets.
- `Services/NetworkInterfacePolicy.cs` — loopback excluded; VPN/tunnel included; down interfaces excluded.
- `Services/ThermalZoneReading.cs` — typed thermal-zone reading with explicit source (`AcpiUnidentified` / `NvidiaSmiGpu`).
- `SystemMonitor.Tests/SystemMonitor.Tests.csproj` — test project linking only pure logic files.
- `SystemMonitor.Tests/ThermalMathTests.cs` — 8 tests.
- `SystemMonitor.Tests/NetworkDeltaCalculatorTests.cs` — 8 tests.
- `SystemMonitor.Tests/NetworkInterfacePolicyTests.cs` — 5 tests.
- `SystemMonitor.Tests/ThermalZoneSourceTests.cs` — 3 tests.
- `SystemMonitor.Tests/HardwareModelsTests.cs` — 14 tests covering all required scenarios.
- `docs/` — refactor plan updated.

---

## Changes applied in this session (LibreHardwareMonitor backend)

**New files:**
- `Services/LhmSensorSelection.cs` — pure sensor-selection and unit-conversion logic. Matched on the (name, sensor-type) pair, never on name alone. Ordered candidate tables with documented fallback semantics. `TryConvert` verifies the source unit before converting and rejects missing/NaN/infinite values.
- `Services/LhmSensorSource.cs` — `ILhmSensorSource` interface and `LhmDeviceDescriptor` record: the backend-neutral contract between the adapter and the mapping logic.
- `Services/LhmSnapshotMapper.cs` — pure mapping from `LhmDeviceDescriptor` onto `HardwareDevice`/`DeviceReading`/`Measurement`, plus `SelectPrimaryGpu`/`SelectPrimaryCpu` (discrete → unknown → integrated, tie-broken by device id for stability).
- `Services/LibreHardwareMonitorSource.cs` — the thin adapter. Owns one `Computer` instance, opens it once, closes it on dispose, disables all non-CPU/GPU groups, updates hardware and sub-hardware recursively, derives vendor and integration class, and maps sensors to the neutral descriptors. Read-only: never writes an `IControl`.
- `SystemMonitor.Tests/LhmFixtures.cs` — fixtures for Intel CPU, AMD CPU, NVIDIA GPU, AMD GPU, Intel integrated GPU, a GPU with no sensors, a fully blocked CPU, a partially blocked CPU, and ambiguous sensor names.
- `SystemMonitor.Tests/LhmSensorSelectionTests.cs` — 15 tests.
- `SystemMonitor.Tests/LhmSensorConversionTests.cs` — 12 tests.
- `SystemMonitor.Tests/LhmSnapshotMapperTests.cs` — 17 tests.
- `SystemMonitor.Tests/LhmSensorSourceTests.cs` — 6 tests.
- `SystemMonitor.Tests/LhmMergePolicyTests.cs` — 6 tests.

**Modified files:**
- `SystemMonitor/SystemMonitor.csproj` — added `LibreHardwareMonitorLib 0.9.6`; raised `System.Management` from 8.0.0 to 10.0.2 to satisfy the transitive requirement (NU1605).
- `Models/SystemMetrics.cs` — added `GpuLoadPercent`, `IntegratedGpuLoadPercent`, `DiscreteGpuLoadPercent`.
- `Services/SystemMetricsService.cs` — owns the sensor source and its lifetime; merges backend readings with the legacy collectors using the documented precedence; `Dispose` closes the backend.
- `Services/SystemMonitorWidgetProvider.cs` — `gpu` prefers the device-identified load; `igpu`/`dgpu` are now integration-class keys.
- `Services/WidgetTemplates.cs` — large template label changed from `Intel: … | NVIDIA: …` to `Integrated: … | Discrete: …` to match the new key semantics.
- `Services/LegacyPayloadPresenter.cs` — uses the same primary-CPU/primary-GPU selection as the widget, so the legacy keys and the per-device keys never disagree.
- `SystemMonitor.Tests/SystemMonitor.Tests.csproj` — links the three new pure files; xunit pinned to 2.9.0 (2.8.2 was not on the feed).
- `docs/refactor-plan.md` — section 5A added: dependency, license, packaging, architecture, selection preference, measured behaviour, testing caveat, known limitations.

---

## Suggested commit message (when ready to commit)

```
Add a read-only LibreHardwareMonitor sensor backend

Backend:
- Add LibreHardwareMonitorLib 0.9.6 (MPL-2.0, fully managed, no native
  binaries or drivers) as a read-only sensor source
- Raise System.Management to 10.0.2 to satisfy the transitive requirement
- Own a single Computer instance: opened once, closed on shutdown, and
  never re-initialized on every poll
- Discover CPU (Intel/AMD) and GPU (NVIDIA/AMD/Intel) hardware, updating
  hardware and sub-hardware recursively
- Derive device identity from the library's own hardware identifier, never
  from list position, and never join devices across backends by name

Sensor mapping:
- Select sensors on the (name, sensor-type) pair, never on name alone
- Document the preference order and fallback semantics per metric
- Verify source units before converting; memory is converted MiB to bytes
- Treat missing, NaN and infinite readings as unavailable, never as zero
- Keep the backend read-only: no IControl writes, no elevation, no
  hardware tuning

Integration:
- Merge backend readings with the vendor-neutral Windows CPU/RAM/disk/
  network collectors; the backend wins when it can read a metric and the
  legacy collectors fill in when it cannot
- Report igpu/dgpu as integration-class keys rather than vendor keys, and
  relabel the large template to match
- Leave the ACPI thermal-zone average in its own key, never presented as
  confirmed CPU or GPU temperature

Tests and docs:
- Add 56 fixture-based tests for selection, conversion, mapping, merge
  policy and partial permission failures
- Document the dependency, license, packaging, limitations and measured
  behaviour in docs/refactor-plan.md

Note: the fixture tests are not physical-hardware certification. Only the
Intel + NVIDIA + Intel iGPU path was verified on real hardware.

Co-Authored-By: Claude Code <noreply@anthropic.com>
```

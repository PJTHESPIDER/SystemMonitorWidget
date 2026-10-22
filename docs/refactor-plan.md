# SystemMonitorWidget — Refactor Plan

**Status:** Measurement bugs fixed; manufacturer-neutral model and immutable snapshots in place; read-only LibreHardwareMonitor sensor backend integrated; single non-overlapping update pipeline in place; NVIDIA command-line collection retained as a bounded, reconciled fallback; GPU-memory compatibility and labelling in place. Build and tests green. **Validated in the live Windows widget host** (2026-10-21): the card was pinned, rendered and measured in all three sizes, and driven through resize, deactivate/reactivate, remove/re-add and multiple instances. The action row was removed after the host was found never to render it.
**Baseline build:** `dotnet build SystemMonitor/SystemMonitor.csproj -c Release -r win-x64` — succeeded, 0 warnings, 0 errors.
**Tests:** `SystemMonitor.Tests` — 405 tests pass (thermal conversion, network delta, interface policy, hardware models, immutable snapshots, legacy presenter, LHM sensor selection/conversion/mapping, update-pipeline lifecycle, NVIDIA command-line fallback, GPU-memory compatibility and labelling, live-host layout fit).
**Last audited against the checkout:** 2026-10-21 (commit `eb2568e`, plus the uncommitted live-host validation work).
**Working directory:** `E:\Code\SystemMonitor`

> **Note on sections 1–3.** Section 1 describes the flows as they are implemented *now*. Section 2
> lists the problems found in the original audit, annotated with whether each is still open.
> Section 3 lists the payload keys. The original audit text described a pre-pipeline implementation
> (two `System.Threading.Timer` loops, `CompactWidgetInfo`, a `Dispatch` helper); those have all
> been replaced, and the audit text was rewritten to match the current code rather than left to
> decay.

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
4. Calls `SystemMonitorWidgetProvider.ShutdownAsync()` and blocks on it (`.GetAwaiter().GetResult()`). This awaits pipeline quiescence **before** the backend is disposed, then revokes the class object (`CoRevokeClassObject`) on exit.

The app runs as an **out-of-process COM server** for the Windows 11 Widget Board. Registration is declared in `build/package-root/AppxManifest.xml` under a `windows.comServer` extension (`com:ExeServer`) and a `windows.appExtension`/`windows.widgets` extension that maps the widget definition `SystemMonitorWidget` to the class ID.

### 1.2 COM activation — `Services/WidgetProviderFactory.cs`

`ComServer.ComRegisterClassObject` hands the Widget Board a class object. When the board creates a widget instance it calls `IClassFactory.CreateInstance`, which returns a `MarshalInspectable<IWidgetProvider>` wrapping a new `SystemMonitorWidgetProvider` (via `WidgetProviderFactory<T>`).

### 1.3 Widget provider lifecycle — `Services/SystemMonitorWidgetProvider.cs`

Implements `IWidgetProvider` + `IDisposable`, marked `[ComVisible]` with GUID `f72c45b3-...`.

The provider owns no timer and no collection loop. It holds a `WidgetRegistry<WidgetSize>`
(`RunningWidgets`), a `SystemMetricsService` (the `IMetricsCollector`), and an
`UpdatePipeline<WidgetSize>` that does the actual work.

**Constructor** (`SystemMonitorWidgetProvider()`):
- Instantiates `SystemMetrics _metrics` (the legacy mutable model). It is passed to the `SystemMetricsService`, but **nothing in the production path reads it**: the payload is built from the immutable `MetricsSnapshot` the collector returns. The model is written every cycle and read by nobody outside the collector itself.
- Calls `WidgetManager.GetDefault().GetWidgetInfos()` to enumerate already-pinned widgets.
- For each existing widget, adds a `WidgetEntry<WidgetSize>` to the static `RunningWidgets` registry keyed by widget ID.
- If any widget is active, calls `EnsurePipelineStarted()`.
- Otherwise sets `EmptyWidgetListEvent`.
- All WMI/WidgetManager calls are wrapped in try/catch and swallowed (non-packaged / test environments).

**`CreateWidget(WidgetContext)`** (widget pinned by user):
- Adds a `WidgetEntry<WidgetSize>` to `RunningWidgets`, resets `EmptyWidgetListEvent`.
- Calls `EnsurePipelineStarted()` then `UpdateWidget(entry)` immediately.

**`Activate(WidgetContext)`** (widget made visible / resized):
- Marks `IsActive = true` and updates size via `RunningWidgets.TrySetActive(...)`; upserts the entry if the widget is not yet registered.
- Calls `EnsurePipelineStarted()` then `UpdateWidget(entry)`.

**`Deactivate(widgetId)`** (widget hidden):
- Marks `IsActive = false`.
- If no widgets are active, calls `StopPipeline()`. This is a **synchronous COM callback**, so it must not block: `Stop()` transitions the pipeline to inactive through the same lock that admits a cycle and requests cancellation, but does not wait for the collector.

**`DeleteWidget(widgetId, customState)`** (widget removed):
- Removes from `RunningWidgets`.
- If no active widgets remain, calls `StopPipeline()` (same non-blocking path as `Deactivate`).
- If the registry is empty, sets `EmptyWidgetListEvent`.

**`OnActionInvoked`**:
- Does nothing. The templates carry no root-level `actions` array, so the host has nothing to invoke. The callback exists because the interface requires it.

  > **Changed on 2026-10-21.** The templates used to carry an action row (a Refresh button and one button per GPU). The live host showed the row never rendered — the board lays it out after the card body and does not scroll, so a full card pushed the buttons past its bottom edge. The action row was removed, and with it the device picker. `UpdatePipeline.ScheduleRefresh()` is therefore no longer reachable from production code; the card refreshes on its own five-second interval, which the live host confirmed. See `docs/hardware-validation.md` §2.9 (D5) and §2.11.

**`OnWidgetContextChanged`**:
- Updates size for the widget ID and calls `UpdateWidget(entry)`.

**`EnsurePipelineStarted()`** (lock `_pipelineLock`):
- Lazily creates one `SystemMetricsService(_metrics)` and one `UpdatePipeline<WidgetSize>` (collector, registry, payload builder, publisher, 5-second interval).
- Calls `_pipeline.Start()`, which creates a fresh activation generation and requests an immediate sample.

**`StopPipeline()`** (lock `_pipelineLock`):
- Calls `_pipeline?.Stop()` — the non-blocking pause. The pipeline and the metrics service are **not** disposed here; they are disposed by `ShutdownAsync()`.

**`ShutdownAsync()`** (static, lock `_pipelineLock` to detach the references):
- `await pipeline.DeactivateAsync()` — awaits quiescence of the retired generation's admitted collection and publishing.
- `await pipeline.DisposeAsync()` — cancels, awaits the coordinator loop, then releases the generation cancellation sources.
- `metricsService.Dispose()` — closes the LibreHardwareMonitor backend. This happens **only after** the pipeline has fully unwound, so the backend is never disposed while a collection is using it.

**`UpdateWidget(WidgetEntry<WidgetSize>, string? payload = null)`**:
- Builds the per-widget payload via `WidgetPayloadPresenter.Present(snapshot, widget.SelectedGpuInstanceId)`.
- Builds the template via `WidgetTemplates.BuildTemplate(size)`.
- Calls `WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(id) { Template = template, Data = data, CustomState = selectionId })`.
- Swallows all exceptions (host disconnected, etc.).

**`BuildPayloadFromSnapshot(MetricsSnapshot)`** (pipeline callback):
- Records the snapshot as the most recent collection and returns an empty string. The payload is built **per widget** inside `UpdateWidget`, because the presented GPU is a per-widget setting: two widgets on the same machine may show different cards. The collection work is not repeated.

**`PublishToWidget(string, WidgetEntry<WidgetSize>)`** (pipeline callback):
- Calls `UpdateWidget(widget)` and returns a completed task. The `WidgetManager` call is blocking COM interop and runs on the pipeline loop thread.

**`WidgetPayloadPresenter.Present(MetricsSnapshot?, string? selectedGpuInstanceId, int maxExtraDevices)`**:
- Builds the flat, manufacturer-neutral payload (see section 3), serialized with `System.Text.Json` via `WidgetPayload.ToJson()`.
- `sampleTime` / `lastUpdated` use the **most recent successful reading time**, not the serialization time.

**`Dispose()`**:
- Calls `StopPipeline()` and disposes the metrics service. This is the best-effort synchronous path; `ShutdownAsync()` is the preferred shutdown route.

**Static helpers** `WaitForCompletion()` / `GetEmptyWidgetListEvent()` — used by `Program.Main` to block until widget activity ends.

### 1.4 Metrics collection — `Services/SystemMetricsService.cs`

Implements `IMetricsCollector` and `IDisposable`. It owns **no timer** — it is called by the
pipeline's coordinator loop.

**`Collect()`** returns one completed immutable `MetricsSnapshot`. The timestamp is captured at the
*start* of collection and is the sample timestamp. Each collector is independently try/caught, so
one failure does not abort the rest. Calls, in order:
1. `UpdateCpu()` — `GetSystemTimes` (kernel32 P/Invoke) delta computation vs previous sample; clamps to [0,100]; reports into the `cpu-load` collector state.
2. `UpdateRam()` — `GlobalMemoryStatusEx` P/Invoke; used = total − available; reports `ram-used` / `ram-total` / `ram-percent`.
3. `UpdateDisk()` — `DriveInfo` on system drive (`SpecialFolder.System` substring 0..3); used = total − free; reports `disk-used` / `disk-total` / `disk-percent`.
4. `UpdateNetwork()` — `NetworkInterface.GetAllNetworkInterfaces()` filtered by `NetworkInterfacePolicy`; per-interface baselines keyed by `NetworkInterface.Id`; bytes/sec computed from **actual elapsed time** via `NetworkDeltaCalculator`; counter resets and wraps are rebaselined rather than emitted as negative rates.
5. `UpdateCpuTemp()` — WMI `root\WMI MSAcpi_ThermalZoneTemperature`; converts `raw / 10.0 - 273.15` °C and averages; reports `acpi-thermal-zone-temperature` (**not** `cpu-temperature` — ACPI zones are not component-identified).
6. `UpdateCpuClock()` — WMI `root\CIMV2 Win32_Processor CurrentClockSpeed`; averages across processors; stored as the CPU-clock fallback.
7. `UpdateSensorBackend()` — reads the LibreHardwareMonitor backend and sets the device-identified values.
8. `UpdateNvidiaFallback()` — runs **after** the backend. If the backend already supplied the GPU load, the GPU temperature and both VRAM figures, the command line is not invoked at all. Otherwise `NvidiaGpuFallback` issues a single bounded `nvidia-smi` invocation, and `NvidiaGpuReconciler` matches the samples to backend devices by PCI device key. A sample that cannot be matched is dropped rather than misattributed.
9. `UpdateGpuLoad()` / `UpdateGpuTemp()` / `UpdateGpuVram()` — turn the reconciled fallback's figures into fallback candidates, for the metrics the backend could not read. Each candidate carries the device the fallback reconciled to, so the attribution follows the figure rather than the source's priority.

`PublishMergedMetrics()` then merges, and the precedence is fixed and identical for every metric
(`ScalarMergePolicy.Resolve`):

1. the **device-identified backend** — it names the device and the sensor the value came from, so
   it wins whenever it read the metric;
2. the **reconciled nvidia-smi fallback** — it names a device too, but it is consulted only for a
   metric the backend could not read;
3. the **Windows counters** — they name no device at all, so their figure is never presented for a
   card.

When no source can supply a metric, the collector state records that no value was read — an
unavailable metric shows as unavailable, never as zero. (The legacy mutable model's corresponding
field is also set to `null`, but that model is not what the payload is built from.)

Each flattened figure is recorded in a `ScalarCollectorState`, which carries the time the value was
**read** (not the time the payload was built), the source, and the id of the device the value
describes. A collector that never succeeded produces `Failed`; a collector that failed after an
earlier success produces `Stale`, stamped with the time of the last successful read. A measured
zero is `Available` — it is a value, not an absence.

The published snapshot carries the attribution ids (`GpuLoadDeviceId`, `GpuTemperatureDeviceId`,
`GpuVramUsedDeviceId`, `GpuVramTotalDeviceId`). The used and total memory figures are attributed
**separately**, because they can come from different sources: the used figure may come from the
reconciled NVIDIA fallback for one card while the capacity comes from the Windows counters, which
name no card at all. `WidgetPayloadPresenter.ResolveMetric` checks the attribution per figure and
never presents a scalar under a device it does not describe.

**There is no series.** No rolling history buffer, no `ObservableCollection<double>`, no
`UpdateSeries` method, and no `CpuSeries` / `RamSeries` / `DiskSeries` properties exist in the
current code. The widget shows a single current value per figure.

**Removed:** the `_diskReadBytesPerSec` / `_diskWriteBytesPerSec` `PerformanceCounter` instances are
gone. They were created in the constructor and disposed in `Dispose()` but `NextValue()` was never
called anywhere; disk usage comes from `DriveInfo`. There is also **no `Dispatch` helper** — it was
a no-op identity that never marshalled anything, and it has been deleted. The `NvidiaGpuPercent`
model property is also gone — it was written every cycle by a subprocess and read by nobody.

### 1.5 GPU service — `Services/GpuService.cs`

`GpuService` has exactly one public method, and it does not invoke `nvidia-smi` — the command line
lives behind `NvidiaGpuFallback` (see "Changes applied (NVIDIA command-line fallback)").

- `GetGpuVramBestEffort()` → `GetWmiGpuVram()` — WMI `Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory.DedicatedUsage` (bytes), falling back to `Win32_VideoController.AdapterRAM`. `AdapterRAM` is a `uint` capped at 4 GB, so it under-reports total VRAM on larger cards and is a last resort only.

  The counters report one instance per adapter and name no device. `GpuMemoryFallbackPolicy` therefore
  uses a figure **only** when the counters described exactly one adapter: usage figures are never
  summed across adapters (that would be the machine's total GPU memory presented as one card's
  usage), a capacity is never taken from "the first" adapter (that would be the integrated part's
  capacity presented as the discrete card's), and a capacity smaller than the usage is discarded
  because it must have come from a different adapter. An ambiguous figure is reported as
  unavailable rather than attributed to a device it may not describe.

**The GPU load, GPU temperature and Intel-specific methods do not exist.** There is no
`GetGpuPercentBestEffort()`, no `GetWindowsGpuPercentBestEffort()`, no
`GetIntelGpuTemperatureBestEffort()` and no `GetGpuTemperatureBestEffort()`. GPU load and
temperature come from the LibreHardwareMonitor backend, or from the reconciled nvidia-smi
fallback. ACPI thermal zones are not GPU-specific, so an unidentified sensor reading is never
presented as a GPU temperature; it is published under its own
`acpiThermalZoneTemperatureC` / `acpiThermalZoneTemp` key.

The old `GetNvidiaGpuPercent()`, `GetNvidiaGpuTemperature()` and `GetNvidiaGpuVram()` methods — and
the three hardcoded `C:\Windows\System32\nvidia-smi.exe` call sites they used — have been removed.
`GetNvidiaGpuPercent()` was in particular a per-cycle subprocess whose result was never consumed.

### 1.5a NVIDIA command-line fallback — `Services/NvidiaGpuFallback.cs` and friends

The command-line path is retained, but strictly as a *fallback*: it is invoked only when the
cross-vendor backend could not supply the GPU load, the GPU temperature or the VRAM figures, and
its readings are merged into a device only after the two sources have been matched by PCI device
id. The full design — parsing, timeouts, executable resolution, reconciliation and the
policy that decides whether to invoke at all — is documented under "Changes applied
(NVIDIA command-line fallback)".

### 1.6 Widget templates — `Services/WidgetTemplates.cs`

Three Adaptive Card 1.6 JSON templates (Small / Medium / Large) returned by
`WidgetTemplates.GetTemplateForSize(WidgetLayoutSize)`. The keys they reference are listed in
section 3.

`WidgetTemplates.BuildTemplate(size)` returns the template for a size. The templates carry **no
root-level `actions` array**.

> **Changed on 2026-10-21.** The templates used to carry an action set spliced into an
> `actions` array: a `refresh` verb and a `selectGpu|<instance id>` verb per device. Widgets
> support only `Action.Execute`, so every action was an `Action.Execute` with an app-defined
> `verb`, and the target device was named **in the verb** because the verb is the one part of
> an action the host is documented to deliver verbatim. The live host showed the row never
> rendered — the board lays it out after the card body and does not scroll, so a full card
> pushed the buttons past its bottom edge, where the host reported them as offscreen. The
> action row was removed rather than given space on the card, and with it the device picker.
> The presented device is still named on the Medium and Large cards, and the other devices
> are still listed. See `docs/hardware-validation.md` §2.9 (D5) and §2.11.

The Small layout is a three-row summary — a CPU row, a GPU row and a RAM/disk row — with
each row headed by the name of what it reports. It names no GPU model and carries no
sub-headings, because the 148-pixel card has no room for them; it therefore binds no memory
label at all, which is consistent with showing the used-memory figure alone, a figure that
claims nothing about the memory pool.

**`ByteFormatter`** (`Services/ByteFormatter.cs`): `FormatBytes`, `FormatNetworkSpeed`, `FormatPercent`, `FormatTemperature`, `FormatClock` — used to render metric strings into the payload. Its `FormatUsage(double, double, double)` is **no longer called**: the two presenters format the used/total/percent triple themselves, because the triple's coherence rules (a capacity of zero, a capacity below the usage, an unavailable component) depend on each figure's *status*, which a bare `(double, double, double)` signature cannot carry.

> **Changed on 2026-10-21.** `FormatBytes` gained a megabyte tier (TB → GB → MB → KB → B). It
> had none, so a GPU-memory figure of about one gigabyte was rendered in kilobytes —
> `1002496.0 KB` — which the live host ellipsized to `1026048.0…`. `FormatNetworkSpeed` already
> had the tier, and the two ladders are now pinned together by a test so they cannot drift
> apart again. See `docs/hardware-validation.md` §2.9 (D3).

### 1.7 Update pipeline — `Services/UpdatePipeline.cs`

The single coordinator. `UpdatePipeline<TSize>` is generic over the widget size type so the test
project can exercise it without referencing the Windows App SDK.

Each cycle: **collect → build one payload from the completed snapshot → publish to every eligible
(active) widget → wait for the next scheduled update**. Collection never overlaps: a cycle is
admitted only when the previous one has unwound.

- **`IMetricsCollector`** (`Services/IMetricsCollector.cs`) — the collector seam. `Collect()` is
  **synchronous**, because the underlying Windows/WMI/native calls are.
- **`WidgetRegistry<TSize>`** (`Services/WidgetRegistry.cs`) — thread-safe widget registry. Entries
  are **immutable** (`WidgetEntry<TSize>` records) and replaced atomically via compare-and-swap. A
  `ConcurrentDictionary` alone would only make the *collection* safe; mutable entry properties
  would still be read and written unsynchronized.
- **Scheduling is wakeable**, not an unconditional delay: the coordinator waits on
  `Task.WhenAny(delay, signal)`, so a lifecycle change or a refresh wakes it promptly.
- **Time is injected** via `TimeProvider`, so tests use `FakeTimeProvider` for virtual time.

The full lifecycle contract is in Phase 3 below.

---

## 2. Confirmed correctness / concurrency problems

All findings below were identified in the original audit. The **Problem** column describes the
state of the code *at the time of the audit* — it is kept verbatim so the finding is traceable. The
**Status** column records whether the finding is still present in the current checkout (`eb2568e`):
**RESOLVED** items were fixed by the phase named; **OPEN** items are carried into Phase 4.

| # | Area | Problem | Severity | Status |
|---|------|---------|----------|--------|
| 1 | `SystemMonitorWidgetProvider` | **Static shared state across provider instances.** `RunningWidgets`, `EmptyWidgetListEvent` are `static`. If the Widget Board ever instantiates more than one provider object (or re-creates the provider), state leaks/collides across instances. Since COM activation creates a new `T()` per `CreateInstance`, multiple provider instances can coexist, all sharing one static dictionary. | High | **OPEN** — still `static`. Phase 3 made the *registry* safe (immutable entries, atomic replacement) but did not remove the `static` itself. |
| 2 | `SystemMonitorWidgetProvider` | **`UpdateAllWidgets` enumerates `RunningWidgets.Values` without a lock.** `RunningWidgets` is a `ConcurrentDictionary`, so enumeration is safe, but the `CompactWidgetInfo` records it reads (`Size`, `WidgetId`) are mutated under `_lock` in `EnsureMetricsServiceStarted`/`Activate`/`Deactivate` while being read here without synchronization. | Medium | **RESOLVED** (Phase 3) — `CompactWidgetInfo` was replaced by the immutable `WidgetEntry<TSize>`, replaced atomically via compare-and-swap. `GetActive()` returns a private snapshot. |
| 3 | `SystemMonitorWidgetProvider` | **`_updateTimer` callback and `StopMetricsService` race.** `EnsureMetricsServiceStarted` creates the timer under `_lock`, but `UpdateAllWidgets`/`UpdateWidget` run on timer-pool threads and touch `_metricsService` / `_metrics` without holding `_lock`. `StopMetricsService` can dispose `_metricsService` while a timer tick is mid-payload-construction (`CreateDataPayload` reads `_metrics` fields). | High | **RESOLVED** (Phase 3) — there is no timer and no shared mutable service. `ShutdownAsync()` awaits pipeline quiescence *before* disposing the backend. |
| 4 | `SystemMonitorWidgetProvider` | **Timer callback re-entrancy.** The timer fires every 5s; `UpdateAllWidgets` → `UpdateWidget` → `WidgetManager.UpdateWidget` is blocking COM interop. If a tick takes longer than 5s, the ThreadPool timer can queue overlapping ticks (System.Threading.Timer does not guarantee non-overlapping callbacks), leading to concurrent `CreateDataPayload` reads and concurrent `WidgetManager` calls. | Medium | **RESOLVED** (Phase 3) — a single coordinator admits a cycle only when the previous one has unwound. Overlap is structurally impossible, and the tests assert `MaxConcurrent == 1`. |
| 5 | `SystemMetricsService` | **`OnTimer` swallows all exceptions.** Any failure in one update (e.g., WMI `ManagementObjectSearcher` throwing on a particular machine) silently aborts the rest of that tick and every subsequent tick continues, but the failed metric is never retried until the next tick. Worse, a persistent exception in e.g. `UpdateCpu` would not stop the timer, so the app appears healthy while metrics go stale. | Medium | **RESOLVED** (Phase 3) — each collector in `Collect()` is independently try/caught, so one failure does not abort the rest. A failed metric is retried on the next cycle. |
| 6 | `SystemMetricsService` | **Timer started in constructor with no way to delay collection.** `OnTimer` runs immediately (`TimeSpan.Zero` due time) on a ThreadPool thread before the caller may be ready. Also the ctor reads initial counters; the first timer tick may race with ctor field initialization (`_prevIdle`, `_prevNetworkBytesReceived`, etc.) — though ctor completes before timer fires, the first tick reads `Interval.TotalSeconds` as a `double` for network rate while `_prevNetwork*` are set in ctor, which is fine. | Low | **RESOLVED** (Phase 3) — there is no timer. Collection starts when the pipeline is started, and the first activation requests an immediate sample. |
| 7 | `SystemMetricsService` | **Disk performance counters created but never used.** `_diskReadBytesPerSec` / `_diskWriteBytesPerSec` are instantiated in the ctor (can throw on systems without counters — caught) and disposed in `Dispose`, but `NextValue()` is never called anywhere. Disk usage comes from `DriveInfo`, not the counters. **[FIXED]** — removed unused counter creation; no consumers found. | Low | **RESOLVED** — the counter creation and its `Dispose` were removed; no consumers were found. Disk usage comes from `DriveInfo`. |
| 8 | `SystemMetricsService` | **`Dispatch` is a no-op identity.** It exists as a placeholder for dispatcher marshalling but does nothing. All model mutations happen directly on the timer thread. If any UI/consumer binds to `SystemMetrics` (INotifyPropertyChanged) from a STA/UI thread, property-changed events fire on the wrong thread. | Low | **RESOLVED** — the `Dispatch` helper was deleted. Collection now runs on the pipeline coordinator thread; nothing binds `SystemMetrics` to a UI thread. |
| 9 | `SystemMetricsService` | **Network bytes can wrap/counter-reset.** `deltaRx = rx - _prevNetworkBytesReceived` using signed `long`; on counter reset (interface disabled/re-enabled, ~4GB wrap for 32-bit interfaces) delta can be negative, producing negative `rxPerSec`. `ByteFormatter.FormatNetworkSpeed` clamps negatives to 0, but the model stores the negative value and the series records it. **[FIXED]** — `NetworkDeltaCalculator` returns `HasValidDelta=false` on reset/wrap; baselines are rebaselined to current sample; no negative rates emitted. | Low | **RESOLVED** — `NetworkDeltaCalculator` returns `HasValidDelta=false` on reset/wrap; baselines are per-interface (keyed by `NetworkInterface.Id`) and are rebaselined to the current sample, so no negative rates are emitted. |
| 10 | `SystemMetricsService` | **`UpdateDisk` reads only the system drive.** Single drive reported as "disk" regardless of how many drives exist; no per-drive breakdown. | Low | **OPEN** — unchanged. |
| 11 | `GpuService` | **Hardcoded `C:\Windows\System32\nvidia-smi.exe`.** No PATH resolution, no presence check beyond try/catch. On systems without NVIDIA drivers or where nvidia-smi is elsewhere, every GPU call spawns and waits on a failing process. | Medium | **RESOLVED** — the three hardcoded call sites are gone. `NvidiaSmiLocator` resolves the executable once from documented locations (`PATH`, `Program Files\NVIDIA Corporation\NVSMI`, `System32`) and caches the result; after a failed lookup it suppresses re-probing for 15 minutes, and `Rediscover()` forces a fresh lookup. |
| 12 | `GpuService` | **`nvidia-smi` calls have no timeout.** `proc.StandardOutput.ReadToEnd()` then `WaitForExit()` with no `proc.StartInfo` timeout; a hung nvidia-smi blocks the collection thread indefinitely. | Medium | **RESOLVED** — every invocation now goes through `SystemProcessRunner`, which bounds the start, the output drain, and the exit wait with one timeout (5 s default), kills the child tree on expiry, and always disposes the `Process`. stdout and stderr are drained concurrently, so a child that fills both pipes cannot deadlock the reader. |
| 13 | `GpuService` | **`GetGpuPercentBestEffort` ignores NVIDIA entirely.** It only calls `GetIntelGpuPercentBestEffort`; `GetNvidiaGpuPercent` exists but is never invoked from `GetGpuPercentBestEffort`. So on NVIDIA-only systems, GPU utilization is always "N/A" even though nvidia-smi could provide it. | Medium | **RESOLVED** — the unwired `GetNvidiaGpuPercent()` and the unread `SystemMetrics.NvidiaGpuPercent` model property were both removed as dead code. NVIDIA utilization now comes from the vendor-neutral LibreHardwareMonitor backend; where the backend cannot supply it, the reconciled nvidia-smi fallback supplies it. *(The fallback's utilization was parsed and reconciled but not consumed until 2026-10-21 — see "Defects found and fixed" below.)* |
| 14 | `GpuService` | **`GetGpuTemperatureBestEffort` order biases NVIDIA.** It checks NVIDIA first, then Intel WMI; on hybrid systems the reported GPU temperature may be the dGPU while utilization/VRAM come from Intel paths, mixing manufacturers in one payload. | Medium | **RESOLVED** — the per-vendor temperature ordering is gone. Temperature comes from the LibreHardwareMonitor backend, and the nvidia-smi fallback is used only for the metrics the backend actually missed, reconciled to a specific device by PCI id. |
| 15 | `SystemMonitorWidgetProvider` / `Program` | **Shutdown can block indefinitely.** `WaitForCompletion()` waits on `EmptyWidgetListEvent` (30s if empty, infinite otherwise). If the Widget Board never deactivates widgets and never disconnects, `Program.Main` never returns, `CoRevokeClassObject` is never called, and the process cannot exit cleanly. | Medium | **OPEN** — unchanged. |
| 16 | `SystemMonitorWidgetProvider` | **Payload built per-widget, but template selected per-widget size.** `UpdateWidget` calls `GetTemplateForSize` then `CreateDataPayload` — payload creation doesn't depend on size, so all widgets of all sizes get the same full JSON, and small templates reference keys that may be absent/empty. Mismatch between template keys and payload keys is possible (see section 3). | Low | **RESOLVED** — the payload is now built by `WidgetPayloadPresenter` and always carries every key any template may reference, so a template never binds to a missing property. `WidgetPayloadPresenterTests.Template_BindsOnlyToNamesThePayloadProduces` asserts this for every size. |
| 17 | `SystemMonitorWidgetProvider` | **No cancellation / graceful shutdown of in-flight updates on `Dispose`.** `StopMetricsService` disposes the timer but does not wait for an in-flight `OnTimer`/`UpdateAllWidgets` to complete, so `Dispose` can race with a tick reading `_metrics` after disposal. | Medium | **RESOLVED** (Phase 3) — `DisposeAsync()` cancels, awaits the coordinator loop, and only then releases the cancellation sources; `ShutdownAsync()` disposes the backend afterwards. A synchronous stop followed by an awaitable deactivation still drains the in-flight collection. |
| 18 | `SystemMetricsService` | **`PerformanceCounter` category "PhysicalDisk" may require admin/elevated permissions on some SKUs.** Ctor catches the exception, so disk counters silently stay null; no surface tells the caller disk-counter data is unavailable. | Low | **RESOLVED** — the `PhysicalDisk` counters are no longer created at all (see #7), so the elevation concern no longer applies. |
| 19 | `SystemMonitorWidgetProvider` | **`DeleteWidget` / `Deactivate` stop the service when the last widget goes away, but `CreateWidget`/`Activate` restart it.** Frequent pin/unpin cycles repeatedly create/dispose `SystemMetricsService`, resetting counter baselines (`_prevIdle`, `_prevNetwork*`) each time, causing a spike of zero/invalid deltas on restart. | Low | **OPEN** — the pipeline is stopped and restarted, but the `SystemMetricsService` and its network baselines are created once and kept for the process lifetime, so baselines are no longer reset. Largely moot, but the stop/restart cycle itself is unchanged. |
| 20 | `WidgetProviderFactory<T>` | **`CreateInstance` allocates a new `T()` twice in the fallback path** (once for the successful QI, then again in the fallback `MarshalInspectable<IWidgetProvider>.FromManaged(new T())` + `QueryInterface`). Wasteful and the second instance is leaked if `QueryInterface` fails. | Low | **OPEN** — unchanged. |

### 2.1 Measurement fixes applied

- **ACPI tenths-of-kelvin conversion**: `ThermalMath.TenthsKelvinToCelsius` now uses `raw / 10.0 - 273.15` (was `273.2`). Verified: raw 3000 → 26.85 °C.
- **ACPI thermal zones no longer labeled CPU/GPU**: `UpdateCpuTemp()` writes to the new `SystemMetrics.AcpiThermalZoneTemperatureC` field instead of `_model.CpuTemperature`, and publishes the reading under its own `acpi-thermal-zone-temperature` metric name (ACPI zones are not GPU-specific). GPU temperature comes only from confirmed GPU sensors — the LibreHardwareMonitor backend, or the reconciled nvidia-smi fallback when the backend could not read it.
- **Network throughput uses actual elapsed time**: `NetworkDeltaCalculator.Calculate` computes `(now - previous.Timestamp).TotalSeconds` instead of a hard-coded 5s interval. First sample establishes a baseline with no rate emitted.
- **Per-interface baselines by interface ID**: `SystemMetricsService` tracks baselines in a `ConcurrentDictionary<string, InterfaceSample>` keyed by `NetworkInterface.Id`. Disappearing interfaces are dropped from baselines; reappearing/reset interfaces are rebaselined (no spike, no negative rate).
- **Loopback/virtual adapter policy**: `NetworkInterfacePolicy.ShouldInclude` excludes loopback interfaces and down interfaces, but includes VPN/tunnel and other virtual adapters carrying legitimate traffic.
- **Consistent clamping**: `ThermalMath.ClampPercent` clamps to [0, 100] and maps NaN/Infinity to `0.0`; used for the CPU, RAM and disk percentages. Note that mapping a non-finite value to zero means a NaN percentage would be reported as a *measured* 0 % — see "Remaining limitations" below.
- **Unused disk throughput counters removed**: `_diskReadBytesPerSec` / `_diskWriteBytesPerSec` (never had `NextValue()` called, no consumers) removed from ctor and `Dispose`.

---

## 3. Current payload keys referenced by each widget template

`WidgetPayloadPresenter.Present(snapshot, selectedGpuInstanceId)` (`Services/WidgetPayloadPresenter.cs`)
builds the flat, manufacturer-neutral payload. It is serialized by `WidgetPayload.ToJson()`.

Two kinds of key, kept apart because they are used differently:

- **Text keys** are bound with `${name}`. Every one is always present, so a template never
  binds to a missing property.
- **Flag keys** are JSON **booleans**, consumed by `$when` conditions.

| Key | Kind | Source | Notes |
|-----|------|--------|-------|
| `cpu` | text | `FormatPercent(snapshot.CpuLoadPercent)` | Always present |
| `cpuClock` | text | `FormatClock(snapshot.CpuClockMHz)` | "N/A" if no clock |
| `cpuTemp` | text | `FormatTemperature(snapshot.CpuTemperatureC)` | "N/A" if no sensor |
| `cpuName` | text | primary CPU's `DisplayName` | "N/A" if unknown |
| `cpuVendor` | text | primary CPU's `Vendor` | "N/A" if unknown |
| `gpu` | text | the **selected** device's load — its own reading, or the `gpu-load` scalar when that scalar names the same device | "N/A" if neither is available |
| `gpuTemp` | text | the selected device's temperature, by the same rule | "N/A" if neither is available |
| `gpuVram` | text | the selected device's "used / total (pct)", by the same rule | "N/A" if neither is available |
| `gpuVramUsed` | text | the selected device's used bytes, by the same rule | |
| `gpuVramLabel` | text | the heading the memory figure is shown under — "Dedicated GPU memory" / "Shared GPU memory" / "GPU memory" | **Never** "Dedicated VRAM" for an unspecified or shared pool |
| `gpuVramLabelCompact` | text | the same heading in its short form — "Dedicated memory" / "Shared memory" / "GPU memory" | Bound by the Small layout, which has less room |
| `gpuName` | text | the selected device's `DisplayName` | "N/A" if unknown |
| `gpuVendor` | text | the selected device's `Vendor` | "N/A" if unknown |
| `gpuIntegration` | text | "integrated" / "discrete" | **Empty when the class is unknown** — no guess is shown |
| `gpuLabel` | text | name + vendor + classification | Truncated to 40 characters |
| `gpuLabelCompact` | text | name + vendor | Truncated to 24 characters; no classification |
| `gpuExtras` | text | the additional devices, one per line | |
| `gpuSelectionNotice` | text | why the default was substituted | Empty when the selection resolved |
| `gpuExtra1..3` | text | one additional device per slot | Empty when there is no such device |
| `gpuExtra1..3Available` | flag | whether that slot is filled | Consumed by `$when` |
| `gpuDevicePresent` | flag | whether a GPU is presented | Consumed by `$when` |
| `gpuHasExtra` | flag | whether any additional device is described | **Not bound by any template** |
| `gpuSelectionMissing` | flag | the selected device is gone | Consumed by `$when` |
| `sampleIsStale` | flag | the newest successful reading is older than the sample | **Not bound by any template** |
| `ram` | text | "used / total (pct)" | |
| `ramUsed` / `ramTotal` / `ramCombined` / `ramPercent` / `ramCompact` | text | RAM figures | |
| `disk` | text | "used / total (pct)" | |
| `diskUsed` / `diskTotal` / `diskCombined` / `diskPercent` / `diskCompact` | text | disk figures | |
| `netDown` / `netUp` | text | network throughput | |
| `tempSummary` | text | combined cpu/gpu temp | |
| `acpiThermalZoneTemp` | text | `FormatTemperature(snapshot.AcpiThermalZoneTemperatureC)` | Never presented as CPU or GPU temperature. **Not bound by any template** |
| `sampleTime` | text | the most recent **successful** reading time | Not the publish time |
| `lastUpdated` | text | same value as `sampleTime` | Kept for the legacy key name |

**The legacy `igpu` / `dgpu` keys are gone.** They bucketed GPUs by integration class, which
collapsed two different cards into one value and could name neither of them. The
manufacturer-neutral payload presents the selected device and, where the layout has room, the
other devices individually.

### Device selection

`GpuSelectionPolicy` (`Services/GpuSelectionPolicy.cs`) decides which GPU a widget presents.

**Default selection policy** (when the user has not chosen a device) — a total order over the
detected GPUs:

1. **Integration class first.** A GPU known to be *discrete* wins; a GPU whose class is
   *unknown* comes next; a GPU known to be *integrated* comes last.
2. **Instance identity as the tie-break** — the lowest `InstanceId`, compared ordinally. The
   instance id is assigned by the platform, so the choice does not depend on enumeration order.
3. **Device id as the final tie-break**, so the order is total and the default is always
   deterministic.

**User selection** is a device **instance id** (`HardwareDevice.InstanceId` — the Windows PNP
device id), never an enumeration index. `HardwareDevice.DeviceId` is an enumeration index
("/gpu-nvidia/0") whose numeric suffix shifts when the device set changes, so it is never
persisted; `PciDeviceKey` identifies a device *model*, which two identical cards share, so it
cannot distinguish them either.

When the selected device is absent, the selection does not resolve: the default policy is
applied instead, and `gpuSelectionMissing` is set so the widget says so. The widget keeps
working against whichever GPU is present rather than showing nothing.

The selection is persisted in `WidgetUpdateRequestOptions.CustomState`, which the widget host
stores per widget and hands back through `WidgetInfo.CustomState` on the next provider start.

> **Changed on 2026-10-21.** The device picker was the widget's only route to a *user* selection,
> and it was removed with the action row (see §1.6). The machinery above is unchanged — the
> provider still reads `SelectedGpuInstanceId` from the host's persisted `CustomState` and
> still applies the default policy when it does not resolve — but nothing in the widget can
> set a selection any more, so on a multi-GPU machine the widget shows the default device.
> `WidgetPayloadPresenter.BuildGpuChoices` was deleted as dead production code. The live
> device switch is therefore **not** validated, and the registry's selection storage is
> covered only by the automated tests. See `docs/hardware-validation.md` §2.11.

### Template → key mapping

**Small template**: `${cpu}`, `${cpuClock}`, `${cpuTemp}`, `${gpu}`, `${gpuTemp}`,
`${gpuVramUsed}`, `${ramCompact}`, `${diskCompact}`. The card is a three-row summary — a CPU
row, a GPU row and a RAM/disk row — and it names no GPU model and carries no sub-headings,
because the 148-pixel card has no room for them. It therefore binds **no** memory label:
the used-memory figure it shows claims nothing about the memory pool, so it needs no heading
to keep the claim honest. **No** additional devices are shown.

**Medium template**: `${cpuName}`, `${cpu}`, `${cpuClock}`, `${cpuTemp}`, `${gpuLabel}`,
`${gpuSelectionNotice}`, `${gpu}`, `${gpuTemp}`, `${gpuVramLabel}`, `${gpuVram}`, `${gpuExtra1}`,
`${ramCompact}`, `${diskCompact}`. One additional device is shown when there is one. The RAM
and storage figures are in their compact one-line form: the medium card overflowed its own
height with the longer "Used / Total" form, and the storage value was clipped away
entirely.

**Large template**: everything the medium layout shows, plus `${gpuExtra2}`, `${gpuExtra3}`,
`${netDown}`, `${netUp}` and `${sampleTime}`. Up to three additional devices. The RAM and
storage figures keep the longer "Used / Total" form, which the large card has room for.

**Observation:** every name a template references is one the payload produces, in every size.
This is asserted directly by `WidgetPayloadPresenterTests.Template_BindsOnlyToNamesThePayloadProduces`,
which parses each template's `${…}` references and checks them against the payload's key set.


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
- `System.Management` `10.0.2`
- `System.Diagnostics.PerformanceCounter` `8.0.0`
- `System.Security.Permissions` `8.0.0`
- `LibreHardwareMonitorLib` `0.9.6`

Solution (`SystemMonitor.sln`) contains two projects: `SystemMonitor` and `SystemMonitor.Tests`.
The test project targets `net8.0` and links only the pure, hardware-independent source files (see
`SystemMonitor.Tests/SystemMonitor.Tests.csproj`) so the test host needs neither Windows App SDK nor
real hardware. It references `Microsoft.Extensions.TimeProvider.Testing` `8.10.0` for
`FakeTimeProvider`.

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

The whole build → pack → sign path lives in **`build/Create-TestMsix.ps1`** (119 lines). The
`build/install-msix.ps1` script is *install-only* (14 lines): it removes any existing
`com.test.systemmonitor` package and calls `Add-AppxPackage -Path` on the prebuilt MSIX. It does
not build, publish, or patch anything.

1. **Build/publish:** `dotnet publish <csproj> -c Release -r $Runtime -o build/publish-temp
   /p:SelfContained=false` (`Create-TestMsix.ps1:36` — this overrides the pubxml
   `SelfContained=true`).
2. **Package root:** copy publish output → `build/package-root`; copy `Package.appxmanifest` →
   `AppxManifest.xml`; copy `WidgetsManifest.xml`; copy assets; rename scaled asset files to
   un-scaled aliases (e.g. `Square44x44Logo.scale-200.png` → `Square44x44Logo.png`).
3. **Manifest patching:** `Create-TestMsix.ps1` rewrites `Identity Name`/`Publisher`
   (`com.test.systemmonitor` / `CN=Developer`), replaces `$targetnametoken$.exe` →
   `SystemMonitor.exe` and `$targetentrypoint$` → `Windows.FullTrustApplication`, and sets
   `ProcessorArchitecture` per runtime.
4. **Certificate:** reuses a trusted root code-signing cert if one exists, otherwise creates a
   10-year self-signed cert in `CurrentUser\My` and exports it to `build/msix-output/testcert.pfx`.
5. **MSIX:** `makeappx pack /d package-root /p msix-output/SystemMonitor.msix`; `signtool sign`
   (SHA256, DigiCert timestamp) with the PFX.
6. **Install:** `build/install-msix.ps1` removes the existing package and installs the MSIX.

**Observed in the current checkout:** the built MSIX contains **366** entries, including all 12 of
`BlackSharp.Core.dll`, `DiskInfoToolkit.dll`, `HidSharp.dll`, `LibreHardwareMonitorLib.dll`,
`Mono.Posix.NETStandard.dll`, `MonoPosixHelper.dll`, `RAMSPDToolkit-NDD.dll`,
`System.CodeDom.dll`, `System.IO.Ports.dll`, `System.Management.dll`,
`System.Threading.AccessControl.dll`, `libMonoPosixHelper.dll`.

**Known build warning:** publishing with a `PublishProfile` that does not resolve emits
`warning NETSDK1198: A publish profile with the name 'win-AnyCPU.pubxml' was not found`. The
`PublishProfile` setting in the csproj is `win-$(Platform).pubxml`; when `Platform` does not
resolve to `x86`/`x64`, MSBuild looks for a `win-AnyCPU.pubxml` that does not exist. The publish
still succeeds — the warning is noise, not a failure.

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

- `HardwareDevice` — durable identity via `DeviceId` (never list position), with `DisplayName`, `Vendor`, `DeviceClass` (Cpu/Gpu/Unknown), and `GpuIntegration` (Integrated/Discrete/Unknown). Supports multiple GPUs from same or different vendors. `InstanceId` carries the per-instance identity (the Windows PNP device id) that a user selection is persisted against; it is distinct from `DeviceId` (an enumeration index) and from `PciDeviceKey` (a model key that two identical cards share).
- `Measurement` — carries `Value`, `Unit`, `Source`, `MeasurementStatus` (Available/Unsupported/Failed/Stale), and `LastSuccessfulUpdate`. Zero is never substituted for unavailable data.
- `DeviceReading` — ties a `Measurement` to a `DeviceId` + `Metric` name.
- `MetricsSnapshot` — immutable snapshot with `Timestamp`, `Cpus`, `Gpus`, `Readings`, and system-level RAM/disk/network measurements. Collections are defensively copied to arrays via `MetricsSnapshot.Create(...)` so no mutable collection leaks.
- `LegacyPayloadPresenter` (`Services/LegacyPayloadPresenter.cs`) — compatibility shim that maps an immutable `MetricsSnapshot` to the legacy flat payload dictionary. The ambiguous `igpu`/`dgpu` keys have been removed; the widget itself is served by the manufacturer-neutral `WidgetPayloadPresenter`.

No vendor-prefixed keys (IntelGpuPercent/NvidiaGpuPercent/AmdGpuPercent) are encoded anywhere in the model.

**Constraint:** Do not add automatic elevation, hardware tuning, clock/voltage writes, fan control, or automatic driver installation. Reads only.

### Phase 2 — Immutable snapshots **[DONE]**
`MetricsSnapshot` replaces mutable `SystemMetrics` as the published contract:

- Collections are `IReadOnlyList<>` backed by arrays — callers cannot mutate them.
- `MeasurementStatus` communicates Availability vs Unsupported/Failed/Stale rather than emitting 0 for missing data.
- `SystemMetrics` remains in place as the legacy mutable model. `SystemMonitorWidgetProvider` still passes one to the collector, but it is **write-only**: the collector writes it every cycle and nothing in the production path reads it. New code paths target `MetricsSnapshot`.

Next: connect the remaining hardware probes (WMI) to populate `HardwareDevice`/`DeviceReading` and emit `MetricsSnapshot` from `SystemMetricsService` instead of mutating `SystemMetrics`. The nvidia-smi probe is now a reconciled fallback (see "Changes applied (NVIDIA command-line fallback)").

### Phase 3 — Single collection/publishing loop **[DONE]**

Before this phase there were **two loops**: `SystemMetricsService.Timer` (collects into mutable `_metrics`) and `SystemMonitorWidgetProvider._updateTimer` (publishes from `_metrics`). Both are gone, replaced by one coordinator.

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
| Quiescence | `DeactivateAsync()` awaits the deactivated generation's `Unwound` signal. It completes only after admitted collection and publishing work has finished or unwound. It is a task-completion signal — never a counter observation, never a sleep — and it never blocks the calling thread. The drain target is the generation that *holds admitted work*, not merely the one that was active when the call was made — see "Drain boundary after a synchronous stop" below. |
| Reactivation race | A new activation is a *separate* generation with its own lifecycle and is not cancelled by the in-flight `DeactivateAsync()`. The new generation awaits the old collection to unwind before admitting its own cycle, so non-overlap holds across the boundary. |
| Refresh | `ScheduleRefresh()` coalesces: several requests during one collection collapse into a single additional collection. **Not currently reachable from production code** — the Refresh button was part of the removed action row, and the card refreshes on its own five-second interval instead. The method is retained because the coalescing semantics are part of the pipeline's tested contract, and `Start()` still requests an immediate sample on reactivation. |
| Disposal | `DisposeAsync()` cancels, awaits the loop, and only then disposes the generation cancellation sources. The caller disposes the collector/backend afterwards, so the backend is never disposed while a collection is running. |

**Widget-host boundary:** a host update that has already been submitted to the widget host cannot be retracted. Only updates not yet submitted are suppressed when a generation is deactivated.

**Drain boundary after a synchronous stop:** the synchronous deactivation (`Stop()`, used by the COM widget callbacks) and the awaitable deactivation (`DeactivateAsync()`, used by the shutdown path) are two halves of one contract, and the second must still drain the first. `Stop()` retires the active generation and clears `_current`, so by the time `DeactivateAsync()` runs there is no active generation left to await — yet the retired generation's collection may still be running. The coordinator therefore records the generation that *admitted* the in-flight cycle (`_inFlightGeneration`), and `DeactivateAsync()` awaits that generation's `Unwound` signal. The retirement and the drain-target capture happen in the **same lock acquisition**, so a reactivation cannot admit a cycle in between and leave the caller awaiting a generation it never retired. `Stop()` and `DeactivateAsync()` share one retirement path (`RetireCurrentGeneration`), so a synchronous stop followed by an awaitable deactivation neither requests cancellation twice nor loses the drain boundary.

**Remaining blocking boundary (not eliminated):** `IMetricsCollector.Collect()` is synchronous. `GetSystemTimes`, `GlobalMemoryStatusEx`, WMI `ManagementObjectSearcher` queries, and the LibreHardwareMonitor sensor reads are synchronous native/WMI calls. The `nvidia-smi` subprocess launch is now bounded by `SystemProcessRunner` and cannot outlive its timeout. A cancellation request marks the token as cancelled and is observed at the next cycle boundary, but it **cannot forcibly stop a native call already in progress**. A deactivation therefore completes only once the in-flight collector returns on its own. This is a property of the platform.

**Provider wiring** (`Services/SystemMonitorWidgetProvider.cs`): `CreateWidget`/`Activate` start the pipeline and request an immediate sample; `Deactivate`/`DeleteWidget` request the pause synchronously (they are COM callbacks and must not block) when the last active widget goes away; `ShutdownAsync()` awaits quiescence and then disposes the backend. `Program.Main` calls `ShutdownAsync()` after `WaitForCompletion()`.

**Tests** (`SystemMonitor.Tests/UpdatePipelineTests.cs`): 14 deterministic tests. Virtual time via `FakeTimeProvider`; a controllable fake collector with explicit per-attempt `Started`/`Release` signals and separate counters for started / completed / failed / published; `TaskCompletionSource` with `RunContinuationsAsynchronously` throughout. No `Task.Delay` is used for synchronization — the only real-time wait is a deadlock watchdog on the final await. Covered: deactivate-while-waiting, deactivate-during-collection (non-cooperative blocked collector; quiescence provably incomplete until release), reactivate-during-old-collection (max concurrency 1, stale publication suppressed, new generation collects without advancing the old interval), reactivate-during-long-interval, failure recovery (attempt 2 throws, attempt 3 succeeds and publishes), and lifecycle stress including deleting the last active widget while inactive widgets remain. Drain-boundary coverage: a synchronous stop followed by an awaitable deactivation still awaits the in-flight collection; deactivating before any activation starts no work; the idle boundary reports idle only after publishing has finished; and a reactivation racing an in-flight deactivation is a separate generation that the deactivation neither cancels nor retires.

Recorded: 110/110 tests pass (before the NVIDIA fallback work); Release/win-x64 build 0 warnings, 0 errors.

### Phase 4 — Concurrency / correctness fixes (open items only)

The items already resolved (#2, #3, #4, #7, #8, #9, #11, #12, #13, #14, #17, #18) are listed in §2 with a
**RESOLVED** status and are not repeated here. The genuinely open findings:

- **#1 — `RunningWidgets` / `EmptyWidgetListEvent` are `static`.** Remove the `static` or make the
  registry instance-scoped, so two provider objects cannot collide. The registry itself is already
  safe (immutable entries, atomic replacement); what remains is the shared-instance question.
- **#10 — `UpdateDisk` reports only the system drive.** Add a per-drive breakdown, or document
  the single-drive limitation.
- **#15 — `Program.Main` can block indefinitely.** `WaitForCompletion()` waits on
  `EmptyWidgetListEvent` with no timeout when widgets are still active, so `CoRevokeClassObject`
  may never be called. Bound the wait or drive exit off the pipeline's drain boundary.
- **#16 — payload/template mismatch.** See Phase 5.
- **#20 — `CreateInstance` double-allocates in the fallback path.** Allocate the provider once and
  reuse it for the fallback `MarshalInspectable` + `QueryInterface` attempt.

**Moot but not closed:** #19 (the service was already made process-lifetime, so baselines are no
longer reset on pin/unpin cycles; only the stop/restart cycle itself remains).

**Deliberately not in scope:** automatic elevation, hardware tuning, clock/voltage writes, fan
control, and automatic driver installation are out of scope for every phase.

### Phase 5 — Payload/template alignment
- Derive the payload **from the template**, not the other way around: each template declares the keys it needs; the publisher supplies only those keys. Eliminates dead payload fields (`ramUsed`, `ramTotal`, `ramPercent`, `diskUsed`, `diskTotal`, `diskPercent`, `gpuVramUsed`, `tempSummary`, `lastUpdated`, `cpuDetailSmall`, `gpuDetailSmall`, `ramCompact`, `diskCompact`, `ramCombined`, `diskCombined`) and the risk of `${key}` references resolving to nothing.
- Consider generating payload keys strongly-typed (record per template size) so missing keys are compile-time errors.

### Phase 6 — Packaging / manifest hygiene
- Replace placeholder `Package.appxmanifest` identity (`com.yourcompany.systemmonitor` / `CN=YourPublisher`) before shipping; `Create-TestMsix.ps1` already patches at build time, but the source file should not ship a placeholder.
- Align `win-arm64.pubxml` `TargetFramework` (`19041.0`) with csproj (`22000.0`) and fix mislabeled `Platform=x64`.
- Decide self-contained vs framework-dependent: csproj does not set `SelfContained`; pubxmls set `true`; `Create-TestMsix.ps1` passes `/p:SelfContained=false`. Pick one and make it consistent.
- Fix the `PublishProfile` pattern (`win-$(Platform).pubxml`) so it does not emit `NETSDK1198` for a
  non-existent `win-AnyCPU.pubxml` when `Platform` does not resolve.

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
- The **fallbacks fill in** when the backend cannot read the metric, so a blocked
  sensor never blanks a working value. The CPU clock falls back to the WMI
  `Win32_Processor` reading; the GPU load, temperature and VRAM figures fall back to the
  reconciled nvidia-smi reading; the VRAM figures have a third tier, the unattributed
  Windows GPU counters.
- When **no** source can supply a metric, the collector state records that no value was
  read and the widget shows `N/A` — a stale value is never left on screen as if it were
  current.
- The **ACPI thermal-zone average** remains in its own `AcpiThermalZoneTemperatureC` /
  `acpiThermalZoneTemp` key. It is never presented as CPU or GPU temperature.

### Payload changes

- `gpu` now presents the **selected** device's load, taken from the device's own reading
  when it has one, and from the snapshot's load scalar when that scalar is attributed to
  the same device. A figure belonging to a different card is never presented for this one.
- The `igpu` / `dgpu` **integration-class** keys are gone. They bucketed GPUs by class rather
  than naming them, so two different cards collapsed into one value and neither could be
  identified. The payload now names the device it presents (`gpuName`, `gpuVendor`,
  `gpuLabel`) and shows the class (`gpuIntegration`) only when the backend actually determined
  it. **This is a visible change to every widget size.**
- The `NvidiaGpuPercent` model property has since been removed: it was written every cycle by a
  subprocess and read by nobody.

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
- `LegacyPayloadPresenter` is **not on the production path**. The widget is served by
  `WidgetPayloadPresenter`, which builds the payload from the immutable `MetricsSnapshot`.
  `LegacyPayloadPresenter` is exercised only by the tests that pin the legacy contract.
  It also **loses status and timestamps** that the production presenter keeps: it renders
  a `Stale` measurement as "N/A" rather than "stale", and it stamps `lastUpdated` with the
  snapshot's collection timestamp rather than the most recent successful read.
- The `Computer` instance holds native handles for the lifetime of the service. It is
  created with the `SystemMetricsService` on first pipeline start and closed in
  `ShutdownAsync()`, i.e. at process shutdown — **not** when the last widget deactivates.

---

## Baseline record

- **Build:** `dotnet build SystemMonitor/SystemMonitor.csproj -c Release -r win-x64` → succeeded, 0 warnings, 0 errors (2026-10-21).
  (A plain `-c Release` with no `-r` fails in the WindowsAppSDK self-contained
  target with "WindowsAppSDKSelfContained requires a supported Windows
  architecture"; the runtime identifier is required.)
- **Tests:** `SystemMonitor.Tests` — **405 tests pass** (0 failed, 0 skipped), by class:
  `WidgetPayloadPresenterTests` 62, `GpuMemoryLabelTests` 29, `DiagnosticsTests` 25,
  `GpuMemoryCompatibilityPolicyTests` 24, `WidgetLayoutFitTests` 20,
  `LhmSnapshotMapperTests` 20, `NvidiaGpuReconcilerTests` 20,
  `NvidiaSmiParserTests` 16, `HardwareModelsTests` 16, `LhmSensorSelectionTests` 16,
  `WidgetSelectionTests` 15, `GpuSelectionPolicyTests` 14, `UpdatePipelineTests` 14,
  `ScalarCollectorStateTests` 12, `LhmSensorConversionTests` 11, `ScalarMergePolicyTests` 10,
  `ThermalMathTests` 10, `GpuMemoryFallbackPolicyTests` 10,
  `NvidiaGpuFallbackTests` 9, `SystemProcessRunnerTests` 9,
  `NetworkDeltaCalculatorTests` 8, `LhmSensorSourceTests` 7, `LhmMergePolicyTests` 6,
  `NvidiaSmiLocatorTests` 6, `NvidiaGpuFallbackPolicyTests` 6,
  `NetworkInterfacePolicyTests` 5, `NvidiaGpuFallbackSelectorTests` 5.
  (`ThermalZoneSourceTests` was removed with the thermal-zone source it
  tested; the thermal-zone reading now goes through the shared
  `ScalarCollectorState`, which `ScalarCollectorStateTests` covers.
  `WidgetLayoutFitTests` was added with the live-host validation: it pins
  the properties behind the four presentation defects the host revealed.)
  Coverage: thermal conversion, network delta, interface policy,
  manufacturer-neutral hardware models, immutable snapshots, legacy payload presenter, LHM sensor
  selection / unit conversion / snapshot mapping / merge policy, the update-pipeline lifecycle
  (generation-scoped cancellation, non-overlap, drain boundary, retired-generation rejection),
  the NVIDIA command-line fallback (CSV parsing, PCI-key reconciliation, fallback policy,
  bounded process execution), and the manufacturer-neutral multi-GPU payload (template binding
  compatibility for every size, the AMD-only / NVIDIA-only / Intel-only / hybrid / no-GPU
  fixtures, long device names, missing temperature and VRAM, a device selection that
  disappears or becomes unavailable, two GPUs with identical display names, the default
  selection policy's determinism, the last-successful-sample timestamp, and N/A without raw
  exception text).
  Hardware-model coverage: Intel CPU + AMD GPU; AMD CPU + NVIDIA GPU; Intel iGPU + NVIDIA dGPU; two
  GPUs with identical names; device with no readable sensors; unknown vendor/classification; legacy
  payload compatibility; missing sensors never reported as zero; blocked sensors reported as Failed
  rather than zero; partial permission failure leaving other metrics of the same device readable.
  Fallback coverage: malformed and truncated rows; unsupported values; multiple devices; nonzero
  exit; timeout; missing executable; unreconciled and ambiguous identities — none of which may be
  merged into the wrong device.
- **MSIX packaging:** `makeappx pack` verified to succeed with the new dependency present. The packed
  `.msix` contains **366 entries**, including `LibreHardwareMonitorLib.dll`, `DiskInfoToolkit.dll`,
  `HidSharp.dll`, `RAMSPDToolkit-NDD.dll`, `Mono.Posix.NETStandard.dll`, `MonoPosixHelper.dll`,
  `BlackSharp.Core.dll`, `libMonoPosixHelper.dll`, `System.CodeDom.dll`, `System.IO.Ports.dll`,
  `System.Management.dll` and `System.Threading.AccessControl.dll`.
- **Publish:** `dotnet publish SystemMonitor/SystemMonitor.csproj -c Release -r win-x64 -o <out>
  -p:SelfContained=false` succeeds. It emits `warning NETSDK1198: A publish profile with the name
  'win-AnyCPU.pubxml' was not found` because the csproj `PublishProfile` is `win-$(Platform).pubxml`
  and `Platform` does not resolve — the publish still succeeds.
- **Hardware validation:** the adapter was run against a real machine (Intel i7-10875H + NVIDIA RTX 2070 Super + Intel UHD Graphics). GPU load/temperature/clock/VRAM read correctly; CPU temperature and clock correctly reported as `Failed` because MSR access requires elevation the widget deliberately does not request. **No AMD machine was available** — the AMD code paths are fixture-verified only.
- **Live-host validation (2026-10-21):** the widget was packaged, installed and driven in the real Windows Widgets board (`Microsoft.WidgetsPlatformRuntime` 1.6.19.0). The installed `SystemMonitor.dll` was verified byte-identical to the packaged and the freshly published one (SHA-256 `325947C716226672A3995FE6D014475B7C3B230A75BC0AE8E5B92221DF64039C`), so the host provably ran the candidate. The card was rendered and measured in Small, Medium and Large; four presentation defects were found and fixed; the action row was removed after the host was found never to render it; and the lifecycle was exercised (activation, resize, refresh, deactivate/reactivate, remove/re-add, multiple instances, sleep/resume). The full record, with the measured geometry and the screenshots, is in `docs/hardware-validation.md` §2.6, §2.7 and §2.9.
- **Tracked artifacts modified in working tree (git status snapshot):** `build/msix-output/SystemMonitor.msix`, `build/package-root/*`, `build/publish-temp/*` — all build outputs.
- **Most recent source commit:** `eb2568e` "Preserve the drain boundary across a synchronous deactivation" — the pipeline coordinator, the generation-scoped lifecycle, the test suite, and the drain-boundary fix.

---

## Changes applied (measurement fixes and neutral hardware model)

**New files:**
- `Models/HardwareModels.cs` — `HardwareDevice`, `DeviceClass`, `GpuIntegration`, `Measurement`, `MeasurementStatus`, `DeviceReading`, `MetricsSnapshot` (immutable), `MetricsSnapshot.Create` factory.
- `Services/LegacyPayloadPresenter.cs` — compatibility presenter mapping `MetricsSnapshot` → legacy flat payload dictionary for `WidgetTemplates`.
- `Services/ThermalMath.cs` — pure `TenthsKelvinToCelsius` and `ClampPercent` helpers.
- `Services/NetworkDeltaCalculator.cs` — pure per-second rate calculation using actual elapsed time; detects counter resets.
- `Services/NetworkInterfacePolicy.cs` — loopback excluded; VPN/tunnel included; down interfaces excluded.
- `SystemMonitor.Tests/SystemMonitor.Tests.csproj` — test project linking only pure logic files.
- `SystemMonitor.Tests/ThermalMathTests.cs` — 10 tests.
- `SystemMonitor.Tests/NetworkDeltaCalculatorTests.cs` — 8 tests.
- `SystemMonitor.Tests/NetworkInterfacePolicyTests.cs` — 5 tests.
- `SystemMonitor.Tests/HardwareModelsTests.cs` — 16 tests covering all required scenarios.

  (`Services/ThermalZoneReading.cs` and `SystemMonitor.Tests/ThermalZoneSourceTests.cs` were
  added at the time and have since been **deleted**: the thermal-zone reading now goes through
  the shared `ScalarCollectorState`, which carries the status and the source directly, so the
  separate typed reading and its test no longer had a job.)

**Modified files:**
- `Models/SystemMetrics.cs` — added `AcpiThermalZoneTemperatureC` property (unidentified ACPI sensors, separate from CPU/GPU).
- `Services/SystemMetricsService.cs` — removed unused `PerformanceCounter` disk counters; ACPI temps converted with corrected formula and written to `AcpiThermalZoneTemperatureC` (not `CpuTemperature`); network now uses per-interface baselines by ID with actual elapsed time; counter reset/wrap produces no negative rate; loopback excluded via `NetworkInterfacePolicy`; clamping consistent via `ThermalMath.ClampPercent`.
- `Services/SystemMonitorWidgetProvider.cs` — payload now includes `acpiThermalZoneTemp` key.
- `SystemMonitor.sln` — added `SystemMonitor.Tests` project.

---

## Changes applied (LibreHardwareMonitor backend)

**New files:**
- `Services/LhmSensorSelection.cs` — pure sensor-selection and unit-conversion logic. Matched on the (name, sensor-type) pair, never on name alone. Ordered candidate tables with documented fallback semantics. `TryConvert` verifies the source unit before converting and rejects missing/NaN/infinite values.
- `Services/LhmSensorSource.cs` — `ILhmSensorSource` interface and `LhmDeviceDescriptor` record: the backend-neutral contract between the adapter and the mapping logic.
- `Services/LhmSnapshotMapper.cs` — pure mapping from `LhmDeviceDescriptor` onto `HardwareDevice`/`DeviceReading`/`Measurement`, plus `SelectPrimaryGpu`/`SelectPrimaryCpu` (discrete → unknown → integrated, tie-broken by device id for stability).
- `Services/LibreHardwareMonitorSource.cs` — the thin adapter. Owns one `Computer` instance, opens it once, closes it on dispose, disables all non-CPU/GPU groups, updates hardware and sub-hardware recursively, derives vendor and integration class, and maps sensors to the neutral descriptors. Read-only: never writes an `IControl`.
- `SystemMonitor.Tests/LhmFixtures.cs` — fixtures for Intel CPU, AMD CPU, NVIDIA GPU, AMD GPU, Intel integrated GPU, a GPU with no sensors, a fully blocked CPU, a partially blocked CPU, and ambiguous sensor names.
- `SystemMonitor.Tests/LhmSensorSelectionTests.cs` — 16 tests.
- `SystemMonitor.Tests/LhmSensorConversionTests.cs` — 11 tests.
- `SystemMonitor.Tests/LhmSnapshotMapperTests.cs` — 20 tests.
- `SystemMonitor.Tests/LhmSensorSourceTests.cs` — 7 tests.
- `SystemMonitor.Tests/LhmMergePolicyTests.cs` — 6 tests.

**Modified files:**
- `SystemMonitor/SystemMonitor.csproj` — added `LibreHardwareMonitorLib 0.9.6`; raised `System.Management` from 8.0.0 to 10.0.2 to satisfy the transitive requirement (NU1605).
- `Models/SystemMetrics.cs` — added `GpuLoadPercent`. (The `IntegratedGpuLoadPercent` / `DiscreteGpuLoadPercent` properties that were added at the same time no longer exist; the integration-class buckets they served are gone.)
- `Services/SystemMetricsService.cs` — owns the sensor source and its lifetime; merges backend readings with the fallback sources using the documented precedence; `Dispose` closes the backend.
- `Services/WidgetTemplates.cs` — the large template's label changed from `Intel: … | NVIDIA: …` to `Integrated: … | Discrete: …`; that label has since been replaced by the per-device `gpuLabel` / `gpuExtra*` keys, and the current templates carry no vendor-prefixed text.
- `Services/LegacyPayloadPresenter.cs` — uses the same primary-CPU/primary-GPU selection as the widget, so the legacy keys and the per-device keys never disagree.
- `SystemMonitor.Tests/SystemMonitor.Tests.csproj` — links the three new pure files; xunit pinned to 2.9.0 (2.8.2 was not on the feed).
- `docs/refactor-plan.md` — section 5A added: dependency, license, packaging, architecture, selection preference, measured behaviour, testing caveat, known limitations.

---
---

## Changes applied (update pipeline and lifecycle)

**New files:**
- `Services/UpdatePipeline.cs` — `UpdatePipeline<TSize>`, the single collection/publishing
  coordinator. Generation-scoped cancellation, one cycle in flight at a time, an explicit
  awaitable drain boundary, and rejection of retired-generation results.
- `Services/IMetricsCollector.cs` — the collector seam (`Collect()` returns one completed
  immutable `MetricsSnapshot`).
- `Services/WidgetRegistry.cs` — `WidgetRegistry<TSize>` over immutable `WidgetEntry<TSize>`
  records, replaced atomically via compare-and-swap.
- `SystemMonitor.Tests/UpdatePipelineTests.cs` — 14 deterministic lifecycle tests.

**Modified files:**
- `Services/SystemMetricsService.cs` — implements `IMetricsCollector`; the timer and the
  `Dispatch` helper are gone; each collector in `Collect()` is independently try/caught.
- `Services/SystemMonitorWidgetProvider.cs` — `EnsurePipelineStarted`/`StopPipeline` replace
  `EnsureMetricsServiceStarted`/`StopMetricsService`; the provider wires the pipeline's collect,
  build and publish callbacks; `ShutdownAsync()` awaits quiescence before disposing the backend.
- `SystemMonitor.Tests/SystemMonitor.Tests.csproj` — links `IMetricsCollector.cs`,
  `UpdatePipeline.cs`, `WidgetRegistry.cs` and `LegacyPayloadPresenter.cs`; adds
  `Microsoft.Extensions.TimeProvider.Testing` 8.10.0 for `FakeTimeProvider`.

**Drain-boundary fix (commit `eb2568e`):** `Stop()` retired the active generation and cleared
`_current`, so a later `DeactivateAsync()` found no generation and returned immediately — while the
retired generation's collection was still running. `ShutdownAsync()` could therefore dispose the
LibreHardwareMonitor backend out from under an in-flight collection, violating "shutdown waits for
owned work before disposing backends". The coordinator now records the generation that *admitted*
the in-flight cycle (`_inFlightGeneration`), and `DeactivateAsync()` awaits that generation's
`Unwound` signal. Retirement and drain-target capture happen in one lock acquisition, so a
reactivation cannot admit a cycle in between; `Stop()` and `DeactivateAsync()` share one retirement
path, so cancellation is never requested twice.

---

## Changes applied (NVIDIA command-line fallback)

### Decision: retained, but demoted to a lazy, reconciled fallback

The cross-vendor LibreHardwareMonitor backend already supplies all five NVIDIA metrics on the
development machine (load, temperature, VRAM used/total, and a PCI identity), and LHM's NVIDIA
path is itself NVML-based — the same `nvml.dll` layer that `nvidia-smi` wraps. The command-line
path is therefore **not** a second source of truth, and it was not retained as one.

It *was* retained, because it is not redundant: LHM's `Computer.Open()` and hardware enumeration
can fail or be blocked on a given machine (driver quirks, missing symbols, permission issues),
while the NVIDIA driver's own tools still work. So the command-line path is kept strictly as a
**fallback for metrics the backend actually missed** — never as a parallel source, never merged
into a device it was not proven to belong to.

Concretely, this replaced three pre-existing defects:

- `GetNvidiaGpuPercent()` spawned a subprocess on **every** sampling cycle and wrote the result
  to `SystemMetrics.NvidiaGpuPercent`, a property **no consumer ever read**. This was dead code
  with a real cost. The method and the property were removed.
- The WMI VRAM path queried `Win32_PerfFormattedData_GPUPerformanceCounters_GPULocalVideoMemory`,
  a class that **does not exist** on this machine, and read `CurrentUsage`/`AvailableUsage` in
  **KB** while treating them as bytes. Corrected to
  `Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory.DedicatedUsage`, which is bytes.
- `Win32_VideoController.AdapterRAM` is a `uint` capped at 4 GB, so it under-reports total VRAM
  on an 8 GB card. It is now a last-resort fallback only, and the value is treated as a cap.

### Design

| Concern | Implementation |
|---|---|
| One invocation per cycle | `NvidiaGpuFallback` issues a single `nvidia-smi` call with `--query-gpu=uuid,name,pci.bus_id,pci.device_id,utilization.gpu,temperature.gpu,memory.used,memory.total --format=csv,noheader,nounits` |
| Parsing | `NvidiaSmiParser` — one row per GPU, `CultureInfo.InvariantCulture`, name reassembled from the variable-width name field, MiB→bytes for VRAM |
| Missing / unsupported values | `[`, `<`, `N/A`, `Not Supported` and empty fields parse to `null` rather than zero; out-of-range percentages (>100) and temperatures (<−50, >200) are rejected as malformed |
| Malformed rows | Rows that do not have the expected field count are skipped, not guessed at |
| Nonzero exit / timeout | `NvidiaGpuFallback` returns an empty list; it never throws and never raises |
| Multiple devices | Every row becomes its own `NvidiaGpuSample` |
| Executable resolution | `NvidiaSmiLocator` resolves once from `PATH`, `Program Files\NVIDIA Corporation\NVSMI`, and `System32`, then caches it |
| Missing executable | After a failed lookup the locator suppresses re-probing for **15 minutes**; `Rediscover()` forces a fresh lookup |
| Injectable runner | `IProcessRunner` — `NvidiaGpuFallback` takes one, so tests use `FakeProcessRunner` and need no NVIDIA hardware |
| Timeout | `SystemProcessRunner` bounds start, drain and exit with one timeout (5 s default) |
| Deadlock-free | stdout and stderr are drained **concurrently**; the child is killed on expiry and the `Process` is always disposed |
| No wrong-device merges | `NvidiaGpuReconciler` matches a sample to a backend device **only** by PCI device key (`VEN_10DE&DEV_1E91`). Index and name are never used. A sample with no key, a key matching nothing, or a key shared by two devices yields `Unreconciled`/`Ambiguous` and the reading is dropped rather than misattributed |
| Value where it helps | `NvidiaGpuFallbackPolicy.ShouldInvoke` — the command line is not invoked at all when the backend already supplied the GPU load, the GPU temperature and both VRAM figures |

### Identity: how the two sources are joined

`IHardware.Identifier` from LibreHardwareMonitor is index-based (`/gpu-nvidia/0`) and is useless
for cross-source matching. The usable key is `GenericGpu.DeviceId` — the Windows PCI PNP id
(`PCI\VEN_10DE&DEV_1E91&SUBSYS_...`), which matches WMI's `PNPDeviceID` exactly. nvidia-smi's
`pci.device_id` (`0x1E9110DE`) puts the device id in the high half and the vendor id in the low
half, and normalizes to the same `VEN_10DE&DEV_1E91` form. `NvidiaGpuIdentity` performs both
conversions; `HardwareDevice.PciDeviceKey` carries the key through the snapshot.

### Proven deadlock-safety of the process runner

`SystemProcessRunnerTests.DoesNotDeadlockWhenTheChildFillsBothPipes` is a real regression guard,
not just a smoke test. The child writes to **stderr first** and floods it past the pipe buffer,
then floods stdout. A runner that reads one stream to completion before the other blocks
permanently: the child is stuck writing to the full stderr pipe and never reaches the stdout
writes that would release the reader.

Measured side by side against the identical script (isolated harness compiling the real
`SystemProcessRunner.cs`):

| Implementation | elapsed | timedOut | stdout | stderr |
|---|---|---|---|---|
| **Real runner (concurrent)** | **2.0 s** | `False` | full, incl. line 20000 | full, incl. line 20000 |
| Naive sequential reader | 30.0 s (hung to timeout) | `True` | empty | empty |

The test asserts `!TimedOut` and a wall-clock bound, so it fails against a naive implementation.

**New files:**
- `Services/IProcessRunner.cs` — the process seam (`ProcessRunResult`, `IProcessRunner`).
- `Services/SystemProcessRunner.cs` — the real runner: bounded, deadlock-free, always disposes the `Process`.
- `Services/NvidiaSmiParser.cs` — pure CSV parser, invariant culture.
- `Services/NvidiaSmiLocator.cs` — one-time executable resolution with a 15-minute failed-lookup backoff and an explicit `Rediscover()`.
- `Services/NvidiaGpuSample.cs` — one parsed row.
- `Services/NvidiaGpuIdentity.cs` — PCI device key normalization and extraction.
- `Services/NvidiaGpuReconciler.cs` — PCI-key-only matching; `Unreconciled`/`Ambiguous` outcomes.
- `Services/NvidiaGpuFallbackPolicy.cs` — the "is the fallback worth invoking" decision.
- `Services/NvidiaGpuFallbackSelector.cs` — picks the fallback values for the metrics the backend missed.
- `Services/NvidiaGpuFallback.cs` — the fallback itself: one bounded invocation, never throws.
- `SystemMonitor.Tests/FakeProcessRunner.cs` — recording fake runner with `WithOutput`/`WithExitCode`/`WithTimeout` builders.
- `SystemMonitor.Tests/NvidiaSmiParserTests.cs` (16), `NvidiaGpuReconcilerTests.cs` (20), `NvidiaGpuFallbackTests.cs` (26 — covering the fallback, its locator, its policy and its selector), `SystemProcessRunnerTests.cs` (9) — **71 new tests**.

**Modified files:**
- `Services/GpuService.cs` — all three nvidia-smi call sites removed. GPU VRAM comes from `GPUAdapterMemory.DedicatedUsage` (bytes) with `AdapterRAM` as a last resort. The GPU-percent and GPU-temperature methods that briefly replaced the nvidia-smi calls have since been removed as well: `GpuService` now has exactly one public method, `GetGpuVramBestEffort()`. GPU load and temperature come from the LibreHardwareMonitor backend or the reconciled nvidia-smi fallback.
- `Services/LibreHardwareMonitorSource.cs` — reads `GenericGpu.DeviceId` and exposes it as `PciDeviceKey`.
- `Services/LhmSensorSource.cs` — `LhmDeviceDescriptor.PciDeviceKey`.
- `Services/LhmSnapshotMapper.cs` — carries `PciDeviceKey` into `HardwareDevice`.
- `Models/HardwareModels.cs` — `HardwareDevice.PciDeviceKey`.
- `Services/SystemMetricsService.cs` — `UpdateSensorBackend()` runs before `UpdateNvidiaFallback()`; the fallback runs only for metrics the backend missed; snapshot records the source of the GPU temperature and VRAM figures.
- `Models/SystemMetrics.cs` — removed the dead `NvidiaGpuPercent` property and its backing field.
- `SystemMonitor.Tests/SystemMonitor.Tests.csproj` — links the ten new source files.

---

## Changes applied (manufacturer-neutral, multi-GPU presentation)

The widget used to present GPUs through a fixed pair of integration-class buckets, `igpu` and
`dgpu`, whose names read as vendor labels on the large layout. Those buckets could not name
either card, collapsed two different devices into one value, and offered no way to choose which
card a widget showed. They are gone.

### What the widget shows now

- **The device is named.** `gpuName` and `gpuVendor` carry the selected device's own display
  name and vendor. `gpuLabel` composes them for the layouts with room.
- **The class is shown only when it is known.** `gpuIntegration` is `integrated`, `discrete`,
  or **empty** — never a guess. Most vendors do not expose the class, and an NVIDIA card
  therefore shows no class at all rather than a wrong one.
- **The CPU is named too,** where the layout has room: `cpuName` and `cpuVendor`.
- **A device picker.** ~~The template carries one `Action.Execute` per device, with the target
  named in the verb (`selectGpu|<instance id>`).~~ **Removed on 2026-10-21** with the action
  row: the live host showed the row never rendered, so the picker was unreachable in every
  size. There is now no way to choose the presented device from the widget, and the widget
  shows the default device on a multi-GPU machine. The presented device is still *named* on
  the Medium and Large cards, and the other devices are still listed. See
  `docs/hardware-validation.md` §2.11.
- **Additional devices, where they fit.** The small layout shows no additional devices; the
  medium layout shows one; the large layout shows up to three. Each is guarded by a `$when`
  condition on a payload boolean, so a line is present only when there is a device for it.
- **Unavailable means "N/A", out-of-date means "stale".** A missing, failed or unsupported
  sensor is "N/A" — never zero. A value that is known but older than the current collection is
  "stale". No raw exception text ever reaches the payload.
- **The timestamp is the last successful sample,** not the time the payload was published.
  `sampleIsStale` is set when the newest successful reading is older than the collection,
  but no template binds it — the "stale" marker on an individual figure carries that
  information today.

### Selection

`GpuSelectionPolicy` implements the documented default policy (section 3) and resolves a user
selection **by instance id only** — never by display name, because two identical cards can
share a name. A selection that no longer resolves falls back to the default policy and the
widget says so, rather than showing nothing.

The selection is persisted in `WidgetUpdateRequestOptions.CustomState`, which the host stores
per widget. It is never an enumeration index: `HardwareDevice.DeviceId` is
"/gpu-nvidia/0", whose numeric suffix shifts when the device set changes, so a selection
stored against it would silently start pointing at a different card. `HardwareDevice.InstanceId`
is the Windows PNP device id — one specific adapter, stable across reboots, and distinct for
two identical cards.

### What was deliberately *not* done

- **The manifest is unchanged.** `IsCustomizable` is an attribute of the `<Definition>`
  element in the appxmanifest `uap3:AppExtension` form. This project registers through the
  older `windows.widgets` extension with a `WidgetsManifest.xml` (`widgets/1.0` `<Widget>`
  form), which does not carry that attribute. A device picker would therefore have to be
  built from ordinary actions on the regular template, rather than from a customization card
  that this package cannot declare — and that route was removed with the action row (see
  above).
- **No host UI API was invented.** Only `IWidgetProvider` / `IWidgetProvider2`,
  `WidgetUpdateRequestOptions`, `WidgetActionInvokedArgs.Verb` and `CustomState` are used —
  all verified against the Windows App SDK 2.5.1 / Widgets 2.0.5 assemblies.
- **The pipeline's lifecycle contract is unchanged.** `BuildPayloadFromSnapshot` and
  `PublishToWidget` keep their signatures, and the payload is now built per widget inside
  `UpdateWidget` rather than once per cycle — so two widgets may present different cards while
  the collection work is still done once.

### Files

**New:**
- `Services/WidgetPayloadPresenter.cs` — the testable presenter. Everything that decides what
  a widget shows lives here, so it can be exercised without a widget host.
- `Services/WidgetPayload.cs` — the payload container. Text values and booleans are kept apart,
  because a value bound into a text block and a value used as a `$when` condition are not the
  same kind of thing. Flags are serialized as real JSON booleans.
- `Services/GpuSelectionPolicy.cs` — the default selection policy and the selection resolver.
- `Services/WidgetLayoutSize.cs` — the project's own size enum, so the templates can be
  exercised by the test project without referencing the Windows App SDK.
- `SystemMonitor.Tests/WidgetPayloadPresenterTests.cs`,
  `SystemMonitor.Tests/GpuSelectionPolicyTests.cs`,
  `SystemMonitor.Tests/WidgetSelectionTests.cs`,
  `SystemMonitor.Tests/TemplateBindings.cs`.

**Modified:**
- `Services/WidgetTemplates.cs` — rewritten around the new keys; the device-picker actions are
  spliced in at build time.
- `Services/SystemMonitorWidgetProvider.cs` — `IWidgetProvider2`; the selection is read from
  and written to `CustomState`; `OnActionInvoked` handles `refresh` and `selectGpu|…`.
- `Services/WidgetRegistry.cs` — `WidgetEntry<TSize>.SelectedGpuInstanceId` and
  `TrySetSelectedGpu`.
- `Services/SystemMetricsService.cs` — the published snapshot now carries the device list and
  the per-device readings, not just the merged scalars.
- `Services/LhmSensorSource.cs`, `Services/LhmSnapshotMapper.cs`,
  `Services/LibreHardwareMonitorSource.cs` — carry the per-instance identity through to
  `HardwareDevice.InstanceId`.
- `Services/LegacyPayloadPresenter.cs` — the `igpu`/`dgpu` keys removed.
- `Models/HardwareModels.cs` — `HardwareDevice.InstanceId`.
- `SystemMonitor.Tests/LhmFixtures.cs` — the GPU fixtures carry realistic per-instance ids.
- `SystemMonitor.Tests/HardwareModelsTests.cs` — the `igpu`/`dgpu` assertions replaced with an
  assertion that the keys are gone.

---

## Suggested commit message — NVIDIA command-line fallback (when ready to commit)

```
Retain NVIDIA command-line collection as a reconciled fallback

The cross-vendor LibreHardwareMonitor backend already supplies every
NVIDIA metric, but its enumeration can fail on a given machine while
the driver's own tools still work. Keep nvidia-smi strictly as a
fallback for metrics the backend actually missed - never as a second
source of truth, never merged into an unproven device.

- Add a bounded, deadlock-free process seam (IProcessRunner /
  SystemProcessRunner): one timeout over start, drain and exit, the
  child killed on expiry, the Process always disposed, and stdout and
  stderr drained concurrently so a child filling both pipes cannot
  deadlock the reader
- Add a single-invocation nvidia-smi fallback that queries identity,
  utilization, temperature and VRAM together, parses one row per GPU
  with invariant culture, and returns null (never zero) for missing or
  unsupported values
- Reconcile samples to backend devices by PCI device key only. A
  sample with no key, a key matching nothing, or a key shared by two
  devices is dropped rather than merged by index or name
- Resolve the executable once, with a 15-minute backoff after a failed
  lookup and an explicit Rediscover() to force a fresh probe
- Skip the command line entirely when the backend already supplied the
  GPU load, the GPU temperature and both VRAM figures
- Remove three pre-existing defects: GetNvidiaGpuPercent() spawned a
  subprocess every cycle to write a property nobody read; the WMI VRAM
  path queried a non-existent class and read KB as bytes; AdapterRAM
  under-reports total VRAM because it is a uint capped at 4 GB
- Carry GenericGpu.DeviceId through the snapshot as PciDeviceKey so the
  two sources can be joined without guessing
- Add 71 tests over fakes; the process test is a proven regression guard
  (a naive sequential reader hangs to timeout against the same script)
- Update docs/refactor-plan.md: findings 11-14 are now RESOLVED, and
  the fallback design is documented

Co-Authored-By: Claude Code <noreply@anthropic.com>
```

## Suggested commit message — manufacturer-neutral, multi-GPU presentation (when ready to commit)

```
Present the widget's GPUs by name, and let the user pick the device

The widget presented GPUs through a fixed pair of integration-class
buckets, igpu and dgpu, whose labels read as vendor names on the large
layout. Those buckets could not name either card, collapsed two
different devices into one value, and offered no way to choose which
card a widget showed. Remove them and name the devices instead.

- Move all payload formatting into a testable presenter
  (WidgetPayloadPresenter) that takes an immutable snapshot and a
  selected device, so every decision about what a widget shows can be
  exercised without a widget host
- Name the device: gpuName, gpuVendor and gpuLabel carry the selected
  card's own display name and vendor, and cpuName / cpuVendor do the
  same for the CPU where the layout has room
- Show the integrated/discrete class only when the backend actually
  determined it. Most vendors do not expose it, so an unknown class is
  now empty rather than a guess
- Add a documented, deterministic default selection policy: discrete
  before unknown before integrated, then the lowest instance id, then
  the device id. The order is total, so the same machine always
  presents the same card
- Add an explicit device selection, resolved by instance id only -
  never by display name, because two identical cards share one. The
  selection is persisted in CustomState as the Windows PNP device id,
  never an enumeration index, so it survives a reboot and still
  points at the same physical card
- Fall back gracefully when the selected device disappears: apply the
  default policy and say so, rather than showing nothing
- Show the selected GPU compactly on the small layout, and additional
  devices on the medium and large layouts only where they fit, each
  guarded by a $when condition on a payload boolean
- Report unavailable values as N/A and out-of-date ones as stale,
  never zero, and never a raw exception message
- Show the last successful sample time rather than the publish time,
  and flag a sample that is older than the current collection
- Offer the device picker as ordinary Action.Execute actions with the
  target named in the verb, because widgets support only that action
  type and the verb is the part the host delivers verbatim. The
  manifest is unchanged: IsCustomizable belongs to the appxmanifest
  Definition element, which this package's widgets/1.0 manifest form
  does not carry
- Keep the pipeline's lifecycle contract intact: the payload is built
  per widget rather than once per cycle, so two widgets may show
  different cards while the collection work is still done once
- Remove the legacy igpu/dgpu keys, and publish the device list on the
  snapshot so the presenter can see more than one GPU
- Add 63 tests: template binding compatibility for every size, the
  AMD-only / NVIDIA-only / Intel-only / hybrid / no-GPU fixtures, long
  device names, missing temperature and VRAM, a selection that
  disappears or becomes unavailable, two GPUs with identical display
  names, the policy's determinism, and the stale/N-A treatment
- Update docs/refactor-plan.md: the payload-key table, the template
  mapping, finding 16 and the baseline record now describe the
  manufacturer-neutral contract

Co-Authored-By: Claude Code <noreply@anthropic.com>
```

---

## Changes applied (refactor review for regressions and unsupported claims)

A review of the whole refactor against this plan, checking for regressions
and for claims the code does not support. The findings below were
confirmed against the checkout, not inherited from an earlier review.

### Defects found and fixed

- **The Windows GPU memory counters were summed across adapters, and the
  capacity was taken from the first adapter.** On the test machine the
  counters reported three LUID instances and two video controllers, so the
  old code would have presented a machine-wide usage figure together with
  the *integrated* card's 1 GB capacity, under the discrete card's name.
  `GpuMemoryFallbackPolicy` now uses a figure only when the counters
  described exactly one adapter, and discards a capacity that is smaller
  than the usage.

- **The scalar collectors always reported `Available`.** A collector that
  had never succeeded — or that had failed — reported the mutable model's
  initial value, zero, as a measurement. `ScalarCollectorState` now
  distinguishes a measured zero from an unavailable figure, and stamps a
  carried-over value with the time it was *read*.

- **The network collector fabricated a zero on its first cycle.** A rate
  needs two samples; the first cycle now reports no value rather than a
  zero that was never measured.

- **`Measurement.AvailableValue` stamped the construction time.** A value
  that was read earlier and published later was shown as having been
  updated "just now". The read time is now passed in explicitly.

- **Two different "primary GPU" orderings existed.** The collection chose
  the device whose sensors it measured; the widget chose the device it
  presented. When they disagreed, one card's figures were presented under
  another's name. Both now delegate to `GpuSelectionPolicy`, and
  `LhmSnapshotMapperTests` asserts they agree for every permutation of the
  device list.

- **The legacy presenter borrowed another card's reading**, and could pair
  one card's usage with another card's capacity. It now returns nothing
  rather than attributing another device's figure.

- **The NVIDIA fallback could displace the backend.** The fallback's
  figures were merged over the backend's, so a reading for a *different*
  card could win the merge. The precedence is now fixed and identical for
  every metric (`ScalarMergePolicy`): backend, then the reconciled
  fallback, then the unattributed Windows counters.

- **The used and total memory figures shared one attribution id.** They
  can come from different sources, so the snapshot now carries them
  separately (`GpuVramUsedDeviceId`, `GpuVramTotalDeviceId`) and the
  presenter checks each figure against the device being presented.

- **The success-path output drain was unbounded.** A grandchild that
  inherited the pipe handles could hold the collection open indefinitely.
  The drain is now bounded by a one-second grace period.

- **A failed kill was swallowed.** The runner now reports whether the
  child was actually terminated, so a surviving child — which would
  accumulate one process per cycle — is visible in the log.

- **The runner ignored its `CancellationToken`.** Cancellation now kills
  the child so the wait ends immediately.

- **`EnsurePipelineStarted` was not atomic.** The collector was
  constructed outside the lock, so two callers could race and the
  pipeline could be built over a service the field no longer held.

- **`WaitForCompletion` could tear down a live widget.** It returned on
  the timeout regardless of whether the host had pinned a widget during
  the window. It now returns only when the list has stayed empty for a
  full inactivity window.

- **`Dispose()` disposed the shared sensor backend.** The pipeline and the
  backend are process-wide; disposing one provider instance closed the
  sensor source out from under a pipeline still running for another
  widget. `Dispose()` now releases nothing, and `ShutdownAsync()` owns the
  release.

- **The CPU temperature's source label was unreachable.** The
  `"MSAcpi_ThermalZoneTemperature"` branch named a source that could never
  supply the CPU temperature; the label now names the backend that does.

- **The ACPI thermal-zone reading had no status.** It was published as a
  bare value with no way to distinguish "not collected" from "read as
  zero". It now goes through the same state as every other metric, and
  reports the source it attempted even when the query throws.

### Confirmed clean

- **No manufacturer-specific assumptions** in `Models/HardwareModels.cs`,
  `Models/SystemMetrics.cs`, `WidgetPayloadPresenter.cs`,
  `LegacyPayloadPresenter.cs` or `GpuSelectionPolicy.cs`. The vendor names
  that do appear are in the *fallback* and in the diagnostic labels, both
  of which are per-source rather than per-model.
- **No automatic elevation.** `app.manifest` carries no
  `requestedExecutionLevel`, and the package capabilities are `runFullTrust`
  plus the widget sizes. A sensor that needs elevation is reported as
  `Failed`.
- **No hardware control.** Nothing writes to the hardware: no clock or
  voltage writes, no fan control, no driver installation.
- **No COM or package identity change.** The CLSID
  `f72c45b3-9fa6-4155-82de-ab4ccf67cdb8` and the identity
  `com.test.systemmonitor` / `CN=Developer` are unchanged.
- **No private keys or newly tracked generated output.**
- **One polling loop.** The single `UpdatePipeline` coordinator admits a
  cycle only when the previous one has unwound; the tests assert
  `MaxConcurrent == 1`.

### Larger changes reported, not implemented

These are architectural, and are out of scope for a defect-fix pass:

- **The Windows WMI queries are not bounded.** No
  `ManagementObjectSearcher` sets `Options.Timeout`, so a hung WMI
  provider could block a collection indefinitely. The same bound the
  subprocess runner uses would apply.
- **The Windows GPU memory counters cannot be attributed to a device.**
  Making them attributable would require correlating the counters' LUIDs
  with the devices the backend reports — a collector-design change.
- **The mutable `SystemMetrics` model still uses non-nullable
  primitives** for the system-level figures. It cannot express the
  difference between a measured zero and an unavailable figure, which is
  why `ScalarCollectorState` exists alongside it. Retiring the mutable
  model would remove the duplication.

### Hardware validation

See `docs/hardware-validation.md`. It records what was tested on real
hardware and — explicitly — which configurations are **not tested**.

### Suggested commit message — refactor review (when ready to commit)

```
Fix cross-device attribution, fabricated zeros, and unbounded waits

Review the refactor against docs/refactor-plan.md and fix the defects
the review confirmed:

- Stop summing the Windows GPU memory counters across adapters and
  taking the capacity from the first adapter. Use a figure only when
  the counters described exactly one adapter.
- Distinguish a measured zero from an unavailable figure for every
  scalar collector, and stamp a carried-over value with the time it
  was read rather than the time the payload was built.
- Report no network rate on the first cycle instead of a fabricated
  zero.
- Use one primary-GPU ordering for both the collection and the
  presentation, so a figure can no longer be presented under another
  card's name.
- Fix the merge precedence: the device-identified backend wins, the
  reconciled NVIDIA fallback fills in, and the unattributed Windows
  counters are never presented for a card.
- Attribute the used and total GPU memory figures separately, since
  they can come from different sources.
- Stop the legacy presenter borrowing another device's reading.
- Bound the subprocess runner's output drain, honour its cancellation
  token, and report whether the child was actually terminated.
- Make pipeline startup atomic, keep WaitForCompletion from tearing
  down a widget pinned during its window, and stop Dispose from
  releasing the process-wide sensor backend.
- Give the ACPI thermal-zone reading a status and a source.

Add docs/hardware-validation.md, separating automated fixture coverage
from real-hardware testing and listing the configurations that are not
tested.

Co-Authored-By: Claude Code <noreply@anthropic.com>
```

---

## Payload-path audit (2026-10-21)

A trace of every production payload path from collection to presentation,
checking three questions: does each displayed scalar's availability come
from an explicit metadata source rather than from a number in
`SystemMetrics`; does any path present a default zero as a measurement; and
does any path lose status or timestamps.

The production path is
`SystemMetricsService.Collect()` → `MetricsSnapshot` →
`WidgetPayloadPresenter.Present()` → `WidgetPayload.ToJson()` →
`WidgetManager.UpdateWidget`. **`Models/SystemMetrics.cs` is not on it.**
The model is written every cycle and read by nobody outside the collector;
it is a working view, not the published contract. No defect was
demonstrated in it, so it was **not** rewritten.

### Where each displayed scalar's availability comes from

Every entry below is a `Measurement` whose `Status` and `Value` come from
`ScalarCollectorState.ToMeasurement(...)` (or, for the per-device GPU
figures, from the device's own `DeviceReading.Measurement`). No entry reads
a number out of `SystemMetrics` to decide whether a figure is available.

| Payload key | Availability source | Status when unavailable | A measured zero |
|---|---|---|---|
| `cpu` | `cpu-load` state → `snapshot.CpuLoadPercent` | "N/A" (`Failed`) or "stale" (`Stale`) | "0.0%" |
| `cpuClock` | `cpu-clock` state → `snapshot.CpuClockMHz` | "N/A" or "stale" | "0 MHz" |
| `cpuTemp` | `cpu-temperature` state → `snapshot.CpuTemperatureC` | "N/A" or "stale" | "0.0°C" |
| `gpu` | per-device reading, else the `gpu-load` scalar **when its device id matches** | "N/A" or "stale" | "0.0%" |
| `gpuTemp` | per-device reading, else the `gpu-temperature` scalar **when its device id matches** | "N/A" or "stale" | "0.0°C" |
| `gpuVram` / `gpuVramUsed` | per-device reading, else the `gpu-vram-used` / `gpu-vram-total` scalars **when their device ids match** | "N/A" or "stale" | "0 B" |
| `ram` / `ramUsed` / `ramTotal` / `ramPercent` | `ram-used` / `ram-total` / `ram-percent` states | "N/A" or "stale" | "0 B" / "0.0%" |
| `disk` / `diskUsed` / `diskTotal` / `diskPercent` | `disk-used` / `disk-total` / `disk-percent` states | "N/A" or "stale" | "0 B" / "0.0%" |
| `netDown` / `netUp` | `network-down` / `network-up` states | "N/A" or "stale" | "0 B/s" |
| `tempSummary` | composed from the two temperature measurements | "N/A" | — |
| `acpiThermalZoneTemp` | `acpi-thermal-zone-temperature` state → `snapshot.AcpiThermalZoneTemperatureC` | "N/A" or "stale" | "0.0°C" |
| `sampleTime` / `lastUpdated` | the most recent **successful** read time across every measurement | "N/A" | — |

**No measured zero is treated as unavailable anywhere on the production
path.** A measured 0 % is presented as "0.0%", and the collector state
records it as `Available`. (`ram` and `disk` are the compound
"used / total (percent)" form; when the percentage is unavailable the used
figure alone is shown, and when *that* is unavailable the key reads
"N/A".)

### Defects found and fixed

- **The ACPI thermal-zone reading was never presented.** The collector
  published it under the metric name `acpi-thermal-zone-temperature`, but
  both presenters looked for a *device reading* named
  `AcpiThermalZoneTemperatureC` — a name the collector never produces. The
  `acpiThermalZoneTemp` payload key was therefore **always "N/A"**, even
  on a successful read. The status-bearing value already existed at
  `snapshot.AcpiThermalZoneTemperatureC`; the presenters now read it from
  there. (No template binds the key, so there was no visible widget
  regression — it was a dead payload key that was also wrong.)

- **The nvidia-smi fallback's utilization was parsed, reconciled and then
  dropped.** `NvidiaFallbackMetrics.UtilizationPercent` was selected but
  never consumed by `SystemMetricsService`, which contradicts finding #13
  ("where the backend cannot supply it, the reconciled nvidia-smi fallback
  supplies it"). On a machine where the backend cannot read GPU load, the
  widget showed "N/A" even though nvidia-smi had supplied a reconciled
  figure. The fallback's utilization is now merged as a fallback candidate
  for `gpu-load`, carrying the device it reconciled to. The invocation
  policy now takes the backend's GPU load into account, so the command line
  is skipped only when the backend supplied the load, the temperature *and*
  both VRAM figures.

- **A failed GPU figure named the source that was not tried.** The
  "attempted source" recorded for an unavailable GPU load, temperature or
  VRAM figure named one source, while two or three had actually been
  consulted. The labels now name every source that was attempted.

### Remaining limitations

These are precise and unresolved. None of them is a demonstrated
production-path defect.

1. **`ClampPercent` maps a non-finite percentage to `0.0`.** A NaN or
   infinite value becomes a *measured* zero and is reported as `Available`.
   Every current call site feeds it a value derived from a finite ratio, so
   this is latent rather than reachable today — but the rule itself is
   "zero stands in for a non-measurement", which is the thing the collector
   state exists to prevent. `UpdateRam` and `UpdateDisk` compute
   `used / total` and pass the result through it; a `total` of zero would
   produce exactly this NaN.

2. **A failed metric keeps its last device id.** `ScalarCollectorState.
   ReportNoValue` sets the no-value flag but does not clear the entry's
   device id, so `DeviceIdFor` still names a device for a figure that
   currently carries no value. The presenter's per-figure device check
   prevents a wrong-device display, so this is a provenance imprecision in
   the snapshot rather than a mis-display.

3. **The legacy presenter loses status and timestamps.** It renders a
   `Stale` measurement as "N/A" (the production presenter shows "stale"),
   stamps `lastUpdated` with the snapshot's collection timestamp rather
   than the most recent successful read, and never sets `gpuDetailSmall`.
   **It is not on any production path** — no widget is affected. It is
   retained because the tests pin the legacy contract.

4. **Several payload keys are produced but bound by no template**:
   `cpuVendor`, `gpuName`, `gpuVendor`, `gpuIntegration`, `gpuExtras`,
   `ramUsed`, `ramTotal`, `ramCombined`, `ramPercent`, `diskUsed`,
   `diskTotal`, `diskCombined`, `diskPercent`, `tempSummary`,
   `acpiThermalZoneTemp`, `lastUpdated`, and the flags `gpuHasExtra` and
   `sampleIsStale`. The GPU clock is projected as a per-device reading but
   is never surfaced either. These are not defects — a payload that
   carries more than the template needs is harmless — but they are dead
   weight, and `sampleIsStale` in particular is a status a template could
   use to show that a figure is out of date.

5. **The mutable `SystemMetrics` model still uses non-nullable primitives**
   for the system-level figures. It cannot express the difference between a
   measured zero and an unavailable figure. It is not read on the
   production path, so this is duplication rather than a defect, and
   retiring it is an architectural change.

6. **`_lastSnapshot` is a static non-volatile field** written by the
   pipeline thread and read by the COM callback threads. The snapshot is
   immutable and the reference assignment is atomic, so the race is benign,
   but it is not formally synchronized.

7. **The nvidia-smi fallback cannot help when the backend reports no
   devices.** The fallback's readings are reconciled to a backend device by
   PCI device key, so it needs at least one backend device to reconcile
   *against*. When the backend's `Computer.Open()` fails or its enumeration
   returns nothing, `UpdateSensorBackend` clears the snapshot and
   `CollectBackendGpuIdentities` returns empty, so the fallback is skipped
   and its readings are dropped. This is the intended consequence of never
   attributing a reading by position — but it means the fallback covers
   "the backend found the card but could not read one of its sensors", not
   "the backend could not find the card at all". A backend *read* failure
   (as opposed to an empty enumeration) does leave the previous
   identities in place, so the fallback can still run in that case.

### Suggested commit message — payload-path audit (when ready to commit)

```
Fix three payload-path defects found by a collection-to-presentation audit

Trace every production payload path from collection to presentation and
check that each displayed scalar's availability comes from an explicit
metadata source rather than from a number in the mutable model.

- Present the ACPI thermal-zone reading. The collector publishes it as a
  scalar measurement, but both presenters looked for a device reading
  under a name the collector never produces, so the key was always N/A
  even on a successful read. Read the status-bearing value from the
  snapshot instead.
- Consume the nvidia-smi fallback's utilization. It was parsed,
  reconciled to a device and then dropped, so a machine whose backend
  could not read GPU load showed N/A even though the fallback had a
  figure. Merge it as a fallback candidate carrying the device it
  reconciled to, and take the backend's load into account in the
  invocation policy.
- Name every source that was actually attempted in a failed GPU figure's
  source label, rather than one of the two or three that were tried.
- Pin the behaviour with tests: the reconciled nvidia-smi utilization is
  presented for the card it reconciled to and for no other card, and a
  measured zero utilization is presented as "0.0%" rather than as "N/A".

The mutable SystemMetrics model is not on the production path: it is
written every cycle and read by nobody outside the collector. No defect
was demonstrated in it, so it is not rewritten.

Correct the overbroad claims in docs/refactor-plan.md and
docs/hardware-validation.md: the series buffers, the GpuService methods
and the update-series helper do not exist; the legacy presenter is not
the widget's presenter and loses status and timestamps; the ACPI and
GPU-load fallback paths are described as they now are; the baseline
record reads 320 tests.

Co-Authored-By: Claude Code <noreply@anthropic.com>
```

---

## Combined GPU-memory compatibility (2026-10-21)

A verification of every combined GPU-memory display and percentage: each one
must be supported by compatible **device attribution** and compatible
**memory semantics** for its used and total figures.

### The displays that were checked

| Display | Path | Status |
|---|---|---|
| `gpuVram` — "used / total (percent)" | `WidgetPayloadPresenter` → `GpuMemoryPresentation.Resolve` → `text["gpuVram"]`, bound by the Medium and Large templates | **production** |
| `gpuVramUsed` — the used amount alone | `WidgetPayloadPresenter.FormatBytesValue(gpuVramUsed)` → `text["gpuVramUsed"]`, bound by the Small template | **production** |
| `gpuVramLabel` / `gpuVramLabelCompact` — the heading | `GpuMemoryPresentation.LabelText` / `CompactLabelText` → `text["gpuVramLabel"]` / `text["gpuVramLabelCompact"]`, bound by the Medium, Large and Small templates | **production** — see "GPU-memory labels" below |
| `gpuVram` — "used / total (percent)" | `LegacyPayloadPresenter` → `GpuMemoryPresentation.Resolve` → `payload["gpuVram"]` | not on any production path — the widget is served by `WidgetPayloadPresenter` |
| `ram`, `disk` — "used / total (percent)" | `WidgetPayloadPresenter.FormatUsage` / `LegacyPayloadPresenter.FormatUsage` | **production**, but system-level: there is no device to attribute to and no two pools to reconcile |

`ByteFormatter.FormatUsage(double, double, double)` is now unused by both
presenters; the presenters format the triple themselves so they can apply
the coherence guards.

### The rule

`Services/GpuMemoryCompatibilityPolicy.cs` (new) decides whether a used
figure and a total figure may be presented as one ratio. It applies three
independent checks.

**1. Attribution.** Both figures must name a device, they must name the
same one, and that one must be the device being presented. A figure that
names no device describes no particular card, so it cannot be part of a
ratio for any card.

**2. Memory semantics.** The two figures must be shown to describe one
memory pool. The rule requires *positive evidence* rather than the absence
of a contradiction:

- When both figures name a pool, they must name the same one. A name in the
  backend's dedicated-memory families ("GPU Memory", "D3D Dedicated Memory",
  "VRAM") means dedicated video memory; a name that marks the sensor as
  shared means shared system memory, which is not VRAM and never pairs with
  anything, including itself. The vendor-neutral fallback reports the card's
  own dedicated video memory.
- When a figure's name does not say which pool it describes — the backend's
  bare "Memory Used" / "Memory Total" — the *family* is the evidence. Two
  figures named from the same memory-sensor family on the same device are
  two figures of one pool, whatever that pool is. Two figures from
  *different* families are not paired: "GPU Memory Total" and "Memory
  Total" are two families, and nothing says they describe the same pool.

**3. Coherence.** A capacity of zero cannot produce a ratio, and a usage
above the capacity proves the two numbers describe different pools.

**A display name is never evidence of anything.** The policy is never given
a display name: it is decided on device ids and on each figure's own
provenance, and on nothing else. Two identical cards have identical names
and are different devices, so one card's usage is never paired with the
other's capacity.

### Defects found and fixed

- **`FormatVram` had no joint device check.** The used and total figures
  were resolved independently, so a figure from the presented device's own
  reading could be paired with a scalar from a different lineage. The
  presenter now resolves each figure to a `ResolvedMetric` that carries the
  device the figure describes, and the policy requires both ids to equal the
  presented device.

- **`FormatVram` had no memory-semantics check.** A dedicated-VRAM used
  figure could be paired with a shared-memory total, and the ratio was
  presented as a usage percentage. The compatibility policy now governs
  the pairing.

- **`FormatVram` had no `used > total` guard** (unlike
  `GpuMemoryFallbackPolicy.Resolve`, which does have one). A usage above the
  capacity was presented as a percentage above 100.

- **The scalar merge threw the sensor name away.** The collector published
  the backend's memory figures under the bare `"LibreHardwareMonitor"`
  label, so the presenter had no way to tell which memory pool a figure
  described. `SystemMetricsService.SensorCandidate` now keeps the sensor's
  own name in the figure's provenance
  (`"LibreHardwareMonitor:GPU Memory Used"`), and the collector state
  records it.

- **RAM and disk could show a percentage beside a figure that was not
  there.** `FormatUsage` now falls back to the used amount alone when the
  total is unavailable, of zero, or below the usage, and shows
  "used / total" without a percentage when the percentage itself is
  unavailable. Both presenters were fixed.

### Remaining limitations

1. **The Windows GPU counters' used figure and capacity are never shown as
   a ratio, and could not honestly be.** Their two figures come from two
   different classes — the adapter-memory performance counter and the
   video-controller class — and neither LUID appears in the other class, so
   the pair cannot be correlated to each other. The only available
   cross-check is `total < used`, which `GpuMemoryFallbackPolicy.Resolve`
   applies. The comment in that class says the pair "is only meaningful
   when both described the same single adapter"; that overstates the
   correlation it performs. It should read: the pair is only *plausible*
   when both classes saw a single adapter, and it is *refuted* when the
   capacity is below the usage. The two counters' figures also name no
   device, so the compatibility policy would refuse to pair them with
   anything regardless.

2. **The backend's generic "Memory Used" / "Memory Total" names do not say
   which pool they describe.** They establish a family, which is enough to
   pair them with each other on one device, but not enough to tell a user
   that the figure is dedicated video memory rather than shared system
   memory. The candidate lists in `LhmSensorSelection` deliberately prefer
   the explicit dedicated-memory names, so this only arises on a card whose
   driver exposes nothing else.

3. **`GpuMemoryFallbackPolicy` is untouched.** Its two counter classes
   share no adapter identity, so "one adapter from each" does not prove
   they are the same adapter. Tightening it is not possible without a
   correlation the counters do not provide, so the `total < used` discard
   stands as the only available check.

### Tests

`SystemMonitor.Tests/GpuMemoryCompatibilityPolicyTests.cs` (new, 24 tests)
covers the policy directly, and `WidgetPayloadPresenterTests` gained a
"Combined GPU memory" section (12 tests) covering the production path, two
of which run the real backend mapping end to end rather than a hand-built
snapshot.

| Case | Policy test | Presenter test |
|---|---|---|
| Same device, different sources | `TheSameDeviceFromTwoSources_MayBeCombined` | `TheSameDeviceFromTwoSources_IsCombinedWhenBothNameTheSamePool` |
| Different devices | `TwoDifferentDevices_MayNotBeCombined`, `AFigureForAnotherDevice_MayNotBeCombinedWithThePresentedOne` | `MemoryFiguresForDifferentDevices_AreNotCombined` |
| One attributed, one unattributed | `AnUnattributedTotal_MayNotBeCombinedWithAnAttributedUsed`, `AnUnattributedUsed_MayNotBeCombinedWithAnAttributedTotal` | `AnUnattributedCapacity_IsNotCombinedWithAnAttributedUsage` |
| Dedicated versus shared | `DedicatedMemoryAndSharedMemory_MayNotBeCombined`, `SharedMemoryAndDedicatedMemory_MayNotBeCombined`, `TwoSharedMemoryFigures_MayNotBeCombined` | `DedicatedAndSharedMemory_AreNotCombined` |
| Missing or zero total | `AMissingTotal_MayNotBeCombined`, `AFailedTotal_MayNotBeCombined`, `AZeroTotal_MayNotBeCombined` | `AMissingCapacity_IsNotCombined`, `AZeroCapacity_IsNotCombined` |
| Matching display names | `AMatchingDeviceName_IsNeverEvidenceOfCompatibility` | `TwoCardsWithTheSameName_AreNotCombinedWithEachOther` |
| Usage above the capacity | `AUsageAboveTheCapacity_MayNotBeCombined` | — |
| Measured zero usage | `AMeasuredZeroUsage_WithARealCapacity_MayBeCombined` | — |
| Same family, unnamed pool | `TwoFiguresFromTheSameFamily_MayBeCombined` | `TwoFiguresFromTheSameFamily_AreCombined` |
| Different families | `TwoFiguresFromDifferentFamilies_MayNotBeCombined` | `TwoFiguresFromDifferentFamilies_AreNotCombined` |
| A figure with no family | `AFigureWithNoFamily_MayNotBeCombinedWithAnyOther` | — |
| End to end through the real mapping | — | `TheBackendFiguresForOneCard_AreCombinedIntoARatio`, `AnIntegratedPartWithOnlySharedMemory_ShowsNoCombinedFigure` |

### Files

**New:**
- `Services/GpuMemoryCompatibilityPolicy.cs` — `GpuMemorySpace` and the
  compatibility policy.
- `SystemMonitor.Tests/GpuMemoryCompatibilityPolicyTests.cs`.

**Modified:**
- `Services/LhmSensorSelection.cs` — the memory-sensor candidate lists are
  now expressed as families (`DedicatedMemoryFamilies`,
  `GenericMemoryFamily`, `MemorySensorQualifiers`), with
  `MemoryFamilyOf`, `IsSharedMemorySensor`, `IsDedicatedMemorySensor` and
  `IsGenericMemorySensor` deciding what a sensor name says.
- `Services/WidgetPayloadPresenter.cs` — `ResolveMetric` returns a
  `ResolvedMetric` carrying the resolved figure's device id; `FormatVram`
  consults the compatibility policy; `FormatUsage` gained the coherence
  guards.
- `Services/LegacyPayloadPresenter.cs` — the same `FormatVram` and
  `FormatUsage` rules.
- `Services/SystemMetricsService.cs` — `SensorCandidate` /
  `ReadMeasurement` keep the sensor's own name in the memory figures'
  provenance.
- `SystemMonitor.Tests/WidgetPayloadPresenterTests.cs` — the "Combined GPU
  memory" section.
- `SystemMonitor.Tests/SystemMonitor.Tests.csproj` — links the new policy
  file.

### Suggested commit message — combined GPU-memory compatibility (when ready to commit)

```
Require compatible attribution and memory semantics for combined VRAM

A "used / total (percent)" figure is a claim that the two numbers describe
the same memory pool of the same device. Nothing on the production path
checked that claim: the used and total figures were resolved independently,
so a figure from the presented device's own reading could be paired with a
scalar from a different lineage; a dedicated-VRAM used figure could be
paired with a shared-memory total; and a usage above the capacity was
presented as a percentage above 100.

Add Services/GpuMemoryCompatibilityPolicy.cs, which decides whether the two
figures may be shown as one ratio. It requires both figures to name the
same device and that device to be the one being presented; requires the two
figures to be shown to describe one memory pool, by an established pool or
by a common memory-sensor family; and refuses a capacity of zero or a
usage above it. The policy is never given a display name, so a matching
name is never evidence of compatibility.

- Resolve each GPU memory figure to the device it describes, so the used
  and total figures are checked against each other and against the
  presented device.
- Keep the sensor's own name in the memory figures' provenance. The bare
  backend label threw away the only evidence of which pool a figure
  described, and without it a dedicated figure could be paired with a
  shared one.
- Omit the ratio when the figures are incompatible, and show the used
  amount alone. The RAM and disk triples get the same coherence guards: no
  percentage beside a figure that is not there, no percentage over a
  capacity of zero, and none over a capacity below the usage.
- Pin the behaviour with 24 policy tests and 12 presenter tests covering
  the same device from two sources, different devices, an attributed figure
  paired with an unattributed one, dedicated versus shared memory, a
  missing or zero capacity, and two cards with identical display names.

The Windows GPU counters' two figures are not covered by a ratio and
cannot be: their classes share no adapter identity, so the pair is only
refutable, never confirmable.

Co-Authored-By: Claude Code <noreply@anthropic.com>
```

---

## GPU-memory labels (2026-10-21)

### The defect

The compatibility work made the *figures* honest. The *heading* above them
was not: the Medium template hard-coded `"VRAM"` and the Large template
hard-coded `"Dedicated VRAM"` as static text.

Both were static Adaptive Card literals, so they were on the production path
and every figure was shown under them regardless of what the figure was. A
generic "Memory Used" / "Memory Total" pair — which the compatibility policy
*accepts*, because the two figures share a family and so describe one pool —
was therefore headed "Dedicated VRAM". So was a figure that could not be read
at all.

The defect was the same class as presenting a default zero as a measurement:
a plausible-looking claim that the collection never established.

### The rule

A heading is a claim about the figure beneath it, and the widget must make
the claim its data supports and no more.

**Compatibility and classification are separate decisions.** The
compatibility policy answers *may these two figures be presented as one
ratio*. The classification answers *which memory pool is that ratio a ratio
of*. They are answered separately, and deliberately:

- A generic "Memory Used" / "Memory Total" pair **may** be combined — the
  two figures are of one family, so they are of one pool, whatever that pool
  is. But their name says nothing about which pool it is, so the pair is
  **not** classified as dedicated. Accepting a pairing never upgrades a
  figure to "dedicated".
- A pair whose two figures both name dedicated video memory is combined
  **and** classified as dedicated.
- A pair whose two figures name different pools is not combined at all, and
  the used figure is shown alone.

**The label is resolved with the figure, never independently of it.**
`Services/GpuMemoryPresentation.cs` resolves the display and its heading
together:

| Case | Display | Heading |
|---|---|---|
| Pair accepted, pool established as dedicated | "used / total (pct)" | "Dedicated GPU memory" |
| Pair accepted, pool established as shared | "used / total (pct)" | "Shared GPU memory" |
| Pair accepted, pool not established (generic family) | "used / total (pct)" | "GPU memory" |
| Pair rejected | the used figure alone | the **used figure's** label |
| No usable figure | "N/A" | the presented device's own category, else "GPU memory" |

One label value serves both displays. When the pair is accepted it describes
one pool, so its used figure describes that same pool; when the pair is
rejected the heading comes from the used figure alone, and a rejected total
contributes nothing. **A heading never borrows semantics from a figure that
was not presented.**

The unavailable case substitutes no figure and no zero: the display is
"N/A", and the heading names the memory category only when the *presented
device's own* figures establish it — a fact about the card, not a borrowing
from a rejected pair.

**The heading text is payload-driven.** Two keys carry the same semantics in
two lengths, so the tight Small layout can use the shorter one without
changing the claim:

| Key | Text |
|---|---|
| `gpuVramLabel` | "Dedicated GPU memory" / "Shared GPU memory" / "GPU memory" |
| `gpuVramLabelCompact` | "Dedicated memory" / "Shared memory" / "GPU memory" |

The compact form drops the word "GPU" — the figure is already in the GPU
section of the layout — but never the claim about the pool. The neutral
"GPU memory" is identical in both, because it is already short.

**No production template carries a static memory heading.** Small binds
`${gpuVramLabelCompact}`; Medium and Large bind `${gpuVramLabel}`. No
template anywhere still says "VRAM" or "Dedicated VRAM" as static text.

RAM and disk labels are untouched by this work.

### Files

**New:**
- `Services/GpuMemoryPresentation.cs` — `GpuMemoryDisplay`, the label
  constants, `LabelText` / `CompactLabelText`, and `Resolve`.
- `SystemMonitor.Tests/GpuMemoryLabelTests.cs` — 30 tests.

**Modified:**
- `Services/GpuMemoryCompatibilityPolicy.cs` — added `EstablishedLabelOf`
  (a figure's own label, or null when it names no pool) and
  `DeviceCategoryOf` (the presented device's own memory category). The
  existing `LabelOf`, `CombinedLabelOf`, `SpaceOf`, `FamilyOf`,
  `FiguresAreCompatible` and `CanCombine` are unchanged.
- `Services/WidgetPayloadPresenter.cs` — the payload now carries
  `gpuVramLabel` and `gpuVramLabelCompact`, resolved together with the
  figure.
- `Services/LegacyPayloadPresenter.cs` — the legacy contract uses the same
  resolver, so it cannot drift from the production path.
- `Services/WidgetTemplates.cs` — Small binds the compact label; Medium and
  Large bind the full label. The static "VRAM" / "Dedicated VRAM" headings
  are gone.
- `SystemMonitor.Tests/SystemMonitor.Tests.csproj` — links the new file.

### Tests

`GpuMemoryLabelTests` (30 tests) covers the policy end to end:

| Case | Test |
|---|---|
| Confirmed dedicated pair → dedicated heading | `AConfirmedDedicatedPair_IsHeadedAsDedicatedGpuMemory`, `AConfirmedDedicatedPair_IsHeadedAsDedicatedMemoryInThePayload` |
| Confirmed shared figure → shared heading | `AConfirmedSharedFigure_IsHeadedAsSharedGpuMemory`, `AConfirmedSharedFigure_IsHeadedAsSharedMemoryInThePayload` |
| Generic family pair → combined but neutral | `AGenericFamilyPair_IsCombinedButNeverHeadedAsDedicatedVram`, `AGenericFamilyPair_IsHeadedNeutrallyInThePayload` |
| Conflicting pair → rejected, used figure's own label | `AConflictingPair_IsRejectedAndTheUsedFiguresOwnLabelStands`, `AConflictingPair_DoesNotBorrowTheRejectedTotalFiguresSemantics`, `AConflictingPair_InThePayloadShowsTheUsedFiguresOwnLabel` |
| Unknown semantics → neutral | `AUsedFigureWithUnknownSemantics_IsHeadedNeutrally`, `AFigureWithNoProvenanceAtAll_IsHeadedNeutrally` |
| Attribution protections intact | `FiguresForDifferentDevices_AreNotCombined`, `AnUnattributedTotal_IsNotCombinedWithAnAttributedUsed`, `AnotherDevicesDedicatedFigure_ContributesNothingToTheHeading` |
| Unavailable → no figure, no zero | `AnUnavailableFigure_ShowsNoFigureAndSubstitutesNoZero`, `AnUnavailableFigure_OnADeviceWithNoEstablishedCategory_KeepsTheNeutralHeading`, `AnUnavailableFigure_OnADeviceWhoseOwnFigureEstablishesTheCategory_KeepsIt` |
| Switching devices updates label and values together | `SwitchingDevices_UpdatesTheLabelAndTheValuesTogether`, `SwitchingFromADedicatedCardToAGenericOne_LeavesNoDedicatedHeadingBehind` |
| Every production size binds the label | `EveryProductionSize_BindsAMemoryLabel`, `EveryProductionSize_RendersTheLabelFromThePayload` |
| No contradictory static heading | `EveryProductionSize_CarriesNoStaticVramHeading` |

`HardwareModelsTests.LegacyPayloadPresenter_PopulatesLegacyKeys` also pins
the legacy contract's heading.

### Remaining limitations

1. **Label fit in the live widget host is awaiting manual confirmation.** The
   heading was made layout-appropriate by construction — the Small layout
   takes the compact form, and both forms carry `wrap` — but the widget was
   not inspected in the host. "Dedicated GPU memory" is the longest heading
   the Medium and Large layouts can show, and whether it fits their column
   widths without overlapping the neighbouring columns has not been observed.
2. **The generic-family heading is neutral by construction, not by
   knowledge.** "GPU memory" is what the widget can honestly say about a
   "Memory Used" / "Memory Total" pair. It is not a claim that the pool is
   shared, and it is not a claim that it is dedicated.

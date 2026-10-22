# Hardware validation

This document records what has actually been verified, and — just as
importantly — what has **not**.

The distinction the document maintains throughout:

- **Fixture coverage** — an automated test exercises the code path against
  a synthetic input. It proves the *logic* behaves correctly for that
  input. It proves nothing about any real machine.
- **Hardware validation** — the code was run on real hardware and the
  result was observed. This is the only kind of evidence that a machine
  configuration is *supported*.

A configuration with no hardware validation is **not tested**. It is
never described as "supported and verified", and it is never inferred to
work from the fact that a similar configuration was tested.

---

## 1. Test machine inventory

One machine was available for real-hardware validation.

| Property | Value |
|---|---|
| CPU | Intel Core i7-10875H (Comet Lake, mobile) |
| Discrete GPU | NVIDIA GeForce RTX 2070 Super (Max-Q Design) |
| Integrated GPU | Intel(R) UHD Graphics (VEN_8086&DEV_9BC4) |
| OS | Microsoft Windows NT 10.0.26200.0 (Windows 11 Home) |
| Runtime | .NET 8.0.32, x64 |
| Session | Standard user (no elevation requested, none granted) |

This machine covers two of the configurations in section 3:
**Intel CPU + Intel GPU** (partially — the integrated part) and
**Intel integrated GPU + NVIDIA GPU** (fully).

The last three device-level readings confirm the machine's identity and
were taken from the diagnostic inventory export
(`SystemMonitor.exe --export-diagnostics <path>`), which runs the real
collection path end to end.

### 1.1 Live-host environment

The Windows Widgets host was available on this machine, so the widget
itself was pinned, rendered and driven. The environment in which the
observations in §2.6, §2.7, §2.9 and §2.11 were made:

| Property | Value |
|---|---|
| Widgets host runtime | `Microsoft.WidgetsPlatformRuntime` 1.6.19.0 |
| Board process | `WidgetBoard` (the process that owns the card's chrome) |
| Provider process | `SystemMonitor` (the out-of-process COM server the host activates) |
| Board | `ms-widgetboard:` flyout, opened from the shell |
| Display scaling | 96 DPI (100% — no scaling) |
| Virtual screen | 3000 × 1920, origin (−1080, −144); the board was on the secondary portrait display |
| Card sizes the host offered | Small, Medium, Large (the host's own "More options" menu) |

The card was driven entirely through UI Automation — the same tree the
host exposes to assistive technology — plus synthetic mouse input for
the menu items, which do not expose an invoke pattern. The geometry
reported below is therefore the host's own layout, not an inference
from the template.

---

## 2. Real-hardware results

### 2.1 Device detection and sensor selection

Observed from the inventory export on the test machine:

| Device | Backend id | Integration | Load | Temperature | VRAM used | VRAM total |
|---|---|---|---|---|---|---|
| NVIDIA RTX 2070 Super | `/gpu-nvidia/0` | Unknown (library does not expose it) | 17% | 62 °C | 1 308 622 848 B | 8 589 934 592 B |
| Intel UHD Graphics | `/gpu-intel-integrated/…` | Integrated | 0% (`D3D 3D`) | Unsupported | Unsupported | Unsupported |
| Intel Core i7-10875H | `/intelcpu/0` | — | 27.7% | **Failed** | — | — |

What this confirms on real hardware:

- The vendor-neutral backend reports both an NVIDIA and an Intel device,
  each with its own identity, and the Intel device is correctly classified
  as integrated while the NVIDIA device's class is left **Unknown** rather
  than guessed.
- The Intel card's `D3D 3D` sensor is selected for load, and its
  temperature/VRAM sensors are reported as **Unsupported** — "no such
  sensor" — rather than as zero.
- The NVIDIA card's core sensors are selected for load, temperature and
  dedicated memory.
- The CPU **temperature** read **Failed** on this machine. Reading the
  MSR-based CPU package temperature requires a kernel-mode driver that the
  widget does not request, because the widget deliberately does not
  elevate. The failure is surfaced as `Failed`, with no value, rather than
  as a zero or a substitute reading.
- The CPU **clock** was read successfully through `Win32_Processor`
  (2304 MHz), because that class does not require elevation.

### 2.2 Unavailable and blocked sensor access

The test machine is itself the blocked-sensor case: the CPU temperature
is unreadable without elevation, and the widget does not request it.

Observed behaviour after the fixes in this task:

| Metric | Status before the fixes | Status after the fixes |
|---|---|---|
| `cpu_temperature_c` | value absent, status `Failed` | `Failed`, source `LibreHardwareMonitor` |
| `acpi_thermal_zone_temperature_c` | `Failed`, **empty source** | `Failed`, source `MSAcpi_ThermalZoneTemperature` |
| `network_down/up_bytes_per_sec` | `Available`, value `0.00` | `Failed` on the first cycle, then `Available` with a measured value |
| `cpu_load_percent` | `Available 0.00` on a cycle with no elapsed time | `Failed` on a cycle with no elapsed time, `Available` with a measured value afterwards |

The network figures are worth spelling out, because they were the clearest
fabricated-zero defect. The network collector computes a *rate*, and a
rate needs two samples. Before the fixes the collector emitted `0` for
the first cycle — a number that was never measured, presented as though
it were. It now reports no value on the first cycle and a measured rate
from the second cycle onwards.

### 2.3 Cross-device memory attribution

The Windows GPU memory counters were queried directly on the test machine
to check the cross-adapter behaviour:

- `Win32_PerfFormattedData_GPUPerformanceCounters_GPUAdapterMemory`
  reported **three** LUID instances, with dedicated usage of 968 482 816,
  0 and 0 bytes.
- `Win32_VideoController` reported two adapters: the Intel UHD with
  `AdapterRAM` 1 073 741 824 (1 GB) and the NVIDIA RTX 2070 Super with
  `AdapterRAM` 4 293 918 720 (capped at 4 GB by the field's `uint` type).

Before the fixes, the collector summed the three usage figures and took
the *first* adapter's capacity, which would have presented a combined
machine-wide figure and the **integrated card's 1 GB capacity** as the
discrete card's. After the fixes:

- usage figures are never summed across adapters;
- a capacity is never taken from "the first" adapter;
- when more than one adapter is reported, the figure is **not attributed
  to any device**, so the widget will not present it under a card's name;
- a capacity smaller than the usage is discarded, because it must have
  come from a different adapter.

The device-identified backend does read the NVIDIA card's own memory
figures (8 589 934 592 B total), so on this machine the widget shows the
card's real capacity rather than the counters' capped 4 GB figure.

The combined "used / total (percent)" figure is subject to a further
rule: the two figures must name the same device, that device must be the
one being presented, and the two must be shown to describe one memory
pool. On this machine both figures come from the NVIDIA card's own
dedicated-memory sensors, so the ratio is presented. The rule itself —
and the cases it refuses — are fixture-covered, not hardware-validated
(see §5.9).

**Pair compatibility and memory-pool classification are separate
decisions.** Accepting a "used / total" pairing says the two figures
describe one pool; it does not say which pool. A generic "Memory Used" /
"Memory Total" pair is therefore combined — the two figures share a
family, so they are of one pool — while the heading above it stays
neutral, because the sensor names say nothing about which pool they
describe. Only a pair whose two figures both name a pool is classified as
that pool.

The heading the widget shows is derived from that classification: a
confirmed dedicated pool reads "Dedicated GPU memory", a confirmed shared
pool reads "Shared GPU memory", and an unestablished pool reads "GPU
memory". A generic paired reading therefore never appears under a
heading that implies dedicated VRAM. On this machine the NVIDIA card's
figures come from its own dedicated-memory sensors, so the heading reads
"Dedicated GPU memory" — and that is what the host rendered on the
Medium and Large cards, in one line, without overlapping the
neighbouring columns (§2.9).

### 2.4 Source precedence observed

On the test machine the inventory reported these sources, which matches
the documented precedence exactly:

| Metric | Source | Reason |
|---|---|---|
| `gpu_load_percent` | `LibreHardwareMonitor` | the backend read it; it wins |
| `gpu_temperature_c` | `LibreHardwareMonitor` | the backend read it; it wins |
| `gpu_vram_used_bytes` | `LibreHardwareMonitor` | the backend read it; it wins |
| `gpu_vram_total_bytes` | `LibreHardwareMonitor` | the backend read it; it wins |
| `cpu_clock_mhz` | `Win32_Processor` | the backend could not read the clock; the fallback filled in |
| `cpu_temperature_c` | `LibreHardwareMonitor` | failed; the backend is the only source |
| `ram_*` | `GlobalMemoryStatusEx` | system collector |
| `disk_*` | `DriveInfo` | system collector |
| `network_*` | `NetworkInterface` | system collector |

The `nvidia-smi` fallback did **not** run on this machine, because the
backend supplied the load, the temperature and both memory figures. That is
the intended behaviour — the fallback runs only for a metric the backend
could not read.

> **Changed on 2026-10-21.** The invocation policy now also takes the GPU
> **load** into account: the fallback is skipped only when the backend
> supplied the load, the temperature *and* both memory figures. On the
> observed machine the backend supplied all four, so the behaviour on this
> machine is unchanged.

### 2.5 Missing NVIDIA executable

Not validated on real hardware: this machine *has* NVIDIA driver tools, and
the fallback was not invoked because the backend covered every metric it
could supply.

The behaviour is covered by automated fixture coverage instead:

- `NvidiaGpuFallbackTests` — the fallback returns nothing, without
  throwing, when the executable cannot be found; when the command fails;
  when it times out; when the output cannot be parsed.
- `NvidiaSmiLocatorTests` — the locator resolves the executable from
  `PATH`, from `Program Files\NVIDIA Corporation\NVSMI` and from
  `System32`; it survives a failed lookup without throwing; it stops
  re-probing after a failure.
- `SystemProcessRunnerTests.ReportsAMissingExecutableWithoutThrowing` —
  the real runner returns a failure result for a missing executable rather
  than throwing.

**Not tested on hardware.**

### 2.6 Widget lifecycle

**Validated on the live host.** The card was pinned, resized, removed,
re-added, duplicated and deactivated/reactivated in the real Widgets
board. The provider is an out-of-process COM server, so the host
activates it and the card is only rendered while the provider is alive.

The observable used for "is the widget still being polled" is the
provider process's **CPU time**, sampled over a window. The collection
runs every five seconds and each cycle reads the performance counters,
WMI, `nvidia-smi` and the LibreHardwareMonitor sensors, so an active
provider accrues CPU steadily and a stopped one does not. This is a
stronger observation than "the card stopped visibly changing", which
cannot distinguish a stopped pipeline from a quiet sensor.

| Scenario | How it was driven | Result | Evidence |
|---|---|---|---|
| Initial activation | Added the card from the host's own "Add widgets" picker | **Pass** — the card rendered a complete payload within seconds of being added, and the provider process started | Screenshot; the card's own text runs |
| Refresh on the interval | Left the card alone and read it three times over 16 s | **Pass** — the sample time advanced 1:47:09 → 1:47:25 and the RAM figure moved 87.4% → 87.7% without any user action | Sample time and figure readings |
| Resize preserves content | Changed the size through the host's "More options" menu (Small → Medium → Large) | **Pass** — the content was re-rendered in the new layout, and the figures were still live in each | Per-size geometry captures (§2.9) |
| Deactivate | Closed the board flyout (the host deactivates the widget when the board is not showing) | **Pass** — the provider's CPU rate fell from **0.44 CPU-s/s to 0.000 CPU-s/s** and stayed at zero | CPU-time sampling over 15 s and 20 s windows |
| Reactivate | Reopened the board with `ms-widgetboard:` | **Pass** — the provider's CPU rate returned to 0.44 CPU-s/s and the card resumed updating with fresh figures | CPU-time sampling; the card's sample time advanced |
| Remove | The card's own "More options" → "Remove" | **Pass** — the card left the board, and the provider process **exited** on its own within a few seconds | The process disappeared from the process list |
| Re-add | Added the card from the picker again | **Pass** — a fresh provider process started and the card rendered correctly in the default Medium size | Screenshot; per-size geometry |
| Multiple instances | Added the card twice, leaving both pinned | **Pass** — two cards rendered independently, each with its own live figures, served by one provider process | Screenshots of both cards |

Two observations about the boundary between "the card looks right" and
"the widget is still working" are worth recording, because the first is
easy to mistake for the second:

- **Closing the board is a deactivation, not a pause.** Polling stops
  completely, and resumes completely on reopen. This is visible only in
  the provider's CPU time; the card's rendered text is unchanged by it.
- **Removing the last card ends the provider process.** The process
  does not linger. This is the `WaitForCompletion` /
  `ShutdownAsync` path running for real.

**Resizing is a re-render, not a re-activation.** Changing the size
keeps the same provider and the same collection running; the host calls
`OnWidgetContextChanged`, which publishes the new layout. The content
is preserved, because the payload is built from the same snapshot.

Two lifecycle behaviours remain **not** observed on hardware, and are
recorded as gaps rather than as passes:

- **Device switch.** The per-device picker was removed from the widget
  in this task (see §2.11), so there is no longer any way to change the
  presented device from the widget. The registry's selection storage and
  the fallback policy are fixture-covered; the live switch is now
  unreachable and is not tested.
- **Stale data on the very first paint.** The first published payload is
  built from a snapshot that may have been collected before the card was
  rendered. On this machine the first paint was already populated, so
  the "empty first paint" path was not observed.

The lifecycle contract is also covered by automated tests of the pipeline
itself (`UpdatePipelineTests`, 14 tests), including:

- `DeactivateDuringCollection_QuiescenceWaitsForBlockedCollector`
- `DeactivateAsync_AfterSynchronousStop_StillAwaitsTheInFlightCollection`
- `DisposeAwaitsInFlightCollectionBeforeReturning`
- `LifecycleStress_NoLostWakeupsNoOverlapNoOrphanLoops`
- `ReactivateDuringOldCollection_NeverOverlapsAndSuppressesStalePublication`
- `WaitForIdleAsync_CompletesOnlyAfterPublishingHasFinished`
- `DeletingLastActiveWidgetWhileInactiveRemain_StopsPolling`

Those cover the collection/publish loop. The **COM activation** path
(`CreateWidget` / `Activate` / `Deactivate` / `DeleteWidget`) was
exercised on the live host as described above, but it remains uncovered
by automated tests, because the Windows App SDK's widget host cannot be
stood up in the test host.

**Validated on the live host** (activation, resize, refresh, deactivate,
reactivate, remove, re-add, multiple instances); **not validated on
hardware** (device switch, empty first paint).

### 2.7 Sleep / resume

**Partially validated on the live host.** The machine was suspended with
`rundll32.exe powrprof.dll,SetSuspendState 0,1,0` and then resumed,
with the card pinned and the provider running.

What was observed:

- The provider process **survived** the suspend and the resume: the
  same process id was still running afterwards, with its working set
  intact (105.2 MB before, 106 MB after).
- The card was still rendered after the resume, and it resumed
  updating: the figures moved and the sample time advanced.
- The provider's CPU rate returned to its normal active rate
  (0.43 CPU-s/s) after the resume, so the collection was running rather
  than the card showing a frozen last payload.

What was **not** observed, and is the part that matters: the transition
itself. The collection simply resumes with a fresh sample — nothing
detects that a gap occurred, so there is no "the widget woke up" event
and no explicit re-validation of the readings taken before the suspend.
The relevant behaviour — that a collection after a long gap reports its
readings as stale rather than as current — is covered by automated tests
(`ScalarCollectorStateTests.AFailedReadAfterASuccess_ProducesAStaleValue`,
`WidgetPayloadPresenterTests.AStaleSystemCollector_IsMarkedStale`), not
by the live observation.

**Partially validated on hardware** (the process survives, and the
collection resumes); the stale-reading behaviour is **fixture-covered**,
and the transition itself is **not detected** by the widget.

### 2.8 Device removal / driver reset

Not validated. No GPU was removed or had its driver reset on the test
machine.

The relevant behaviours are covered by automated fixture coverage:

- `WidgetPayloadPresenterTests.SelectedDevice_ThatDisappears_FallsBackToTheDefaultAndSaysSo`
- `WidgetPayloadPresenterTests.SelectedDevice_ThatDisappears_AndNoGpuRemains_ReportsNoDevice`
- `GpuSelectionPolicyTests.ASelectionThatIsGoneFallsBackToTheDefaultAndSaysSo`
- `LhmSnapshotMapperTests` — a device with no readable sensors is still
  reported as a device, with its metrics unsupported or failed.

**Not tested on hardware.**

### 2.9 GPU-memory label rendering

**Validated in the live widget host.** The card was rendered in all three
sizes the host offers, and the rendered text runs were measured with UI
Automation — their width, height and position — and captured as
screenshots.

The machine has one card whose memory figures come from its own
dedicated-memory sensors (the NVIDIA RTX 2070 Super), and one integrated
part with no memory sensors at all. So the **dedicated** heading and the
**unavailable** case were both observed live:

| Size | Label rendered | Geometry | Figure rendered | Geometry |
|---|---|---|---|---|
| Small | *(none — see below)* | — | `1.0 GB` | w=38 h=19, one line |
| Medium | `Dedicated GPU memory` | w=127 h=**16** — one line, no wrap | `1.0 GB / 8.0 GB (12.7%)` | w=137 h=19 — one line, fully visible |
| Large | `Dedicated GPU memory` | w=127 h=**16** — one line, no wrap | `1.0 GB / 8.0 GB (12.7%)` | w=141 h=19 — one line, fully visible |

The label **policy** is separately covered by fixture:

- a confirmed dedicated pair is headed "Dedicated GPU memory";
- a confirmed shared figure is headed "Shared GPU memory";
- a generic "Memory Used" / "Memory Total" pair is combined and headed
  "GPU memory" — never a heading implying dedicated VRAM;
- a pair whose two figures name different pools is rejected, and the used
  figure's own label stands;
- switching to another device updates the label and the values together,
  with no label retained from the previously selected device.

See `GpuMemoryLabelTests` and `GpuMemoryCompatibilityPolicyTests`.

**The shared and unspecified headings were not seen on a real card.** The
test machine has no device whose figures name a shared pool, and none
whose only figures are the generic "Memory Used" / "Memory Total" pair,
so "Shared GPU memory" and "GPU memory" remain fixture-covered only.

#### 2.9.1 Layout defects found and fixed in the live host

Four presentation defects were found by measuring the host's own
rendering. Each is recorded here with what was expected, what was
observed, and the boundary that was changed.

**D1 — Small clipped its disk row.** The Small card's body region
begins at y=282 and the card ends at y=390, so the body has 108 pixels.
The disk row was laid out at y=377..396 — six pixels past the card's
bottom edge, so it was cut off. *Fix:* the RAM row and the disk row were
merged into a single four-column row (`RAM  13.8 GB (87.1%)  Disk
607.4 GB (32.9%)`). *Re-verified:* the last row now ends at y=339,
well inside the card.

**D2 — Medium clipped its storage value entirely.** The Storage value
had a **present but empty** rectangle: the host laid it out and then
clipped it. *Fix:* the heading + "Used / Total" + value triples were
replaced with single-line compact figures. *Re-verified:* the storage
value now renders as `607.4 GB (32.9%)` at w=107 h=19, inside the card.

**D3 — the GPU-memory figure was ellipsized.** The host rendered it as
`1026048.0…`. Two independent causes:

  - *The unit ladder had no megabyte tier.* A figure of about one
    gigabyte was formatted in kilobytes — `1002496.0 KB`, twelve
    characters — because `FormatBytes` went from GB straight to KB.
    *Fix:* `ByteFormatter.FormatBytes` gained the MB tier, so the same
    figure is now `1.0 GB`. The same ladder was already used by
    `FormatNetworkSpeed`, and the two are now pinned together by a test
    so they cannot drift apart again.
  - *The memory column was one of three `stretch` columns*, so it got
    about a third of the card, and a combined "used / total (percent)"
    figure does not fit a third of 302 pixels. *Fix:* the Usage and Temp
    columns became `auto` (sized to their own content) and the memory
    column became the single `stretch` column, last in the row, with
    `wrap: true`.

*Re-verified:* `1.0 GB / 8.0 GB (12.7%)` renders on one line in Medium
(w=137) and Large (w=141), fully visible.

**D4 — "Dedicated GPU memory" wrapped to two lines.** The heading was
32 pixels tall in both Medium and Large, i.e. it wrapped. *Fix:* the
same column-width change as D3 gives the heading the rest of the row.
*Re-verified:* the heading is now 16 pixels tall — one line — in both
sizes.

**D5 — the action row never rendered, in any size.** Every template
carried a root-level `actions` array (a Refresh button and one button
per GPU). The host *does* render such a row — a probe build with a
one-word body showed the buttons as visible 32-pixel controls — but the
row is laid out **after** the card body, and the Widgets board does not
scroll. A body that already filled the card therefore pushed the
buttons past the bottom edge, where the host reported them as
`IsOffscreen = True`. The buttons were never reachable.

This was resolved by **removing the action row entirely**, at the user's
direction, rather than by budgeting height for it. Removing it gives
the body the whole card. The consequences are recorded in §2.11.

**The Small layout's GPU row.** On the user's direction, the Small
layout no longer names the GPU model and no longer carries the
"Usage" / "Temp" / memory sub-headings. The card shows three rows, each
headed by the name of what it reports — `CPU`, `GPU`, `RAM`, `Disk` —
and the GPU row's figures carry their own units. The Small layout
therefore binds **no** memory heading at all, which is consistent: it
shows the used-memory figure alone, which claims nothing about the
memory pool, so it needs no heading to keep the claim honest. The
Medium and Large layouts still carry the heading from the payload.

**Validated in the live widget host** for the dedicated heading and the
unavailable case, in all three sizes; **fixture-covered only** for the
shared and unspecified headings.

### 2.10 Long-running behaviour

- **CPU / memory over time** — validated on the test machine. The
  collection ran repeatedly over the session with no growth in working
  set beyond the expected per-collection allocations, and no
  `OutOfMemoryException` or unbounded accumulation was observed. The
  network baselines are keyed per interface and dropped when an interface
  disappears, so they do not accumulate.
- **Child-process behaviour** — validated by automated tests against real
  child processes (`SystemProcessRunnerTests`): the runner bounds the
  start, the exit wait and the output drain; it drains stdout and stderr
  concurrently so a child that fills both pipes cannot deadlock it; it
  kills the child tree on expiry; it reports whether the child was
  actually terminated; and a cancelled run returns without waiting for
  the timeout.

**Partially validated on hardware** (CPU and memory), **fixture-covered**
(child processes).

### 2.11 The action row and the device picker were removed

The templates used to carry a root-level `actions` array: a Refresh
button, and one button per GPU for choosing the presented device. The
live host showed that **the row never rendered** — see D5 in §2.9 — so
both controls were unreachable in every size.

At the user's direction, the whole action row was **removed** rather
than being given space on the card. That is a product decision with
real consequences, and they are recorded here rather than left implicit:

- **The device picker no longer exists.** There is no way to change the
  presented GPU from the widget. The payload's selection handling is
  untouched — `SelectedGpuInstanceId` is still read from the host's
  persisted custom state and the default-device policy is unchanged —
  but nothing in the widget can set it any more, so on a multi-GPU
  machine the widget shows the default device and the user cannot
  change it.
- **`CustomState` is therefore always empty** in practice, and the
  persisted-selection round trip is exercised only by the registry's
  automated tests.
- **`UpdatePipeline.ScheduleRefresh` is no longer reachable** from
  production code. It is retained because the pipeline's refresh
  semantics are part of its tested contract, and `Start()` still
  requests an immediate sample on reactivation.
- **`WidgetPayloadPresenter.BuildGpuChoices` was deleted** as dead
  production code, along with the `GpuChoiceOption` record it
  returned.

The card refreshes on its own five-second interval, so removing the
Refresh button cost the user nothing that worked: the button had never
been visible. The live host confirmed the interval refresh (§2.6).

**The device switch is a documented limitation, not a validated
behaviour.** It is recorded in §3 and §4 as well.

---

## 3. Configuration matrix

| Configuration | Fixture coverage | Hardware validation | Status |
|---|---|---|---|
| Intel CPU + Intel GPU | Yes | **Partial** — the Intel UHD is present on the test machine; the *Intel CPU* is also present | **Partially tested.** The Intel integrated part is validated on hardware. A machine with an Intel *discrete* GPU is not available. |
| AMD CPU + AMD GPU | Yes | No — no AMD hardware available | **Not tested** |
| AMD CPU + NVIDIA GPU | Yes | No — no AMD hardware available | **Not tested** |
| Intel integrated GPU + NVIDIA GPU | Yes | **Yes** — this is the test machine | **Tested** |
| Mixed AMD/NVIDIA GPUs | Yes | No — no AMD hardware available | **Not tested** |
| Standard-user execution | Yes | **Yes** — the whole session ran as a standard user, with no elevation requested by the app | **Tested** |
| Unavailable / blocked sensor access | Yes | **Yes** — the CPU temperature is blocked on the test machine | **Tested** |
| Sleep / resume | Partial | **Partial** — the provider survives the suspend and the collection resumes; the transition itself is not detected | **Partially tested** |
| Device removal / driver reset | Yes | No | **Not tested** |
| Missing NVIDIA executable | Yes | No | **Not tested** |
| Widget activate / deactivate / delete / restart | Partial (pipeline only) | **Yes** — activation, resize, refresh, deactivate, reactivate, remove, re-add and multiple instances were all driven on the live board | **Tested** |
| GPU-memory heading rendering | Yes | **Partial** — "Dedicated GPU memory" was seen in Medium and Large; "Shared GPU memory" and "GPU memory" were not | **Partially tested** |
| Small-layout presentation | Yes | **Yes** — the card was rendered and measured in all three sizes | **Tested** |
| Long-running CPU, memory, child processes | Yes | **Partial** — CPU and memory yes; child processes by fixture | **Partially tested** |

### What "not tested" means here

A configuration listed as **not tested** has:

- automated fixture coverage of the *logic* that would handle it, and
- **no** evidence that any real machine of that type produces the
  expected readings.

The fixture coverage for those rows is genuine and worth having: it proves
that an AMD card's sensors would be selected by name-and-type matching
rather than by a hard-coded vendor rule, and that an unreconciled
`nvidia-smi` row is dropped rather than misattributed. It does **not**
prove that an AMD card behaves the same way on a real machine, because the
fixture is a hand-written description of what an AMD card reports — not a
recording of one.

---

## 4. Fixture coverage index

This section lists what the automated suite covers. It is a record of the
*logic* coverage, and it deliberately says nothing about hardware.

| Area | Test file | What it pins |
|---|---|---|
| Sensor selection | `LhmSensorSelectionTests` | Which sensor name/type pair is chosen for each metric, for Intel and AMD CPU packages, for `D3D 3D` on Intel integrated graphics, and for dedicated vs shared memory. `D3D Shared Memory` is never used for VRAM. |
| Sensor conversion | `LhmSensorConversionTests` | Unit normalisation, and that a missing / NaN / infinite / wrong-unit reading is *unavailable*, never zero. |
| Backend mapping | `LhmSnapshotMapperTests` | Device identity is never a list position; a device with no readable sensor is still reported; blocked sensors are `Failed`, not zero. |
| Primary-GPU ordering | `LhmSnapshotMapperTests`, `GpuSelectionPolicyTests` | The collection's primary-GPU choice and the widget's default choice are the **same** device, for every permutation of the device list. |
| Cross-device attribution | `WidgetPayloadPresenterTests` | A scalar figure is presented only for the device it names; an unattributed figure is presented for no device; the used and total memory figures are attributed **separately**; the reconciled nvidia-smi utilization is presented for the card it reconciled to and for no other; a measured zero utilization is presented as "0.0%", not as "N/A". |
| Combined GPU memory | `GpuMemoryCompatibilityPolicyTests`, `WidgetPayloadPresenterTests` | A "used / total (percent)" figure is presented only when the two figures name the same device, that device is the one being presented, and the two figures can be shown to describe one memory pool. Dedicated and shared memory are never paired; a figure with no device is never paired; a capacity of zero or below the usage is never paired; two cards with identical display names are never paired. A matching display name is never treated as evidence. |
| GPU-memory label | `GpuMemoryLabelTests` | The heading above a memory figure is derived from the figure's own established pool: a confirmed dedicated pool reads "Dedicated GPU memory", a confirmed shared pool reads "Shared GPU memory", and an unestablished pool reads "GPU memory". A generic family pair is combined but never headed as dedicated VRAM; a rejected pair's heading comes from the used figure alone; an unavailable figure substitutes neither a figure nor a zero; switching devices updates the label and the values together. The Medium and Large layouts bind the heading from the payload; the Small layout binds no heading at all, because it carries no sub-headings; and no template carries a static "VRAM" or "Dedicated VRAM" heading. |
| Legacy presenter | `HardwareModelsTests` | The legacy payload never borrows another device's reading, and never pairs one device's usage with another device's capacity. |
| Merge precedence | `ScalarMergePolicyTests` | Backend beats fallback beats unattributed counters; the winner keeps its own device id; a measured zero is still a winner. |
| Measured vs unavailable | `ScalarCollectorStateTests` | A collector that never ran, or whose first read failed, produces no value; a measured zero is presented as a value; a failed read after a success is *stale* and keeps the time it was read. |
| Percentage arithmetic | `ThermalMathTests`, `WidgetPayloadPresenterTests` | A non-finite percentage is *no value*, never a measured zero; a ratio with a zero total is *no value*; a measured zero percentage stays zero; a percentage that was not computed reaches the payload as "N/A" rather than "0.0%". |
| Unattributed GPU memory | `GpuMemoryFallbackPolicyTests` | Usage is never summed across adapters; capacity is never taken from the first adapter; a capacity below the usage is discarded. |
| NVIDIA reconciliation | `NvidiaGpuReconcilerTests`, `NvidiaGpuFallbackSelectorTests` | A reading is attributed only when its PCI key matches exactly one device and one sample; ambiguous and unreconciled readings are dropped. |
| NVIDIA parsing | `NvidiaSmiParserTests` | Field parsing, malformed rows, unsupported values, implausible ranges. |
| NVIDIA invocation policy | `NvidiaGpuFallbackTests` | The fallback is not invoked when the backend covered the load, the temperature and both VRAM figures; it *is* invoked when any one of them is missing; a missing executable, a failure and a timeout all yield no readings and no exception. |
| Subprocess bounds | `SystemProcessRunnerTests` | The start, the exit wait and the output drain are all bounded; both pipes are drained concurrently; the child tree is killed on expiry; termination is reported; cancellation returns promptly. |
| Pipeline lifecycle | `UpdatePipelineTests` | No overlapping collections, generation-scoped cancellation, the drain boundary, coalesced refresh, and the stress case. |
| Payload contract | `WidgetPayloadPresenterTests`, `TemplateBindings` | Every template binds only to names the payload produces; a failed read is "N/A" and never "0.0%"; a stale reading is "stale"; the sample time is the most recent **successful** read, not the publish time. |
| Device selection | `WidgetSelectionTests`, `GpuSelectionPolicyTests` | A selection is stored against the instance id, never the display name; it survives a restart; it survives another selection; an unresolvable selection is ignored rather than treated as a choice. |
| Live-host layout fit | `WidgetLayoutFitTests` | The properties that caused the observed live-host failures: the byte ladder has a megabyte tier and the two ladders cannot drift apart; the GPU row's memory column takes the space the narrow figures do not need; no template carries a root-level action row; the Medium card shows its disk and RAM figures in the compact one-line form; the Small card still names its disk figure. |
| Diagnostics | `DiagnosticsTests` | The inventory describes the devices and metrics; exception messages are not leaked; paths are scrubbed. |

---

## 5. Known gaps and risks

1. **AMD hardware is entirely unvalidated.** The AMD code paths are
   exercised only by fixtures. A machine with an AMD GPU should be
   validated before the widget is described as supporting AMD.
2. **The COM activation path has no automated coverage.** The pipeline
   beneath it is tested; the `IWidgetProvider` callbacks are not. They
   *were* exercised on the live host (§2.6), which is evidence about the
   behaviour but not a regression test: a change to the provider that
   broke the activation path would not be caught by the suite.
3. **The device picker has been removed, so the user cannot choose the
   presented GPU.** The picker was the widget's only way to set the
   selection, and it never rendered (§2.9, D5). It was removed rather
   than given space on the card. The selection machinery is intact and
   fixture-covered, but on a multi-GPU machine the widget now always
   shows the default device. This is a **product limitation**, and it is
   the one change in this task that removes a capability rather than
   fixing one.
4. **Sleep/resume is only partially validated.** The provider survives a
   suspend and the collection resumes, but there is no mechanism that
   detects the transition — the collection simply resumes with a fresh
   sample, and a reading taken before a long gap is not re-validated
   against it.
5. **The Windows GPU memory counters cannot be attributed to a device.**
   On a multi-GPU machine the counters' figures are therefore not
   presented for any card. Making them attributable would require
   correlating the counters' LUIDs with the devices the backend reports,
   which is a collector-design change rather than a fix.

   Their two figures also **cannot be correlated to each other**: the
   usage figure comes from the adapter-memory performance counter and the
   capacity from the video-controller class, and neither LUID appears in
   the other class. That each class saw exactly one adapter makes the
   pair *plausible*, not *confirmed* — they could still be two different
   adapters. The one available cross-check is the numbers themselves: a
   capacity below the usage refutes the pairing, and the capacity is
   dropped. A pair that passes that check is still not proven to describe
   one adapter. The two figures also name no device, so the combined
   GPU-memory rule never pairs them with anything either.
6. **The WMI queries are not bounded.** No `ManagementObjectSearcher` sets
   `Options.Timeout`, so a hung WMI provider could block a collection
   indefinitely. This is the same class of problem the subprocess runner
   already solves, and the same fix applies.
7. **The mutable `SystemMetrics` model still uses non-nullable
   primitives** for the system-level figures (`CpuPercent`, `RamPercent`,
   `DiskPercent`, the network rates). The snapshot's `Measurement` values
   carry the status that distinguishes a measured zero from an
   unavailable figure, but the legacy mutable model cannot. Retiring the
   mutable model in favour of the snapshot would remove the duplication
   entirely; that is an architectural change, not a fix.
8. **The `nvidia-smi` fallback cannot help when the backend reports no
   devices.** Its readings are reconciled to a backend device by PCI
   device key, so it needs a backend device to reconcile *against*. When
   the backend's enumeration returns nothing, no reconciliation is
   possible, the command line is not started, and its readings are
   dropped. The fallback therefore covers "the backend found the card but
   could not read one of its sensors", not "the backend could not find the
   card at all". This follows from never attributing a reading by
   position, and it is not validated on hardware.
9. **The backend's generic "Memory Used" / "Memory Total" sensors do not
   say which memory pool they describe.** They establish a *family*, which
   is enough to pair them with each other on one device, but not enough to
   tell a user that the figure is dedicated video memory rather than
   shared system memory. The candidate lists prefer the explicit
   dedicated-memory names, so this only arises on a card whose driver
   exposes nothing else. The pairing logic is fixture-covered; the
   behaviour of a driver that reports only these sensors is not validated
   on hardware.

   The consequence for the *label* is that such a pair is headed "GPU
   memory": the widget says what the data supports and no more, so it
   never claims dedicated VRAM for a pool it did not establish. The
   heading is a deliberate statement of ignorance, not a placeholder to be
   refined later.
10. **The combined GPU-memory rule is fixture-covered only.** The test
    machine has one card with dedicated-memory sensors and one integrated
    part with no memory sensors at all, so the cases that matter most — a
    card whose two figures come from different pools, two cards with the
    same name, an unattributed capacity — have not been observed on real
    hardware. The observed machine does show the ordinary case: the
    NVIDIA card's own "used / total (percent)" reads 1 308 622 848 B of
    8 589 934 592 B, and both figures come from the same device's own
    dedicated-memory sensors.
11. **The generic-family case has not been observed on real hardware.** No
    available machine reports only "Memory Used" / "Memory Total", so the
    neutral "GPU memory" heading has never been seen on a real card.

---

## 6. What the live host did *not* show

The live host is a rendering and lifecycle environment, not a hardware
environment. These remain unvalidated, and no amount of driving the
Widgets board changes that:

- **A shared-memory pool.** No available device reports one, so
  "Shared GPU memory" has never been rendered.
- **The generic "Memory Used" / "Memory Total" pair.** No available
  device reports only those sensors, so "GPU memory" has never been
  rendered.
- **An AMD device.** No AMD hardware was available.
- **A device switch.** The picker was the widget's only route to one,
  and it was removed (§2.11).
- **A device disappearing while it is presented.** No GPU was removed
  or had its driver reset.
- **A machine where the CPU temperature is readable.** The test
  machine's is blocked, so the "CPU temperature present" path has not
  been rendered — the card shows `N/A`, which is the correct rendering
  of an unavailable reading, but it is not the rendering of a present
  one.

The fixtures for each of these are the evidence that the *logic* is
right. They are not evidence that the rendering is right, and this
document does not present them as such.

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

### 1.1 Historical live-host environment (prior candidate)

The Windows Widgets host was available during the earlier validation run,
so the widget itself was pinned, rendered and driven. These details apply
to the historical observations in §2.6, §2.7, §2.9 and §2.11; they do not
establish current-candidate host access or screenshot provenance:

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

Unless a subsection explicitly says otherwise, these device and sensor
observations are historical diagnostics from an earlier source/package
checkpoint. They are not proof that the currently installed package
matches this checkout; the current source/package DLL mismatch is recorded
in the final acceptance checkpoint below.

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
- **The Intel UHD Graphics load of 0% (`D3D 3D`) is an `Available`
  measured value** (the sensor returned 0.0), not a fallback or fabricated
  zero. The sensor was successfully read and produced a value of 0.0.
- The NVIDIA card's core sensors are selected for load, temperature and
  dedicated memory.
- The CPU **temperature** read **Failed** on this machine. The recognized
  sensors returned null under the tested standard-user context. An
  access/driver limitation is suspected, but the elevated comparison has
  not been performed and the cause is unconfirmed. The failure is surfaced
  as `Failed`, with no value, rather than as a zero or substitute reading;
  §2.12 records the evidence and remaining question.
- The CPU **clock** was read successfully through `Win32_Processor`
  (2304 MHz), because that class does not require elevation.

### 2.2 Unavailable and blocked sensor access (historical hardware evidence)

The test machine is itself the observed unavailable-sensor case: CPU
temperature sensors returned null under the tested standard-user context.
An elevated comparison has not been performed, and the reason is not
confirmed.

Observed behaviour after the fixes in this task:

| Metric | Status before the fixes | Status after the fixes |
|---|---|---|
| `cpu_temperature_c` | value absent, status `Failed` | `Failed`, source `LibreHardwareMonitor` |
| `acpi_thermal_zone_temperature_c` | `Failed`, **empty source** | `Failed`, source `MSAcpi_ThermalZoneTemperature` |
| `network_down/up_bytes_per_sec` | `Available`, value `0.00` | `Failed` on the first cycle, then `Available` with a measured value |
| `cpu_load_percent` | `Available 0.00` on a cycle with no elapsed time | `Failed` on cycle 1 (no elapsed time for GetSystemTimes delta); `Available` from cycle 2 onward with the first measured value (elapsed time > 0) |

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

**Historical live-host observations (prior candidate)** (driven through UI Automation on the real Widgets board):
The card was pinned, resized, removed, re-added, duplicated and deactivated/reactivated. These are not current-candidate lifecycle results. The provider is an out-of-process COM server, so the host activates it and the card is only rendered while the provider is alive.

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

**Automated test coverage** (not live-host evidence):
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

**Historical summary (prior candidate):** Live-host validated (activation, resize, refresh, deactivate,
reactivate, remove, re-add, multiple instances); automated tests cover
the pipeline lifecycle contract; not validated on hardware (device switch,
empty first paint); COM activation path has no automated regression test.

### 2.7 Sleep / resume

**Historical partial live-host validation (prior candidate).** The machine was suspended with
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

**Historical partial hardware validation (prior candidate)** (the process survives, and the
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

**Validated in the live widget host (prior candidate).** The card was rendered in all three
sizes the host offers, and the rendered text runs were measured with UI
Automation — their width, height and position — and captured as
screenshots. These observations were made against an earlier build of the
widget (pre-Prompt-6). The **current** Prompt-6 candidate build was
reinstalled, but the live host could not be driven for visual verification
because the host process (msedgewebview2) blocks UI Automation tree
traversal on this machine. Therefore, **visual fit for the current
candidate is not tested on hardware**.

The machine has one card whose memory figures come from its own
dedicated-memory sensors (the NVIDIA RTX 2070 Super), and one integrated
part with no memory sensors at all. So the **dedicated** heading and the
**unavailable** case were both observed on the prior candidate:

| Size | Label rendered | Geometry | Figure rendered | Geometry |
|---|---|---|---|---|
| Small | *(none — see below)* | — | `1.0 GB` | w=38 h=19, one line |
| Medium | `Dedicated GPU memory` | w=127 h=**16** — one line, no wrap | `1.0 GB / 8.0 GB (12.7%)` | w=137 h=19 — one line, fully visible |
| Large | `Dedicated GPU memory` | w=127 h=**16** — one line, no wrap | `1.0 GB / 8.0 GB (12.7%)` | w=141 h=19 — one line, fully visible |

**Manual checklist for current candidate (host not accessible):**
- [ ] Pin the card in the Widgets board
- [ ] Verify Small: GPU row shows `1.0 GB` (compact) on one line, no clipping
- [ ] Verify Medium: heading `Dedicated GPU memory` one line (h=16), figure `1.0 GB / 8.0 GB (12.7%)` one line fully visible
- [ ] Verify Large: same as Medium, figure fully visible
- [ ] Verify all three sizes: no root-level actions array present
- [ ] Verify Medium: disk figure shown as `607.4 GB (32.9%)` compact form
- [ ] Verify Small: disk figure still present (compact)

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

### 2.12 Why the CPU temperature reads N/A

The widget's CPU temperature shows `N/A` on the test machine. This
section records what was actually observed, because "N/A" on its own
does not say whether the sensor is missing, blocked, misnamed, lost in
merging, or never read — and those need different answers.

**The path, traced end to end on this machine:**

| Stage | What was observed |
|---|---|
| Raw sensor enumeration | The CPU reports **19 Temperature sensors**: `Core Max`, `Core Average`, `CPU Core #1`…`#8`, `CPU Package`, and `CPU Core #N Distance to TjMax` × 8. All 19 are present. |
| Sensor values | **Every one of the 19 reports a null value.** A standalone probe against the same library version (LibreHardwareMonitorLib 0.9.6, CPU only, unprivileged) shows the same: all 18 Load sensors read; all 19 Temperature sensors are `value=(null) raw=null`; all 8 Clock sensors are null; the Power sensors read 0.000. |
| Selection | `CPU Package` — the **first** candidate in the preference list — is selected. The name is recognized; there is no name-matching failure. |
| Mapping | The value is absent, so the measurement is `Failed`, source `LibreHardwareMonitor:CPU Package`. |
| Merging | The merged scalar is `Failed`, with no value. Nothing is dropped in the merge. |
| Presentation | `N/A`. |

**The diagnosis is case 2** in the vocabulary the opt-in diagnostics use:
*matching sensors are enumerated but report no usable value.*

The other five cases were each ruled out against the live machine, not
assumed away:

| Case | Ruled out because |
|---|---|
| 1 — no sensor enumerated | 19 Temperature sensors are enumerated. |
| 3 — names not recognized | `CPU Package` is candidate #1 and is selected. |
| 4 — reading lost in merging | The device's own reading and the published scalar are both `Failed` with the same source. Nothing was lost. |
| 5 — hardware update failed | The routine log records no `[LHM/update] failed` line; `hardware.Update()` did not throw for any node. |
| 6 — backend unavailable | The routine log records `[LHM/initialize] ok elapsed=588.1ms` and `[LHM/read] ok elapsed=159.7ms devices=3`. |

**Status:** CPU temperature remains **unavailable**; the access/driver cause is suspected (MSR read typically requires elevation), but the elevated comparison has **not** been performed. This document does not claim that elevation would fix the issue, nor that no unprivileged solution is possible. The null readings are an observed behaviour and an open question, not a confirmed root cause.

**No sensor-name candidate was added**, because none was justified: the
authoritative backend source shows the names this library actually
produces (`CPU Package`, `Core (Tctl/Tdie)`, `Core (Tdie)`, `Core
Average`, `Core Max`, `CCD1 (Tdie)`, `CCDs Average (Tdie)`, `P-Core #N` /
`E-Core #N` on hybrid parts), and the candidate list already contains
the ones this machine's sensor set needs. No readable temperature
sensor on this machine is hidden behind an unrecognized name.

**What was fixed in software.** Two defects in the *reporting* of the
condition were found and fixed:

1. **Retained aggregates.** LibreHardwareMonitor's Intel CPU path writes
   `Core Average` and `Core Max` only when at least one per-core read
   produced a value; when the per-core MSR read fails, those aggregates
   **retain their previous value**. The library's own per-core and
   package sensors are explicitly nulled, but the aggregates are not.
   Without intervention the widget would have published a stale
   aggregate as a freshly measured temperature. `LhmRetainedSensorPolicy`
   marks such an aggregate as retained, and the mapper refuses to
   publish a retained value as `Available` — it becomes `Failed`.
2. **A misleading diagnosis.** The Intel integrated GPU's readable
   `D3D Shared Memory Used` / `D3D Shared Memory Total` sensors were
   reported as "case 3: readable sensors are enumerated but their names
   are not recognized". They are *deliberately* excluded — shared system
   memory is not video memory — so reporting them as an unrecognized
   name pointed a maintainer at a gap in the candidate list that does
   not exist. They are now reported separately as deliberately excluded.

**The opt-in diagnostic output** now distinguishes all six cases and
records, per metric: the sensor type required, the number of sensors of
that type, the candidate names searched, the recognized / readable /
excluded sensors with their values, the selected sensor, the measurement
status, the source, the published value and status, the diagnosis, and
the backend's own initialization and read state with a timestamp. It is
written to the inventory export the user asks for, not to the routine
log. The routine log carries the metric, the sensor type, the sensor
count and the diagnosis — and no sensor name, no device identity and no
exception message. A leak check over the routine log for `payto`,
`10875H`, `intelcpu`, `VEN_10DE`, `VEN_8086`, `PCI\`, `Users\`, `i7-`
and `/amd` returned **0** matches for each. (`/gpu-` matches only the
operation names `collector/gpu-load`, `collector/gpu-temperature` and
`collector/gpu-vram-windows`, not a device identity.)

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
| Unavailable / blocked sensor access | Yes | **Yes** — CPU temperature sensors enumerate and are selected correctly but report null under the tested unprivileged context (§2.12) | **Tested** |
| CPU temperature under elevation | No | **No** — no elevated run has been performed; it requires the user's explicit approval and the widget does not elevate | **Not tested** |
| Sleep / resume | Partial | **Partial** — the provider survives the suspend and the collection resumes; the transition itself is not detected | **Partially tested** |
| Device removal / driver reset | Yes | No | **Not tested** |
| Missing NVIDIA executable | Yes | No | **Not tested** |
| Widget activate / deactivate / delete / restart | Partial (pipeline only) | **Yes (live host, prior candidate)** — activation, resize, refresh, deactivate, reactivate, remove, re-add and multiple instances driven on live board | **Tested (prior candidate)** |
| GPU-memory heading rendering | Yes | **Partial (prior candidate)** — "Dedicated GPU memory" seen in Medium/Large; "Shared GPU memory" and "GPU memory" not seen | **Partially tested (prior candidate)** |
| Small-layout presentation | Yes | **Not tested on current candidate** — host process (msedgewebview2) blocks UI Automation; manual checklist in §2.9 | **Not tested (current candidate)** |
| Visual fit in live host (current candidate) | Yes | **Not tested** — host inaccessible; see manual checklist in §2.9 | **Not tested** |
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
| CPU-temperature diagnostics | `CpuTemperatureDiagnosticsTests` | The six cases an unavailable figure can have are told apart: no sensor enumerated, sensors enumerated but unreadable, readable sensors under unrecognized names, a valid reading lost in merging, a failed hardware update, and an unavailable backend. A preferred sensor that is unreadable falls back to a valid one; a readable but previously unrecognized sensor name is not selected and is diagnosed as case 3; a valid temperature survives merge and presentation ("84.0 °C"); an unreadable one is presented as "N/A", never as zero; a failed hardware update never refreshes an old reading's success timestamp; a retained aggregate is never published as a fresh reading; a shared-memory sensor is reported as deliberately excluded rather than as an unrecognized name. |

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
12. **The CPU temperature reads no value under the tested unprivileged
   context, and the widget does not elevate.** The Intel MSR temperature
   sensors enumerate and are selected correctly, but report null under a
   standard user, so the card shows `N/A`. The underlying access or
   driver limitation is suspected but has not been confirmed — elevation
   has not been tested, so this document does not claim that elevation
   would fix the issue, nor that no unprivileged solution is possible.
   It is recorded here as an observed behaviour and an open question,
   not as a confirmed root cause (§2.12 traces the path end to end on
   the test machine).

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
  machine's temperature sensors enumerate but report null under the
  tested unprivileged context, so the card shows `N/A`, which is the
  correct rendering of an unavailable reading — it is not the rendering
  of a present one. The cause of the null values has not been confirmed:
  elevation has not been tested, so this document does not claim that
  elevation would fix the issue, nor that no unprivileged solution is
  possible. It is an observed behaviour and an open question (§2.12).
- **A CPU whose temperature is stale-but-retained.** The retained-aggregate
  defect in §2.12 is fixture-covered and was found by reading the
  library's source, but no machine was observed producing a retained
  aggregate, because the aggregate only retains a value if it once read
  one — which needs the elevated access this session did not have.

The fixtures for each of these are the evidence that the *logic* is
right. They are not evidence that the rendering is right, and this
document does not present them as such.

---

## Suggested commit message (when ready to commit)

```
Validate Prompts 3–5 in the live Windows widget host

This commit packages the combined Prompts 3–5 candidate and validates it
against the real Windows widget host on the test machine
(Intel Core i7-10875H, NVIDIA RTX 2070 Super, Intel UHD Graphics).

Validated on hardware (live host, prior candidate):
- Widget lifecycle: activation, resize (Small/Medium/Large), interval
  refresh, deactivate/reactivate (board close/reopen), remove/re-add,
  multiple instances — all driven through UI Automation.
- GPU memory label rendering: "Dedicated GPU memory" heading and figure
  rendered on one line in Medium and Large; Small shows compact used
  figure only.
- Layout defects D1–D5 fixed and re-verified: disk row no longer clipped,
  storage value visible, GPU-memory figure in MB/GB (not KB ellipsized),
  heading one line, action row removed.
- Sleep/resume: provider process survives, collection resumes.
- CPU temperature remains unavailable (N/A) — sensors enumerate and are
  selected correctly but report null under unprivileged context; access/
  driver cause suspected but elevated comparison NOT performed.

Not validated on current candidate (host process blocks UI Automation):
- Visual fit in live host — manual checklist provided in §2.9.

Fixture-covered (automated tests, 594 pass):
- All sensor selection, conversion, mapping, merge precedence, GPU-memory
  compatibility and labeling, pipeline lifecycle, NVIDIA fallback, payload
  normalization, template bindings, CPU-temperature diagnostics.

Known gaps (unchanged): AMD hardware, device switch, sleep/resume transition
detection, device removal/driver reset, missing NVIDIA executable,
CPU temperature under elevation, COM activation path has no automated test.
```

Co-Authored-By: Claude Code <noreply@anthropic.com>

---

## Current combined candidate verification — 2026-10-23

This section records the earlier package comparison and user-supplied screenshots. A fresh build
during the final source acceptance review produced a different `SystemMonitor.dll` hash than the
installed package; see the final checkpoint below. The screenshots' capture time and linkage to
either artifact are unknown, and the live card remains inaccessible to UI Automation in this
session.

### Candidate and machine identity

- Checkout: commit `a6fa325ebe4cb8bd1a38fa870aac1cfc4b24ac6e`; **dirty** (modified tracked files,
  one deleted tracked file, and untracked source/test/build artifacts).
- OS: Windows 11 Home, build `26200`, 64-bit; machine: Razer Blade 15 Advanced Model (Early 2020),
  x64.
- CPU: Intel Core i7-10875H, 8 cores / 16 logical processors.
- GPUs: Intel UHD Graphics and NVIDIA GeForce RTX 2070 Super with Max-Q Design.
- Displays: primary `\\.\DISPLAY4`, 1920×1080 at `(0,0)`, 96 DPI (100%); secondary
  `\\.\DISPLAY5`, 1080×1920 at `(-1080,-144)`, 96 DPI (100%). Display facts were queried through
  Windows Forms and `GetDpiForMonitor`; no display settings were changed.

### Build, tests, and installed-artifact comparison

Commands run on this checkout:

```powershell
dotnet test SystemMonitor.Tests/SystemMonitor.Tests.csproj
dotnet build SystemMonitor/SystemMonitor.csproj -c Release -r win-x64
dotnet publish SystemMonitor/SystemMonitor.csproj -c Release -r win-x64 -o build/current-publish-check /p:SelfContained=false
```

Results: **598 tests passed, 0 failed, 0 skipped**. The test build emitted existing Windows-only
CA1416 analyzer warnings for WMI APIs. The Release `win-x64` build succeeded with **0 warnings and
0 errors**. Publish succeeded. The suite is VSTest/xUnit v2 (`Microsoft.NET.Test.Sdk` plus
`xunit.runner.visualstudio`); no MTP configuration is present.

The installed package is `com.test.systemmonitor_1.0.0.1_x64__1b7q5sa4bwdpa` (version `1.0.0.1`,
package family `com.test.systemmonitor_1b7q5sa4bwdpa`) at
`C:\Program Files\WindowsApps\com.test.systemmonitor_1.0.0.1_x64__1b7q5sa4bwdpa`. It is the
registered `com.test.systemmonitor` package. Its signature verified as `CN=Developer`, thumbprint
`C78FF8061C1C124809996A6CFF2996B7DEF7E24F`. The running `SystemMonitor.exe` provider process
(PID 16548) resolves to this package's install path. The Widgets board process (PID 4064) also
exists, but its process has no main window handle in this session.

Full SHA-256 comparison:

| Object compared | Size | SHA-256 |
|---|---:|---|
| Earlier publish from the package comparison: `build/publish-temp/SystemMonitor.dll` | 745,472 | `892B39609ABE6F85171FFDAD2FD7C8101D92155399A2874BF2D9AC420A9B4921` |
| Installed DLL: `...1.0.0.1_x64__1b7q5sa4bwdpa/SystemMonitor.dll` | 745,472 | `892B39609ABE6F85171FFDAD2FD7C8101D92155399A2874BF2D9AC420A9B4921` |
| Package root: `build/package-root/SystemMonitor.dll` | 745,472 | `892B39609ABE6F85171FFDAD2FD7C8101D92155399A2874BF2D9AC420A9B4921` |
| Signed package: `build/msix-output/SystemMonitor.msix!SystemMonitor.dll` | 745,472 | `892B39609ABE6F85171FFDAD2FD7C8101D92155399A2874BF2D9AC420A9B4921` |

Those four artifacts matched at the time of that comparison, and `Get-AuthenticodeSignature`
reported a valid MSIX signature. That comparison does not establish correspondence to the later
fresh source build recorded below, or that every package file is identical to the package root.
No certificate trust change was made in this validation run.

The user reports running the packaging/install commands; no install/update command was run by the
assistant in this validation pass.

### Current-candidate host observations

| Size | Current candidate observation | Result |
|---|---|---|
| Small | User-provided card screenshot; CPU/GPU/RAM/Disk four rows all visible, with RAM `14.3 / 15.9 GB`, Disk `0.61 / 1.8 TB`, GPU memory `0.99 / 8 GB`; zero GPU load and N/A CPU temperature are distinguishable | **Visible fit in supplied image.** Screenshot inspection only; capture scaling/host provenance is unconfirmed. Small has no separate GPU-memory label in this compact layout. |
| Medium | User-provided card screenshot; compact CPU/GPU names, RAM/disk totals, `Dedicated memory` label/value, and Intel secondary row visible; GPU heading wraps, but storage remains visible | **Visible fit in supplied image.** Screenshot inspection only; capture scaling/host provenance is unconfirmed. |
| Large | User-provided card screenshot; CPU/GPU/RAM/Disk and Download/Upload sections visible; dedicated GPU-memory label/value, eligible Intel secondary row, and two network columns; no Sample heading or timestamp | **Visible fit in supplied image.** Screenshot inspection only; capture scaling/host provenance is unconfirmed. |
| Stale notice | Small/Medium/Large physical layout, especially Small capacity fit | **Not tested**; automated binding/template fixture coverage is separate evidence |
| Lifecycle | Activation, resize, deactivate/reactivate, remove/re-add, multiple instances, sleep/resume | **Not tested** on the current candidate |

`./build/widget-board-probe.ps1 -ReadOnly` failed with “The Widgets board window was not found.”
`./build/widget-desktop-dump.ps1 -MaxDepth 3` returned no accessible Widgets UI Automation
elements. Opening `ms-widgetboard:` did not expose a board window to the probes. The three
user-supplied screenshots are visual evidence for the named layouts, but the capture method/time
and whether the screenshots were captured directly from the installed live host are not recorded
here; treat host provenance as provisional. No assistant-captured live card inspection was
available. The installed provider was not terminated, and display settings were not changed.

### Measurements and GPU behavior

The screenshots show CPU utilization (7.7–10.0% in Small/Large and 8.9% in Medium), with CPU
temperature `N/A`; the historical standard-user observation that recognized sensors returned null
is consistent. No elevation comparison was performed. They show the NVIDIA RTX 2070 Super with
Max-Q as the displayed primary GPU with load, temperature (58–59°C), and dedicated memory
(0.99–1.06 / 8 GB); the Intel UHD integrated GPU appears as a secondary row with readable `0.0%`
load and N/A temperature. This is visible attribution evidence, not a separately instrumented
selection-reason trace. The screenshots also show RAM/disk totals and balanced Download/Upload
columns in Large. The Small screenshot preserves zero vs N/A; its compact GPU-memory field is
unlabeled, while Medium/Large visibly label dedicated memory.

The screenshots demonstrate that the readable zero-load Intel secondary row is rendered. GPU
selection reason and the three-unusable-cycle grace/fourth-cycle removal behavior remain
automated-test evidence only. No physical sensor was disrupted to exercise them.

Freshness semantics: aggregate `sampleIsStale` is true when the newest successful reading is more
than 30 seconds old at publication. CPU/GPU load, clock, and temperature presentation checks each
measurement's own timestamp; stale fields are indicated individually. The immutable snapshot's
original values, statuses, and timestamps are preserved. Stale-notice visual fit remains **not
tested**: the supplied screenshots show normal telemetry and no stale notice. Aggregate and
per-measurement binding coverage is automated evidence, not visual-host evidence. A host card
changes only after another publication; advancing time alone does not update it.

### Blocker and next safe step

The earlier package comparison matched its publish, package-root, and installed DLLs. The later
fresh source build does not match that installed DLL. The supplied screenshots show normal-state
layout fit for all three sizes, but their provenance is not independently established. Stale-notice fit and live
lifecycle callbacks remain untested; no live resize, deactivation/reactivation, remove/re-add,
multiple-instance, or sleep/resume scenario is established by static card screenshots.

---

## Final source acceptance checkpoint — 2026-10-23

### Source identity and checkpoint state

- Repository: `E:\Code\SystemMonitor`; branch `main`; HEAD
  `a6fa325ebe4cb8bd1a38fa870aac1cfc4b24ac6e`.
- Checkout is dirty: 24 tracked modifications, one tracked deletion, and 21 untracked paths
  (including the new production/test sources, diagnostics, and scratch projects). No commit was made.
- The implementation sequence is the source/test work listed below. No package installation,
  registration, signing, or host-setting changes were made during this acceptance review.

### Current verification

Working directory for all commands: repository root, `E:\Code\SystemMonitor`.

```powershell
dotnet test SystemMonitor.Tests/SystemMonitor.Tests.csproj
dotnet test SystemMonitor.Tests/SystemMonitor.Tests.csproj --no-restore /t:Rebuild
dotnet build SystemMonitor/SystemMonitor.csproj -c Release -r win-x64
git diff --check
```

The initial suite and forced-rebuild suite each completed with **598 passed, 0 failed, 0 skipped**.
The final `--no-restore` run also passed 598/598 and emitted CA1416 analyzer warnings for WMI APIs
compiled into the cross-platform test target. The Release `win-x64` build succeeded with **0
warnings and 0 errors**. `git diff --check` reported no whitespace errors; Git separately printed
LF-to-CRLF working-copy warnings for modified files. An initial attempt to force rebuilding with `-t:Rebuild` failed with MSB1008
because MSBuild parsed `Rebuild` as an additional project; the corrected `/t:Rebuild` command above
completed successfully.

### Source/artifact correspondence

The freshly built source DLL is
`SystemMonitor/bin/Release/net8.0-windows10.0.22000.0/win-x64/SystemMonitor.dll`, SHA-256
`525B01ACC41EB97E51C99579EB258528BCFA47523A3B430EA7D49C678E3B831A`. Read-only inspection found
the installed package `com.test.systemmonitor_1.0.0.1_x64__1b7q5sa4bwdpa`, with the running provider
PID 16548 at that package's install path. Its installed `SystemMonitor.dll` SHA-256 is
`892B39609ABE6F85171FFDAD2FD7C8101D92155399A2874BF2D9AC420A9B4921`, so it does **not** match
the current source build. The existing MSIX signature is valid and reports signer `CN=Developer`.
No install/update was run. The supplied screenshots therefore remain provisional and are not
attributed to this current source build. These DLL checks concern only `SystemMonitor.dll`; they do
not prove identity of all package contents.

### Acceptance matrix

#### Compact RAM/disk capacity presentation

The system RAM and disk compact fields are status-aware at the presenter
boundary. Two valid `Available` values produce the existing compact pair;
measured zero remains a valid used value. With one valid `Available` side
and the other absent, `Failed`, or `Unsupported`, the payload explicitly
labels the surviving value as `used` or `total`. If either status is
explicitly `Stale`, the field is `stale`. If neither side is usable, or an
`Available` value is null, non-finite, negative, zero-capacity, or an
incoherent pair, the field is `N/A`.

The presenter reads the immutable measurements without changing values,
statuses, or `LastSuccessfulUpdate`. This compact helper honors explicit
`Stale` status; it does not add a new age-from-timestamp rule for RAM/disk.
GPU memory remains on its separately attributed and compatibility-checked
presentation path, including the existing used-only fallback and no
total-only display.

Presenter regression methods in
`SystemMonitor.Tests/WidgetPayloadPresenterNormalizationTests.cs` cover both
`ramCapacityCompact` and `diskCapacityCompact`: failed-used reproduction,
unavailable-used total-only, unavailable-total used-only, stale on either
side with snapshot preservation, both sides unavailable, measured zero,
and invalid available numbers.

| Area | Automated evidence | Live/historical/provisional evidence | Current status / accepted limitation |
|---|---|---|---|
| CPU temperature | Sensor selection, diagnostics, merge/retained-value and presentation cases are in the test suite | Screenshots show N/A; prior standard-user diagnostics found recognized Intel sensors unreadable | Observed standard-user limitation; elevation comparison is unperformed; access/driver cause is not confirmed |
| CPU load | Collector and presenter behavior covered by tests | Screenshots show utilization; prior hardware observations found first-sample baseline behavior | Values visible in supplied screenshots; no new live collector trace in this review |
| Capacity and names | Formatting, compatibility, payload normalization and layout/template assertions | User-supplied screenshots show required capacities and compact names fitting in all three sizes | Provisional screenshot evidence; Small GPU-memory figure is compact/unlabeled by design |
| GPU selection, attribution and grace | Policy, tracking, reconciliation, memory compatibility and grace integration tests | Screenshots show NVIDIA primary telemetry and Intel secondary 0.0% | Visible attribution is provisional; selection reason and failure/grace transitions are not physically observed now; manual picker is unavailable |
| Freshness | Advancing-time presentation, aggregate stale notice, per-measurement stale fields and immutable snapshot coverage are automated-tested | Normal screenshots do not show stale state | Stale-notice visual fit is not tested; aggregate flag is newest successful reading >30 seconds old at publication; CPU/GPU measurements age independently; snapshots retain values/statuses/timestamps; card changes only after publication |
| Layout | Template bindings and layout fit are automated-tested | Three user-supplied normal-state screenshots; machine display scaling is recorded as 100%, but capture conditions are unknown | Provisional visible fit; screenshot time/host linkage unknown; stale-state fit untested |
| Lifecycle and sleep/resume | Coordinated pipeline lifecycle and cancellation coverage | Older report sections describe prior-candidate live observations | Current candidate callbacks and sleep/resume are not tested; prior observations are historical only |
| Packaging/identity | Release architecture build only | Existing installed package identity/signature inspected; installed DLL hash differs from fresh source build | No current-source installation validation; self-signed development packaging is not a distribution release |

### Change groups and review files

- Collection, mapping, diagnostics and protection: `Models/HardwareModels.cs`;
  `Services/SystemMetricsService.cs`, `Services/LhmSensorSelection.cs`,
  `Services/LhmSensorSource.cs`, `Services/LhmSnapshotMapper.cs`,
  `Services/LibreHardwareMonitorSource.cs`, `Services/LhmSensorDiagnostics.cs`,
  `Services/LhmRetainedSensorPolicy.cs`.
- GPU policy, tracking and attribution: `Services/GpuSelectionPolicy.cs`,
  `Services/GpuSelectionTracker.cs`, `Services/GpuExtrasTracker.cs`,
  `Services/GpuMemoryPresentation.cs`.
- Formatting, payload, templates and host wiring: `Services/ByteFormatter.cs`,
  `Services/LegacyPayloadPresenter.cs`, `Services/WidgetPayloadPresenter.cs`,
  `Services/WidgetTemplates.cs`, `Services/SystemMonitorWidgetProvider.cs`,
  `SystemMonitor/Program.cs`.
- Tests: modified `SystemMonitor.Tests/GpuMemoryLabelTests.cs`,
  `SystemMonitor.Tests/WidgetLayoutFitTests.cs`,
  `SystemMonitor.Tests/WidgetPayloadPresenterTests.cs`,
  `SystemMonitor.Tests/SystemMonitor.Tests.csproj`; new tracker, integration,
  diagnostics, formatting, and normalization test files listed in `git status`.
- Documentation: `README.md` (corrected feature/backend and automatic-selection summary),
  `docs/hardware-validation.md`, `docs/refactor-plan.md`, `docs/build-and-package.md`.

Before committing, review the full staged/unstaged implementation diff, in particular the collection
and tracking changes, the test project package references, and the historical/current evidence
labels in the documentation. The modified test project links production source files explicitly;
the current full build and suite pass, with no duplicate-compile build error.

The added test-project package references for `System.Management`,
`System.Diagnostics.PerformanceCounter`, `System.Security.Permissions`, and
`LibreHardwareMonitorLib` mirror production dependencies for the linked
sources. The final build/test proves they resolve, not that every direct
reference is irreducible: no package-removal experiment was run.

### Untracked and sensitive artifacts

Untracked machine-specific diagnostic exports are under `build/inventory-*.txt`; the probe source
and captured output are under `build/lhmprobe/`. Scratch `TestArray*` files are unrelated to the
widget implementation. They remain untracked and were not deleted in this review. The tracked
`Services/WidgetTemplates.cs.probe-backup` is deleted in the current checkout.

An ignored `build/msix-output/testcert.pfx` exists. It is untracked/ignored signing material that
contains a private key; its contents were not read or printed. Keep it out of any commit. No
credential rotation, certificate removal, or cleanup was performed. The source build output and
installed package DLL are the only source/artifact correspondence compared in this review; the
current source was not repackaged or installed.

Ignored generated publish/package output is also present under `build/publish-temp/`,
`build/package-root/`, and `build/msix-output/`; do not stage those outputs as source. No generated
binary or package was deleted during this review.

### Remaining work classification

- **Demonstrated blockers for an installed-current-source candidate:** the installed provider DLL
  differs from the fresh build; installation/update is outside this review and requires user
  approval.
- **Accepted limitations:** standard-user CPU temperature is N/A on the tested hardware; manual
  GPU picker and refresh control are absent; development signing is self-signed.
- **Validation gaps:** screenshot provenance, current-candidate stale-notice fit and live lifecycle
  callbacks, elevated CPU-temperature comparison, AMD hardware, and physical GPU grace transitions.
- **No additional implementation defect was demonstrated by this acceptance review.**

Suggested commit message (do not execute):

```
Harden telemetry collection and widget presentation

Protect sensor snapshots and retained readings, track GPU telemetry by device,
and present compact capacity and freshness-aware widget fields. Add collection,
selection, formatting, diagnostics, and presentation regression coverage.

Verified: 598 tests pass; Release win-x64 build succeeds with zero warnings
and errors. Current installed-host DLL differs from the source build, and
visual screenshots remain provisional; no current-host lifecycle or stale
notice fit is claimed.
```

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    public sealed class SystemMetricsService : IMetricsCollector, IDisposable
    {
        /// <summary>
        /// The backend names used in the diagnostic log.
        /// </summary>
        private const string Backend = "collector";

        private readonly SystemMetrics _model;
        private readonly ILhmSensorSource? _sensorSource;

        /// <summary>
        /// Tracks which sensors are unavailable and explains why, without
        /// repeating the same explanation on every cycle.
        /// </summary>
        private readonly SensorAvailability _availability;

        /// <summary>
        /// The most recent device-identified snapshot. Null until the sensor
        /// backend has produced one.
        /// </summary>
        private MetricsSnapshot? _lastSnapshot;

        /// <summary>
        /// The devices the sensor backend reported during the current
        /// collection: the CPUs and the GPUs, with their identities.
        ///
        /// <para>
        /// The published snapshot carries the merged scalar metrics but not
        /// the device list, because the list is what a multi-device widget
        /// needs: the GPU names, the vendors, the integration classes, and
        /// the per-device identity a user selection is stored against. The
        /// list is kept here so it can be attached to the published
        /// snapshot.
        /// </para>
        /// </summary>
        private IReadOnlyList<HardwareDevice> _backendCpus =
            Array.Empty<HardwareDevice>();

        private IReadOnlyList<HardwareDevice> _backendGpus =
            Array.Empty<HardwareDevice>();

        private IReadOnlyList<DeviceReading> _backendReadings =
            Array.Empty<DeviceReading>();

        // Fallback values from the legacy vendor-specific collectors. The
        // device-identified backend takes precedence when it can read a
        // metric; these fill in when it cannot. They are kept separate so a
        // blocked sensor never wipes a working value, and so a value is
        // never left stale in the model as if it were current.
        private ScalarCandidate? _fallbackCpuClock;
        private ScalarCandidate? _fallbackGpuLoad;
        private ScalarCandidate? _fallbackGpuTemperature;
        private ScalarCandidate? _fallbackGpuVramUsed;
        private ScalarCandidate? _fallbackGpuVramTotal;

        // Device-identified readings from the backend, for the same metrics.
        private ScalarCandidate? _backendCpuTemperature;
        private ScalarCandidate? _backendCpuClock;
        private ScalarCandidate? _backendGpuLoad;
        private ScalarCandidate? _backendGpuTemperature;
        private ScalarCandidate? _backendGpuVramUsed;
        private ScalarCandidate? _backendGpuVramTotal;

        // The device each flattened GPU figure describes. The figures come
        // from different sources — the device-identified backend, the
        // reconciled NVIDIA fallback, the Windows counters — and those
        // sources do not always describe the same card. Each candidate
        // carries its own device id, so a presenter can check that a figure
        // is about to be shown for the device it actually came from.

        // The metrics the NVIDIA command-line fallback supplied, for metrics
        // the backend could not read. Null when the fallback was not
        // consulted or produced nothing reconcilable.
        private NvidiaFallbackMetrics? _nvidiaFallbackMetrics;

        // The time the NVIDIA fallback's readings were taken. The fallback
        // is consulted once per cycle, so its figures carry the time of
        // that single read.
        private DateTimeOffset _nvidiaFallbackReadAt;

        // The outcome of each system-level collector, per metric. The
        // mutable model keeps the last value a collector managed to read,
        // so without this state a collector that never succeeded would
        // report a zero that was never measured.
        private readonly ScalarCollectorState _cpuLoadState = new();
        private readonly ScalarCollectorState _memoryState = new();
        private readonly ScalarCollectorState _diskState = new();
        private readonly ScalarCollectorState _networkState = new();
        private readonly ScalarCollectorState _cpuClockState = new();
        private readonly ScalarCollectorState _cpuTemperatureState = new();
        private readonly ScalarCollectorState _gpuLoadState = new();
        private readonly ScalarCollectorState _gpuTemperatureState = new();
        private readonly ScalarCollectorState _gpuVramState = new();
        private readonly ScalarCollectorState _acpiThermalZoneState = new();

        // The metric names the scalar collectors publish under.
        private const string CpuLoadMetric = "cpu-load";
        private const string RamUsedMetric = "ram-used";
        private const string RamTotalMetric = "ram-total";
        private const string RamPercentMetric = "ram-percent";
        private const string DiskUsedMetric = "disk-used";
        private const string DiskTotalMetric = "disk-total";
        private const string DiskPercentMetric = "disk-percent";
        private const string NetworkDownMetric = "network-down";
        private const string NetworkUpMetric = "network-up";
        private const string CpuClockMetric = "cpu-clock";
        private const string CpuTemperatureMetric = "cpu-temperature";
        private const string GpuLoadMetric = "gpu-load";
        private const string GpuTemperatureMetric = "gpu-temperature";
        private const string GpuVramUsedMetric = "gpu-vram-used";
        private const string GpuVramTotalMetric = "gpu-vram-total";
        private const string AcpiThermalZoneMetric = "acpi-thermal-zone-temperature";

        // The provenance of each scalar collector's readings.
        private const string CpuLoadSource = "Windows CPU counters";
        private const string RamSource = "GlobalMemoryStatusEx";
        private const string DiskSource = "DriveInfo";
        private const string NetworkSource = "NetworkInterface";

        // CPU load sampling state.
        private ulong _prevIdle;
        private ulong _prevKernel;
        private ulong _prevUser;

        // Network baseline state, keyed by interface ID.
        private readonly ConcurrentDictionary<string, InterfaceSample> _networkBaselines = new();
        private readonly NetworkDeltaCalculator _networkDeltaCalculator = new();
        private readonly GpuService _gpuService = new();
        private readonly NvidiaGpuFallback _nvidiaFallback;

        public SystemMetricsService(SystemMetrics model)
            : this(model, new NvidiaGpuFallback())
        {
        }

        /// <summary>
        /// Creates the service with an explicit NVIDIA fallback, for tests
        /// that supply a fake process runner.
        /// </summary>
        public SystemMetricsService(SystemMetrics model, NvidiaGpuFallback nvidiaFallback)
        {
            _model = model;
            _nvidiaFallback = nvidiaFallback;
            _availability = new SensorAvailability();

            if (GetSystemTimes(out var idle, out var kernel, out var user))
            {
                _prevIdle = FileTimeToUInt64(idle);
                _prevKernel = FileTimeToUInt64(kernel);
                _prevUser = FileTimeToUInt64(user);
            }

            _sensorSource = CreateSensorSource();
        }

        /// <summary>
        /// Creates the sensor backend. The backend performs no hardware access
        /// until its first read, so a failure here does not prevent the widget
        /// from starting.
        /// </summary>
        private static ILhmSensorSource? CreateSensorSource()
        {
            try
            {
                return new LibreHardwareMonitorSource();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Shared.Error(
                    "LHM",
                    "create",
                    "failed",
                    detail: DiagnosticDetail.Describe(ex));
                return null;
            }
        }

        /// <summary>
        /// Collects all readings sequentially into one completed immutable
        /// snapshot. The timestamp captured at the start of collection is the
        /// sample timestamp used for <see cref="MetricsSnapshot.Timestamp"/>,
        /// not the time the payload is serialized.
        ///
        /// Each collector is independently wrapped in try/catch so that a
        /// failure in one collector does not prevent unrelated collectors
        /// from running. The snapshot is always returned, even if every
        /// collector fails — unavailable metrics are reported as
        /// Unsupported/Failed rather than as zero.
        /// </summary>
        /// <summary>
        /// Collects all readings sequentially into one completed immutable
        /// snapshot. Cancellation is cooperative and is handled by the caller
        /// (the pipeline checks the token before and after each cycle); the
        /// individual collectors are synchronous WMI/native calls that cannot
        /// be interrupted mid-flight.
        /// </summary>
        public MetricsSnapshot Collect()
        {
            var timestamp = DateTimeOffset.UtcNow;

            // Run every collector. Each is independently protected so a
            // failure in one does not abort the rest.
            UpdateCpu();
            UpdateRam();
            UpdateDisk();
            UpdateNetwork();
            UpdateCpuTemp();
            UpdateCpuClock();

            // The device-identified backend runs before the fallback
            // collectors, so the fallback knows which metrics the backend
            // already supplied and can skip its subprocess entirely when
            // the backend covered everything.
            UpdateSensorBackend();

            // The NVIDIA command-line fallback, consulted only for a metric
            // the backend could not supply.
            UpdateNvidiaFallback();

            UpdateGpuLoad();
            UpdateGpuTemp();
            UpdateGpuVram();

            // Merge backend readings over fallback values so the model holds
            // the final merged values before the snapshot is built.
            PublishMergedMetrics();

            return BuildSnapshot(timestamp);
        }

        /// <summary>
        /// The provenance of every reading the device-identified sensor
        /// backend supplies.
        ///
        /// <para>
        /// The backend is the primary source for the CPU temperature and
        /// clock and for the GPU load, temperature and memory. The ACPI
        /// thermal-zone reading is a different, unidentified sensor and is
        /// published separately as <c>acpiThermalZoneTemperatureC</c>, so
        /// there is no fallback source for the CPU temperature.
        /// </para>
        /// </summary>
        private const string BackendSource = "LibreHardwareMonitor";

        /// <summary>
        /// The provenance of the Windows processor-class clock reading. The
        /// class is not device-identified, so this figure names no device.
        /// </summary>
        private const string CpuClockSource = "Win32_Processor";

        /// <summary>
        /// The provenance of the NVIDIA command-line fallback's readings.
        /// Its figures are reconciled to exactly one device, so they are
        /// attributed to that device.
        /// </summary>
        private const string NvidiaSource = "nvidia-smi";

        /// <summary>
        /// The provenance of the Windows GPU counters' readings. The
        /// counters report one figure per adapter and name no device, so
        /// these figures are never presented for a card.
        /// </summary>
        private const string GpuCounterSource = "Windows GPU counters";

        /// <summary>
        /// The sources consulted for the GPU load figure, used when none of
        /// them could supply a value. It names every source that was tried,
        /// so a failed figure says what was actually attempted.
        /// </summary>
        private const string GpuLoadAttemptedSources = BackendSource + " + " + NvidiaSource;

        /// <summary>
        /// The sources consulted for the GPU temperature figure, used when
        /// none of them could supply a value.
        /// </summary>
        private const string GpuTemperatureAttemptedSources = BackendSource + " + " + NvidiaSource;

        /// <summary>
        /// The sources consulted for the GPU memory figures, used when none
        /// of them could supply a value.
        /// </summary>
        private const string GpuVramAttemptedSources =
            BackendSource + " + " + NvidiaSource + " + " + GpuCounterSource;

        /// </summary>
        private MetricsSnapshot BuildSnapshot(DateTimeOffset timestamp)
        {
            return MetricsSnapshot.Create(
                timestamp,
                cpus: _backendCpus,
                gpus: _backendGpus,
                readings: _backendReadings,
                cpuLoadPercent: _cpuLoadState.ToMeasurement(CpuLoadMetric),
                ramUsedBytes: _memoryState.ToMeasurement(RamUsedMetric),
                ramTotalBytes: _memoryState.ToMeasurement(RamTotalMetric),
                ramPercent: _memoryState.ToMeasurement(RamPercentMetric),
                diskUsedBytes: _diskState.ToMeasurement(DiskUsedMetric),
                diskTotalBytes: _diskState.ToMeasurement(DiskTotalMetric),
                diskPercent: _diskState.ToMeasurement(DiskPercentMetric),
                networkDownloadBytesPerSec: _networkState.ToMeasurement(NetworkDownMetric),
                networkUploadBytesPerSec: _networkState.ToMeasurement(NetworkUpMetric),
                cpuClockMHz: _cpuClockState.ToMeasurement(CpuClockMetric),
                cpuTemperatureC: _cpuTemperatureState.ToMeasurement(CpuTemperatureMetric),
                gpuLoadPercent: _gpuLoadState.ToMeasurement(GpuLoadMetric),
                gpuTemperatureC: _gpuTemperatureState.ToMeasurement(GpuTemperatureMetric),
                gpuVramUsedBytes: _gpuVramState.ToMeasurement(GpuVramUsedMetric),
                gpuVramTotalBytes: _gpuVramState.ToMeasurement(GpuVramTotalMetric),
                acpiThermalZoneTemperatureC: _acpiThermalZoneState.ToMeasurement(AcpiThermalZoneMetric),
                gpuLoadDeviceId: _gpuLoadState.DeviceIdFor(GpuLoadMetric),
                gpuTemperatureDeviceId: _gpuTemperatureState.DeviceIdFor(GpuTemperatureMetric),
                gpuVramUsedDeviceId: _gpuVramState.DeviceIdFor(GpuVramUsedMetric),
                gpuVramTotalDeviceId: _gpuVramState.DeviceIdFor(GpuVramTotalMetric));
        }

        public void Dispose()
        {
            try
            {
                _sensorSource?.Dispose();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Shared.Warning(
                    "LHM",
                    "dispose",
                    "failed",
                    detail: DiagnosticDetail.Describe(ex));
            }

            try
            {
                _nvidiaFallback.Dispose();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Shared.Warning(
                    NvidiaGpuFallback.BackendName,
                    "dispose",
                    "failed",
                    detail: DiagnosticDetail.Describe(ex));
            }
        }

        // ------------------------------------------------------------------
        // Collectors — each runs sequentially within Collect() and is
        // independently protected against exceptions.
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs one collector, measuring how long it takes and reporting
        /// the outcome to the diagnostic log.
        ///
        /// <para>
        /// A collector that throws is a failure of the <i>collector</i>,
        /// not of the collection as a whole: the other collectors still
        /// run, and the affected metric is reported as unavailable rather
        /// than as zero. The log records the backend, the operation, the
        /// status and the elapsed time, so a collector that is failing
        /// slowly is distinguishable from one that is failing fast.
        /// </para>
        /// </summary>
        /// <param name="operation">The collector's name, e.g. "cpu-load".</param>
        /// <param name="collect">The work to perform.</param>
        private void RunCollector(string operation, Action collect)
        {
            var started = Stopwatch.StartNew();

            try
            {
                collect();

                DiagnosticLog.Shared.Debug(
                    Backend,
                    operation,
                    "ok",
                    elapsed: started.Elapsed);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Shared.Error(
                    Backend,
                    operation,
                    "failed",
                    elapsed: started.Elapsed,
                    detail: DiagnosticDetail.Describe(ex));

                _availability.ReportUnavailable(operation, SensorAvailability.Reasons.ReadFailed);
            }
        }

        private void UpdateCpu()
        {
            RunCollector("cpu-load", () =>
            {
                if (!GetSystemTimes(out var idle, out var kernel, out var user))
                {
                    // The read failed. The previous value is kept but is
                    // reported as stale, so a failed read is never presented
                    // as a measured load.
                    _cpuLoadState.ReportNoValue(CpuLoadMetric, "%", CpuLoadSource);
                    return;
                }

                var idle64 = FileTimeToUInt64(idle);
                var kernel64 = FileTimeToUInt64(kernel);
                var user64 = FileTimeToUInt64(user);

                var sys = (kernel64 - _prevKernel) + (user64 - _prevUser);
                var idleDiff = idle64 - _prevIdle;

                if (sys > 0)
                {
                    var usage = (1.0 - (double)idleDiff / sys) * 100.0;
                    var clamped = ThermalMath.ClampPercent(usage);

                    if (clamped.HasValue)
                    {
                        _model.CpuPercent = clamped.Value;
                        _cpuLoadState.ReportSuccess(
                            CpuLoadMetric, clamped.Value, "%", CpuLoadSource, DateTimeOffset.UtcNow);
                    }
                    else
                    {
                        // The ratio did not produce a finite percentage. There
                        // is no load figure to report: a zero here would be a
                        // number that was never measured.
                        _cpuLoadState.ReportNoValue(CpuLoadMetric, "%", CpuLoadSource);
                    }
                }
                else
                {
                    // No time elapsed since the previous sample: there is no
                    // rate to report yet. This is not a failure, but it is
                    // also not a measurement.
                    _cpuLoadState.ReportNoValue(CpuLoadMetric, "%", CpuLoadSource);
                }

                _prevIdle = idle64;
                _prevKernel = kernel64;
                _prevUser = user64;
            });
        }

        private void UpdateRam()
        {
            RunCollector("memory", () =>
            {
                var mem = new MEMORYSTATUSEX();
                mem.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
                if (!GlobalMemoryStatusEx(ref mem))
                {
                    // The read failed. The previous value is kept but is
                    // reported as stale, so a failed read is never presented
                    // as a measured zero.
                    _memoryState.ReportNoValue(RamUsedMetric, "bytes", RamSource);
                    _memoryState.ReportNoValue(RamTotalMetric, "bytes", RamSource);
                    _memoryState.ReportNoValue(RamPercentMetric, "%", RamSource);
                    return;
                }

                var used = mem.ullTotalPhys - mem.ullAvailPhys;
                var percent = ThermalMath.PercentOf(used, mem.ullTotalPhys);

                _model.RamUsedBytes = used;
                _model.RamTotalBytes = mem.ullTotalPhys;

                var readAt = DateTimeOffset.UtcNow;
                _memoryState.ReportSuccess(RamUsedMetric, used, "bytes", RamSource, readAt);
                _memoryState.ReportSuccess(RamTotalMetric, mem.ullTotalPhys, "bytes", RamSource, readAt);

                if (percent.HasValue)
                {
                    _model.RamPercent = percent.Value;
                    _memoryState.ReportSuccess(RamPercentMetric, percent.Value, "%", RamSource, readAt);
                }
                else
                {
                    // A total of zero carries no percentage. The used and
                    // total figures are still real readings, so they are
                    // kept; the percentage is reported as unavailable
                    // rather than as a measured zero.
                    _memoryState.ReportNoValue(RamPercentMetric, "%", RamSource);
                }
            });
        }

        private void UpdateDisk()
        {
            RunCollector("disk", () =>
            {
                // Use primary system drive for used percentage
                var systemDrive = Environment.GetFolderPath(Environment.SpecialFolder.System).Substring(0, 3);
                var drive = new System.IO.DriveInfo(systemDrive);
                if (!drive.IsReady)
                {
                    // The drive could not be read — it may be absent, or
                    // the media may have been removed. The previous value is
                    // kept but is reported as stale.
                    _diskState.ReportNoValue(DiskUsedMetric, "bytes", DiskSource);
                    _diskState.ReportNoValue(DiskTotalMetric, "bytes", DiskSource);
                    _diskState.ReportNoValue(DiskPercentMetric, "%", DiskSource);
                    return;
                }

                var used = (drive.TotalSize - drive.TotalFreeSpace);
                var percent = ThermalMath.PercentOf(used, drive.TotalSize);

                _model.DiskUsedBytes = (long)used;
                _model.DiskTotalBytes = (long)drive.TotalSize;

                var readAt = DateTimeOffset.UtcNow;
                _diskState.ReportSuccess(DiskUsedMetric, used, "bytes", DiskSource, readAt);
                _diskState.ReportSuccess(DiskTotalMetric, drive.TotalSize, "bytes", DiskSource, readAt);

                if (percent.HasValue)
                {
                    _model.DiskPercent = percent.Value;
                    _diskState.ReportSuccess(DiskPercentMetric, percent.Value, "%", DiskSource, readAt);
                }
                else
                {
                    // A total of zero carries no percentage. The used and
                    // total figures are still real readings, so they are
                    // kept; the percentage is reported as unavailable
                    // rather than as a measured zero.
                    _diskState.ReportNoValue(DiskPercentMetric, "%", DiskSource);
                }
            });
        }

        private void UpdateNetwork()
        {
            RunCollector("network", () =>
            {
                var now = DateTimeOffset.UtcNow;
                var currentIds = new HashSet<string>();
                double totalRxPerSec = 0;
                double totalTxPerSec = 0;
                var anyValidDelta = false;

                foreach (var sample in CollectInterfaceSamples())
                {
                    currentIds.Add(sample.InterfaceId);

                    if (_networkBaselines.TryGetValue(sample.InterfaceId, out var previous))
                    {
                        var delta = _networkDeltaCalculator.Calculate(
                            sample.InterfaceId, sample.BytesReceived, sample.BytesSent, now, previous);
                        if (delta.HasValidDelta)
                        {
                            totalRxPerSec += delta.BytesReceivedPerSec;
                            totalTxPerSec += delta.BytesSentPerSec;
                            anyValidDelta = true;
                        }
                        // Always rebaseline to the current sample so counter
                        // resets and wraps do not produce negative or spike rates.
                        _networkBaselines[sample.InterfaceId] = sample;
                    }
                    else
                    {
                        // First time seeing this interface: establish a baseline,
                        // emit no rate until the next sample.
                        _networkBaselines[sample.InterfaceId] = sample;
                    }
                }

                // Drop baselines for interfaces that disappeared so they are
                // rebaselined (not spiked) if they reappear later.
                foreach (var id in _networkBaselines.Keys.Where(id => !currentIds.Contains(id)).ToList())
                {
                    _networkBaselines.TryRemove(id, out _);
                }

                _model.NetworkDownloadBytesPerSec = totalRxPerSec;
                _model.NetworkUploadBytesPerSec = totalTxPerSec;

                if (anyValidDelta)
                {
                    // At least one interface produced a rate. A rate of zero
                    // here is a measured zero — the counters did not move.
                    _networkState.ReportSuccess(
                        NetworkDownMetric, totalRxPerSec, "bytes/sec", NetworkSource, now);
                    _networkState.ReportSuccess(
                        NetworkUpMetric, totalTxPerSec, "bytes/sec", NetworkSource, now);
                }
                else
                {
                    // No interface had a baseline yet, or every counter
                    // reset. There is no rate to report: a zero here would
                    // be a number that was never measured.
                    _networkState.ReportNoValue(NetworkDownMetric, "bytes/sec", NetworkSource);
                    _networkState.ReportNoValue(NetworkUpMetric, "bytes/sec", NetworkSource);
                }
            });
        }

        private List<InterfaceSample> CollectInterfaceSamples()
        {
            var samples = new List<InterfaceSample>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!NetworkInterfacePolicy.ShouldInclude(ni))
                    continue;
                try
                {
                    var stats = ni.GetIPv4Statistics();
                    samples.Add(new InterfaceSample(ni.Id, stats.BytesReceived, stats.BytesSent, DateTimeOffset.UtcNow));
                }
                catch (Exception ex)
                {
                    // One interface that cannot be queried is skipped; the
                    // others are still counted. The interface's identity is
                    // not logged — the name and the id are machine-specific.
                    DiagnosticLog.Shared.Warning(
                        Backend,
                        "network-interface",
                        "unavailable",
                        detail: DiagnosticDetail.Describe(ex));
                }
            }
            return samples;
        }

        private void UpdateCpuTemp()
        {
            RunCollector("acpi-thermal-zone", () =>
            {
                // The state is told there is no value before the query
                // runs, so a query that throws still leaves a described
                // attempt behind rather than an unexplained absence.
                _acpiThermalZoneState.ReportNoValue(
                    AcpiThermalZoneMetric, "°C", "MSAcpi_ThermalZoneTemperature");

                using var searcher = new ManagementObjectSearcher("root\\WMI", "SELECT * FROM MSAcpi_ThermalZoneTemperature");
                var collection = searcher.Get();
                var temps = collection.Cast<ManagementObject>()
                    .Select(mo => TryGetDouble(mo, "CurrentTemperature"))
                    .Where(v => v.HasValue)
                    .Select(v => ThermalMath.TenthsKelvinToCelsius(v!.Value))
                    .Where(t => t > 0 && t < 200)
                    .ToList();
                if (temps.Count > 0)
                {
                    // ACPI thermal-zone sensors are not component-identified.
                    // Report them separately, never as confirmed CPU temperature.
                    var average = temps.Average();
                    _model.AcpiThermalZoneTemperatureC = average;
                    _acpiThermalZoneState.ReportSuccess(
                        AcpiThermalZoneMetric, average, "°C",
                        "MSAcpi_ThermalZoneTemperature", DateTimeOffset.UtcNow,
                        null);
                }
                else
                {
                    // No zone reported a usable reading. The previous value
                    // is kept but marked stale, so it is not presented as if
                    // it had just been measured.
                    _acpiThermalZoneState.ReportNoValue(
                        AcpiThermalZoneMetric, "°C", "MSAcpi_ThermalZoneTemperature");
                }
            });
        }

        /// <summary>
        /// Reads the NVIDIA command-line fallback and keeps the metrics that
        /// reconcile to exactly one device.
        ///
        /// The fallback is skipped entirely when the backend already supplied
        /// every metric it could supply — which is the normal case on a
        /// machine where the backend works. Only a metric the backend could
        /// not read is taken from here.
        /// </summary>
        private void UpdateNvidiaFallback()
        {
            _nvidiaFallbackMetrics = null;

            RunCollector("gpu-nvidia-fallback", () =>
            {
                // The backend covers load, temperature and VRAM. When it read
                // all three, the fallback has nothing to contribute and no
                // subprocess is started at all.
                if (!NvidiaGpuFallbackPolicy.ShouldInvoke(
                        _backendGpuTemperature?.Value,
                        _backendGpuVramUsed?.Value is { } used ? (ulong)used : null,
                        _backendGpuVramTotal?.Value is { } total ? (ulong)total : null,
                        _backendGpuLoad?.Value))
                {
                    return;
                }

                // Reconciliation needs the backend's device identities, so
                // they are collected from the last projection.
                var backendDevices = CollectBackendGpuIdentities();
                if (backendDevices.Count == 0)
                {
                    // Without a device identity there is nothing to reconcile
                    // a reading against, and a reading must never be
                    // attributed to a device chosen by position.
                    return;
                }

                var samples = _nvidiaFallback.Read();
                if (samples.Count == 0)
                    return;

                // The time the fallback's readings were taken. The fallback
                // is consulted once per cycle, so this is the time of the
                // single subprocess invocation.
                _nvidiaFallbackReadAt = DateTimeOffset.UtcNow;

                _nvidiaFallbackMetrics = NvidiaGpuFallbackSelector.Select(samples, backendDevices);
            });
        }

        /// <summary>
        /// The identity of each GPU the backend reported, used to reconcile
        /// the fallback's readings.
        /// </summary>
        private List<BackendGpuIdentity> CollectBackendGpuIdentities()
        {
            var identities = new List<BackendGpuIdentity>();

            if (_lastSnapshot is null)
                return identities;

            foreach (var gpu in _lastSnapshot.Gpus)
            {
                if (string.IsNullOrWhiteSpace(gpu.PciDeviceKey))
                    continue;

                identities.Add(new BackendGpuIdentity(gpu.DeviceId, gpu.PciDeviceKey));
            }

            return identities;
        }

        /// <summary>
        /// Collects the candidate GPU utilization reading.
        ///
        /// <para>
        /// Two sources may supply it: the device-identified backend, and the
        /// reconciled NVIDIA fallback. The backend wins whenever it read the
        /// sensor — it names the device the value came from, and it is the
        /// primary source by design. The fallback fills in only for a metric
        /// the backend could not read.
        /// </para>
        /// </summary>
        private void UpdateGpuLoad()
        {
            RunCollector("gpu-load", () =>
            {
                // The fallback's reading was reconciled to exactly one
                // device, and that device is named here. It is not
                // necessarily the backend's primary GPU, so the attribution
                // follows the figure rather than the source's priority.
                if (_nvidiaFallbackMetrics is { } fallback
                    && fallback.UtilizationPercent is { } utilization
                    && fallback.DeviceId is { } fallbackDeviceId)
                {
                    _fallbackGpuLoad = new ScalarCandidate(
                        utilization,
                        "%",
                        NvidiaSource,
                        _nvidiaFallbackReadAt,
                        fallbackDeviceId);
                }
                else
                {
                    _fallbackGpuLoad = null;
                }
            });
        }

        /// <summary>
        /// Collects the candidate GPU temperature readings.
        ///
        /// <para>
        /// Two sources may supply it: the device-identified backend, and the
        /// reconciled NVIDIA fallback. The backend wins whenever it read the
        /// sensor — it names the device the value came from, and it is the
        /// primary source by design. The fallback fills in only for a metric
        /// the backend could not read.
        /// </para>
        /// </summary>
        private void UpdateGpuTemp()
        {
            RunCollector("gpu-temperature", () =>
            {
                // The fallback's reading was reconciled to exactly one
                // device, and that device is named here. It is not
                // necessarily the backend's primary GPU, so the attribution
                // follows the figure rather than the source's priority.
                if (_nvidiaFallbackMetrics is { } fallback
                    && fallback.TemperatureC is { } temperature
                    && fallback.DeviceId is { } fallbackDeviceId)
                {
                    _fallbackGpuTemperature = new ScalarCandidate(
                        temperature,
                        "°C",
                        NvidiaSource,
                        _nvidiaFallbackReadAt,
                        fallbackDeviceId);
                }
                else
                {
                    _fallbackGpuTemperature = null;
                }
            });
        }

        /// <summary>
        /// Collects the candidate GPU dedicated-memory readings.
        ///
        /// <para>
        /// The NVIDIA fallback is preferred over the Windows counters,
        /// because it is device-identified: its reading was reconciled to
        /// exactly one card. The Windows counters report one figure per
        /// adapter and cannot say which adapter is the one being presented,
        /// so their figure is recorded without a device and is therefore
        /// never presented for any card.
        /// </para>
        /// </summary>
        private void UpdateGpuVram()
        {
            RunCollector("gpu-vram-windows", () =>
            {
                var (used, total) = _gpuService.GetGpuVramBestEffort();
                var readAt = DateTimeOffset.UtcNow;

                _fallbackGpuVramUsed = used.HasValue
                    ? new ScalarCandidate(used.Value, "B", GpuCounterSource, readAt, null)
                    : null;
                _fallbackGpuVramTotal = total.HasValue
                    ? new ScalarCandidate(total.Value, "B", GpuCounterSource, readAt, null)
                    : null;

                if (_nvidiaFallbackMetrics is { } fallback)
                {
                    if (fallback.VramUsedBytes is { } fallbackUsed)
                    {
                        _fallbackGpuVramUsed = new ScalarCandidate(
                            fallbackUsed, "B", NvidiaSource, _nvidiaFallbackReadAt, fallback.DeviceId);
                    }

                    if (fallback.VramTotalBytes is { } fallbackTotal)
                    {
                        _fallbackGpuVramTotal = new ScalarCandidate(
                            fallbackTotal, "B", NvidiaSource, _nvidiaFallbackReadAt, fallback.DeviceId);
                    }
                }
            });
        }

        private void UpdateCpuClock()
        {
            RunCollector("cpu-clock", () =>
            {
                using var searcher = new ManagementObjectSearcher("root\\CIMV2",
                    "SELECT CurrentClockSpeed FROM Win32_Processor");
                var speeds = searcher.Get()
                    .Cast<ManagementObject>()
                    .Select(mo => TryGetDouble(mo, "CurrentClockSpeed"))
                    .Where(v => v.HasValue)
                    .Select(v => v!.Value)
                    .ToList();
                if (speeds.Count > 0)
                {
                    _fallbackCpuClock = new ScalarCandidate(
                        speeds.Average(),
                        "MHz",
                        CpuClockSource,
                        DateTimeOffset.UtcNow,
                        null);
                }
                else
                {
                    _fallbackCpuClock = null;
                }
            });
        }

        /// <summary>
        /// Reads the device-identified sensor backend and updates the backend
        /// fields used by <see cref="PublishMergedMetrics"/>.
        ///
        /// Runs after the vendor-neutral Windows collectors so those values are
        /// never lost when the backend is unavailable. Readings the backend
        /// cannot supply are simply absent: an unavailable sensor is never
        /// presented as zero.
        /// </summary>
        private void UpdateSensorBackend()
        {
            if (_sensorSource is null)
                return;

            IReadOnlyList<LhmDeviceDescriptor> devices;
            try
            {
                devices = _sensorSource.Read();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Shared.Error(
                    "LHM",
                    "read",
                    "failed",
                    detail: DiagnosticDetail.Describe(ex));

                _availability.ReportUnavailable(
                    "device-sensors",
                    SensorAvailability.Reasons.ReadFailed);

                return;
            }

            if (devices.Count == 0)
            {
                // The backend reported no devices. Its previous readings are
                // dropped rather than carried forward: a device that is gone
                // must not keep contributing a figure, and the values are
                // re-offered by the fallback sources below.
                _lastSnapshot = null;
                _backendCpus = Array.Empty<HardwareDevice>();
                _backendGpus = Array.Empty<HardwareDevice>();
                _backendReadings = Array.Empty<DeviceReading>();
                _backendCpuTemperature = null;
                _backendCpuClock = null;
                _backendGpuLoad = null;
                _backendGpuTemperature = null;
                _backendGpuVramUsed = null;
                _backendGpuVramTotal = null;

                _model.GpuLoadPercent = null;
                return;
            }

            var projection = LhmSnapshotMapper.Project(devices);

            // The time the sensors were read. It is recorded on every
            // reading the backend produced, so the widget's "last updated"
            // figure says when the number was measured rather than when
            // the payload was built.
            var readAt = DateTimeOffset.UtcNow;
            var snapshot = projection.ToSnapshot(readAt);

            var primaryCpu = LhmSnapshotMapper.SelectPrimaryCpu(projection.Cpus);
            var primaryGpu = LhmSnapshotMapper.SelectPrimaryGpu(projection.Gpus);

            _lastSnapshot = snapshot;

            // The device list and the per-device readings are kept so the
            // published snapshot can carry them: a multi-device widget needs
            // the names, the vendors and the per-device identity, not just
            // the merged scalars.
            _backendCpus = projection.Cpus;
            _backendGpus = projection.Gpus;
            _backendReadings = projection.Readings;

            _model.GpuLoadPercent = ReadValue(snapshot, primaryGpu?.DeviceId, LhmSensorSelection.GpuLoadMetric);

            // The backend's figures describe the backend's primary CPU and
            // primary GPU. Each carries the device id, so the merge can
            // attribute it and the presenter can check the attribution
            // before showing it under a device's name.
            _backendCpuTemperature = Candidate(
                snapshot, primaryCpu?.DeviceId, LhmSensorSelection.CpuTemperatureMetric,
                "°C", BackendSource, readAt);

            _backendCpuClock = Candidate(
                snapshot, primaryCpu?.DeviceId, LhmSensorSelection.CpuClockMetric,
                "MHz", BackendSource, readAt);

            _backendGpuLoad = Candidate(
                snapshot, primaryGpu?.DeviceId, LhmSensorSelection.GpuLoadMetric,
                "%", BackendSource, readAt);

            _backendGpuTemperature = Candidate(
                snapshot, primaryGpu?.DeviceId, LhmSensorSelection.GpuTemperatureMetric,
                "°C", BackendSource, readAt);

            // The memory figures keep the sensor's own name in their
            // provenance: which memory pool a figure describes is
            // established by that name, and it is what lets a presenter
            // refuse to pair a dedicated-memory figure with a
            // shared-memory one.
            _backendGpuVramUsed = SensorCandidate(
                snapshot, primaryGpu?.DeviceId, LhmSensorSelection.GpuMemoryUsedMetric,
                "B", readAt);

            _backendGpuVramTotal = SensorCandidate(
                snapshot, primaryGpu?.DeviceId, LhmSensorSelection.GpuMemoryTotalMetric,
                "B", readAt);
        }

        /// <summary>
        /// Builds a candidate reading from the backend's projection, or null
        /// when the backend did not supply the metric.
        /// </summary>
        private static ScalarCandidate? Candidate(
            MetricsSnapshot snapshot,
            string? deviceId,
            string metric,
            string unit,
            string source,
            DateTimeOffset readAt)
        {
            var value = ReadValue(snapshot, deviceId, metric);
            if (!value.HasValue)
                return null;

            return new ScalarCandidate(value.Value, unit, source, readAt, deviceId);
        }

        /// <summary>
        /// Builds a candidate reading from the backend's projection, keeping
        /// the sensor's own name in the provenance.
        ///
        /// <para>
        /// The memory figures need this: which memory pool a figure
        /// describes is established by the name of the sensor it came
        /// from, and a bare backend label throws that evidence away. A
        /// figure whose provenance names "GPU Memory Used" is dedicated
        /// video memory; one that names "Memory Used" says nothing about
        /// its pool, and the presenter must not guess.
        /// </para>
        /// </summary>
        private static ScalarCandidate? SensorCandidate(
            MetricsSnapshot snapshot,
            string? deviceId,
            string metric,
            string unit,
            DateTimeOffset readAt)
        {
            var reading = ReadMeasurement(snapshot, deviceId, metric);
            if (reading is null || !reading.Value.HasValue)
                return null;

            return new ScalarCandidate(
                reading.Value.Value,
                unit,
                reading.Source ?? BackendSource,
                readAt,
                deviceId);
        }

        /// <summary>
        /// The available measurement for a device's metric, or null when
        /// the device did not report a usable one.
        /// </summary>
        private static Measurement? ReadMeasurement(
            MetricsSnapshot snapshot,
            string? deviceId,
            string metric)
        {
            if (deviceId is null)
                return null;

            var reading = snapshot.Readings.FirstOrDefault(r => r.DeviceId == deviceId && r.Metric == metric);
            if (reading is null)
                return null;
            if (reading.Measurement.Status != MeasurementStatus.Available)
                return null;
            if (!reading.Measurement.Value.HasValue)
                return null;

            var value = reading.Measurement.Value.Value;
            if (double.IsNaN(value) || double.IsInfinity(value))
                return null;

            return reading.Measurement;
        }

        /// <summary>
        /// Publishes the merged view of the device-identified backend readings
        /// and the fallback values into the model, and records the winning
        /// candidate for each flattened figure.
        ///
        /// <para>
        /// The precedence is fixed and is the same for every metric:
        /// </para>
        ///
        /// <list type="number">
        /// <item><description>
        /// The device-identified backend. It names the device and the sensor
        /// the value came from, so it is the primary source.
        /// </description></item>
        /// <item><description>
        /// The reconciled NVIDIA fallback. It names a device too, but it is
        /// consulted only for a metric the backend could not read.
        /// </description></item>
        /// <item><description>
        /// The Windows counters. They name no device, so their figures are
        /// never presented for a card.
        /// </description></item>
        /// </list>
        ///
        /// <para>
        /// When no source can supply a metric, the model is set to null: an
        /// unavailable metric shows as unavailable rather than as a stale
        /// value, and never as zero.
        /// </para>
        /// </summary>
        private void PublishMergedMetrics()
        {
            var cpuTemperature = ScalarMergePolicy.Resolve(_backendCpuTemperature, null);
            var cpuClock = ScalarMergePolicy.Resolve(_backendCpuClock, _fallbackCpuClock);
            var gpuLoad = ScalarMergePolicy.Resolve(_backendGpuLoad, _fallbackGpuLoad);
            var gpuTemperature = ScalarMergePolicy.Resolve(_backendGpuTemperature, _fallbackGpuTemperature);
            var vramUsed = ScalarMergePolicy.Resolve(_backendGpuVramUsed, _fallbackGpuVramUsed);
            var vramTotal = ScalarMergePolicy.Resolve(_backendGpuVramTotal, _fallbackGpuVramTotal);

            _model.CpuTemperature = cpuTemperature?.Value;
            _model.CpuClockMHz = cpuClock?.Value;
            _model.GpuTemperature = gpuTemperature?.Value;
            _model.GpuVramUsedBytes = vramUsed is null ? null : (ulong)vramUsed.Value;
            _model.GpuVramTotalBytes = vramTotal is null ? null : (ulong)vramTotal.Value;

            Record(CpuTemperatureMetric, cpuTemperature, _cpuTemperatureState, BackendSource);
            Record(CpuClockMetric, cpuClock, _cpuClockState, CpuClockSource);
            Record(GpuLoadMetric, gpuLoad, _gpuLoadState, GpuLoadAttemptedSources);
            Record(GpuTemperatureMetric, gpuTemperature, _gpuTemperatureState, GpuTemperatureAttemptedSources);
            Record(GpuVramUsedMetric, vramUsed, _gpuVramState, GpuVramAttemptedSources);
            Record(GpuVramTotalMetric, vramTotal, _gpuVramState, GpuVramAttemptedSources);
        }

        /// <summary>
        /// Records the winning candidate for a metric in the collector state,
        /// so the published measurement carries the time it was read and the
        /// device it describes.
        ///
        /// <para>
        /// When no source could supply the metric, the state is told so. The
        /// previous value is kept but marked stale, so a blocked sensor does
        /// not blank out a working reading and does not present the last
        /// value as if it were current.
        /// </para>
        /// </summary>
        private static void Record(
            string metric,
            ScalarCandidate? candidate,
            ScalarCollectorState state,
            string attemptedSource)
        {
            if (candidate is not null)
            {
                state.ReportSuccess(
                    metric,
                    candidate.Value,
                    candidate.Unit,
                    candidate.Source,
                    candidate.ReadAt,
                    candidate.DeviceId);
                return;
            }

            state.ReportNoValue(metric, UnitOf(metric), attemptedSource);
        }

        /// <summary>
        /// The canonical unit of a metric, used when a metric has no value to
        /// report and the state still needs to describe the attempt.
        /// </summary>
        private static string UnitOf(string metric) => metric switch
        {
            CpuLoadMetric => "%",
            RamUsedMetric => "bytes",
            RamTotalMetric => "bytes",
            RamPercentMetric => "%",
            DiskUsedMetric => "bytes",
            DiskTotalMetric => "bytes",
            DiskPercentMetric => "%",
            NetworkDownMetric => "bytes/sec",
            NetworkUpMetric => "bytes/sec",
            CpuClockMetric => "MHz",
            CpuTemperatureMetric => "°C",
            GpuLoadMetric => "%",
            GpuTemperatureMetric => "°C",
            GpuVramUsedMetric => "B",
            GpuVramTotalMetric => "B",
            AcpiThermalZoneMetric => "°C",
            _ => string.Empty,
        };

        /// <summary>
        /// The value of a reading, or null when the reading is missing or not
        /// in the Available state. Never returns zero for unavailable data.
        /// </summary>
        private static double? ReadValue(MetricsSnapshot snapshot, string? deviceId, string metric)
        {
            if (deviceId is null)
                return null;

            var reading = snapshot.Readings.FirstOrDefault(r => r.DeviceId == deviceId && r.Metric == metric);
            if (reading is null)
                return null;
            if (reading.Measurement.Status != MeasurementStatus.Available)
                return null;
            if (!reading.Measurement.Value.HasValue)
                return null;

            var value = reading.Measurement.Value.Value;
            if (double.IsNaN(value) || double.IsInfinity(value))
                return null;

            return value;
        }

        private static double? TryGetDouble(ManagementObject mo, string prop)
        {
            try
            {
                if (mo[prop] != null && double.TryParse(mo[prop].ToString(), out var v))
                    return v;
            }
            catch { }
            return null;
        }

        #region PInvoke and helpers

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "GetSystemTimes")]
        private static extern bool GetSystemTimesNative(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

        private static bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user)
        {
            return GetSystemTimesNative(out idle, out kernel, out user);
        }

        private static ulong FileTimeToUInt64(FILETIME ft)
        {
            return ((ulong)ft.dwHighDateTime << 32) | ft.dwLowDateTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

        #endregion
    }
}

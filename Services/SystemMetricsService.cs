using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    public sealed class SystemMetricsService : IMetricsCollector, IDisposable
    {
        private readonly SystemMetrics _model;
        private readonly ILhmSensorSource? _sensorSource;

        /// <summary>
        /// The most recent device-identified snapshot. Null until the sensor
        /// backend has produced one.
        /// </summary>
        private MetricsSnapshot? _lastSnapshot;

        // Fallback values from the legacy vendor-specific collectors. The
        // device-identified backend takes precedence when it can read a
        // metric; these fill in when it cannot. They are kept separate so a
        // blocked sensor never wipes a working value, and so a value is
        // never left stale in the model as if it were current.
        private double? _fallbackCpuClockMHz;
        private double? _fallbackGpuTemperature;
        private ulong? _fallbackGpuVramUsedBytes;
        private ulong? _fallbackGpuVramTotalBytes;

        // Device-identified readings from the backend, for the same metrics.
        private double? _backendCpuTemperature;
        private double? _backendCpuClockMHz;
        private double? _backendGpuTemperature;
        private ulong? _backendGpuVramUsedBytes;
        private ulong? _backendGpuVramTotalBytes;

        // CPU load sampling state.
        private ulong _prevIdle;
        private ulong _prevKernel;
        private ulong _prevUser;

        // Network baseline state, keyed by interface ID.
        private readonly ConcurrentDictionary<string, InterfaceSample> _networkBaselines = new();
        private readonly NetworkDeltaCalculator _networkDeltaCalculator = new();
        private readonly GpuService _gpuService = new();

        public SystemMetricsService(SystemMetrics model)
        {
            _model = model;

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
                Debug.WriteLine($"Sensor backend unavailable: {ex.Message}");
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
            UpdateGpuBestEffort();
            UpdateCpuTemp();
            UpdateGpuTemp();
            UpdateGpuVram();
            UpdateCpuClock();
            UpdateSensorBackend();

            // Merge backend readings over fallback values so the model holds
            // the final merged values before the snapshot is built.
            PublishMergedMetrics();

            return BuildSnapshot(timestamp);
        }

        /// <summary>
        /// Builds an immutable snapshot from the current model values. The
        /// supplied timestamp is the sample time captured at the start of
        /// collection, not the serialization time.
        /// </summary>
        private MetricsSnapshot BuildSnapshot(DateTimeOffset timestamp)
        {
            return MetricsSnapshot.Create(
                timestamp,
                cpuLoadPercent: Measurement.AvailableValue(_model.CpuPercent, "%", "Windows CPU counters"),
                ramUsedBytes: Measurement.AvailableValue((double)_model.RamUsedBytes, "bytes", "GlobalMemoryStatusEx"),
                ramTotalBytes: Measurement.AvailableValue((double)_model.RamTotalBytes, "bytes", "GlobalMemoryStatusEx"),
                ramPercent: Measurement.AvailableValue(_model.RamPercent, "%", "GlobalMemoryStatusEx"),
                diskUsedBytes: Measurement.AvailableValue(Math.Max(0, (double)_model.DiskUsedBytes), "bytes", "DriveInfo"),
                diskTotalBytes: Measurement.AvailableValue(Math.Max(0, (double)_model.DiskTotalBytes), "bytes", "DriveInfo"),
                diskPercent: Measurement.AvailableValue(_model.DiskPercent, "%", "DriveInfo"),
                networkDownloadBytesPerSec: Measurement.AvailableValue(_model.NetworkDownloadBytesPerSec, "bytes/sec", "NetworkInterface"),
                networkUploadBytesPerSec: Measurement.AvailableValue(_model.NetworkUploadBytesPerSec, "bytes/sec", "NetworkInterface"),
                cpuClockMHz: _model.CpuClockMHz.HasValue
                    ? Measurement.AvailableValue(_model.CpuClockMHz.Value, "MHz", _backendCpuClockMHz.HasValue ? "LibreHardwareMonitor" : "Win32_Processor")
                    : null,
                cpuTemperatureC: _model.CpuTemperature.HasValue
                    ? Measurement.AvailableValue(_model.CpuTemperature.Value, "°C", _backendCpuTemperature.HasValue ? "LibreHardwareMonitor" : "MSAcpi_ThermalZoneTemperature")
                    : null,
                gpuLoadPercent: _model.GpuLoadPercent.HasValue
                    ? Measurement.AvailableValue(_model.GpuLoadPercent.Value, "%", "Device-identified backend")
                    : null,
                gpuTemperatureC: _model.GpuTemperature.HasValue
                    ? Measurement.AvailableValue(_model.GpuTemperature.Value, "°C", _backendGpuTemperature.HasValue ? "LibreHardwareMonitor" : "nvidia-smi")
                    : null,
                gpuVramUsedBytes: _model.GpuVramUsedBytes.HasValue
                    ? Measurement.AvailableValue((double)_model.GpuVramUsedBytes.Value, "bytes", _backendGpuVramUsedBytes.HasValue ? "LibreHardwareMonitor" : "nvidia-smi/WMI")
                    : null,
                gpuVramTotalBytes: _model.GpuVramTotalBytes.HasValue
                    ? Measurement.AvailableValue((double)_model.GpuVramTotalBytes.Value, "bytes", _backendGpuVramTotalBytes.HasValue ? "LibreHardwareMonitor" : "nvidia-smi/WMI")
                    : null,
                acpiThermalZoneTemperatureC: _model.AcpiThermalZoneTemperatureC.HasValue
                    ? Measurement.AvailableValue(_model.AcpiThermalZoneTemperatureC.Value, "°C", "MSAcpi_ThermalZoneTemperature")
                    : null);
        }

        public void Dispose()
        {
            try
            {
                _sensorSource?.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Sensor backend dispose failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Collectors — each runs sequentially within Collect() and is
        // independently protected against exceptions.
        // ------------------------------------------------------------------

        private void UpdateCpu()
        {
            try
            {
                if (!GetSystemTimes(out var idle, out var kernel, out var user))
                    return;

                var idle64 = FileTimeToUInt64(idle);
                var kernel64 = FileTimeToUInt64(kernel);
                var user64 = FileTimeToUInt64(user);

                var sys = (kernel64 - _prevKernel) + (user64 - _prevUser);
                var idleDiff = idle64 - _prevIdle;

                if (sys > 0)
                {
                    var usage = (1.0 - (double)idleDiff / sys) * 100.0;
                    var clamped = ThermalMath.ClampPercent(usage);
                    _model.CpuPercent = clamped;
                    UpdateSeries(_model.CpuSeries, clamped);
                }

                _prevIdle = idle64;
                _prevKernel = kernel64;
                _prevUser = user64;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CPU collector failed: {ex.Message}");
            }
        }

        private void UpdateRam()
        {
            try
            {
                var mem = new MEMORYSTATUSEX();
                mem.dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>();
                if (GlobalMemoryStatusEx(ref mem))
                {
                    var used = mem.ullTotalPhys - mem.ullAvailPhys;
                    var percent = (double)used / mem.ullTotalPhys * 100.0;
                    var clamped = ThermalMath.ClampPercent(percent);

                    _model.RamUsedBytes = used;
                    _model.RamTotalBytes = mem.ullTotalPhys;
                    _model.RamPercent = clamped;
                    UpdateSeries(_model.RamSeries, clamped);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RAM collector failed: {ex.Message}");
            }
        }

        private void UpdateDisk()
        {
            try
            {
                // Use primary system drive for used percentage
                var systemDrive = Environment.GetFolderPath(Environment.SpecialFolder.System).Substring(0, 3);
                var drive = new System.IO.DriveInfo(systemDrive);
                if (drive.IsReady)
                {
                    var used = (drive.TotalSize - drive.TotalFreeSpace);
                    var percent = (double)used / drive.TotalSize * 100.0;
                    var clamped = ThermalMath.ClampPercent(percent);

                    _model.DiskUsedBytes = (long)used;
                    _model.DiskTotalBytes = (long)drive.TotalSize;
                    _model.DiskPercent = clamped;
                    UpdateSeries(_model.DiskSeries, clamped);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Disk collector failed: {ex.Message}");
            }
        }

        private void UpdateNetwork()
        {
            try
            {
                var now = DateTimeOffset.UtcNow;
                var currentIds = new HashSet<string>();
                double totalRxPerSec = 0;
                double totalTxPerSec = 0;

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
                UpdateSeries(_model.NetworkDownloadSeries, totalRxPerSec);
                UpdateSeries(_model.NetworkUploadSeries, totalTxPerSec);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Network collector failed: {ex.Message}");
            }
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
                catch
                {
                    // Skip individual interfaces that cannot be queried.
                }
            }
            return samples;
        }

        private void UpdateGpuBestEffort()
        {
            try
            {
                var gpuPercent = _gpuService.GetGpuPercentBestEffort();
                var nvidiaGpuPercent = _gpuService.GetNvidiaGpuPercent();
                _model.GpuPercent = gpuPercent;
                _model.NvidiaGpuPercent = nvidiaGpuPercent;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GPU best-effort collector failed: {ex.Message}");
            }
        }

        private void UpdateCpuTemp()
        {
            try
            {
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
                    _model.AcpiThermalZoneTemperatureC = temps.Average();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CPU temperature collector failed: {ex.Message}");
            }
        }

        private void UpdateGpuTemp()
        {
            try
            {
                var temp = _gpuService.GetGpuTemperatureBestEffort();
                _fallbackGpuTemperature = temp;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GPU temperature collector failed: {ex.Message}");
            }
        }

        private void UpdateGpuVram()
        {
            try
            {
                var (used, total) = _gpuService.GetGpuVramBestEffort();
                _fallbackGpuVramUsedBytes = used;
                _fallbackGpuVramTotalBytes = total;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GPU VRAM collector failed: {ex.Message}");
            }
        }

        private void UpdateCpuClock()
        {
            try
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
                    _fallbackCpuClockMHz = speeds.Average();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CPU clock collector failed: {ex.Message}");
            }
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
                Debug.WriteLine($"Sensor backend read failed: {ex.Message}");
                return;
            }

            if (devices.Count == 0)
            {
                _lastSnapshot = null;
                _backendCpuTemperature = null;
                _backendCpuClockMHz = null;
                _backendGpuTemperature = null;
                _backendGpuVramUsedBytes = null;
                _backendGpuVramTotalBytes = null;

                _model.GpuLoadPercent = null;
                _model.IntegratedGpuLoadPercent = null;
                _model.DiscreteGpuLoadPercent = null;
                return;
            }

            var projection = LhmSnapshotMapper.Project(devices);
            var snapshot = projection.ToSnapshot(DateTimeOffset.UtcNow);

            var primaryCpu = LhmSnapshotMapper.SelectPrimaryCpu(projection.Cpus);
            var primaryGpu = LhmSnapshotMapper.SelectPrimaryGpu(projection.Gpus);

            var integratedGpu = projection.Gpus.FirstOrDefault(g => g.GpuIntegration == GpuIntegration.Integrated);
            var discreteGpu = projection.Gpus.FirstOrDefault(g => g.GpuIntegration == GpuIntegration.Discrete);

            _lastSnapshot = snapshot;

            _model.GpuLoadPercent = ReadValue(snapshot, primaryGpu?.DeviceId, LhmSensorSelection.GpuLoadMetric);
            _model.IntegratedGpuLoadPercent = ReadValue(snapshot, integratedGpu?.DeviceId, LhmSensorSelection.GpuLoadMetric);

            // The "discrete" slot takes the load of a GPU known to be
            // discrete, or of the primary GPU when the primary is not
            // known to be integrated. Most vendors do not expose the
            // integration class, and a separate card is the better
            // candidate for the discrete slot than reporting nothing.
            var discreteLoad = discreteGpu is not null
                ? ReadValue(snapshot, discreteGpu.DeviceId, LhmSensorSelection.GpuLoadMetric)
                : (primaryGpu is not null && primaryGpu.GpuIntegration != GpuIntegration.Integrated
                    ? ReadValue(snapshot, primaryGpu.DeviceId, LhmSensorSelection.GpuLoadMetric)
                    : null);
            _model.DiscreteGpuLoadPercent = discreteLoad;

            _backendCpuTemperature = ReadValue(snapshot, primaryCpu?.DeviceId, LhmSensorSelection.CpuTemperatureMetric);
            _backendCpuClockMHz = ReadValue(snapshot, primaryCpu?.DeviceId, LhmSensorSelection.CpuClockMetric);
            _backendGpuTemperature = ReadValue(snapshot, primaryGpu?.DeviceId, LhmSensorSelection.GpuTemperatureMetric);

            var vramUsed = ReadValue(snapshot, primaryGpu?.DeviceId, LhmSensorSelection.GpuMemoryUsedMetric);
            var vramTotal = ReadValue(snapshot, primaryGpu?.DeviceId, LhmSensorSelection.GpuMemoryTotalMetric);
            _backendGpuVramUsedBytes = vramUsed.HasValue ? (ulong)vramUsed.Value : null;
            _backendGpuVramTotalBytes = vramTotal.HasValue ? (ulong)vramTotal.Value : null;
        }

        /// <summary>
        /// Publishes the merged view of the device-identified backend readings
        /// and the legacy collector values into the model.
        ///
        /// The backend wins whenever it can read the metric, because it names
        /// the device the value came from. The legacy collectors fill in when
        /// the backend cannot read the metric, so a blocked sensor does not
        /// blank out a working value. When neither source can supply a
        /// metric, the model is set to null: an unavailable metric shows as
        /// unavailable rather than as a stale value.
        /// </summary>
        private void PublishMergedMetrics()
        {
            _model.CpuTemperature = _backendCpuTemperature;
            _model.CpuClockMHz = _backendCpuClockMHz ?? _fallbackCpuClockMHz;
            _model.GpuTemperature = _backendGpuTemperature ?? _fallbackGpuTemperature;

            var vramUsed = _backendGpuVramUsedBytes ?? _fallbackGpuVramUsedBytes;
            var vramTotal = _backendGpuVramTotalBytes ?? _fallbackGpuVramTotalBytes;
            _model.GpuVramUsedBytes = vramUsed;
            _model.GpuVramTotalBytes = vramTotal;
        }

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

        private static void UpdateSeries(System.Collections.ObjectModel.ObservableCollection<double> series, double value, int maxPoints = 30)
        {
            // Keep values reasonable for charting (e.g., clamp for visual stability)
            if (double.IsNaN(value) || double.IsInfinity(value))
                value = 0.0;
            value = ThermalMath.ClampPercent(value);
            lock (series)
            {
                series.Add(value);
                while (series.Count > maxPoints)
                    series.RemoveAt(0);
            }
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

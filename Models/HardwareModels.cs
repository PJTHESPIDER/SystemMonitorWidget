using System;
using System.Collections.Generic;
using System.Linq;

namespace System_Monitor.Models
{
    /// <summary>
    /// The class of a hardware device.
    /// </summary>
    public enum DeviceClass
    {
        Unknown,
        Cpu,
        Gpu,
    }

    /// <summary>
    /// Classification of a GPU's integration with the platform.
    /// Unknown when the integration cannot be reliably determined.
    /// </summary>
    public enum GpuIntegration
    {
        Unknown,
        Integrated,
        Discrete,
    }

    /// <summary>
    /// Status of a measurement reading.
    /// </summary>
    public enum MeasurementStatus
    {
        /// <summary>The value was successfully read.</summary>
        Available,

        /// <summary>The sensor class is not supported on this platform.</summary>
        Unsupported,

        /// <summary>The read attempt failed.</summary>
        Failed,

        /// <summary>The last known value is older than the collection window.</summary>
        Stale,
    }

    /// <summary>
    /// A single hardware measurement with full provenance.
    /// Zero is never used as a substitute for unavailable data — use
    /// <see cref="Value"/> being null with an appropriate <see cref="Status"/>.
    /// </summary>
    public sealed record Measurement
    {
        public double? Value { get; init; }
        public string? Unit { get; init; }
        public string? Source { get; init; }
        public MeasurementStatus Status { get; init; } = MeasurementStatus.Unsupported;
        public DateTimeOffset? LastSuccessfulUpdate { get; init; }

        /// <summary>
        /// Creates an Available measurement.
        /// </summary>
        public static Measurement AvailableValue(double? value, string? unit = null, string? source = null) =>
            new()
            {
                Value = value,
                Unit = unit,
                Source = source,
                Status = MeasurementStatus.Available,
                LastSuccessfulUpdate = value.HasValue ? DateTimeOffset.UtcNow : null,
            };

        /// <summary>
        /// Creates an Unsupported measurement (no sensor present).
        /// </summary>
        public static Measurement Unsupported(string? source = null) =>
            new() { Status = MeasurementStatus.Unsupported, Source = source };

        /// <summary>
        /// Creates a Failed measurement (read attempted, failed).
        /// </summary>
        public static Measurement Failed(string? source = null) =>
            new() { Status = MeasurementStatus.Failed, Source = source };

        /// <summary>
        /// Creates a Stale measurement (last known value is too old).
        /// </summary>
        public static Measurement Stale(DateTimeOffset? lastSuccessfulUpdate = null, string? source = null) =>
            new() { Status = MeasurementStatus.Stale, LastSuccessfulUpdate = lastSuccessfulUpdate, Source = source };
    }

    /// <summary>
    /// A detected hardware device with a stable, durable identity.
    /// Identity is the <see cref="DeviceId"/> — never the device's position
    /// in a list, and never derived solely from display name.
    /// </summary>
    public sealed record HardwareDevice
    {
        /// <summary>
        /// Stable durable identity assigned at detection time. Backends should
        /// generate this from a platform-unique identifier (e.g. WMI PNPDeviceID,
        /// LUID, or a combination of vendor+device ID + instance) so the same
        /// physical device is recognized across collections.
        /// </summary>
        public string DeviceId { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;
        public string? Vendor { get; init; }
        public DeviceClass DeviceClass { get; init; } = DeviceClass.Unknown;

        /// <summary>
        /// GPU integration classification. Unknown when not reliably known.
        /// Only meaningful for GPUs.
        /// </summary>
        public GpuIntegration GpuIntegration { get; init; } = GpuIntegration.Unknown;
    }

    /// <summary>
    /// A reading taken from a specific device during a collection.
    /// </summary>
    public sealed record DeviceReading
    {
        /// <summary>References <see cref="HardwareDevice.DeviceId"/>.</summary>
        public string DeviceId { get; init; } = string.Empty;

        /// <summary>Measurement semantic, e.g. "LoadPercent", "TemperatureC", "ClockMHz", "VramUsedBytes".</summary>
        public string Metric { get; init; } = string.Empty;

        public Measurement Measurement { get; init; } = Measurement.Unsupported();
    }

    /// <summary>
    /// Immutable snapshot of all hardware state at a single point in time.
    /// All collections are read-only copies; callers cannot mutate the
    /// snapshot or the collections it contains.
    /// </summary>
    public sealed record MetricsSnapshot
    {
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>Detected CPU devices.</summary>
        public System.Collections.Generic.IReadOnlyList<HardwareDevice> Cpus { get; init; } =
            System.Array.Empty<HardwareDevice>();

        /// <summary>Detected GPU devices (any vendor, integrated or discrete).</summary>
        public System.Collections.Generic.IReadOnlyList<HardwareDevice> Gpus { get; init; } =
            System.Array.Empty<HardwareDevice>();

        /// <summary>All device readings keyed implicitly by DeviceId + Metric.</summary>
        public System.Collections.Generic.IReadOnlyList<DeviceReading> Readings { get; init; } =
            System.Array.Empty<DeviceReading>();

        /// <summary>
        /// Creates a snapshot with collections defensively copied into arrays so
        /// no mutable collection leaks into the published snapshot.
        /// </summary>
        public static MetricsSnapshot Create(
            DateTimeOffset timestamp,
            IEnumerable<HardwareDevice>? cpus = null,
            IEnumerable<HardwareDevice>? gpus = null,
            IEnumerable<DeviceReading>? readings = null,
            Measurement? ramUsedBytes = null,
            Measurement? ramTotalBytes = null,
            Measurement? ramPercent = null,
            Measurement? diskUsedBytes = null,
            Measurement? diskTotalBytes = null,
            Measurement? diskPercent = null,
            Measurement? networkDownloadBytesPerSec = null,
            Measurement? networkUploadBytesPerSec = null,
            Measurement? cpuLoadPercent = null,
            Measurement? cpuClockMHz = null,
            Measurement? cpuTemperatureC = null,
            Measurement? gpuLoadPercent = null,
            Measurement? gpuTemperatureC = null,
            Measurement? gpuVramUsedBytes = null,
            Measurement? gpuVramTotalBytes = null,
            Measurement? acpiThermalZoneTemperatureC = null) =>
            new()
            {
                Timestamp = timestamp,
                Cpus = cpus?.ToArray() ?? System.Array.Empty<HardwareDevice>(),
                Gpus = gpus?.ToArray() ?? System.Array.Empty<HardwareDevice>(),
                Readings = readings?.ToArray() ?? System.Array.Empty<DeviceReading>(),
                RamUsedBytes = ramUsedBytes,
                RamTotalBytes = ramTotalBytes,
                RamPercent = ramPercent,
                DiskUsedBytes = diskUsedBytes,
                DiskTotalBytes = diskTotalBytes,
                DiskPercent = diskPercent,
                NetworkDownloadBytesPerSec = networkDownloadBytesPerSec,
                NetworkUploadBytesPerSec = networkUploadBytesPerSec,
                CpuLoadPercent = cpuLoadPercent,
                CpuClockMHz = cpuClockMHz,
                CpuTemperatureC = cpuTemperatureC,
                GpuLoadPercent = gpuLoadPercent,
                GpuTemperatureC = gpuTemperatureC,
                GpuVramUsedBytes = gpuVramUsedBytes,
                GpuVramTotalBytes = gpuVramTotalBytes,
                AcpiThermalZoneTemperatureC = acpiThermalZoneTemperatureC,
            };

        /// <summary>System-level RAM measurement (not tied to a single device).</summary>
        public Measurement? RamUsedBytes { get; init; }
        public Measurement? RamTotalBytes { get; init; }
        public Measurement? RamPercent { get; init; }

        /// <summary>System-level disk measurement (system drive).</summary>
        public Measurement? DiskUsedBytes { get; init; }
        public Measurement? DiskTotalBytes { get; init; }
        public Measurement? DiskPercent { get; init; }

        /// <summary>System-level network throughput.</summary>
        public Measurement? NetworkDownloadBytesPerSec { get; init; }
        public Measurement? NetworkUploadBytesPerSec { get; init; }

        /// <summary>
        /// System-wide CPU load, from the Windows CPU time counters.
        ///
        /// This is the value the legacy widget contract has always shown. It is
        /// kept separate from the per-device "LoadPercent" reading so a
        /// multi-package machine is never silently reduced to one package's
        /// load.
        /// </summary>
        public Measurement? CpuLoadPercent { get; init; }

        /// <summary>
        /// CPU clock rate. The device-identified backend wins when it can read
        /// the sensor; otherwise the Windows fallback is used.
        /// </summary>
        public Measurement? CpuClockMHz { get; init; }

        /// <summary>CPU temperature, from the device-identified backend when readable.</summary>
        public Measurement? CpuTemperatureC { get; init; }

        /// <summary>
        /// Load of the primary GPU, merged across the device-identified
        /// backend and the vendor-neutral Windows fallbacks.
        /// </summary>
        public Measurement? GpuLoadPercent { get; init; }

        /// <summary>Primary GPU temperature, merged across the available sources.</summary>
        public Measurement? GpuTemperatureC { get; init; }

        /// <summary>Primary GPU dedicated-memory usage, merged across the available sources.</summary>
        public Measurement? GpuVramUsedBytes { get; init; }

        /// <summary>Primary GPU dedicated-memory capacity, merged across the available sources.</summary>
        public Measurement? GpuVramTotalBytes { get; init; }

        /// <summary>
        /// Temperature of an unidentified ACPI thermal zone. Never presented as
        /// a confirmed CPU or GPU temperature.
        /// </summary>
        public Measurement? AcpiThermalZoneTemperatureC { get; init; }
    }
}

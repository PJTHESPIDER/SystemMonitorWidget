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
        /// <param name="value">The value that was read.</param>
        /// <param name="unit">The canonical unit of the metric.</param>
        /// <param name="source">The provenance of the reading.</param>
        /// <param name="readAt">
        /// The time the value was read. It defaults to now, which is right
        /// only when the value was read at the moment the measurement is
        /// built. A value that was read earlier — and is being published
        /// now — must be stamped with the time of the read, or the widget
        /// will show it as having been updated just now.
        /// </param>
        public static Measurement AvailableValue(
            double? value,
            string? unit = null,
            string? source = null,
            DateTimeOffset? readAt = null) =>
            new()
            {
                Value = value,
                Unit = unit,
                Source = source,
                Status = MeasurementStatus.Available,
                LastSuccessfulUpdate = value.HasValue
                    ? readAt ?? DateTimeOffset.UtcNow
                    : null,
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

        /// <summary>
        /// The device's PCI vendor and device id, in the canonical
        /// "VEN_xxxx&amp;DEV_yyyy" form, or null when the backend does not
        /// expose it.
        ///
        /// This is the key that lets a reading from a vendor command-line
        /// tool be attributed to this specific device. It identifies a device
        /// *model*, not an individual card: two identical cards share it.
        /// </summary>
        public string? PciDeviceKey { get; init; }

        /// <summary>
        /// The device's per-instance identity, suitable for persisting a
        /// user selection across sessions, or null when the backend does not
        /// expose one.
        ///
        /// <para>
        /// This is deliberately distinct from the other two identities on
        /// this type:
        /// </para>
        ///
        /// <list type="bullet">
        /// <item><description>
        /// <see cref="DeviceId"/> is the backend's own identifier, which for
        /// LibreHardwareMonitor is an <i>enumeration index</i>
        /// ("/gpu-nvidia/0"). It is stable within a session but says
        /// nothing about which physical card was measured, and its numeric
        /// suffix shifts when the device set changes. It is therefore never
        /// persisted.
        /// </description></item>
        /// <item><description>
        /// <see cref="PciDeviceKey"/> identifies a device <i>model</i>. Two
        /// identical cards share it, so it cannot distinguish them either.
        /// </description></item>
        /// <item><description>
        /// This property identifies the individual device: it is the
        /// Windows PNP device id (or an equivalent per-adapter identifier)
        /// that the platform assigns to that one card. Two identical cards
        /// have different values, and the value does not change when the
        /// device set is re-enumerated.
        /// </description></item>
        /// </list>
        ///
        /// <para>
        /// A user selection is persisted against this value. When a selected
        /// device is absent, the selection simply does not resolve, and the
        /// caller falls back to the documented default policy.
        /// </para>
        /// </summary>
        public string? InstanceId { get; init; }
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
            Measurement? acpiThermalZoneTemperatureC = null,
            string? gpuLoadDeviceId = null,
            string? gpuTemperatureDeviceId = null,
            string? gpuVramUsedDeviceId = null,
            string? gpuVramTotalDeviceId = null) =>
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
                GpuLoadDeviceId = gpuLoadDeviceId,
                GpuTemperatureDeviceId = gpuTemperatureDeviceId,
                GpuVramUsedDeviceId = gpuVramUsedDeviceId,
                GpuVramTotalDeviceId = gpuVramTotalDeviceId,
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
        /// The device the scalar GPU load figure describes, or null when
        /// the collection could not attribute it to a device.
        ///
        /// <para>
        /// The scalar GPU figures are per-device readings that have been
        /// flattened into single values. These properties name the device
        /// each one came from, so a presenter can check that it is about to
        /// present a figure for the same device. Without them, a fallback
        /// figure could be shown under another card's name.
        /// </para>
        ///
        /// <para>
        /// The device id is the backend's own, matching
        /// <see cref="HardwareDevice.DeviceId"/>.
        /// </para>
        /// </summary>
        public string? GpuLoadDeviceId { get; init; }

        /// <summary>
        /// The device the scalar GPU temperature figure describes, or null
        /// when it could not be attributed.
        /// </summary>
        public string? GpuTemperatureDeviceId { get; init; }

        /// <summary>
        /// The device the scalar GPU <i>used</i> memory figure describes, or
        /// null when it could not be attributed.
        /// </summary>
        public string? GpuVramUsedDeviceId { get; init; }

        /// <summary>
        /// The device the scalar GPU <i>total</i> memory figure describes, or
        /// null when it could not be attributed.
        ///
        /// <para>
        /// The used and total figures are tracked separately because they can
        /// come from different sources: the used figure may come from the
        /// reconciled NVIDIA fallback while the capacity comes from the
        /// Windows counters, which name no device at all. Attributing them
        /// together would let one card's capacity be presented as another's.
        /// </para>
        /// </summary>
        public string? GpuVramTotalDeviceId { get; init; }

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

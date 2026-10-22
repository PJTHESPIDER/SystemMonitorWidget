using System;

namespace System_Monitor.Services
{
    /// <summary>
    /// One NVIDIA GPU as reported by a single nvidia-smi query row.
    ///
    /// Every value carries its own identity. A sample is never merged into
    /// another device's slot on the strength of its position in a list or on
    /// its display name: reconciliation is done by
    /// <see cref="NvidiaGpuReconciler"/> against an explicit identity key.
    /// </summary>
    public sealed record NvidiaGpuSample
    {
        /// <summary>
        /// The device's own UUID, as reported by nvidia-smi. Stable for the
        /// lifetime of the card.
        /// </summary>
        public string Uuid { get; init; } = string.Empty;

        /// <summary>
        /// The device's display name, as reported by nvidia-smi. Informational
        /// only: it is never used to identify a device.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// The PCI address, e.g. "00000000:01:00.0". Informational only.
        /// </summary>
        public string PciBusId { get; init; } = string.Empty;

        /// <summary>
        /// The raw PCI device id as reported by nvidia-smi, e.g. "0x1E9110DE".
        /// </summary>
        public string PciDeviceId { get; init; } = string.Empty;

        /// <summary>
        /// The normalized identity key for this device's PCI vendor and
        /// device ids, e.g. "VEN_10DE&amp;DEV_1E91". Empty when the device id
        /// could not be interpreted.
        /// </summary>
        public string PciDeviceKey { get; init; } = string.Empty;

        /// <summary>Core utilization in percent, or null when unsupported.</summary>
        public double? UtilizationPercent { get; init; }

        /// <summary>Core temperature in degrees Celsius, or null when unsupported.</summary>
        public double? TemperatureC { get; init; }

        /// <summary>Dedicated memory in use, in bytes, or null when unsupported.</summary>
        public ulong? VramUsedBytes { get; init; }

        /// <summary>Dedicated memory total, in bytes, or null when unsupported.</summary>
        public ulong? VramTotalBytes { get; init; }

        /// <summary>
        /// True when the sample carries an identity that can be reconciled
        /// against a device-identified backend.
        /// </summary>
        public bool HasIdentity =>
            !string.IsNullOrWhiteSpace(Uuid) || !string.IsNullOrWhiteSpace(PciDeviceKey);
    }
}

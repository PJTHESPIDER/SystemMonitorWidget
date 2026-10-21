using System;
using System.Collections.Generic;

namespace System_Monitor.Services
{
    /// <summary>
    /// A device exposed by a sensor backend, in backend-neutral terms.
    /// </summary>
    public sealed record LhmDeviceDescriptor
    {
        /// <summary>
        /// Stable identity within this backend. Derived from the source's own
        /// hardware identifier — never from the device's position in a list.
        /// </summary>
        public string DeviceId { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        /// <summary>Vendor as reported by the backend, or null when unknown.</summary>
        public string? Vendor { get; init; }

        /// <summary>True for CPU devices, false for GPU devices.</summary>
        public bool IsCpu { get; init; }

        /// <summary>True when the backend can state the GPU is integrated with the CPU.</summary>
        public bool? IsIntegratedGpu { get; init; }

        public IReadOnlyList<LhmSensorDescriptor> Sensors { get; init; } =
            Array.Empty<LhmSensorDescriptor>();
    }

    /// <summary>
    /// Read-only sensor source. Implemented by the LibreHardwareMonitor adapter
    /// and by test fixtures, so mapping logic can be exercised without
    /// initializing hardware access.
    /// </summary>
    public interface ILhmSensorSource : IDisposable
    {
        /// <summary>
        /// False when the backend failed to initialize or is unavailable. The
        /// widget must keep operating normally in that case, reporting the
        /// affected metrics as unavailable.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Reason the backend is unavailable, for diagnostics. Null when available.
        /// </summary>
        string? UnavailableReason { get; }

        /// <summary>
        /// Refreshes sensors and returns the current devices. Implementations
        /// must be safe to call repeatedly and must not throw for expected
        /// failures such as blocked drivers.
        /// </summary>
        IReadOnlyList<LhmDeviceDescriptor> Read();
    }
}

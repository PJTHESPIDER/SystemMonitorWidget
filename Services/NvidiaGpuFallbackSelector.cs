using System.Collections.Generic;
using System.Linq;

namespace System_Monitor.Services
{
    /// <summary>
    /// The GPU metrics the NVIDIA fallback can supply, for a single
    /// reconciled device.
    /// </summary>
    /// <param name="UtilizationPercent">Core utilization, or null when unsupported.</param>
    /// <param name="TemperatureC">Core temperature, or null when unsupported.</param>
    /// <param name="VramUsedBytes">Dedicated memory in use, or null when unsupported.</param>
    /// <param name="VramTotalBytes">Dedicated memory total, or null when unsupported.</param>
    /// <param name="DeviceId">The backend device id the readings were attributed to, or null.</param>
    public sealed record NvidiaFallbackMetrics(
        double? UtilizationPercent,
        double? TemperatureC,
        ulong? VramUsedBytes,
        ulong? VramTotalBytes,
        string? DeviceId);

    /// <summary>
    /// Chooses which nvidia-smi reading, if any, may be used for a metric
    /// the primary source could not supply.
    ///
    /// The rule is that a reading is used only when it is reconciled to
    /// exactly one device. A reading that cannot be reconciled — because its
    /// identity is absent, because it matches no device, or because the
    /// identity is shared by more than one card — is not used. It is not
    /// attributed to the "first" or "primary" GPU, because that would be
    /// attributing a reading to a device it may not have come from.
    /// </summary>
    public static class NvidiaGpuFallbackSelector
    {
        /// <summary>
        /// Selects the metrics to use from the nvidia-smi samples.
        ///
        /// <paramref name="samples"/> are the readings from one nvidia-smi
        /// invocation. <paramref name="backendDevices"/> are the devices the
        /// primary backend reported, with their identity keys.
        ///
        /// When more than one sample reconciles to exactly one device, the
        /// first reconciled sample wins. A single nvidia-smi invocation
        /// reports one row per device, so this can only happen when the
        /// backend reported a device that appears twice in the sample set.
        /// </summary>
        public static NvidiaFallbackMetrics Select(
            IReadOnlyList<NvidiaGpuSample> samples,
            IReadOnlyList<BackendGpuIdentity> backendDevices)
        {
            var empty = new NvidiaFallbackMetrics(null, null, null, null, null);

            if (samples is null || samples.Count == 0)
                return empty;

            var reconciled = NvidiaGpuReconciler.Reconcile(samples, backendDevices);

            var match = reconciled.FirstOrDefault(r => r.IsMatchable);
            if (match is null)
            {
                // Either nothing reconciled, or the readings were ambiguous.
                // In both cases no reading may be attributed to a device.
                return empty;
            }

            var sample = match.Sample;

            return new NvidiaFallbackMetrics(
                sample.UtilizationPercent,
                sample.TemperatureC,
                sample.VramUsedBytes,
                sample.VramTotalBytes,
                match.MatchedDeviceId);
        }
    }
}

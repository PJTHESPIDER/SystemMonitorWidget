using System;
using System.Collections.Generic;

namespace System_Monitor.Services
{
    /// <summary>
    /// One adapter's dedicated-memory usage, as reported by the Windows
    /// GPU adapter-memory performance counters.
    /// </summary>
    /// <param name="AdapterName">
    /// The counter instance name. It identifies the adapter's LUID, not a
    /// device in the domain model, so it is used for counting and
    /// diagnostics only — never to select a device.
    /// </param>
    /// <param name="DedicatedUsageBytes">The adapter's dedicated memory in use, in bytes.</param>
    public readonly record struct GpuAdapterMemorySample(
        string AdapterName,
        ulong DedicatedUsageBytes);

    /// <summary>
    /// The dedicated-memory figures read from the Windows counters, before
    /// they have been checked for ambiguity.
    /// </summary>
    /// <param name="UsedBytes">
    /// The per-adapter used figures, one entry per adapter the
    /// adapter-memory counter reported.
    /// </param>
    /// <param name="TotalBytes">
    /// The per-adapter capacities, one entry per adapter the
    /// video-controller class reported. Empty when the class reported
    /// nothing usable.
    /// </param>
    public sealed record GpuMemoryCounterReadings(
        IReadOnlyList<GpuAdapterMemorySample> UsedBytes,
        IReadOnlyList<ulong> TotalBytes);

    /// <summary>
    /// Decides whether a Windows GPU memory counter reading may be
    /// attributed to a single device.
    ///
    /// <para>
    /// The Windows counters are not device-identified in the way the sensor
    /// backend is. The adapter-memory counter reports one instance per
    /// adapter, keyed by an LUID, and the video-controller class reports
    /// one row per adapter. Neither carries the domain model's device
    /// identity, and neither LUID appears in the other class, so the two
    /// cannot be correlated to each other either.
    /// </para>
    ///
    /// <para>
    /// Two tempting readings are both wrong:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><description>
    /// <b>Summing the used figures across adapters.</b> The result is a
    /// system-wide total. Presenting it as one card's memory in use
    /// reports another device's usage as this device's.
    /// </description></item>
    /// <item><description>
    /// <b>Taking the first adapter's total.</b> Which adapter is "first"
    /// is an enumeration accident, not a fact about the device the widget
    /// is presenting. On a hybrid machine that is very often the
    /// integrated part, whose capacity is nothing like the discrete card's.
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// The rule here is the only one that cannot misattribute: a figure is
    /// usable when the counters described exactly one adapter, in which case
    /// it is unambiguously that adapter's. When more than one adapter is
    /// present the reading is ambiguous, and an ambiguous reading is
    /// reported as unavailable rather than as a number that belongs to no
    /// single device.
    /// </para>
    ///
    /// <para>
    /// <b>What this cannot do.</b> The two figures this class pairs come
    /// from two different counter classes, and neither carries the other's
    /// adapter identity. That each class saw exactly one adapter therefore
    /// makes the pair <i>plausible</i>, not <i>confirmed</i>: they could
    /// still be two different adapters. The one cross-check available is
    /// the numbers themselves — a capacity below the amount in use refutes
    /// the pairing, and the capacity is dropped. A pair that passes that
    /// check is still not proven to describe one adapter.
    /// </para>
    ///
    /// <para>
    /// The figures these counters produce also name no device in the
    /// domain model, so they are never presented for a particular card and
    /// are never paired with another figure to make a ratio. See
    /// <see cref="GpuMemoryCompatibilityPolicy"/>.
    /// </para>
    /// </summary>
    public static class GpuMemoryFallbackPolicy
    {
        /// <summary>
        /// The used and total bytes, when the counters described exactly
        /// one adapter.
        /// </summary>
        /// <param name="readings">The raw per-adapter figures.</param>
        public static (ulong? usedBytes, ulong? totalBytes) Resolve(
            GpuMemoryCounterReadings readings)
        {
            if (readings is null)
                return (null, null);

            var used = UnambiguousUsedBytes(readings.UsedBytes);
            var total = UnambiguousTotalBytes(readings.TotalBytes);

            // The two classes cannot be correlated to each other, so the
            // pair is only *plausible* when both described a single
            // adapter — it is never confirmed. The one cross-check
            // available is the numbers themselves: a capacity below the
            // amount in use cannot describe the same adapter, and
            // reporting it would produce a usage percentage above 100.
            if (used.HasValue && total.HasValue && total.Value < used.Value)
            {
                // A capacity below the amount in use cannot describe the
                // same adapter. Reporting it would produce a usage
                // percentage above 100.
                return (used, null);
            }

            return (used, total);
        }

        /// <summary>
        /// The used bytes, when the counters described exactly one adapter.
        /// Null when the reading is ambiguous or empty.
        /// </summary>
        /// <param name="samples">One sample per adapter the counter reported.</param>
        public static ulong? UnambiguousUsedBytes(IReadOnlyList<GpuAdapterMemorySample> samples)
        {
            if (samples is null || samples.Count == 0)
                return null;

            // More than one adapter: the counter reports a per-adapter
            // figure and there is no way to tell which adapter the widget
            // is presenting. Refuse rather than sum.
            if (samples.Count != 1)
                return null;

            return samples[0].DedicatedUsageBytes;
        }

        /// <summary>
        /// The total bytes, when the counters described exactly one adapter
        /// with a usable capacity.
        /// </summary>
        /// <param name="adapterRamBytes">
        /// The <c>AdapterRAM</c> value of each adapter the video-controller
        /// class reported. Adapters that report no usable value are not
        /// included by the caller.
        /// </param>
        public static ulong? UnambiguousTotalBytes(IReadOnlyList<ulong> adapterRamBytes)
        {
            if (adapterRamBytes is null || adapterRamBytes.Count == 0)
                return null;

            if (adapterRamBytes.Count != 1)
                return null;

            return adapterRamBytes[0];
        }
    }
}

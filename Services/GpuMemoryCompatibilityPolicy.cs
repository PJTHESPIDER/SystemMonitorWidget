using System;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// The memory pool a GPU memory figure describes.
    /// </summary>
    public enum GpuMemorySpace
    {
        /// <summary>
        /// The pool could not be established from the figure's provenance.
        ///
        /// <para>
        /// This is not a claim that the figure is wrong. It is a claim that
        /// the figure's source does not say which pool it came from, so
        /// nothing can be concluded about it either way.
        /// </para>
        /// </summary>
        Unknown,

        /// <summary>
        /// Dedicated video memory: the card's own VRAM.
        /// </summary>
        Dedicated,

        /// <summary>
        /// Shared system memory, which is not VRAM.
        /// </summary>
        Shared,
    }

    /// <summary>
    /// The label a GPU-memory figure is presented under.
    ///
    /// <para>
    /// The label says what the figure is known to be, and no more. A
    /// figure whose memory pool was never established is labelled
    /// "GPU memory" — never "Dedicated VRAM" — because the widget's
    /// heading is itself a claim, and an unestablished claim about a
    /// memory pool is a claim the data does not support.
    /// </para>
    /// </summary>
    public enum GpuMemoryLabel
    {
        /// <summary>
        /// The pool was not established. The figure is GPU memory of an
        /// unknown kind.
        /// </summary>
        Unspecified,

        /// <summary>
        /// The figure is the card's own dedicated video memory.
        /// </summary>
        Dedicated,

        /// <summary>
        /// The figure is shared system memory.
        /// </summary>
        Shared,
    }

    /// <summary>
    /// Decides whether a used-memory figure and a total-memory figure may
    /// be presented as one ratio.
    ///
    /// <para><b>Why this exists</b></para>
    ///
    /// <para>
    /// A "used / total (percent)" figure is a claim that the two numbers
    /// describe the same memory pool of the same device. Three things can
    /// make that claim false, and each of them is checked here:
    /// </para>
    ///
    /// <list type="number">
    /// <item><description>
    /// <b>Attribution.</b> The two figures may come from different devices,
    /// or one of them may name no device at all. A capacity that names no
    /// device may be any card's capacity — very often the integrated
    /// part's, whose size bears no relation to the discrete card's.
    /// </description></item>
    /// <item><description>
    /// <b>Memory semantics.</b> The two figures may describe different
    /// pools: a card's dedicated VRAM on one side, shared system memory on
    /// the other. The ratio of those two numbers is not a usage
    /// percentage, it is a coincidence.
    /// </description></item>
    /// <item><description>
    /// <b>Coherence.</b> A capacity of zero cannot produce a ratio at all,
    /// and a usage above the capacity proves the two numbers describe
    /// different pools.
    /// </description></item>
    /// </list>
    ///
    /// <para><b>What compatibility is never inferred from</b></para>
    ///
    /// <para>
    /// A device's <i>display name</i> is not evidence of anything. Two
    /// identical cards have identical names and are different devices, and
    /// one card's name says nothing about which memory pool its figures
    /// came from. Compatibility is decided on device ids and on the
    /// provenance of each figure, and on nothing else.
    /// </para>
    ///
    /// <para><b>How a figure's memory pool is established</b></para>
    ///
    /// <para>
    /// The pool is read from the figure's own provenance. A reading from
    /// the device-identified backend names the sensor it came from, and
    /// the sensor's name is the source's own declaration of what it
    /// measures: a name in the backend's dedicated-memory families is
    /// dedicated memory, and a name that marks the sensor as shared is
    /// shared memory. The vendor-neutral fallback reports the card's own
    /// dedicated video memory. The Windows counters establish no pool at
    /// all.
    /// </para>
    ///
    /// <para>
    /// A family that does not say which pool it describes — the backend's
    /// bare "Memory Used" / "Memory Total" — establishes no pool, but it
    /// does establish a <i>family</i>. Two figures from the same family
    /// are two figures of one pool, whatever that pool is, so they may be
    /// paired. A figure from one family may not be paired with a figure
    /// from another: that is the dedicated-versus-shared case, and the
    /// generic-versus-dedicated case.
    /// </para>
    ///
    /// <para>
    /// The rule is therefore to require positive evidence. A pair is
    /// combined when the two figures can be shown to describe one pool:
    /// because both independently name dedicated video memory, or because
    /// both name the same family. A pair that cannot be shown to describe
    /// one pool is not combined, and the ratio is omitted rather than
    /// shown on the strength of a coincidence.
    /// </para>
    /// </summary>
    public static class GpuMemoryCompatibilityPolicy
    {
        /// <summary>
        /// The provenance label of the NVIDIA command-line fallback's
        /// readings. Its memory figures are the card's own dedicated
        /// video memory.
        /// </summary>
        public const string NvidiaSource = "nvidia-smi";

        /// <summary>
        /// The memory pool a figure describes, as far as its provenance
        /// establishes.
        /// </summary>
        /// <param name="measurement">The figure to classify.</param>
        public static GpuMemorySpace SpaceOf(Measurement? measurement)
        {
            var source = measurement?.Source;

            if (string.IsNullOrWhiteSpace(source))
                return GpuMemorySpace.Unknown;

            // A reading from the device-identified backend names the sensor
            // it came from, and the sensor's name is the evidence about
            // which pool the number describes.
            var backendPrefix = LhmSnapshotMapper.SourceName + ":";
            if (source.StartsWith(backendPrefix, StringComparison.Ordinal))
            {
                return SpaceForSensor(source.Substring(backendPrefix.Length));
            }

            // The vendor-neutral fallback reports the card's own dedicated
            // video memory.
            if (string.Equals(source, NvidiaSource, StringComparison.Ordinal))
                return GpuMemorySpace.Dedicated;

            // The Windows counters report the adapter's dedicated usage but
            // take their capacity from a field that describes the
            // adapter's whole video memory, which is shared system memory
            // on an integrated part. They therefore establish no pool
            // between the two figures, and their figures name no device.
            return GpuMemorySpace.Unknown;
        }

        /// <summary>
        /// The memory pool a sensor name denotes.
        /// </summary>
        /// <param name="sensorName">The sensor's name.</param>
        public static GpuMemorySpace SpaceForSensor(string? sensorName)
        {
            if (string.IsNullOrWhiteSpace(sensorName))
                return GpuMemorySpace.Unknown;

            if (LhmSensorSelection.IsSharedMemorySensor(sensorName))
                return GpuMemorySpace.Shared;

            if (LhmSensorSelection.IsDedicatedMemorySensor(sensorName))
                return GpuMemorySpace.Dedicated;

            return GpuMemorySpace.Unknown;
        }

        /// <summary>
        /// The family a figure belongs to, as far as its provenance
        /// establishes, or null when the figure names no family.
        ///
        /// <para>
        /// The family is what makes two figures comparable: a used figure
        /// and a total figure from one family describe one pool of memory
        /// whatever that pool is.
        /// </para>
        /// </summary>
        /// <param name="measurement">The figure to classify.</param>
        public static string? FamilyOf(Measurement? measurement)
        {
            var source = measurement?.Source;

            if (string.IsNullOrWhiteSpace(source))
                return null;

            var backendPrefix = LhmSnapshotMapper.SourceName + ":";
            if (source.StartsWith(backendPrefix, StringComparison.Ordinal))
            {
                return LhmSensorSelection.MemoryFamilyOf(source.Substring(backendPrefix.Length));
            }

            // The vendor-neutral fallback reports the card's own dedicated
            // video memory, and its used and total figures are two figures
            // of that one pool.
            if (string.Equals(source, NvidiaSource, StringComparison.Ordinal))
                return NvidiaSource;

            // The Windows counters' two figures come from two different
            // classes and establish no family between them.
            return null;
        }

        /// <summary>
        /// The memory pool that a pair of figures establishes, as distinct
        /// from whether the pair may be combined at all.
        ///
        /// <para>
        /// Compatibility and classification are separate questions, and
        /// they are answered separately here:
        /// </para>
        ///
        /// <list type="bullet">
        /// <item><description>
        /// <b>Compatibility</b> asks whether two figures may be presented
        /// as one ratio. A generic "Memory Used" / "Memory Total" pair
        /// may: the two figures are of one family, so they are of one
        /// pool, whatever that pool is.
        /// </description></item>
        /// <item><description>
        /// <b>Classification</b> asks which pool that is. A generic pair
        /// does not say: its name names no pool, so the pool stays
        /// unspecified. A pair is classified only when <i>both</i> figures
        /// name the same pool.
        /// </description></item>
        /// </list>
        ///
        /// <para>
        /// So a compatible pair is not thereby a dedicated-memory pair,
        /// and a presenter that says "dedicated" is saying more than the
        /// compatibility check established. This is what keeps an unknown
        /// pool from being presented as dedicated VRAM.
        /// </para>
        /// </summary>
        /// <param name="used">The used-memory figure.</param>
        /// <param name="total">The total-memory figure.</param>
        public static GpuMemorySpace EstablishedSpaceOf(
            Measurement? used,
            Measurement? total)
        {
            var usedSpace = SpaceOf(used);
            var totalSpace = SpaceOf(total);

            // Both figures must name the same pool for the pair to have
            // one. A figure that names no pool cannot confirm the other
            // figure's pool, and it cannot be used to deny it either — so
            // the pair's pool stays unspecified.
            if (usedSpace == GpuMemorySpace.Unknown)
                return GpuMemorySpace.Unknown;

            return usedSpace == totalSpace ? usedSpace : GpuMemorySpace.Unknown;
        }

        /// <summary>
        /// True when the figure describes shared system memory.
        ///
        /// <para>
        /// Shared system memory is not VRAM. A figure that describes it is
        /// never presented as the card's memory usage, and is never paired
        /// with another figure to make a ratio.
        /// </para>
        /// </summary>
        /// <param name="measurement">The figure to classify.</param>
        public static bool DescribesSharedMemory(Measurement? measurement) =>
            SpaceOf(measurement) == GpuMemorySpace.Shared;

        /// <summary>
        /// The label a figure is presented under, from what its own
        /// provenance establishes.
        ///
        /// <para>
        /// This is the used-only case: the label comes from the used
        /// figure alone. A total figure that was rejected, that belongs to
        /// another device, or that says nothing about its pool, contributes
        /// nothing here — the label must not borrow semantics from a
        /// figure that was not presented.
        /// </para>
        /// </summary>
        /// <param name="measurement">The figure to label.</param>
        public static GpuMemoryLabel LabelOf(Measurement? measurement) =>
            SpaceOf(measurement) switch
            {
                GpuMemorySpace.Dedicated => GpuMemoryLabel.Dedicated,
                GpuMemorySpace.Shared => GpuMemoryLabel.Shared,
                _ => GpuMemoryLabel.Unspecified,
            };

        /// <summary>
        /// The label a figure establishes on its own, or null when the
        /// figure names no memory pool at all.
        ///
        /// <para>
        /// This is the distinction between "the figure names no pool" and
        /// "the figure names a pool". A generic "Memory Used" reading
        /// establishes no pool, so it establishes no label either — but
        /// that is not the same as establishing the neutral label: the
        /// neutral label is what is <i>shown</i>, while <c>null</c> here is
        /// what says no pool was named at all.
        /// </para>
        ///
        /// <para>
        /// The distinction matters for the unavailable case: when there is
        /// no figure to show, the heading may still name the memory
        /// category the device's own figures establish, and a figure that
        /// names no pool says nothing that could establish one.
        /// </para>
        /// </summary>
        /// <param name="measurement">The figure to classify.</param>
        public static GpuMemoryLabel? EstablishedLabelOf(Measurement? measurement) =>
            SpaceOf(measurement) switch
            {
                GpuMemorySpace.Dedicated => GpuMemoryLabel.Dedicated,
                GpuMemorySpace.Shared => GpuMemoryLabel.Shared,
                _ => null,
            };

        /// <summary>
        /// The label a *combined* used/total display is presented under.
        ///
        /// <para>
        /// The label comes from the established pool of the displayed
        /// combination, not from either figure alone. A pair whose pool
        /// was never established is labelled "GPU memory", even though the
        /// compatibility policy accepts the pair: accepting a pairing says
        /// the two figures describe one pool, not which pool that is.
        /// </para>
        /// </summary>
        /// <param name="used">The used-memory figure.</param>
        /// <param name="total">The total-memory figure.</param>
        public static GpuMemoryLabel CombinedLabelOf(
            Measurement? used,
            Measurement? total) =>
            EstablishedSpaceOf(used, total) switch
            {
                GpuMemorySpace.Dedicated => GpuMemoryLabel.Dedicated,
                GpuMemorySpace.Shared => GpuMemoryLabel.Shared,
                _ => GpuMemoryLabel.Unspecified,
            };

        /// <summary>
        /// The memory category the presented device's own figures
        /// establish, or null when none of them names a pool.
        ///
        /// <para>
        /// This answers a question the compatibility policy does not: when
        /// there is no figure to show, what may the heading claim? A
        /// heading may name the device's memory category when the device's
        /// own readings established it, and nothing else.
        /// </para>
        ///
        /// <para>
        /// Every figure considered here must belong to the device being
        /// presented. A figure belonging to another card says nothing
        /// about this one, and an unattributed figure says nothing at all.
        /// </para>
        /// </summary>
        /// <param name="used">The used-memory figure.</param>
        /// <param name="usedDeviceId">The device the used figure describes.</param>
        /// <param name="total">The total-memory figure.</param>
        /// <param name="totalDeviceId">The device the total figure describes.</param>
        /// <param name="presentedDeviceId">The device being presented.</param>
        public static GpuMemoryLabel? DeviceCategoryOf(
            Measurement? used,
            string? usedDeviceId,
            Measurement? total,
            string? totalDeviceId,
            string? presentedDeviceId)
        {
            if (presentedDeviceId is null)
                return null;

            return CategoryOfOwnFigure(used, usedDeviceId, presentedDeviceId)
                   ?? CategoryOfOwnFigure(total, totalDeviceId, presentedDeviceId);
        }

        /// <summary>
        /// The label a figure establishes, when the figure belongs to the
        /// device being presented.
        /// </summary>
        private static GpuMemoryLabel? CategoryOfOwnFigure(
            Measurement? measurement,
            string? deviceId,
            string presentedDeviceId)
        {
            if (deviceId is null)
                return null;

            if (!string.Equals(deviceId, presentedDeviceId, StringComparison.Ordinal))
                return null;

            return EstablishedLabelOf(measurement);
        }

        /// <summary>
        /// True when a measurement carries a value that may be shown.
        ///
        /// <para>
        /// A failed or unsupported measurement has no value, and a stale
        /// one is not presented as if it were current.
        /// </para>
        /// </summary>
        /// <param name="measurement">The measurement to test.</param>
        public static bool IsUsable(Measurement? measurement) =>
            measurement is not null
            && measurement.Status == MeasurementStatus.Available
            && measurement.Value.HasValue;

        /// <summary>
        /// True when two figures can be shown to describe one memory pool.
        ///
        /// <para>
        /// The rule is to require positive evidence rather than to require
        /// the absence of a contradiction. There are two kinds of evidence:
        /// </para>
        ///
        /// <list type="bullet">
        /// <item><description>
        /// <b>An established pool.</b> When both figures independently name
        /// a pool, they must name the same one. Two dedicated-memory
        /// figures are one pool; a dedicated figure and a shared one are
        /// not.
        /// </description></item>
        /// <item><description>
        /// <b>A common family.</b> When a figure's name does not say which
        /// pool it describes, the family is the evidence: two figures
        /// named from the same memory-sensor family on the same device are
        /// two figures of one pool, whatever that pool is. "Memory Used"
        /// and "Memory Total" are such a pair. "GPU Memory Total" and
        /// "Memory Total" are not: they are two different families, and
        /// nothing says they describe the same pool.
        /// </description></item>
        /// </list>
        ///
        /// <para>
        /// When neither kind of evidence is available the figures are not
        /// paired, and the ratio is omitted.
        /// </para>
        /// </summary>
        /// <param name="used">The used-memory figure.</param>
        /// <param name="total">The total-memory figure.</param>
        public static bool FiguresAreCompatible(Measurement? used, Measurement? total)
        {
            var usedSpace = SpaceOf(used);
            var totalSpace = SpaceOf(total);

            // Shared system memory is not VRAM and is never presented as
            // the card's memory usage, so it pairs with nothing.
            if (usedSpace == GpuMemorySpace.Shared || totalSpace == GpuMemorySpace.Shared)
                return false;

            // Both figures name their pool. They must name the same one.
            if (usedSpace != GpuMemorySpace.Unknown && totalSpace != GpuMemorySpace.Unknown)
                return usedSpace == totalSpace;

            // At least one figure does not say which pool it describes.
            // The family is then the evidence, and it has to be there: a
            // figure with no family at all cannot be shown to belong to
            // the other figure's pool.
            var usedFamily = FamilyOf(used);
            var totalFamily = FamilyOf(total);

            if (usedFamily is null || totalFamily is null)
                return false;

            return string.Equals(usedFamily, totalFamily, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Decides whether a used figure and a total figure may be shown as
        /// one ratio.
        ///
        /// <para>
        /// When they may not, the ratio is omitted: the used figure is
        /// presented on its own, and no percentage is derived from a pair
        /// that does not describe one pool of one device.
        /// </para>
        /// </summary>
        /// <param name="used">The used-memory figure.</param>
        /// <param name="usedDeviceId">The device the used figure describes.</param>
        /// <param name="total">The total-memory figure.</param>
        /// <param name="totalDeviceId">The device the total figure describes.</param>
        /// <param name="presentedDeviceId">The device being presented.</param>
        public static bool CanCombine(
            Measurement? used,
            string? usedDeviceId,
            Measurement? total,
            string? totalDeviceId,
            string? presentedDeviceId)
        {
            if (!IsUsable(used) || !IsUsable(total))
                return false;

            // --- Attribution ---
            //
            // Both figures must name a device, they must name the same
            // one, and that one must be the device being presented. A
            // figure that names no device describes no particular card, so
            // it cannot be part of a ratio for any card.
            if (usedDeviceId is null || totalDeviceId is null)
                return false;

            if (!string.Equals(usedDeviceId, totalDeviceId, StringComparison.Ordinal))
                return false;

            if (presentedDeviceId is null)
                return false;

            if (!string.Equals(usedDeviceId, presentedDeviceId, StringComparison.Ordinal))
                return false;

            // --- Memory semantics ---
            //
            // The two figures must be shown to describe one memory pool.
            // Shared system memory is never presented as VRAM, so it pairs
            // with nothing, and a pool that was never established has to
            // be established by the family the two figures share.
            if (!FiguresAreCompatible(used, total))
                return false;

            // --- Coherence ---
            //
            // A capacity of zero cannot produce a ratio. A usage above
            // the capacity proves the two numbers describe different
            // pools, so the ratio would be a number no device reports.
            var usedValue = used!.Value!.Value;
            var totalValue = total!.Value!.Value;

            if (totalValue <= 0)
                return false;

            if (usedValue > totalValue)
                return false;

            return true;
        }
    }
}

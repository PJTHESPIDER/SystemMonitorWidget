using System;

namespace System_Monitor.Services
{
    /// <summary>
    /// Decides which source's reading wins for a flattened metric.
    ///
    /// <para>
    /// A metric may be offered by several sources, and they are not
    /// equivalent. The precedence is fixed:
    /// </para>
    ///
    /// <list type="number">
    /// <item><description>
    /// The device-identified backend. It names both the device and the
    /// sensor the value came from, so it is the primary source. When it
    /// read the metric, nothing else is consulted for that metric.
    /// </description></item>
    /// <item><description>
    /// The reconciled vendor fallback. It names a device too, but it is
    /// consulted only for a metric the backend could not read.
    /// </description></item>
    /// <item><description>
    /// The Windows counters. They name no device at all, so their figure
    /// is never presented for a card — but it is still a real reading, and
    /// it is preferred over having nothing to show.
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// The winning candidate keeps its own device id. The id is never
    /// inherited from the source that lost: a card's capacity must not be
    /// attributed to the card whose usage was read.
    /// </para>
    /// </summary>
    public static class ScalarMergePolicy
    {
        /// <summary>
        /// Selects the winning candidate for a metric.
        /// </summary>
        /// <param name="primary">
        /// The device-identified backend's candidate, or null when it did not
        /// supply the metric.
        /// </param>
        /// <param name="secondary">
        /// The reconciled fallback's candidate, or null when it did not
        /// supply the metric.
        /// </param>
        /// <param name="tertiary">
        /// The unattributed Windows counters' candidate, or null when they
        /// did not supply the metric.
        /// </param>
        /// <returns>
        /// The winning candidate, or null when no source supplied the
        /// metric. A null result means "unavailable", never "zero".
        /// </returns>
        public static ScalarCandidate? Resolve(
            ScalarCandidate? primary,
            ScalarCandidate? secondary,
            ScalarCandidate? tertiary)
        {
            if (primary is not null)
                return primary;

            if (secondary is not null)
                return secondary;

            return tertiary;
        }

        /// <summary>
        /// Selects the winning candidate for a metric where only the
        /// device-identified backend and one fallback may supply it.
        /// </summary>
        public static ScalarCandidate? Resolve(
            ScalarCandidate? primary,
            ScalarCandidate? secondary) =>
            Resolve(primary, secondary, null);
    }
}

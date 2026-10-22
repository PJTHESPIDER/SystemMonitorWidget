using System;

namespace System_Monitor.Services
{
    /// <summary>
    /// One candidate reading for a flattened metric, offered by a source.
    ///
    /// <para>
    /// A metric may be offered by more than one source: the device-identified
    /// backend, the reconciled NVIDIA fallback, and the Windows counters each
    /// may be able to supply a given figure. Each candidate carries the
    /// value, the time it was <i>read</i>, the provenance, and — when the
    /// source identifies a device — the device the value describes.
    /// </para>
    ///
    /// <para>
    /// The device id is what makes cross-device mixing detectable. A
    /// candidate with no device id says "this value describes no particular
    /// card", and is therefore never presented for one.
    /// </para>
    /// </summary>
    /// <param name="Value">The value that was read.</param>
    /// <param name="Unit">The canonical unit of the metric.</param>
    /// <param name="Source">The provenance of the reading.</param>
    /// <param name="ReadAt">The time the value was read.</param>
    /// <param name="DeviceId">
    /// The device the value describes, or null when the source does not
    /// identify one.
    /// </param>
    public sealed record ScalarCandidate(
        double Value,
        string Unit,
        string Source,
        DateTimeOffset ReadAt,
        string? DeviceId);
}

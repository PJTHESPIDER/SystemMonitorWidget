using System;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// A GPU-memory display: the text shown, and the memory pool it was
    /// established to describe.
    /// </summary>
    /// <param name="Display">
    /// The combined figure — "used / total (percent)" — when the two
    /// figures may be presented as one ratio, and the used figure alone
    /// when they may not.
    /// </param>
    /// <param name="Label">
    /// The memory pool the display was established to describe, or
    /// <see cref="GpuMemoryLabel.Unspecified"/> when no pool was
    /// established.
    /// </param>
    public readonly record struct GpuMemoryDisplay(string Display, GpuMemoryLabel Label);

    /// <summary>
    /// Builds a GPU-memory display and the heading it is shown under.
    ///
    /// <para><b>Why the label is resolved with the figure</b></para>
    ///
    /// <para>
    /// A heading is a claim. "Dedicated VRAM" claims the figure is the
    /// card's own video memory; "GPU memory" claims only that it is the
    /// card's memory. The widget must make the claim its data supports
    /// and no more, so the heading cannot be a fixed string and it cannot
    /// be resolved independently of the figure it describes.
    /// </para>
    ///
    /// <para>
    /// This is the same class of defect as presenting a default zero as a
    /// measurement: a plausible-looking value that the collection never
    /// established.
    /// </para>
    ///
    /// <para><b>Compatibility and classification are separate</b></para>
    ///
    /// <para>
    /// The compatibility policy answers whether two figures may be
    /// presented as one ratio. The classification answers which memory
    /// pool that ratio is a ratio <i>of</i>. The two are answered
    /// differently, and deliberately:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><description>
    /// A generic "Memory Used" / "Memory Total" pair <b>may</b> be
    /// combined: the two figures are of one family, so they are of one
    /// pool, whatever that pool is. But their name says nothing about
    /// which pool it is, so the pair is <b>not</b> classified as
    /// dedicated. It is headed "GPU memory".
    /// </description></item>
    /// <item><description>
    /// A pair whose two figures both name dedicated video memory is
    /// combined <i>and</i> classified as dedicated.
    /// </description></item>
    /// <item><description>
    /// A pair whose two figures name different pools is not combined at
    /// all, and the used figure is shown alone.
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// So accepting a pairing never upgrades a figure to "dedicated". A
    /// figure whose pool was not established is never headed as dedicated
    /// VRAM.
    /// </para>
    ///
    /// <para><b>One label serves both displays</b></para>
    ///
    /// <para>
    /// The combined figure and the used figure alone are two displays, and
    /// they need not be the same display. They do, however, describe the
    /// same pool whenever both are on screen: a pair the compatibility
    /// policy accepts describes one pool, so its used figure describes
    /// that same pool; and when the pair is rejected, the heading comes
    /// from the used figure alone.
    /// </para>
    ///
    /// <para>
    /// So one label value describes the displayed figure in every case,
    /// and the heading never borrows semantics from a total figure that
    /// was not presented.
    /// </para>
    /// </summary>
    public static class GpuMemoryPresentation
    {
        /// <summary>
        /// Shown in place of a figure that could not be read.
        /// </summary>
        public const string NotAvailable = "N/A";

        /// <summary>
        /// The heading for a figure whose memory pool was established as
        /// dedicated video memory.
        /// </summary>
        public const string DedicatedLabel = "Dedicated GPU memory";

        /// <summary>
        /// The heading for a figure whose memory pool was established as
        /// shared system memory.
        /// </summary>
        public const string SharedLabel = "Shared GPU memory";

        /// <summary>
        /// The heading for a figure whose memory pool was not established.
        ///
        /// <para>
        /// This names the figure without claiming a pool. It is the
        /// neutral form, and it is the one an unestablished pool always
        /// gets.
        /// </para>
        /// </summary>
        public const string UnspecifiedLabel = "GPU memory";

        /// <summary>
        /// The compact heading for a figure whose memory pool was
        /// established as dedicated video memory.
        ///
        /// <para>
        /// The compact form drops the word "GPU" — the figure is already
        /// in the GPU section of the layout, so the word costs width
        /// without adding meaning. It is used only where the layout has
        /// little room for it.
        /// </para>
        /// </summary>
        public const string DedicatedLabelCompact = "Dedicated memory";

        /// <summary>
        /// The compact heading for a figure whose memory pool was
        /// established as shared system memory.
        /// </summary>
        public const string SharedLabelCompact = "Shared memory";

        /// <summary>
        /// The heading text for a memory label.
        /// </summary>
        /// <param name="label">The established label.</param>
        public static string LabelText(GpuMemoryLabel label) => label switch
        {
            GpuMemoryLabel.Dedicated => DedicatedLabel,
            GpuMemoryLabel.Shared => SharedLabel,
            _ => UnspecifiedLabel,
        };

        /// <summary>
        /// The compact heading text for a memory label.
        ///
        /// <para>
        /// The same semantics in fewer characters, for the layout that has
        /// little room. It never claims a different pool than the full
        /// form does.
        /// </para>
        /// </summary>
        /// <param name="label">The established label.</param>
        public static string CompactLabelText(GpuMemoryLabel label) => label switch
        {
            GpuMemoryLabel.Dedicated => DedicatedLabelCompact,
            GpuMemoryLabel.Shared => SharedLabelCompact,
            _ => UnspecifiedLabel,
        };

        /// <summary>
        /// Resolves a GPU-memory display and its heading together.
        ///
        /// <para>
        /// The rules, in order:
        /// </para>
        ///
        /// <list type="number">
        /// <item><description>
        /// The two figures are combined into one ratio only when the
        /// compatibility policy accepts them: the same device, the device
        /// being presented, one memory pool, and a coherent pair. The
        /// combined display's heading is the pool the pair
        /// <i>establishes</i>, which is not the pool the compatibility
        /// policy merely <i>allows</i>.
        /// </description></item>
        /// <item><description>
        /// When the pair is not combined, the used figure is shown on its
        /// own, and its heading comes from the used figure alone. A total
        /// figure that was rejected — because it belongs to another
        /// device, describes another pool, or is incoherent — contributes
        /// nothing to the heading.
        /// </description></item>
        /// <item><description>
        /// When there is no usable figure at all, the display is "N/A" and
        /// no figure is substituted. The heading stays neutral unless the
        /// presented device's <i>own</i> figures establish the memory
        /// category independently — a category established by the device is
        /// a fact about the card, not a borrowing from a rejected pair.
        /// </description></item>
        /// </list>
        /// </summary>
        /// <param name="used">The used-memory figure.</param>
        /// <param name="usedDeviceId">The device the used figure describes.</param>
        /// <param name="total">The total-memory figure.</param>
        /// <param name="totalDeviceId">The device the total figure describes.</param>
        /// <param name="presentedDeviceId">The device being presented.</param>
        public static GpuMemoryDisplay Resolve(
            Measurement? used,
            string? usedDeviceId,
            Measurement? total,
            string? totalDeviceId,
            string? presentedDeviceId)
        {
            if (!GpuMemoryCompatibilityPolicy.IsUsable(used))
            {
                // Nothing to describe, so nothing is claimed about the
                // figure. The heading names the memory category only when
                // the presented device's own figures establish it.
                var category = GpuMemoryCompatibilityPolicy.DeviceCategoryOf(
                    used, usedDeviceId, total, totalDeviceId, presentedDeviceId);

                return new GpuMemoryDisplay(
                    NotAvailable,
                    category ?? GpuMemoryLabel.Unspecified);
            }

            var usedValue = used!.Value!.Value;

            var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
                used,
                usedDeviceId,
                total,
                totalDeviceId,
                presentedDeviceId);

            if (canCombine)
            {
                var totalValue = total!.Value!.Value;
                var pct = usedValue / totalValue * 100.0;

                var display =
                    $"{ByteFormatter.FormatBytes(usedValue)} / {ByteFormatter.FormatBytes(totalValue)} ({pct:F1}%)";

                // The combined display's heading is the pool the pair
                // establishes — which is not the pool the compatibility
                // policy merely allows.
                var label = GpuMemoryCompatibilityPolicy.CombinedLabelOf(used, total);

                return new GpuMemoryDisplay(display, label);
            }

            // The used figure alone. Its heading comes from the used figure
            // alone: the rejected total contributes nothing.
            return new GpuMemoryDisplay(
                ByteFormatter.FormatBytes(usedValue),
                GpuMemoryCompatibilityPolicy.LabelOf(used));
        }
    }
}

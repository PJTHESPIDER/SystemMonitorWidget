using System;
using System.Collections.Generic;
using System.Linq;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// The outcome of resolving which GPU a widget should present.
    /// </summary>
    public enum GpuSelectionOutcome
    {
        /// <summary>
        /// A user-selected device was found and is the presented device.
        /// </summary>
        Selected,

        /// <summary>
        /// No device was selected, and the documented default policy chose
        /// the presented device.
        /// </summary>
        DefaultPolicy,

        /// <summary>
        /// A device was selected, but it is not present in this collection —
        /// it was removed, disabled, or the machine changed. The documented
        /// default policy chose the presented device instead.
        /// </summary>
        SelectionMissing,

        /// <summary>
        /// A device was selected, but the collection reported no GPU at all.
        /// Nothing is presented.
        /// </summary>
        NoGpuAvailable,
    }

    /// <summary>
    /// The GPU a widget presents, and why.
    /// </summary>
    /// <param name="Device">The presented device, or null when none is available.</param>
    /// <param name="Outcome">Why that device was chosen.</param>
    /// <param name="RequestedDeviceId">
    /// The instance id the user selected, when there was one. Retained even
    /// when it did not resolve, so the caller can tell "the user chose this
    /// and it is gone" from "the user never chose anything".
    /// </param>
    public sealed record GpuSelection(
        HardwareDevice? Device,
        GpuSelectionOutcome Outcome,
        string? RequestedDeviceId = null)
    {
        /// <summary>True when the presented device is the one the user chose.</summary>
        public bool IsUserSelected => Outcome == GpuSelectionOutcome.Selected;
    }

    /// <summary>
    /// Chooses which GPU a widget presents.
    ///
    /// <para><b>Default selection policy</b></para>
    ///
    /// <para>
    /// When the user has not chosen a device, the default is chosen by a
    /// total order over the detected GPUs:
    /// </para>
    ///
    /// <list type="number">
    /// <item><description>
    /// <b>Integration class first.</b> A GPU known to be <i>discrete</i> wins,
    /// because a separate card is the device whose load and temperature are
    /// the conventional headline figures. A GPU whose class is <i>unknown</i>
    /// comes next — most vendors do not expose the class, and an unknown
    /// class is far more often discrete than integrated. A GPU known to be
    /// <i>integrated</i> comes last: it is the on-die part of the CPU, and is
    /// only the headline when nothing else is present.
    /// </description></item>
    /// <item><description>
    /// <b>Instance identity as the tie-break.</b> Within the same
    /// integration class, the device with the lowest
    /// <see cref="HardwareDevice.InstanceId"/> wins, compared ordinally. The
    /// instance id is a per-device identifier assigned by the platform, so
    /// this choice does not depend on the order in which the devices were
    /// enumerated. It is therefore stable across collections, across
    /// reboots, and regardless of which card the backend happened to list
    /// first.
    /// </description></item>
    /// <item><description>
    /// <b>Device id as the final tie-break.</b> Two devices that share an
    /// instance id (which should not happen, but can when the backend
    /// reports a device twice) are separated by their device id, again
    /// ordinally. The order is total, so the default is always
    /// deterministic.
    /// </description></item>
    /// </list>
    ///
    /// <para><b>User selection</b></para>
    ///
    /// <para>
    /// A user selection is a device <i>instance id</i>, never an
    /// enumeration index. When the selected device is present, it is
    /// presented. When it is absent — the card was removed, disabled, or the
    /// machine changed — the selection does not resolve, and the default
    /// policy above is applied instead. The widget then keeps working
    /// against whichever GPU is present, rather than showing nothing.
    /// </para>
    /// </summary>
    public static class GpuSelectionPolicy
    {
        /// <summary>
        /// Resolves the GPU to present.
        /// </summary>
        /// <param name="gpus">The detected GPUs, in any order.</param>
        /// <param name="selectedInstanceId">
        /// The instance id the user selected, or null/empty when no
        /// selection has been made.
        /// </param>
        public static GpuSelection Resolve(
            IReadOnlyList<HardwareDevice>? gpus,
            string? selectedInstanceId)
        {
            var devices = (gpus ?? Array.Empty<HardwareDevice>())
                .Where(g => g is not null)
                .ToList();

            var requested = string.IsNullOrWhiteSpace(selectedInstanceId) ? null : selectedInstanceId.Trim();

            if (devices.Count == 0)
            {
                return new GpuSelection(
                    null,
                    requested is null ? GpuSelectionOutcome.NoGpuAvailable : GpuSelectionOutcome.NoGpuAvailable,
                    requested);
            }

            if (requested is not null)
            {
                // Match on the instance id only. The display name is never
                // used to resolve a selection: two identical cards can share
                // a name, and matching by name would silently pick the
                // wrong one.
                var selected = devices.FirstOrDefault(g =>
                    string.Equals(g.InstanceId, requested, StringComparison.OrdinalIgnoreCase));

                if (selected is not null)
                    return new GpuSelection(selected, GpuSelectionOutcome.Selected, requested);

                // The selected device is gone. Fall back to the default
                // policy rather than presenting nothing.
                return new GpuSelection(
                    SelectDefault(devices),
                    GpuSelectionOutcome.SelectionMissing,
                    requested);
            }

            return new GpuSelection(SelectDefault(devices), GpuSelectionOutcome.DefaultPolicy, null);
        }

        /// <summary>
        /// Applies the documented default policy.
        /// </summary>
        private static HardwareDevice SelectDefault(List<HardwareDevice> devices) =>
            devices
                .OrderBy(g => IntegrationRank(g.GpuIntegration))
                .ThenBy(g => g.InstanceId ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(g => g.DeviceId, StringComparer.Ordinal)
                .First();

        private static int IntegrationRank(GpuIntegration integration) => integration switch
        {
            GpuIntegration.Discrete => 0,
            GpuIntegration.Unknown => 1,
            GpuIntegration.Integrated => 2,
            _ => 3,
        };

        /// <summary>
        /// Lists the devices a user may choose from, in a stable order.
        ///
        /// The order matches the default policy, so the first entry is the
        /// device the widget would present without a selection.
        /// </summary>
        public static IReadOnlyList<HardwareDevice> OrderForPresentation(
            IReadOnlyList<HardwareDevice>? gpus)
        {
            var devices = (gpus ?? Array.Empty<HardwareDevice>())
                .Where(g => g is not null)
                .ToList();

            return devices
                .OrderBy(g => IntegrationRank(g.GpuIntegration))
                .ThenBy(g => g.InstanceId ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(g => g.DeviceId, StringComparer.Ordinal)
                .ToList();
        }
    }
}

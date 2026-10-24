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
    /// <para><b>Default selection policy (with telemetry awareness)</b></para>
    ///
    /// <para>
    /// When the user has not chosen a device, the default is chosen by a
    /// total order over the detected GPUs, with a preference for devices
    /// that have usable current telemetry:
    /// </para>
    ///
    /// <list type="number">
    /// <item><description>
    /// <b>Telemetry availability first.</b> A device that has at least one
    /// usable current reading (load, temperature, or memory — including
    /// properly attributed fallback scalars) is preferred over a device with
    /// no usable telemetry. A valid zero-percent load is a real reading and
    /// counts as usable. Stale readings do not count.
    /// </description></item>
    /// <item><description>
    /// <b>Integration class.</b> Among devices with equal telemetry
    /// availability, a GPU known to be <i>discrete</i> wins, because a
    /// separate card is the device whose load and temperature are the
    /// conventional headline figures. A GPU whose class is <i>unknown</i>
    /// comes next — most vendors do not expose the class, and an unknown
    /// class is far more often discrete than integrated. A GPU known to be
    /// <i>integrated</i> comes last: it is the on-die part of the CPU, and
    /// is only the headline when nothing else is present.
    /// </description></item>
    /// <item><description>
    /// <b>Instance identity as the tie-break.</b> Within the same
    /// integration class and telemetry availability, the device with the
    /// lowest <see cref="HardwareDevice.InstanceId"/> wins, compared
    /// ordinally. The instance id is a per-device identifier assigned by
    /// the platform, so this choice does not depend on the order in which
    /// the devices were enumerated. It is therefore stable across
    /// collections, across reboots, and regardless of which card the
    /// backend happened to list first.
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
    ///
    /// <para><b>Explicit selection preservation</b></para>
    ///
    /// <para>
    /// A present explicitly selected device is never silently replaced
    /// solely because its sensors are unavailable. The widget shows
    /// unavailable values honestly for that device. This preserves the
    /// user's choice even when the picker is inaccessible.
    /// </para>
    /// </summary>
    public static class GpuSelectionPolicy
    {
        /// <summary>
        /// Resolves the GPU to present, using the device list only (no
        /// telemetry). This overload is used when the snapshot is not
        /// available, such as during early initialization or in tests that
        /// only exercise the device-ordering logic.
        /// </summary>
        public static GpuSelection Resolve(
            IReadOnlyList<HardwareDevice>? gpus,
            string? selectedInstanceId)
        {
            return Resolve(gpus, selectedInstanceId, snapshot: null, gpuLoadPercent: null, gpuLoadDeviceId: null, gpuTemperatureC: null, gpuTemperatureDeviceId: null, gpuVramUsedBytes: null, gpuVramUsedDeviceId: null, gpuVramTotalBytes: null, gpuVramTotalDeviceId: null);
        }

        /// <summary>
        /// Resolves the GPU to present, with full telemetry awareness for
        /// the automatic primary selection.
        /// </summary>
        /// <param name="gpus">The detected GPUs, in any order.</param>
        /// <param name="selectedInstanceId">The instance id the user selected, or null/empty.</param>
        /// <param name="snapshot">The completed collection snapshot, for telemetry checks.</param>
        /// <param name="gpuLoadPercent">The snapshot's scalar GPU load.</param>
        /// <param name="gpuLoadDeviceId">The device the scalar load describes.</param>
        /// <param name="gpuTemperatureC">The snapshot's scalar GPU temperature.</param>
        /// <param name="gpuTemperatureDeviceId">The device the scalar temperature describes.</param>
        /// <param name="gpuVramUsedBytes">The snapshot's scalar VRAM used.</param>
        /// <param name="gpuVramUsedDeviceId">The device the scalar VRAM used describes.</param>
        /// <param name="gpuVramTotalBytes">The snapshot's scalar VRAM total.</param>
        /// <param name="gpuVramTotalDeviceId">The device the scalar VRAM total describes.</param>
        public static GpuSelection Resolve(
            IReadOnlyList<HardwareDevice>? gpus,
            string? selectedInstanceId,
            MetricsSnapshot? snapshot,
            Measurement? gpuLoadPercent,
            string? gpuLoadDeviceId,
            Measurement? gpuTemperatureC,
            string? gpuTemperatureDeviceId,
            Measurement? gpuVramUsedBytes,
            string? gpuVramUsedDeviceId,
            Measurement? gpuVramTotalBytes,
            string? gpuVramTotalDeviceId)
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
                    SelectDefault(devices, snapshot, gpuLoadPercent, gpuLoadDeviceId, gpuTemperatureC, gpuTemperatureDeviceId, gpuVramUsedBytes, gpuVramUsedDeviceId, gpuVramTotalBytes, gpuVramTotalDeviceId),
                    GpuSelectionOutcome.SelectionMissing,
                    requested);
            }

            return new GpuSelection(
                SelectDefault(devices, snapshot, gpuLoadPercent, gpuLoadDeviceId, gpuTemperatureC, gpuTemperatureDeviceId, gpuVramUsedBytes, gpuVramUsedDeviceId, gpuVramTotalBytes, gpuVramTotalDeviceId),
                GpuSelectionOutcome.DefaultPolicy,
                null);
        }

        /// <summary>
        /// Applies the documented default policy with telemetry awareness.
        /// </summary>
        private static HardwareDevice SelectDefault(
            List<HardwareDevice> devices,
            MetricsSnapshot? snapshot,
            Measurement? gpuLoadPercent,
            string? gpuLoadDeviceId,
            Measurement? gpuTemperatureC,
            string? gpuTemperatureDeviceId,
            Measurement? gpuVramUsedBytes,
            string? gpuVramUsedDeviceId,
            Measurement? gpuVramTotalBytes,
            string? gpuVramTotalDeviceId)
        {
            // When no snapshot is available (legacy path, tests), fall back
            // to the original ordering.
            if (snapshot is null)
            {
                return devices
                    .OrderBy(g => IntegrationRank(g.GpuIntegration))
                    .ThenBy(g => g.InstanceId ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(g => g.DeviceId, StringComparer.Ordinal)
                    .First();
            }

            // Partition devices by whether they have usable telemetry.
            var withTelemetry = new List<HardwareDevice>();
            var withoutTelemetry = new List<HardwareDevice>();

            foreach (var device in devices)
            {
                if (HasUsableTelemetry(device, snapshot, gpuLoadPercent, gpuLoadDeviceId, gpuTemperatureC, gpuTemperatureDeviceId, gpuVramUsedBytes, gpuVramUsedDeviceId, gpuVramTotalBytes, gpuVramTotalDeviceId))
                    withTelemetry.Add(device);
                else
                    withoutTelemetry.Add(device);
            }

            // Prefer devices with usable telemetry, then apply the standard
            // ordering within each partition.
            var ordered = withTelemetry.Count > 0 ? withTelemetry : withoutTelemetry;

            return ordered
                .OrderBy(g => IntegrationRank(g.GpuIntegration))
                .ThenBy(g => g.InstanceId ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(g => g.DeviceId, StringComparer.Ordinal)
                .First();
        }

        /// <summary>
        /// Determines whether a GPU has at least one usable current reading.
        /// A valid zero-percent load counts as usable. Stale readings do not.
        /// Properly attributed fallback scalars count for the device they describe.
        /// </summary>
        private static bool HasUsableTelemetry(
            HardwareDevice device,
            MetricsSnapshot snapshot,
            Measurement? gpuLoadPercent,
            string? gpuLoadDeviceId,
            Measurement? gpuTemperatureC,
            string? gpuTemperatureDeviceId,
            Measurement? gpuVramUsedBytes,
            string? gpuVramUsedDeviceId,
            Measurement? gpuVramTotalBytes,
            string? gpuVramTotalDeviceId)
        {
            var deviceId = device.DeviceId;

            // Check device-level readings first.
            var load = snapshot.Readings.FirstOrDefault(r => r.DeviceId == deviceId && r.Metric == LhmSensorSelection.GpuLoadMetric)?.Measurement;
            if (IsUsable(load))
                return true;

            var temp = snapshot.Readings.FirstOrDefault(r => r.DeviceId == deviceId && r.Metric == LhmSensorSelection.GpuTemperatureMetric)?.Measurement;
            if (IsUsable(temp))
                return true;

            var vramUsed = snapshot.Readings.FirstOrDefault(r => r.DeviceId == deviceId && r.Metric == LhmSensorSelection.GpuMemoryUsedMetric)?.Measurement;
            if (IsUsable(vramUsed))
                return true;

            var vramTotal = snapshot.Readings.FirstOrDefault(r => r.DeviceId == deviceId && r.Metric == LhmSensorSelection.GpuMemoryTotalMetric)?.Measurement;
            if (IsUsable(vramTotal))
                return true;

            // Check properly attributed scalar fallbacks.
            if (IsUsable(gpuLoadPercent) && string.Equals(gpuLoadDeviceId, deviceId, StringComparison.Ordinal))
                return true;

            if (IsUsable(gpuTemperatureC) && string.Equals(gpuTemperatureDeviceId, deviceId, StringComparison.Ordinal))
                return true;

            if (IsUsable(gpuVramUsedBytes) && string.Equals(gpuVramUsedDeviceId, deviceId, StringComparison.Ordinal))
                return true;

            if (IsUsable(gpuVramTotalBytes) && string.Equals(gpuVramTotalDeviceId, deviceId, StringComparison.Ordinal))
                return true;

            return false;
        }

        private static bool IsUsable(Measurement? measurement) =>
            measurement is not null
            && measurement.Status == MeasurementStatus.Available
            && measurement.Value.HasValue;

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

        /// <summary>
        /// Lists the devices that have usable telemetry for display as
        /// secondary rows, in presentation order.
        ///
        /// A device qualifies if it has at least one usable reading that the
        /// extra-device row actually displays (load or temperature). A device
        /// with only memory telemetry does not qualify, because the extra
        /// row does not show memory.
        ///
        /// Devices that are in the grace period (tracked by the collector's
        /// extras tracker) but have no current telemetry are still included
        /// at the end of the list so they can be presented with N/A values.
        /// </summary>
        public static IReadOnlyList<HardwareDevice> OrderExtrasForPresentation(
            IReadOnlyList<HardwareDevice>? gpus,
            MetricsSnapshot? snapshot,
            Measurement? gpuLoadPercent,
            string? gpuLoadDeviceId,
            Measurement? gpuTemperatureC,
            string? gpuTemperatureDeviceId)
        {
            var devices = (gpus ?? Array.Empty<HardwareDevice>())
                .Where(g => g is not null)
                .ToList();

            if (snapshot is null)
                return devices
                    .OrderBy(g => IntegrationRank(g.GpuIntegration))
                    .ThenBy(g => g.InstanceId ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(g => g.DeviceId, StringComparer.Ordinal)
                    .ToList();

            var withTelemetry = new List<HardwareDevice>();
            var withoutTelemetry = new List<HardwareDevice>();

            foreach (var device in devices)
            {
                if (HasUsableExtraTelemetry(device, snapshot, gpuLoadPercent, gpuLoadDeviceId, gpuTemperatureC, gpuTemperatureDeviceId))
                    withTelemetry.Add(device);
                else
                    withoutTelemetry.Add(device);
            }

            // Return devices with telemetry first (ordered by policy), then
            // devices without telemetry (also ordered by policy). This ensures
            // devices in grace period without current telemetry are still
            // included for presentation with N/A values.
            return withTelemetry
                .OrderBy(g => IntegrationRank(g.GpuIntegration))
                .ThenBy(g => g.InstanceId ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(g => g.DeviceId, StringComparer.Ordinal)
                .Concat(withoutTelemetry
                    .OrderBy(g => IntegrationRank(g.GpuIntegration))
                    .ThenBy(g => g.InstanceId ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(g => g.DeviceId, StringComparer.Ordinal))
                .ToList();
        }

        /// <summary>
        /// Determines whether a GPU has usable telemetry for the extra-device
        /// row (load or temperature). Memory-only telemetry does not qualify,
        /// because the extra row only shows load and temperature.
        /// </summary>
        private static bool HasUsableExtraTelemetry(
            HardwareDevice device,
            MetricsSnapshot snapshot,
            Measurement? gpuLoadPercent,
            string? gpuLoadDeviceId,
            Measurement? gpuTemperatureC,
            string? gpuTemperatureDeviceId)
        {
            var deviceId = device.DeviceId;

            var load = snapshot.Readings.FirstOrDefault(r => r.DeviceId == deviceId && r.Metric == LhmSensorSelection.GpuLoadMetric)?.Measurement;
            if (IsUsable(load))
                return true;

            var temp = snapshot.Readings.FirstOrDefault(r => r.DeviceId == deviceId && r.Metric == LhmSensorSelection.GpuTemperatureMetric)?.Measurement;
            if (IsUsable(temp))
                return true;

            if (IsUsable(gpuLoadPercent) && string.Equals(gpuLoadDeviceId, deviceId, StringComparison.Ordinal))
                return true;

            if (IsUsable(gpuTemperatureC) && string.Equals(gpuTemperatureDeviceId, deviceId, StringComparison.Ordinal))
                return true;

            return false;
        }
    }
}

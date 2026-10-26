using System;
using System.Collections.Generic;
using System.Linq;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// Tracks GPU selection across collection cycles with a grace period for
    /// transient telemetry failures.
    ///
    /// <para><b>Why this exists</b></para>
    ///
    /// <para>
    /// The telemetry-aware selection prefers devices with usable current
    /// readings. A single failed read (permission hiccup, WMI glitch, brief
    /// driver stall) would otherwise cause an immediate fallback to a
    /// different GPU, which is visually disruptive and logically incorrect —
    /// the device hasn't disappeared, it just had one bad cycle.
    /// </para>
    ///
    /// <para><b>Grace policy</b></para>
    ///
    /// <para>
    /// The tracker retains the last automatically selected GPU for a
    /// configurable number of consecutive cycles without usable telemetry
    /// before allowing the default policy to choose a different device.
    /// </para>
    ///
    /// <list type="number">
    /// <item><description>
    /// <b>Explicit user selection is never affected.</b> A device the user
    /// chose is presented even when all its sensors fail, until the device
    /// disappears from the system. The grace period only applies to the
    /// automatic default selection.
    /// </description></item>
    /// <item><description>
    /// <b>Valid zero-percent load counts as usable telemetry.</b> A device
    /// reporting 0% load is providing a real reading and resets the failure
    /// counter.
    /// </description></item>
    /// <item><description>
    /// <b>Stale, failed, unsupported, null, and nonfinite readings do not
    /// reset the counter.</b> Only a fresh <see cref="MeasurementStatus.Available"/>
    /// reading with a finite value counts.
    /// </description></item>
    /// <item><description>
    /// <b>Wrong-device or unattributed fallback scalars do not reset the
    /// counter.</b> A scalar figure that names a different device, or no
    /// device at all, cannot qualify the tracked device.
    /// </description></item>
    /// <item><description>
    /// <b>Recovery is deterministic.</b> When the tracked device produces a
    /// usable reading again, the failure counter resets to zero immediately.
    /// </description></item>
    /// <item><description>
    /// <b>Device disappearance ends the grace period immediately.</b> If the
    /// tracked GPU is no longer in the device list, the tracker clears and
    /// the default policy applies at once.
    /// </description></item>
    /// </list>
    ///
    /// <para><b>State ownership</b></para>
    ///
    /// <para>
    /// The tracker is owned by the collector (<see cref="SystemMetricsService"/>)
    /// which calls <see cref="Update"/> once per cycle. The ranking logic in
    /// <see cref="GpuSelectionPolicy"/> remains pure; the tracker wraps it
    /// with stateful grace-period behaviour. The presenter does not hold
    /// selection state — it receives the already-resolved device from the
    /// snapshot.
    /// </para>
    ///
    /// <para><b>Deterministic tests</b></para>
    ///
    /// <para>
    /// The tracker accepts an injected <see cref="TimeProvider"/> so tests
    /// can advance virtual time and verify exact cycle counts without
    /// sleeping.
    /// </para>
    /// </summary>
    public sealed class GpuSelectionTracker
    {
        public sealed record State(string? TrackedInstanceId, int ConsecutiveFailures, string? TrackedDeviceId, DateTimeOffset LastUpdate);

        public State CaptureState() => new(_trackedInstanceId, _consecutiveFailures, _trackedDeviceId, _lastUpdate);

        public void RestoreState(State state)
        {
            _trackedInstanceId = state.TrackedInstanceId;
            _consecutiveFailures = state.ConsecutiveFailures;
            _trackedDeviceId = state.TrackedDeviceId;
            _lastUpdate = state.LastUpdate;
        }

        /// <summary>
        /// How many consecutive cycles without usable telemetry the tracker
        /// retains the automatically selected GPU before permitting a fallback.
        /// </summary>
        public const int DefaultGraceCycles = 3;

        private readonly int _graceCycles;
        private readonly TimeProvider _timeProvider;

        // The instance id of the GPU that was automatically selected
        // (not user-selected) and is currently in its grace period.
        private string? _trackedInstanceId;

        // Number of consecutive cycles the tracked GPU has lacked usable
        // telemetry. Reset to 0 when usable telemetry appears.
        private int _consecutiveFailures;

        // The device id of the tracked GPU from the last cycle it had
        // usable telemetry. Used to verify the device still exists.
        private string? _trackedDeviceId;

        // The last time the tracker updated its state. Used for diagnostics.
        private DateTimeOffset _lastUpdate;

        /// <summary>
        /// Creates a new tracker with the default grace period.
        /// </summary>
        public GpuSelectionTracker()
            : this(DefaultGraceCycles, TimeProvider.System)
        {
        }

        /// <summary>
        /// Creates a new tracker with a custom grace period and time provider.
        /// </summary>
        /// <param name="graceCycles">Number of cycles to retain a device without usable telemetry.</param>
        /// <param name="timeProvider">Time source for diagnostics (tests use FakeTimeProvider).</param>
        public GpuSelectionTracker(int graceCycles, TimeProvider timeProvider)
        {
            _graceCycles = Math.Max(0, graceCycles);
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        }

        /// <summary>
        /// Updates the tracker with the current device list and telemetry,
        /// returning the GPU that should be presented.
        ///
        /// <para>
        /// This is called once per collection cycle by the coordinator. It
        /// applies the grace period to the automatic default selection only.
        /// </para>
        /// </summary>
        /// <param name="gpus">The GPUs currently detected.</param>
        /// <param name="selectedInstanceId">The user's explicit selection, or null.</param>
        /// <param name="snapshot">The completed collection snapshot for telemetry checks.</param>
        /// <param name="gpuLoadPercent">The snapshot's scalar GPU load.</param>
        /// <param name="gpuLoadDeviceId">The device the scalar load describes.</param>
        /// <param name="gpuTemperatureC">The snapshot's scalar GPU temperature.</param>
        /// <param name="gpuTemperatureDeviceId">The device the scalar temperature describes.</param>
        /// <param name="gpuVramUsedBytes">The snapshot's scalar VRAM used.</param>
        /// <param name="gpuVramUsedDeviceId">The device the scalar VRAM used describes.</param>
        /// <param name="gpuVramTotalBytes">The snapshot's scalar VRAM total.</param>
        /// <param name="gpuVramTotalDeviceId">The device the scalar VRAM total describes.</param>
        /// <returns>The GPU to present, or null when none is available.</returns>
        public HardwareDevice? Update(
            IReadOnlyList<HardwareDevice> gpus,
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

            _lastUpdate = _timeProvider.GetUtcNow();

            // No GPUs at all: clear tracking and return null.
            if (devices.Count == 0)
            {
                ClearTracking();
                return null;
            }

            var requested = string.IsNullOrWhiteSpace(selectedInstanceId) ? null : selectedInstanceId.Trim();

            // Explicit user selection: always honour it if present, never
            // apply grace period. The tracker does not retain user choices.
            if (requested is not null)
            {
                var selected = devices.FirstOrDefault(g =>
                    string.Equals(g.InstanceId, requested, StringComparison.OrdinalIgnoreCase));

                if (selected is not null)
                {
                    ClearTracking();
                    return selected;
                }

                // Selected device is gone. Fall back to default policy
                // without grace (the user's choice is already lost).
                ClearTracking();
                return SelectDefault(devices, snapshot, gpuLoadPercent, gpuLoadDeviceId, gpuTemperatureC, gpuTemperatureDeviceId, gpuVramUsedBytes, gpuVramUsedDeviceId, gpuVramTotalBytes, gpuVramTotalDeviceId);
            }

            // Automatic selection: apply grace period to the currently tracked device.
            return UpdateAutomaticSelection(devices, snapshot, gpuLoadPercent, gpuLoadDeviceId, gpuTemperatureC, gpuTemperatureDeviceId, gpuVramUsedBytes, gpuVramUsedDeviceId, gpuVramTotalBytes, gpuVramTotalDeviceId);
        }

        /// <summary>
        /// Determines whether a GPU has at least one usable current reading.
        /// Mirrors <see cref="GpuSelectionPolicy.HasUsableTelemetry"/> but is
        /// kept here so the tracker is self-contained for tests.
        /// </summary>
        private static bool HasUsableTelemetry(
            HardwareDevice device,
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
            if (snapshot is null)
                return false;

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
            && measurement.Value.HasValue
            && !double.IsNaN(measurement.Value.Value)
            && !double.IsInfinity(measurement.Value.Value);

        private HardwareDevice? UpdateAutomaticSelection(
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
            // If we're tracking a device, check if it's still present.
            if (_trackedInstanceId is not null)
            {
                var tracked = devices.FirstOrDefault(g =>
                    string.Equals(g.InstanceId, _trackedInstanceId, StringComparison.OrdinalIgnoreCase));

                if (tracked is null)
                {
                    // Tracked device disappeared. Clear and re-evaluate.
                    ClearTracking();
                    return SelectDefault(devices, snapshot, gpuLoadPercent, gpuLoadDeviceId, gpuTemperatureC, gpuTemperatureDeviceId, gpuVramUsedBytes, gpuVramUsedDeviceId, gpuVramTotalBytes, gpuVramTotalDeviceId);
                }

                // Device still exists. Check if it has usable telemetry now.
                bool hasTelemetry = HasUsableTelemetry(tracked, snapshot, gpuLoadPercent, gpuLoadDeviceId, gpuTemperatureC, gpuTemperatureDeviceId, gpuVramUsedBytes, gpuVramUsedDeviceId, gpuVramTotalBytes, gpuVramTotalDeviceId);

                if (hasTelemetry)
                {
                    // Telemetry recovered: reset failure counter, keep tracking.
                    _consecutiveFailures = 0;
                    _trackedDeviceId = tracked.DeviceId;
                    return tracked;
                }

                // No usable telemetry this cycle.
                _consecutiveFailures++;

                if (_consecutiveFailures <= _graceCycles)
                {
                    // Within grace period: keep presenting the tracked device.
                    return tracked;
                }

                // Grace period exhausted: clear tracking and re-evaluate.
                ClearTracking();
            }

            // No tracked device (or grace exhausted): run default policy.
            var selected = SelectDefault(devices, snapshot, gpuLoadPercent, gpuLoadDeviceId, gpuTemperatureC, gpuTemperatureDeviceId, gpuVramUsedBytes, gpuVramUsedDeviceId, gpuVramTotalBytes, gpuVramTotalDeviceId);

            // If the default policy chose a device with usable telemetry, start tracking it.
            if (selected is not null)
            {
                bool hasTelemetry = HasUsableTelemetry(selected, snapshot, gpuLoadPercent, gpuLoadDeviceId, gpuTemperatureC, gpuTemperatureDeviceId, gpuVramUsedBytes, gpuVramUsedDeviceId, gpuVramTotalBytes, gpuVramTotalDeviceId);
                if (hasTelemetry)
                {
                    _trackedInstanceId = selected.InstanceId;
                    _trackedDeviceId = selected.DeviceId;
                    _consecutiveFailures = 0;
                }
            }

            return selected;
        }

        private void ClearTracking()
        {
            _trackedInstanceId = null;
            _trackedDeviceId = null;
            _consecutiveFailures = 0;
        }

        /// <summary>
        /// Applies the default policy without grace period. Mirrors
        /// <see cref="GpuSelectionPolicy.SelectDefault"/> for self-contained tests.
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
            // When no snapshot is available, fall back to the original ordering.
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

            // Prefer devices with usable telemetry, then apply the standard ordering within each partition.
            var ordered = withTelemetry.Count > 0 ? withTelemetry : withoutTelemetry;

            return ordered
                .OrderBy(g => IntegrationRank(g.GpuIntegration))
                .ThenBy(g => g.InstanceId ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(g => g.DeviceId, StringComparer.Ordinal)
                .First();
        }

        private static int IntegrationRank(GpuIntegration integration) => integration switch
        {
            GpuIntegration.Discrete => 0,
            GpuIntegration.Unknown => 1,
            GpuIntegration.Integrated => 2,
            _ => 3,
        };

        /// <summary>
        /// Gets the instance id of the GPU currently being tracked (in grace
        /// period), or null when no device is tracked.
        /// </summary>
        public string? TrackedInstanceId => _trackedInstanceId;

        /// <summary>
        /// Gets the number of consecutive cycles the tracked GPU has lacked
        /// usable telemetry.
        /// </summary>
        public int ConsecutiveFailures => _consecutiveFailures;

        /// <summary>
        /// Gets the remaining grace cycles before fallback is permitted.
        /// </summary>
        public int RemainingGraceCycles => Math.Max(0, _graceCycles - _consecutiveFailures);

        /// <summary>
        /// Gets the time of the last tracker update.
        /// </summary>
        public DateTimeOffset LastUpdate => _lastUpdate;
    }
}

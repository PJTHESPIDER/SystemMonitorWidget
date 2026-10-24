using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// Tracks secondary GPU (extra device) eligibility across collection
    /// cycles with a grace period for transient telemetry failures.
    ///
    /// <para><b>Why this exists</b></para>
    ///
    /// <para>
    /// The telemetry-aware extra-device ordering prefers devices with usable
    /// current readings (load or temperature). A single failed read would
    /// otherwise cause an immediate removal of the secondary row, which is
    /// visually disruptive. The tracker retains eligible secondary devices
    /// for a configurable number of consecutive cycles without usable displayed
    /// telemetry before removing them.
    /// </para>
    ///
    /// <para><b>Grace policy</b></para>
    ///
    /// <para>
    /// The tracker retains each eligible secondary GPU for a configurable
    /// number of consecutive cycles without usable <i>displayed</i> telemetry
    /// (load or temperature — memory-only does not qualify) before removing it.
    /// </para>
    ///
    /// <list type="number">
    /// <item><description>
    /// <b>Eligibility starts with useful displayed telemetry.</b> A device
    /// becomes eligible when it has at least one usable current reading of
    /// load or temperature. Valid 0% counts. Memory-only telemetry does not
    /// qualify. A device that has never qualified does not receive grace
    /// merely because it was detected.
    /// </description></item>
    /// <item><description>
    /// <b>Retain eligibility through transient failures.</b> After qualifying,
    /// retain the device through three consecutive completed collections
    /// without usable displayed telemetry. Remove it on the fourth consecutive
    /// failed/unusable collection. A qualifying successful collection resets
    /// the failure count to zero.
    /// </description></item>
    /// <item><description>
    /// <b>Count completed collection evaluations.</b> Count completed
    /// collection evaluations, not presentation calls, widget instances, or
    /// refresh publications. Do not count a canceled/aborted collection that
    /// produces no accepted snapshot as an additional failure.
    /// </description></item>
    /// <item><description>
    /// <b>Availability separate from eligibility.</b> Grace preserves the
    /// row/device identity, not measurement freshness. Present current
    /// failures as N/A or stale where applicable. Never mark retained
    /// measurements as newly successful or update their last-successful-read
    /// timestamps. Do not fabricate values to fill a retained row.
    /// </description></item>
    /// <item><description>
    /// <b>Device disappearance ends eligibility immediately.</b> If the
    /// tracked GPU is no longer in the device list, its eligibility is
    /// removed at once. Reappearance after disappearance starts a new
    /// eligibility history. Never transfer history between identically named
    /// devices.
    /// </description></item>
    /// <item><description>
    /// <b>Role transitions.</b> A device becoming the primary GPU is
    /// excluded from extras. A device transitioning from primary to secondary
    /// starts with a fresh eligibility history (it must qualify on its own
    /// displayed telemetry). A role change does not manufacture a fresh
    /// qualifying reading or reset an existing failure streak without
    /// justification.
    /// </description></item>
    /// </list>
    ///
    /// <para><b>State ownership</b></para>
    ///
    /// <para>
    /// The tracker is owned by the collector (<see cref="SystemMetricsService"/>)
    /// which calls <see cref="Update"/> once per <i>completed</i> collection
    /// cycle. The ranking logic in <see cref="GpuSelectionPolicy"/> remains
    /// pure; the tracker wraps it with stateful grace-period behaviour. The
    /// presenter does not hold selection state — it receives the already-resolved
    /// eligible devices from the snapshot.
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
    public sealed class GpuExtrasTracker
    {
        /// <summary>
        /// How many consecutive cycles without usable displayed telemetry the
        /// tracker retains an eligible secondary GPU before removing it.
        /// </summary>
        public const int DefaultGraceCycles = 3;

        private readonly int _graceCycles;
        private readonly TimeProvider _timeProvider;

        // Per-instance-id state for each tracked secondary GPU.
        private readonly Dictionary<string, ExtraTrackingState> _tracked = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Creates a new tracker with the default grace period.
        /// </summary>
        public GpuExtrasTracker()
            : this(DefaultGraceCycles, TimeProvider.System)
        {
        }

        /// <summary>
        /// Creates a new tracker with a custom grace period and time provider.
        /// </summary>
        /// <param name="graceCycles">Number of cycles to retain a device without usable displayed telemetry.</param>
        /// <param name="timeProvider">Time source for diagnostics (tests use FakeTimeProvider).</param>
        public GpuExtrasTracker(int graceCycles, TimeProvider timeProvider)
        {
            _graceCycles = Math.Max(0, graceCycles);
            _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        }

        /// <summary>
        /// Tracks a secondary GPU's eligibility state.
        /// </summary>
        private sealed class ExtraTrackingState
        {
            /// <summary>The instance id of the tracked device.</summary>
            public string InstanceId { get; init; } = string.Empty;

            /// <summary>The device id of the tracked device (for presence verification).</summary>
            public string DeviceId { get; init; } = string.Empty;

            /// <summary>Number of consecutive cycles without usable displayed telemetry.</summary>
            public int ConsecutiveFailures { get; set; }

            /// <summary>Whether the device has ever qualified (had usable displayed telemetry) in its CURRENT secondary tenure.</summary>
            public bool HasEverQualified { get; set; }

            /// <summary>Last time this device had usable displayed telemetry.</summary>
            public DateTimeOffset? LastQualifiedAt { get; set; }

            /// <summary>Whether this device was the primary GPU in the previous cycle.</summary>
            public bool WasPrimaryPreviousCycle { get; set; }
        }

        /// <summary>
        /// Result of the extras tracking update.
        /// </summary>
        /// <param name="EligibleInstanceIds">Instance ids of GPUs eligible for secondary rows.</param>
        /// <param name="PrimaryInstanceId">The instance id of the primary GPU (to exclude).</param>
        public sealed record UpdateResult(
            IReadOnlyList<string> EligibleInstanceIds,
            string? PrimaryInstanceId);

        /// <summary>
        /// Updates the tracker with the current device list, telemetry, and the
        /// primary GPU instance id, returning the GPUs eligible for secondary rows.
        ///
        /// <para>
        /// This is called once per <i>completed</i> collection cycle by the
        /// coordinator. It applies the grace period to secondary GPU eligibility.
        /// </para>
        /// </summary>
        /// <param name="gpus">The GPUs currently detected.</param>
        /// <param name="primaryInstanceId">The instance id of the primary GPU (excluded from extras).</param>
        /// <param name="snapshot">The completed collection snapshot for telemetry checks.</param>
        /// <param name="gpuLoadPercent">The snapshot's scalar GPU load.</param>
        /// <param name="gpuLoadDeviceId">The device the scalar load describes.</param>
        /// <param name="gpuTemperatureC">The snapshot's scalar GPU temperature.</param>
        /// <param name="gpuTemperatureDeviceId">The device the scalar temperature describes.</param>
        /// <returns>
        /// The instance ids of GPUs eligible for secondary rows, in presentation order,
        /// and the primary instance id for reference.
        /// </returns>
        public UpdateResult Update(
            IReadOnlyList<HardwareDevice> gpus,
            string? primaryInstanceId,
            MetricsSnapshot? snapshot,
            Measurement? gpuLoadPercent,
            string? gpuLoadDeviceId,
            Measurement? gpuTemperatureC,
            string? gpuTemperatureDeviceId)
        {
            var devices = (gpus ?? Array.Empty<HardwareDevice>())
                .Where(g => g is not null)
                .ToList();

            // Remove tracked devices that are no longer present.
            var currentInstanceIds = devices
                .Select(g => g.InstanceId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var toRemove = _tracked.Keys
                .Where(k => !currentInstanceIds.Contains(k))
                .ToList();

            foreach (var key in toRemove)
            {
                _tracked.Remove(key);
            }

            // Detect role transitions: devices that were primary in previous cycle
            // and are now secondary should have their state reset.
            // We do this by checking WasPrimaryPreviousCycle flag.
            foreach (var kvp in _tracked)
            {
                if (kvp.Value.WasPrimaryPreviousCycle &&
                    !string.Equals(kvp.Key, primaryInstanceId, StringComparison.OrdinalIgnoreCase))
                {
                    // Device was primary last cycle, now secondary -> reset state for fresh tenure
                    kvp.Value.HasEverQualified = false;
                    kvp.Value.ConsecutiveFailures = 0;
                    kvp.Value.LastQualifiedAt = null;
                }
            }

            // Track the current primary instance id for next cycle's role transition detection.
            // Mark all tracked devices with whether they are the primary THIS cycle.
            foreach (var kvp in _tracked)
            {
                kvp.Value.WasPrimaryPreviousCycle = string.Equals(kvp.Key, primaryInstanceId, StringComparison.OrdinalIgnoreCase);
            }

            // Evaluate each device (except the primary) for eligibility.
            foreach (var device in devices)
            {
                // Skip the primary GPU.
                if (string.Equals(device.InstanceId, primaryInstanceId, StringComparison.OrdinalIgnoreCase))
                    continue;

                var instanceId = device.InstanceId;
                if (string.IsNullOrWhiteSpace(instanceId))
                    continue; // Cannot track without instance id.

                // Get or create tracking state.
                if (!_tracked.TryGetValue(instanceId, out var state))
                {
                    state = new ExtraTrackingState
                    {
                        InstanceId = instanceId,
                        DeviceId = device.DeviceId,
                        ConsecutiveFailures = 0,
                        HasEverQualified = false,
                        LastQualifiedAt = null,
                        WasPrimaryPreviousCycle = false,
                    };
                    _tracked[instanceId] = state;
                }
                else
                {
                    // Verify the device id matches (catch identity changes).
                    if (!string.Equals(state.DeviceId, device.DeviceId, StringComparison.Ordinal))
                    {
                        // Device identity changed (e.g., re-enumeration) — treat as new device.
                        state = new ExtraTrackingState
                        {
                            InstanceId = instanceId,
                            DeviceId = device.DeviceId,
                            ConsecutiveFailures = 0,
                            HasEverQualified = false,
                            LastQualifiedAt = null,
                            WasPrimaryPreviousCycle = false,
                        };
                        _tracked[instanceId] = state;
                    }
                }

                // Check if this device has usable displayed telemetry (load or temp).
                bool hasDisplayedTelemetry = HasUsableDisplayedTelemetry(
                    device, snapshot, gpuLoadPercent, gpuLoadDeviceId, gpuTemperatureC, gpuTemperatureDeviceId);

                if (hasDisplayedTelemetry)
                {
                    // Qualified this cycle: reset failure count, mark as qualified.
                    state.ConsecutiveFailures = 0;
                    state.HasEverQualified = true;
                    state.LastQualifiedAt = _timeProvider.GetUtcNow();
                }
                else
                {
                    // No usable displayed telemetry this cycle.
                    if (state.HasEverQualified)
                    {
                        state.ConsecutiveFailures++;
                    }
                    // If never qualified, it stays at 0 failures and is not eligible.
                }
            }

            // Determine eligible devices: those that have qualified and are within grace.
            // Also exclude the primary GPU even if it was previously tracked.
            var eligibleInstanceIds = _tracked
                .Where(kvp => kvp.Value.HasEverQualified
                           && kvp.Value.ConsecutiveFailures <= _graceCycles
                           && !string.Equals(kvp.Key, primaryInstanceId, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .ToList();

            // Order them by the same presentation policy (telemetry-aware, then integration, then instance id).
            // Include BOTH devices with current telemetry AND devices in grace period (without current telemetry).
            var orderedEligible = OrderEligibleForPresentation(devices, eligibleInstanceIds, snapshot,
                gpuLoadPercent, gpuLoadDeviceId, gpuTemperatureC, gpuTemperatureDeviceId);

            return new UpdateResult(orderedEligible, primaryInstanceId);
        }

        /// <summary>
        /// Determines whether a GPU has at least one usable current reading
        /// of load or temperature (the metrics displayed in the extra row).
        /// Memory-only telemetry does not count.
        /// </summary>
        private static bool HasUsableDisplayedTelemetry(
            HardwareDevice device,
            MetricsSnapshot? snapshot,
            Measurement? gpuLoadPercent,
            string? gpuLoadDeviceId,
            Measurement? gpuTemperatureC,
            string? gpuTemperatureDeviceId)
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

            // Check properly attributed scalar fallbacks.
            if (IsUsable(gpuLoadPercent) && string.Equals(gpuLoadDeviceId, deviceId, StringComparison.Ordinal))
                return true;

            if (IsUsable(gpuTemperatureC) && string.Equals(gpuTemperatureDeviceId, deviceId, StringComparison.Ordinal))
                return true;

            return false;
        }

        private static bool IsUsable(Measurement? measurement) =>
            measurement is not null
            && measurement.Status == MeasurementStatus.Available
            && measurement.Value.HasValue
            && !double.IsNaN(measurement.Value.Value)
            && !double.IsInfinity(measurement.Value.Value);

        /// <summary>
        /// Orders eligible devices by the same presentation policy used for extras.
        /// </summary>
        private static IReadOnlyList<string> OrderEligibleForPresentation(
            List<HardwareDevice> devices,
            IReadOnlyList<string> eligibleInstanceIds,
            MetricsSnapshot? snapshot,
            Measurement? gpuLoadPercent,
            string? gpuLoadDeviceId,
            Measurement? gpuTemperatureC,
            string? gpuTemperatureDeviceId)
        {
            if (snapshot is null)
            {
                // Fallback to static ordering.
                return devices
                    .Where(g => eligibleInstanceIds.Contains(g.InstanceId, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(g => IntegrationRank(g.GpuIntegration))
                    .ThenBy(g => g.InstanceId ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(g => g.DeviceId, StringComparer.Ordinal)
                    .Select(g => g.InstanceId!)
                    .ToList();
            }

            // Partition eligible devices by whether they currently have usable displayed telemetry.
            var withTelemetry = new List<HardwareDevice>();
            var withoutTelemetry = new List<HardwareDevice>();

            foreach (var device in devices)
            {
                if (!eligibleInstanceIds.Contains(device.InstanceId, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (HasUsableDisplayedTelemetry(device, snapshot, gpuLoadPercent, gpuLoadDeviceId, gpuTemperatureC, gpuTemperatureDeviceId))
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
                .Select(g => g.InstanceId!)
                .ToList();
        }

        private static int IntegrationRank(GpuIntegration integration) => integration switch
        {
            GpuIntegration.Discrete => 0,
            GpuIntegration.Unknown => 1,
            GpuIntegration.Integrated => 2,
            _ => 3,
        };

        /// <summary>
        /// Gets the current tracking state for diagnostics/testing.
        /// </summary>
        public IReadOnlyDictionary<string, (bool HasEverQualified, int ConsecutiveFailures)> GetTrackingState() =>
            _tracked.ToDictionary(
                kvp => kvp.Key,
                kvp => (kvp.Value.HasEverQualified, kvp.Value.ConsecutiveFailures),
                StringComparer.OrdinalIgnoreCase);
    }
}
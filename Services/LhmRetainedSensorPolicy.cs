using System;
using System.Collections.Generic;
using System.Linq;

namespace System_Monitor.Services
{
    /// <summary>
    /// Recognizes the sensors the backend derives from other sensors, and
    /// marks the ones whose value could not have been derived in this
    /// cycle.
    ///
    /// <para><b>Why this exists</b></para>
    ///
    /// <para>
    /// The backend is not uniform about a failed read. When a per-core
    /// temperature read fails it clears that core's temperature sensor and
    /// the package sensor — so the next read sees a null rather than an
    /// old number. But the <i>aggregate</i> sensors are written only when
    /// at least one core produced a value, so a failed read leaves them
    /// holding the value they read earlier.
    /// </para>
    ///
    /// <para>
    /// Intel's "Core Average" and "Core Max" are the mean and the maximum
    /// of the per-core temperatures; AMD's "CCDs Average (Tdie)" and "CCDs
    /// Max (Tdie)" are the same over the CCD temperatures. Both are
    /// derived, not read.
    /// </para>
    ///
    /// <para>
    /// A retained aggregate is a real reading — of some earlier moment —
    /// but it is not a measurement of this cycle. Publishing it as one
    /// would present a stale temperature as a current one, which is
    /// exactly the mistake the scalar collector state exists to prevent.
    /// This policy marks it, so the collection path can refuse to publish
    /// it as fresh.
    /// </para>
    ///
    /// <para>
    /// The policy is deliberately narrow: every other temperature sensor
    /// on the device must be unreadable. If any per-core reading
    /// succeeded, the aggregate was computed from it and is current.
    /// </para>
    ///
    /// <para>
    /// This is pure logic over the selection's own types, so it is
    /// exercised directly by the test suite.
    /// </para>
    /// </summary>
    public static class LhmRetainedSensorPolicy
    {
        /// <summary>
        /// The temperature sensors the backend derives from the per-core
        /// readings rather than reading directly.
        /// </summary>
        private static readonly HashSet<string> AggregateTemperatureNames =
            new(StringComparer.OrdinalIgnoreCase)
            {
                "Core Average",
                "Core Max",
                "CCDs Average (Tdie)",
                "CCDs Max (Tdie)",
            };

        /// <summary>
        /// True when the sensor is one the backend derives from the
        /// per-core readings.
        /// </summary>
        public static bool IsAggregateTemperature(string? sensorName) =>
            !string.IsNullOrWhiteSpace(sensorName)
            && AggregateTemperatureNames.Contains(sensorName);

        /// <summary>
        /// Marks the aggregate temperature sensors that are holding a value
        /// the backend could not have computed in this cycle.
        /// </summary>
        /// <param name="sensors">The device's sensors.</param>
        /// <returns>
        /// The sensors, with the retained aggregates marked. The input list
        /// is not modified.
        /// </returns>
        public static IReadOnlyList<LhmSensorDescriptor> MarkRetainedAggregates(
            IReadOnlyList<LhmSensorDescriptor> sensors)
        {
            if (sensors is null || sensors.Count == 0)
                return sensors ?? Array.Empty<LhmSensorDescriptor>();

            var temperatures = sensors
                .Where(s => string.Equals(s.SensorType, "Temperature", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (temperatures.Count == 0)
                return sensors;

            if (!temperatures.Any(s => IsAggregateTemperature(s.Name)))
                return sensors;

            // The per-core readings the aggregates are derived from. The
            // headroom sensors are excluded: they are not inputs to the
            // aggregates, and they carry their own readings.
            var inputs = temperatures
                .Where(s => !IsAggregateTemperature(s.Name))
                .Where(s => !s.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (inputs.Count == 0)
                return sensors;

            // If any input read, the aggregate was computed from it and is
            // current.
            if (inputs.Any(s => s.Value.HasValue))
                return sensors;

            var result = new List<LhmSensorDescriptor>(sensors.Count);

            foreach (var sensor in sensors)
            {
                var isStaleAggregate =
                    IsAggregateTemperature(sensor.Name)
                    && sensor.Value.HasValue;

                result.Add(isStaleAggregate ? sensor with { IsRetained = true } : sensor);
            }

            return result;
        }
    }
}

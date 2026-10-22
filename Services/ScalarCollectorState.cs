using System;
using System.Collections.Generic;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// Tracks the outcome of one flattened metric across collection cycles.
    ///
    /// <para>
    /// A collector either produces a new value or it does not, and the
    /// difference matters to the widget:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item><description>
    /// <b>A new value was read.</b> The measurement is
    /// <see cref="MeasurementStatus.Available"/> and carries the time of
    /// the <i>read</i>, not the time the payload was published.
    /// </description></item>
    /// <item><description>
    /// <b>The read failed, but an earlier cycle succeeded.</b> The
    /// measurement is <see cref="MeasurementStatus.Stale"/>, stamped with
    /// the time of the last successful read. The widget says the value is
    /// out of date instead of implying it is current.
    /// </description></item>
    /// <item><description>
    /// <b>The read failed and no earlier cycle succeeded.</b> The
    /// measurement is <see cref="MeasurementStatus.Failed"/> and carries no
    /// value at all.
    /// </description></item>
    /// </list>
    ///
    /// <para>
    /// This is what keeps a failed read from being presented as a measured
    /// zero, and what keeps a carried-over value from being stamped with
    /// the time it was published rather than the time it was read.
    /// </para>
    ///
    /// <para>
    /// One collector may publish several metrics — the memory collector
    /// publishes the used, total and percentage figures — so the metrics
    /// are tracked by name within one state. A metric may also come from
    /// more than one source, so each state records the device the value
    /// describes: a figure that names a different card must not be
    /// presented under this one's name.
    /// </para>
    ///
    /// <para>
    /// The type has no hardware dependencies, so it is exercised directly
    /// by the test suite.
    /// </para>
    /// </summary>
    public sealed class ScalarCollectorState
    {
        private readonly Dictionary<string, Entry> _entries =
            new(StringComparer.Ordinal);

        /// <summary>
        /// One metric's state within a collector.
        /// </summary>
        private sealed class Entry
        {
            /// <summary>The last measurement the collector managed to read.</summary>
            public Measurement? Last;

            /// <summary>True when the most recent attempt produced no value.</summary>
            public bool NoValue;

            /// <summary>The canonical unit of the metric.</summary>
            public string Unit = string.Empty;

            /// <summary>The provenance of the reading.</summary>
            public string Source = string.Empty;

            /// <summary>
            /// The device the value describes, or null when the source does
            /// not identify one.
            /// </summary>
            public string? DeviceId;
        }

        /// <summary>
        /// Records that a metric was read successfully.
        /// </summary>
        /// <param name="metric">The metric's name, e.g. "ram-used".</param>
        /// <param name="value">The value that was read.</param>
        /// <param name="unit">The canonical unit of the metric.</param>
        /// <param name="source">The provenance of the reading.</param>
        /// <param name="readAt">
        /// The time the value was read. It must be the time of the read
        /// itself, not the time the payload is built, so the widget's
        /// "last updated" figure says when the number was measured.
        /// </param>
        /// <param name="deviceId">
        /// The device the value describes, or null when the source does
        /// not identify one.
        /// </param>
        public void ReportSuccess(
            string metric,
            double value,
            string unit,
            string source,
            DateTimeOffset readAt,
            string? deviceId = null)
        {
            var entry = GetOrAdd(metric);
            entry.Unit = unit;
            entry.Source = source;
            entry.DeviceId = deviceId;
            entry.NoValue = false;
            entry.Last = new Measurement
            {
                Value = value,
                Unit = unit,
                Source = source,
                Status = MeasurementStatus.Available,
                LastSuccessfulUpdate = readAt,
            };
        }

        /// <summary>
        /// Records that a metric was attempted and produced no value.
        /// </summary>
        /// <param name="metric">The metric's name, e.g. "ram-used".</param>
        /// <param name="unit">The canonical unit of the metric.</param>
        /// <param name="source">The provenance of the reading.</param>
        public void ReportNoValue(string metric, string unit, string source)
        {
            var entry = GetOrAdd(metric);
            entry.Unit = unit;
            entry.Source = source;
            entry.NoValue = true;
        }

        /// <summary>
        /// Returns the state for a metric, creating it if this is the first
        /// time the metric has been reported.
        /// </summary>
        private Entry GetOrAdd(string metric)
        {
            if (!_entries.TryGetValue(metric, out var entry))
            {
                entry = new Entry();
                _entries[metric] = entry;
            }

            return entry;
        }

        /// <summary>
        /// Produces the measurement for a metric in the current cycle.
        /// </summary>
        /// <param name="metric">The metric's name, e.g. "ram-used".</param>
        public Measurement ToMeasurement(string metric)
        {
            if (!_entries.TryGetValue(metric, out var entry))
            {
                // The collector never reported this metric. There is no
                // value, and the number zero is not one.
                return Measurement.Failed(string.Empty);
            }

            if (!entry.NoValue && entry.Last is not null)
            {
                // The value was read during this cycle.
                return entry.Last;
            }

            if (entry.Last is not null)
            {
                // A value was read earlier and has not been refreshed since.
                // It is kept, but marked stale so it is not presented as
                // current, and it keeps the time it was read.
                return Measurement.Stale(entry.Last.LastSuccessfulUpdate, entry.Source);
            }

            // Nothing was ever read. There is no value to show.
            return Measurement.Failed(entry.Source);
        }

        /// <summary>
        /// The device the metric's value describes, or null when the source
        /// did not identify one.
        /// </summary>
        /// <param name="metric">The metric's name, e.g. "gpu-temperature".</param>
        public string? DeviceIdFor(string metric) =>
            _entries.TryGetValue(metric, out var entry) ? entry.DeviceId : null;

        /// <summary>
        /// The most recent successful read time across every metric this
        /// state tracks, or null when nothing has ever been read.
        ///
        /// <para>
        /// This is the time the newest value was <i>measured</i>, which is
        /// what the widget's "last updated" figure shows. It is never the
        /// time the payload was built.
        /// </para>
        /// </summary>
        public DateTimeOffset? LastSuccessfulUpdate
        {
            get
            {
                DateTimeOffset? latest = null;

                foreach (var entry in _entries.Values)
                {
                    if (entry.Last?.LastSuccessfulUpdate is not { } stamp)
                        continue;

                    if (latest is null || stamp > latest.Value)
                        latest = stamp;
                }

                return latest;
            }
        }
    }
}

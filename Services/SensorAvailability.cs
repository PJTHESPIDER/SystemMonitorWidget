using System;
using System.Collections.Generic;

namespace System_Monitor.Services
{
    /// <summary>
    /// Tracks which sensors are unavailable and explains why, without
    /// repeating the same complaint on every collection cycle.
    ///
    /// <para><b>Why this exists</b></para>
    ///
    /// <para>
    /// A sensor that is not present is a permanent condition, not a
    /// transient failure. A machine with no GPU temperature sensor would
    /// otherwise produce the same "GPU temperature unavailable" message
    /// every five seconds, forever. That is noise: the user cannot act on
    /// it repeatedly, and it drowns out the events that <i>are</i> new.
    /// </para>
    ///
    /// <para><b>What it does</b></para>
    ///
    /// <para>
    /// The first time a sensor is found unavailable, the reason is
    /// recorded and reported once. After that, repeats are counted and
    /// reported only as a summary at a long interval, so the log still
    /// says "this has been happening for an hour" without saying it five
    /// hundred times. When the sensor starts working again, that is
    /// reported once too, so the recovery is visible and the next
    /// regression is reported afresh.
    /// </para>
    ///
    /// <para><b>What the user sees</b></para>
    ///
    /// <para>
    /// The reason is a short, stable phrase — "no sensor on this device",
    /// "the driver does not expose it", "read failed" — never an
    /// exception message and never a device identifier. The widget shows
    /// the phrase next to the unavailable value, so the user is told
    /// <i>why</i> it is unavailable once, and is not troubled again.
    /// </para>
    /// </summary>
    public sealed class SensorAvailability
    {
        /// <summary>
        /// The reason a sensor is unavailable, in the vocabulary the widget
        /// shows. The strings are deliberately short and stable: they are
        /// user-facing text, not log text.
        /// </summary>
        public static class Reasons
        {
            /// <summary>The device does not have this sensor at all.</summary>
            public const string NotPresent = "no sensor on this device";

            /// <summary>The driver does not expose the sensor to user-mode readers.</summary>
            public const string NotExposed = "not exposed by the driver";

            /// <summary>The sensor exists but the read failed.</summary>
            public const string ReadFailed = "read failed";

            /// <summary>The reading was attempted but returned no value.</summary>
            public const string NoValue = "no value reported";
        }

        private readonly object _lock = new();
        private readonly Dictionary<string, SensorState> _states = new(StringComparer.Ordinal);
        private readonly DiagnosticLog _log;
        private readonly TimeProvider _timeProvider;

        /// <summary>
        /// Creates the tracker.
        /// </summary>
        /// <param name="log">The log the availability transitions are reported to.</param>
        /// <param name="timeProvider">The clock, so tests can control time.</param>
        public SensorAvailability(DiagnosticLog? log = null, TimeProvider? timeProvider = null)
        {
            _log = log ?? DiagnosticLog.Shared;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <summary>
        /// How long an unavailable sensor stays quiet before it is
        /// summarized.
        /// </summary>
        public TimeSpan SummaryInterval { get; set; } = TimeSpan.FromHours(1);

        /// <summary>
        /// The reason a sensor is currently unavailable, or null when it is
        /// available. The widget uses this to explain the "N/A" next to a
        /// value.
        /// </summary>
        public string? ReasonFor(string sensor)
        {
            if (string.IsNullOrWhiteSpace(sensor))
                return null;

            lock (_lock)
            {
                return _states.TryGetValue(sensor, out var state) && state.IsUnavailable
                    ? state.Reason
                    : null;
            }
        }

        /// <summary>
        /// Records that a sensor could not be read, and explains why.
        /// </summary>
        /// <param name="sensor">
        /// The sensor's name, e.g. "gpu-temperature". It identifies a
        /// <i>kind</i> of sensor, not a device: the log must not carry
        /// device identities.
        /// </param>
        /// <param name="reason">One of the <see cref="Reasons"/> phrases.</param>
        public void ReportUnavailable(string sensor, string reason)
        {
            if (string.IsNullOrWhiteSpace(sensor))
                return;

            if (string.IsNullOrWhiteSpace(reason))
                reason = Reasons.ReadFailed;

            var now = _timeProvider.GetUtcNow();
            var shouldLog = false;
            var detail = string.Empty;

            lock (_lock)
            {
                if (!_states.TryGetValue(sensor, out var state))
                {
                    state = new SensorState
                    {
                        Reason = reason,
                        IsUnavailable = true,
                        ConsecutiveFailures = 1,
                        FirstSeenAt = now,
                        LastSummaryAt = now,
                    };
                    _states[sensor] = state;
                    shouldLog = true;
                    detail = reason;
                }
                else
                {
                    var wasUnavailable = state.IsUnavailable;

                    state.IsUnavailable = true;
                    state.Reason = reason;
                    state.ConsecutiveFailures++;
                    state.LastSeenAt = now;

                    if (!wasUnavailable)
                    {
                        // The sensor was working and has stopped. That is
                        // news: report it in full.
                        shouldLog = true;
                        detail = reason;
                    }
                    else if (now - state.LastSummaryAt >= SummaryInterval)
                    {
                        // It has been unavailable for a long time. Say so
                        // once, with the count, rather than repeating the
                        // original line.
                        shouldLog = true;
                        detail = $"still unavailable after {state.ConsecutiveFailures} consecutive attempt(s)";
                        state.LastSummaryAt = now;
                    }
                }
            }

            if (shouldLog)
            {
                _log.Warning(
                    "sensor",
                    sensor,
                    "unavailable",
                    detail: detail);
            }
        }

        /// <summary>
        /// Records that a sensor is working again.
        /// </summary>
        /// <param name="sensor">The sensor's name.</param>
        public void ReportAvailable(string sensor)
        {
            if (string.IsNullOrWhiteSpace(sensor))
                return;

            var shouldLog = false;
            var detail = string.Empty;

            lock (_lock)
            {
                if (_states.TryGetValue(sensor, out var state) && state.IsUnavailable)
                {
                    state.IsUnavailable = false;
                    state.ConsecutiveFailures = 0;
                    shouldLog = true;
                    detail = "reading again";
                }
                else if (!_states.ContainsKey(sensor))
                {
                    _states[sensor] = new SensorState { IsUnavailable = false };
                }
            }

            if (shouldLog)
            {
                _log.Info(
                    "sensor",
                    sensor,
                    "recovered",
                    detail: detail);
            }
        }

        /// <summary>
        /// The sensors currently known to be unavailable, and their reasons.
        /// Used by the diagnostic inventory.
        /// </summary>
        public IReadOnlyDictionary<string, string> UnavailableSensors()
        {
            lock (_lock)
            {
                var result = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var pair in _states)
                {
                    if (pair.Value.IsUnavailable)
                        result[pair.Key] = pair.Value.Reason;
                }
                return result;
            }
        }

        /// <summary>
        /// The state of one sensor.
        /// </summary>
        private sealed class SensorState
        {
            public bool IsUnavailable { get; set; }

            public string Reason { get; set; } = string.Empty;

            public int ConsecutiveFailures { get; set; }

            public DateTimeOffset FirstSeenAt { get; set; }

            public DateTimeOffset LastSeenAt { get; set; }

            public DateTimeOffset LastSummaryAt { get; set; }
        }
    }
}

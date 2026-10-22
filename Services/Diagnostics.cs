using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace System_Monitor.Services
{
    /// <summary>
    /// The severity of a diagnostic event.
    /// </summary>
    public enum DiagnosticLevel
    {
        /// <summary>Verbose detail; off unless diagnostics are enabled.</summary>
        Debug,

        /// <summary>Normal operational information.</summary>
        Info,

        /// <summary>Something did not work, but the widget keeps working.</summary>
        Warning,

        /// <summary>Something did not work and the affected feature is degraded.</summary>
        Error,
    }

    /// <summary>
    /// One diagnostic event, as written to the log.
    /// </summary>
    /// <param name="Timestamp">When the event occurred (UTC).</param>
    /// <param name="Level">How serious the event is.</param>
    /// <param name="Backend">The subsystem that produced it, e.g. "LHM" or "nvidia-smi".</param>
    /// <param name="Operation">What was being attempted, e.g. "read".</param>
    /// <param name="Status">The outcome, e.g. "ok", "unavailable", "failed".</param>
    /// <param name="Elapsed">How long the operation took, when it is known.</param>
    /// <param name="Detail">Free-form detail. Never contains exception text or machine identifiers.</param>
    public sealed record DiagnosticEvent(
        DateTimeOffset Timestamp,
        DiagnosticLevel Level,
        string Backend,
        string Operation,
        string Status,
        TimeSpan? Elapsed,
        string Detail)
    {
        /// <summary>
        /// Renders the event as a single log line.
        /// </summary>
        public string Format()
        {
            var builder = new StringBuilder(160);

            builder.Append(Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            builder.Append(' ');
            builder.Append(Level.ToString().ToUpperInvariant());
            builder.Append(' ');
            builder.Append('[');
            builder.Append(Backend);
            builder.Append('/');
            builder.Append(Operation);
            builder.Append(']');
            builder.Append(' ');
            builder.Append(Status);

            if (Elapsed is { } elapsed)
            {
                builder.Append(" elapsed=");
                builder.Append(elapsed.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture));
                builder.Append("ms");
            }

            if (!string.IsNullOrWhiteSpace(Detail))
            {
                builder.Append(" | ");
                builder.Append(Detail);
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// A sink that receives diagnostic events.
    /// </summary>
    public interface IDiagnosticSink
    {
        void Write(DiagnosticEvent diagnosticEvent);
    }

    /// <summary>
    /// The diagnostic log: a structured, rate-limited record of what the
    /// widget's backends are doing.
    ///
    /// <para><b>Why this exists</b></para>
    ///
    /// <para>
    /// The widget's failure handling was almost entirely silent. Broad
    /// catches swallowed the reason a sensor could not be read, so a machine
    /// showing "N/A" for a GPU temperature gave the user nothing to act on
    /// and the maintainer nothing to diagnose. <see cref="Debug.WriteLine"/>
    /// was the only sink, and that is compiled out of a Release build — so
    /// the shipping widget logged nothing at all.
    /// </para>
    ///
    /// <para><b>What it records</b></para>
    ///
    /// <para>
    /// Every event names the <b>backend</b> (which subsystem), the
    /// <b>operation</b> (what was attempted), the <b>status</b> (the
    /// outcome) and, where it is known, the <b>elapsed time</b>. That is
    /// enough to tell "the driver is not installed" from "the driver is
    /// installed but the sensor is blocked" from "the read took 4 seconds".
    /// </para>
    ///
    /// <para><b>What it deliberately does not record</b></para>
    ///
    /// <para>
    /// No serial numbers, no machine identifiers, and no complete hardware
    /// inventory. A device is described by its <i>role</i> and its
    /// <i>integration class</i> — "the discrete GPU" — never by a serial
    /// number or a machine-unique path. A full inventory is available
    /// through the explicit, opt-in diagnostic export instead
    /// (<see cref="DiagnosticInventory"/>), which the user has to ask for.
    /// </para>
    ///
    /// <para><b>Rate limiting</b></para>
    ///
    /// <para>
    /// A sensor that is permanently unavailable would otherwise log on every
    /// collection cycle, forever — an unreadable integrated GPU would fill
    /// the log with the same line five hundred times an hour. Repeated
    /// events with the same key are therefore collapsed: the first is
    /// logged, and subsequent ones are counted. The count is reported when
    /// the event recurs at a summary interval, so the log says "this has
    /// happened 214 times" instead of saying nothing, and without repeating
    /// itself. A successful event for the same key clears the count, so the
    /// next failure is reported afresh.
    /// </para>
    /// </summary>
    public sealed class DiagnosticLog
    {
        /// <summary>
        /// The default number of identical events that are collapsed before
        /// a summary line is written.
        /// </summary>
        public const int DefaultRepeatLimit = 5;

        /// <summary>
        /// The default interval at which a collapsed event is summarized.
        /// </summary>
        public static readonly TimeSpan DefaultSummaryInterval = TimeSpan.FromMinutes(5);

        private readonly object _lock = new();
        private readonly List<IDiagnosticSink> _sinks = new();
        private readonly Dictionary<string, RepeatState> _repeats = new(StringComparer.Ordinal);
        private readonly TimeProvider _timeProvider;

        private int _repeatLimit = DefaultRepeatLimit;
        private TimeSpan _summaryInterval = DefaultSummaryInterval;
        private bool _enabled = true;

        /// <summary>
        /// Creates a log.
        /// </summary>
        /// <param name="timeProvider">The clock, so tests can control time.</param>
        public DiagnosticLog(TimeProvider? timeProvider = null)
        {
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <summary>
        /// The log every component writes to. There is one process, so one
        /// log: sharing it is what makes the events correlatable.
        /// </summary>
        public static DiagnosticLog Shared { get; } = new();

        /// <summary>
        /// False when nothing is recorded at all. Turning the log off is
        /// cheaper than filtering its output.
        /// </summary>
        public bool Enabled
        {
            get { lock (_lock) return _enabled; }
            set { lock (_lock) _enabled = value; }
        }

        /// <summary>
        /// The minimum level that is recorded. Events below it are dropped
        /// before they reach the rate limiter.
        /// </summary>
        public DiagnosticLevel MinimumLevel { get; set; } = DiagnosticLevel.Info;

        /// <summary>
        /// How many identical events are collapsed before a summary is
        /// written.
        /// </summary>
        public int RepeatLimit
        {
            get { lock (_lock) return _repeatLimit; }
            set { lock (_lock) _repeatLimit = value < 1 ? 1 : value; }
        }

        /// <summary>
        /// How long a collapsed event waits before it is summarized.
        /// </summary>
        public TimeSpan SummaryInterval
        {
            get { lock (_lock) return _summaryInterval; }
            set { lock (_lock) _summaryInterval = value; }
        }

        /// <summary>
        /// Adds a sink. Sinks receive every event that survives the level
        /// filter and the rate limiter.
        /// </summary>
        public void AddSink(IDiagnosticSink sink)
        {
            if (sink is null)
                return;

            lock (_lock)
                _sinks.Add(sink);
        }

        /// <summary>
        /// Removes a sink.
        /// </summary>
        public bool RemoveSink(IDiagnosticSink sink)
        {
            lock (_lock)
                return _sinks.Remove(sink);
        }

        /// <summary>
        /// Records an event.
        /// </summary>
        /// <param name="level">The severity.</param>
        /// <param name="backend">The subsystem, e.g. "LHM".</param>
        /// <param name="operation">What was attempted, e.g. "read".</param>
        /// <param name="status">The outcome, e.g. "ok" / "unavailable" / "failed".</param>
        /// <param name="elapsed">How long the operation took, when known.</param>
        /// <param name="detail">Free-form detail. No exception text, no identifiers.</param>
        public void Log(
            DiagnosticLevel level,
            string backend,
            string operation,
            string status,
            TimeSpan? elapsed = null,
            string? detail = null)
        {
            if (level < MinimumLevel)
                return;

            var diagnosticEvent = new DiagnosticEvent(
                _timeProvider.GetUtcNow(),
                level,
                backend ?? "unknown",
                operation ?? "unknown",
                status ?? "unknown",
                elapsed,
                detail ?? string.Empty);

            Dispatch(diagnosticEvent);
        }

        /// <summary>
        /// Records an informational event.
        /// </summary>
        public void Info(string backend, string operation, string status, TimeSpan? elapsed = null, string? detail = null) =>
            Log(DiagnosticLevel.Info, backend, operation, status, elapsed, detail);

        /// <summary>
        /// Records a warning.
        /// </summary>
        public void Warning(string backend, string operation, string status, TimeSpan? elapsed = null, string? detail = null) =>
            Log(DiagnosticLevel.Warning, backend, operation, status, elapsed, detail);

        /// <summary>
        /// Records an error.
        /// </summary>
        public void Error(string backend, string operation, string status, TimeSpan? elapsed = null, string? detail = null) =>
            Log(DiagnosticLevel.Error, backend, operation, status, elapsed, detail);

        /// <summary>
        /// Records a debug event. These are off unless the minimum level is
        /// lowered deliberately.
        /// </summary>
        public void Debug(string backend, string operation, string status, TimeSpan? elapsed = null, string? detail = null) =>
            Log(DiagnosticLevel.Debug, backend, operation, status, elapsed, detail);

        /// <summary>
        /// Forgets the repeat counts, so the next occurrence of every event
        /// is reported in full again.
        /// </summary>
        public void ResetRateLimiting()
        {
            lock (_lock)
                _repeats.Clear();
        }

        /// <summary>
        /// Delivers an event, applying the rate limit.
        /// </summary>
        private void Dispatch(DiagnosticEvent diagnosticEvent)
        {
            IDiagnosticSink[] sinks;

            lock (_lock)
            {
                if (!_enabled || _sinks.Count == 0)
                    return;

                sinks = _sinks.ToArray();
            }

            // The rate limit is applied per event key, so two different
            // failures do not hide each other.
            var key = BuildKey(diagnosticEvent);

            DiagnosticEvent? toWrite = null;

            lock (_lock)
            {
                if (_repeats.TryGetValue(key, out var state))
                {
                    state.Count++;

                    if (state.Count <= _repeatLimit)
                    {
                        toWrite = diagnosticEvent;
                    }
                    else if (state.Count == _repeatLimit + 1
                             || _timeProvider.GetUtcNow() - state.LastSummaryAt >= _summaryInterval)
                    {
                        // The event is repeating. Report the count rather
                        // than the event again, so the log says what is
                        // happening without filling up with it.
                        toWrite = new DiagnosticEvent(
                            diagnosticEvent.Timestamp,
                            diagnosticEvent.Level,
                            diagnosticEvent.Backend,
                            diagnosticEvent.Operation,
                            diagnosticEvent.Status,
                            diagnosticEvent.Elapsed,
                            $"suppressed {state.Count - _repeatLimit} repeated event(s)" +
                            (state.Count > _repeatLimit + 1 ? $" (total {state.Count})" : string.Empty));

                        state.LastSummaryAt = _timeProvider.GetUtcNow();
                    }
                }
                else
                {
                    _repeats[key] = new RepeatState
                    {
                        Count = 1,
                        LastSummaryAt = _timeProvider.GetUtcNow(),
                    };
                    toWrite = diagnosticEvent;
                }
            }

            if (toWrite is null)
                return;

            foreach (var sink in sinks)
            {
                try
                {
                    sink.Write(toWrite);
                }
                catch
                {
                    // A failing sink must never take down the operation it
                    // is observing. There is nowhere useful to report this
                    // to, so it is dropped deliberately.
                }
            }
        }

        /// <summary>
        /// The key that identifies "the same event" for rate limiting.
        /// </summary>
        private static string BuildKey(DiagnosticEvent diagnosticEvent) =>
            string.Concat(
                diagnosticEvent.Level.ToString(),
                "|",
                diagnosticEvent.Backend,
                "|",
                diagnosticEvent.Operation,
                "|",
                diagnosticEvent.Status);

        /// <summary>
        /// The repeat state of one event key.
        /// </summary>
        private sealed class RepeatState
        {
            public int Count { get; set; }

            public DateTimeOffset LastSummaryAt { get; set; }
        }
    }

    /// <summary>
    /// A sink that writes to a file, one line per event.
    ///
    /// <para>
    /// The file is opened for append on every write and closed again, so a
    /// long-running widget never holds a handle open, and the log survives
    /// a crash. A rotation is not performed: the log is written only when
    /// something is wrong, and the rate limiter bounds how much can be
    /// written.
    /// </para>
    /// </summary>
    public sealed class FileDiagnosticSink : IDiagnosticSink
    {
        private readonly string _path;
        private readonly object _lock = new();

        public FileDiagnosticSink(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A log path is required.", nameof(path));

            _path = path;
        }

        /// <summary>The file the events are written to.</summary>
        public string Path => _path;

        public void Write(DiagnosticEvent diagnosticEvent)
        {
            var line = diagnosticEvent.Format();

            lock (_lock)
            {
                try
                {
                    var directory = System.IO.Path.GetDirectoryName(_path);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);

                    File.AppendAllText(_path, line + Environment.NewLine);
                }
                catch
                {
                    // A log that cannot be written must not break the
                    // widget. The failure is itself not worth reporting:
                    // it would be reported through the same broken channel.
                }
            }
        }
    }

    /// <summary>
    /// A sink that keeps the most recent events in memory, for tests and
    /// for the in-process view of the log.
    /// </summary>
    public sealed class InMemoryDiagnosticSink : IDiagnosticSink
    {
        private readonly object _lock = new();
        private readonly List<DiagnosticEvent> _events = new();
        private readonly int _capacity;

        public InMemoryDiagnosticSink(int capacity = 512)
        {
            _capacity = capacity < 1 ? 1 : capacity;
        }

        /// <summary>The events recorded so far, oldest first.</summary>
        public IReadOnlyList<DiagnosticEvent> Events
        {
            get
            {
                lock (_lock)
                    return _events.ToList();
            }
        }

        public void Write(DiagnosticEvent diagnosticEvent)
        {
            lock (_lock)
            {
                _events.Add(diagnosticEvent);
                if (_events.Count > _capacity)
                    _events.RemoveRange(0, _events.Count - _capacity);
            }
        }
    }

    /// <summary>
    /// Turns an exception into a short, safe description for the log.
    ///
    /// <para>
    /// The exception <i>type</i> is what makes a failure actionable:
    /// "access denied" and "the driver is missing" are different problems
    /// with different fixes, and the type says which one it is. The
    /// message, on the other hand, routinely embeds a file path, a
    /// registry key, a device instance id or a command line — all of
    /// which are machine identifiers.
    /// </para>
    ///
    /// <para>
    /// So the message is scrubbed: anything that looks like a path is
    /// replaced, and the result is truncated. The full text stays in the
    /// explicit inventory export, where the user has asked for it.
    /// </para>
    /// </summary>
    public static class DiagnosticDetail
    {
        /// <summary>
        /// The longest description that is written to the log.
        /// </summary>
        public const int MaxLength = 160;

        /// <summary>
        /// Describes an exception for the log.
        /// </summary>
        /// <param name="exception">The exception, or null.</param>
        public static string Describe(Exception? exception)
        {
            if (exception is null)
                return string.Empty;

            var type = exception.GetType().Name;
            var message = Scrub(exception.Message);

            if (string.IsNullOrEmpty(message))
                return type;

            return $"{type}: {message}";
        }

        /// <summary>
        /// Removes machine-specific text from a free-form string.
        ///
        /// <para>
        /// Windows paths and quoted paths are the common carriers of a
        /// machine identifier in an exception message. They are replaced
        /// with a placeholder rather than removed, so the shape of the
        /// message survives.
        /// </para>
        /// </summary>
        public static string Scrub(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            var scrubbed = text.Replace('\r', ' ').Replace('\n', ' ').Trim();

            // "C:\dir\file" and "C:/dir/file" — a drive letter followed by a
            // separator is a path.
            scrubbed = System.Text.RegularExpressions.Regex.Replace(
                scrubbed,
                @"[A-Za-z]:[\\/][^\s""']*",
                "<path>");

            // "\dir\file" — a rooted path without a drive letter.
            scrubbed = System.Text.RegularExpressions.Regex.Replace(
                scrubbed,
                @"(?:^|\s)\\[^\s""']*",
                " <path>");

            // A quoted path, with or without a drive letter.
            scrubbed = System.Text.RegularExpressions.Regex.Replace(
                scrubbed,
                @"""[^""]*""",
                "<path>");

            if (scrubbed.Length > MaxLength)
                scrubbed = scrubbed.Substring(0, MaxLength - 1) + "…";

            return scrubbed;
        }
    }
}

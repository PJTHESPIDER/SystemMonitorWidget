using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace System_Monitor.Services
{
    /// <summary>
    /// The NVIDIA command-line fallback.
    ///
    /// This exists for one reason: the cross-vendor sensor backend
    /// (LibreHardwareMonitor) may be unavailable or unable to read a metric
    /// on a given machine, while the NVIDIA driver tools can still read it.
    /// The backend is the primary source; this is the fallback, and it is
    /// only consulted for a metric the backend could not supply.
    ///
    /// It deliberately does *not* re-read what the backend already provides.
    /// On a machine where the backend works, this class runs no subprocess at
    /// all.
    ///
    /// Behaviour:
    ///
    ///   - **One invocation per cycle.** Identity, utilization, temperature,
    ///     memory used and memory total are all requested in a single
    ///     nvidia-smi query, not one process per metric.
    ///   - **Bounded.** The subprocess is started, read and awaited under a
    ///     timeout, so it can never hold the pipeline's drain boundary open.
    ///   - **Reconciled, not merged by position.** A reading is attributed to
    ///     a device only when an identity key matches exactly one device. See
    ///     <see cref="NvidiaGpuReconciler"/>.
    /// </summary>
    public sealed class NvidiaGpuFallback : IDisposable
    {
        /// <summary>
        /// The backend name used in the diagnostic log.
        /// </summary>
        public const string BackendName = "nvidia-smi";

        /// <summary>
        /// The metrics requested from nvidia-smi, in the order the parser
        /// expects them. Identity first, then the metrics.
        /// </summary>
        public const string QueryFields =
            "uuid,name,pci.bus_id,pci.device_id,utilization.gpu,temperature.gpu,memory.used,memory.total";

        /// <summary>
        /// The arguments used for the query. <c>nounits</c> gives raw numbers;
        /// memory comes back in mebibytes and is converted by the parser.
        /// </summary>
        public const string QueryArguments =
            "--query-gpu=" + QueryFields + " --format=csv,noheader,nounits";

        /// <summary>
        /// The default bound on the whole subprocess interaction.
        /// </summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

        private readonly IProcessRunner _processRunner;
        private readonly NvidiaSmiLocator _locator;
        private readonly TimeSpan _timeout;
        private readonly TimeProvider _timeProvider;
        private readonly object _lock = new();

        private bool _disposed;

        public NvidiaGpuFallback(
            IProcessRunner? processRunner = null,
            NvidiaSmiLocator? locator = null,
            TimeSpan? timeout = null)
            : this(processRunner, locator, timeout, null)
        {
        }

        /// <summary>
        /// Creates the fallback with an explicit clock, so the elapsed
        /// times in the log can be controlled in tests.
        /// </summary>
        public NvidiaGpuFallback(
            IProcessRunner? processRunner,
            NvidiaSmiLocator? locator,
            TimeSpan? timeout,
            TimeProvider? timeProvider)
        {
            _processRunner = processRunner ?? new SystemProcessRunner();
            _locator = locator ?? new NvidiaSmiLocator();
            _timeout = timeout ?? DefaultTimeout;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        /// <summary>
        /// The number of times a subprocess was actually launched. Exposed
        /// for diagnostics and for tests.
        /// </summary>
        public int InvocationCount { get; private set; }

        /// <summary>
        /// Reads the NVIDIA GPUs in a single invocation.
        ///
        /// Returns an empty list when the executable is not present, when the
        /// command fails, when it times out, or when its output cannot be
        /// parsed. Never throws for an expected failure.
        /// </summary>
        public async Task<IReadOnlyList<NvidiaGpuSample>> ReadAsync(
            CancellationToken cancellationToken = default)
        {
            if (_disposed)
                return Array.Empty<NvidiaGpuSample>();

            var executable = _locator.Resolve();
            if (executable is null)
            {
                // No NVIDIA driver tools on this machine. The locator stops
                // looking for a while, so this costs nothing per cycle.
                return Array.Empty<NvidiaGpuSample>();
            }

            var started = _timeProvider.GetUtcNow();

            ProcessRunResult result;
            try
            {
                InvocationCount++;

                result = await _processRunner
                    .RunAsync(executable, QueryArguments, _timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DiagnosticLog.Shared.Error(
                    BackendName,
                    "query",
                    "failed",
                    elapsed: _timeProvider.GetUtcNow() - started,
                    detail: DiagnosticDetail.Describe(ex));
                return Array.Empty<NvidiaGpuSample>();
            }

            var elapsed = _timeProvider.GetUtcNow() - started;

            if (result.TimedOut)
            {
                // A child that could not be terminated is still running.
                // The collector runs once per cycle, so a surviving child
                // would accumulate one process per cycle. It is reported
                // separately from the timeout itself, because the timeout
                // is expected behaviour and the survivor is not.
                var detail = result.ChildTerminated
                    ? $"limit={_timeout.TotalSeconds:F0}s"
                    : $"limit={_timeout.TotalSeconds:F0}s child-not-terminated";

                DiagnosticLog.Shared.Error(
                    BackendName,
                    "query",
                    "timeout",
                    elapsed: elapsed,
                    detail: detail);
                return Array.Empty<NvidiaGpuSample>();
            }

            if (!result.Succeeded)
            {
                // A nonzero exit means the tool ran but could not answer —
                // typically no devices, or an unsupported field. The exit
                // code is recorded; the tool's own message is scrubbed,
                // because it routinely embeds the machine's device list.
                DiagnosticLog.Shared.Warning(
                    BackendName,
                    "query",
                    "failed",
                    elapsed: elapsed,
                    detail: $"exit={result.ExitCode} {DiagnosticDetail.Scrub(result.StandardError)}");
                return Array.Empty<NvidiaGpuSample>();
            }

            var samples = NvidiaSmiParser.Parse(result.StandardOutput);

            DiagnosticLog.Shared.Info(
                BackendName,
                "query",
                "ok",
                elapsed: elapsed,
                detail: $"devices={samples.Count}");

            return samples;
        }

        /// <summary>
        /// Reads the NVIDIA GPUs in a single invocation, synchronously.
        ///
        /// The collector seam (<see cref="IMetricsCollector.Collect"/>) is
        /// synchronous. The work is pushed onto the thread pool rather than
        /// blocked inline, so the calling thread never waits on a task that
        /// might need that same thread to complete.
        /// </summary>
        public IReadOnlyList<NvidiaGpuSample> Read()
        {
            try
            {
                return Task.Run(() => ReadAsync()).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                DiagnosticLog.Shared.Error(
                    BackendName,
                    "read",
                    "failed",
                    detail: DiagnosticDetail.Describe(ex));
                return Array.Empty<NvidiaGpuSample>();
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                    return;

                _disposed = true;
            }
        }
    }
}

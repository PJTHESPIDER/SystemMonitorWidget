using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace System_Monitor.Services
{
    /// <summary>
    /// Locates the nvidia-smi executable.
    ///
    /// The executable is resolved once and remembered. When it cannot be
    /// found, the locator stops looking for a cooling-off period rather than
    /// re-probing the filesystem on every collection cycle — on a machine
    /// with no NVIDIA driver that would be a wasted directory walk every few
    /// seconds, forever.
    ///
    /// An explicit rediscovery (for example after the user installs a
    /// driver) is requested through <see cref="Rediscover"/>.
    /// </summary>
    public sealed class NvidiaSmiLocator
    {
        /// <summary>
        /// How long to wait before looking for the executable again, after a
        /// failed lookup.
        /// </summary>
        public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromMinutes(15);

        private static readonly string[] System32RelativePaths =
        {
            "nvidia-smi.exe",
        };

        private readonly object _lock = new();
        private readonly Func<string, bool> _fileExists;
        private readonly string[] _candidatePaths;
        private readonly TimeSpan _retryInterval;
        private readonly TimeProvider _timeProvider;

        private string? _resolvedPath;
        private bool _resolutionAttempted;
        private DateTimeOffset _nextAttemptAt = DateTimeOffset.MinValue;

        public NvidiaSmiLocator(
            Func<string, bool>? fileExists = null,
            IEnumerable<string>? candidatePaths = null,
            TimeSpan? retryInterval = null,
            TimeProvider? timeProvider = null)
        {
            _fileExists = fileExists ?? File.Exists;
            _retryInterval = retryInterval ?? DefaultRetryInterval;
            _timeProvider = timeProvider ?? TimeProvider.System;
            _candidatePaths = candidatePaths is null
                ? BuildDefaultCandidates().ToArray()
                : new List<string>(candidatePaths).ToArray();
        }

        /// <summary>
        /// The resolved path, or null when the executable has not been found.
        /// </summary>
        public string? ResolvedPath
        {
            get
            {
                lock (_lock)
                    return _resolvedPath;
            }
        }

        /// <summary>
        /// True when the executable is known to be present.
        /// </summary>
        public bool IsAvailable => ResolvedPath is not null;

        /// <summary>
        /// Returns the path to nvidia-smi, or null when it cannot be found.
        ///
        /// After a failed lookup the executable is not looked for again until
        /// the retry interval has elapsed, so a machine without NVIDIA
        /// hardware does not pay for a lookup on every sampling cycle.
        /// </summary>
        public string? Resolve()
        {
            lock (_lock)
            {
                if (_resolvedPath is not null)
                    return _resolvedPath;

                var now = _timeProvider.GetUtcNow();
                if (_resolutionAttempted && now < _nextAttemptAt)
                    return null;

                _resolutionAttempted = true;

                foreach (var candidate in _candidatePaths)
                {
                    if (string.IsNullOrWhiteSpace(candidate))
                        continue;

                    try
                    {
                        if (_fileExists(candidate))
                        {
                            _resolvedPath = candidate;

                            DiagnosticLog.Shared.Info(
                                NvidiaGpuFallback.BackendName,
                                "locate",
                                "ok",
                                detail: "executable found");

                            return candidate;
                        }
                    }
                    catch (Exception ex)
                    {
                        // The candidate path is a machine-specific location,
                        // so it is not written to the log. The exception
                        // type is enough to diagnose the failure.
                        DiagnosticLog.Shared.Warning(
                            NvidiaGpuFallback.BackendName,
                            "locate",
                            "failed",
                            detail: DiagnosticDetail.Describe(ex));
                    }
                }

                _nextAttemptAt = now + _retryInterval;

                // The executable is not on this machine. That is a normal
                // condition on a machine without an NVIDIA card, so it is
                // reported at the lowest severity and the rate limiter keeps
                // it from repeating on every cycle.
                DiagnosticLog.Shared.Info(
                    NvidiaGpuFallback.BackendName,
                    "locate",
                    "not-found",
                    detail: $"no nvidia-smi in {_candidatePaths.Length} candidate location(s)");

                return null;
            }
        }

        /// <summary>
        /// Forces the next <see cref="Resolve"/> to look for the executable
        /// again, for use after an explicit rediscovery event.
        /// </summary>
        public void Rediscover()
        {
            lock (_lock)
            {
                _resolvedPath = null;
                _resolutionAttempted = false;
                _nextAttemptAt = DateTimeOffset.MinValue;
            }
        }

        /// <summary>
        /// The documented locations that are searched, in order.
        ///
        /// The System32 copy is the one the driver installer maintains. The
        /// NVSMI copies are the standalone driver-tool installs, which is
        /// where the executable lives on machines that installed only the
        /// driver utilities.
        /// </summary>
        private static IEnumerable<string> BuildDefaultCandidates()
        {
            var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!string.IsNullOrEmpty(system32))
            {
                foreach (var relative in System32RelativePaths)
                    yield return Path.Combine(system32, relative);
            }

            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
            {
                yield return Path.Combine(programFiles, @"NVIDIA Corporation\NVSMI\nvidia-smi.exe");
            }

            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(programFilesX86))
            {
                yield return Path.Combine(programFilesX86, @"NVIDIA Corporation\NVSMI\nvidia-smi.exe");
            }
        }
    }
}

using System;
using System.Threading;
using System.Threading.Tasks;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// Coordinates the machine-wide PawnIO setup flow.
    ///
    /// Responsibilities:
    /// - Requires an explicit setup request (never triggered by startup, sensor reads, or polling).
    /// - Serializes setup across widget instances/processes via a named mutex.
    /// - Orchestrates the installer (download → verify → launch elevated).
    /// - Re-checks dependency state after installation.
    /// - Triggers backend reinitialization when new hardware access is available.
    ///
    /// Does NOT add widget UI, execute downloads during startup/sensor reads,
    /// or automatically reinstall when metadata is present but readings
    /// remain unavailable.
    ///
    /// The mutex is concurrency control, NOT a security boundary. It is
    /// created with a cross-process name ("Global\\" prefix) so all widget
    /// instances share it. Abandoned ownership (e.g., process crash) is
    /// handled by WaitHandle's built-in abandonment detection.
    ///
    /// <para><b>Shared State Ownership</b></para>
    ///
    /// <list type="bullet">
    /// <item><description>
    /// <b>Setup state is machine-wide</b>: The coordinator's <see cref="RequestSetupAsync"/>
    /// result represents the machine-wide PawnIO installation state. The widget
    /// provider caches the most recent result in a process-wide static field
    /// (<c>_lastSetupResult</c> in <see cref="SystemMonitorWidgetProvider"/>)
    /// so that all widget instances in the process see the same setup status.
    /// </description></item>
    /// <item><description>
    /// <b>Per-widget preferences</b>: The <c>ShowCpuTemperature</c> flag and GPU
    /// selection are stored per-widget in the host's <c>CustomState</c> JSON
    /// (serialized via <see cref="SystemMonitorWidgetProvider.SerializeCustomState"/>).
    /// They are NOT machine-wide — each widget instance has its own preference.
    /// </description></item>
    /// <item><description>
    /// <b>Transient states are not persisted</b>: <c>SetupInProgress</c> and other
    /// transient states are not written to <c>CustomState</c>. On provider restart,
    /// the coordinator is re-queried via <see cref="RequestSetupAsync"/> (or a
    /// fresh check) to rebuild the cached setup state.
    /// </description></item>
    /// <item><description>
    /// <b>Execution gate</b>: <see cref="SystemMonitorWidgetProvider.SetupExecutionEnabled"/>
    /// defaults to <c>false</c> in production. When disabled, the widget explains
    /// that setup is not enabled in this candidate rather than silently no-oping
    /// or claiming installation started. Tests can override the property to enable
    /// execution.
    /// </description></item>
    /// </list>
    /// </summary>
    public sealed class CpuTemperatureSetupCoordinator
    {
        /// <summary>
        /// The named mutex used for machine-wide setup serialization.
        /// Global scope so all processes (including out-of-process widget host) share it.
        /// </summary>
        public const string MutexName = @"Global\SystemMonitor_PawnIoSetup";

        private readonly PawnIoInstaller _installer;
        private readonly IPawnIoInstallationChecker _installationChecker;

        /// <summary>
        /// The sensor backend source to reinitialize after a successful install.
        /// May be null if the coordinator is used without a backend to reinit.
        /// </summary>
        private readonly ILhmSensorSource? _sensorSource;

        /// <summary>
        /// Creates the coordinator with explicit dependencies for testing.
        /// </summary>
        /// <summary>
        /// Creates the coordinator with explicit dependencies for testing.
        /// The <paramref name="installationChecker"/> is injected so tests can
        /// control pre-check and post-check behavior without hitting the registry.
        /// </summary>
        internal CpuTemperatureSetupCoordinator(
            TrustedPawnIoInstaller descriptor,
            ITrustedDownloader downloader,
            IProcessRunner processRunner,
            IPawnIoInstallationChecker installationChecker,
            ILhmSensorSource? sensorSource = null)
        {
            _installer = new PawnIoInstaller(descriptor, downloader, processRunner);
            _installationChecker = installationChecker;
            _sensorSource = sensorSource;
        }

        /// <summary>
        /// Creates the coordinator with explicit dependencies for testing.
        /// Uses the real installation checker (reads registry).
        /// </summary>
        public CpuTemperatureSetupCoordinator(
            TrustedPawnIoInstaller descriptor,
            ITrustedDownloader downloader,
            IProcessRunner processRunner,
            ILhmSensorSource? sensorSource = null)
            : this(descriptor, downloader, processRunner, new PawnIoInstallationChecker(), sensorSource)
        {
        }

        /// <summary>
        /// Creates the coordinator with default production dependencies.
        /// </summary>
        public CpuTemperatureSetupCoordinator(
            TrustedPawnIoInstaller descriptor,
            ILhmSensorSource? sensorSource = null)
            : this(descriptor, new TrustedDownloader(), new SystemProcessRunner(), new PawnIoInstallationChecker(), sensorSource)
        {
        }

        /// <summary>
        /// Requests the PawnIO setup flow. This method is the single entry point
        /// — setup is never triggered during startup, sensor reads, or polling.
        ///
        /// The flow:
        /// 1. Acquire machine-wide mutex (serialized across widget instances).
        /// 2. Check current installation state.
        /// 3. If already installed + compatible → AlreadyConfigured (no action).
        /// 4. If not installed/unknown → download, verify, launch installer.
        /// 5. After installer exits → re-check dependency state.
        /// 6. If installed + compatible → reinitialize backend.
        /// 7. Return structured result.
        ///
        /// Does NOT automatically reinstall when metadata is present but readings
        /// remain unavailable.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token — user can cancel setup.</param>
        /// <returns>Structured setup result with outcome and state.</returns>
        public async Task<PawnIoSetupResult> RequestSetupAsync(CancellationToken cancellationToken = default)
        {
            var requestedAt = DateTimeOffset.UtcNow;

            // Step 1: Check current state before acquiring the mutex.
            // This is a fast read-only check, no need to hold the lock for it.
            var preCheck = _installationChecker.Check();
            var preCompat = PawnIoCompatibility.Evaluate(preCheck);

            if (preCheck.Status == PawnIoInstallationStatus.Installed &&
                preCompat.Status == PawnIoCompatibilityStatus.Compatible)
            {
                // Already installed and compatible — no setup action needed.
                // Do NOT reinstall. The user may still need backend reinit,
                // but that's a separate action.
                return new PawnIoSetupResult
                {
                    Outcome = PawnIoSetupOutcome.AlreadyConfigured,
                    InstallationState = preCheck,
                    Compatibility = preCompat,
                    FailureReason = null,
                    RequestedAt = requestedAt,
                    CompletedAt = DateTimeOffset.UtcNow,
                    RebootRequired = false,
                };
            }

            // Step 2: Acquire machine-wide mutex.
            using var mutex = new Mutex(initiallyOwned: false, name: MutexName);
            var acquired = false;

            try
            {
                // Wait with cancellation support. Poll every 100ms so
                // cancellation is responsive. The 100ms wait is non-cancellable
                // (Mutex.WaitOne(int) doesn't take a token in .NET 8), but we
                // check the token between each poll.
                while (!acquired && !cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        if (mutex.WaitOne(100))
                        {
                            acquired = true;
                        }
                    }
                    catch (AbandonedMutexException)
                    {
                        // Previous owner exited without releasing — we now own it.
                        // This is normal for crash recovery.
                        acquired = true;
                    }
                }

                if (!acquired)
                {
                    // We failed to acquire the mutex before cancellation.
                    // Distinguish two cases:
                    // 1. Token was already cancelled before we started waiting
                    //    (user-initiated cancellation) → Cancelled
                    // 2. Token expired while waiting (another caller holds the mutex)
                    //    → InProgress
                    if (cancellationToken.IsCancellationRequested)
                    {
                        // Check if the mutex is still held by another caller.
                        // WaitOne(0) returns false if mutex is still held,
                        // true if it's free (acquired with 0 timeout).
                        var mutexStillHeld = !mutex.WaitOne(0);
                        if (mutexStillHeld)
                        {
                            // Mutex still held — another setup is in progress.
                            return new PawnIoSetupResult
                            {
                                Outcome = PawnIoSetupOutcome.InProgress,
                                InstallationState = preCheck,
                                Compatibility = preCompat,
                                FailureReason = "Another setup operation is in progress",
                                RequestedAt = requestedAt,
                                CompletedAt = DateTimeOffset.UtcNow,
                                RebootRequired = false,
                            };
                        }
                        // Mutex was free — we acquired it with WaitOne(0).
                        // Clean up so we don't leak it.
                        try { mutex.ReleaseMutex(); } catch { }
                    }

                    return new PawnIoSetupResult
                    {
                        Outcome = PawnIoSetupOutcome.Cancelled,
                        InstallationState = null,
                        Compatibility = null,
                        FailureReason = "User cancelled while waiting for setup lock",
                        RequestedAt = requestedAt,
                        CompletedAt = DateTimeOffset.UtcNow,
                        RebootRequired = false,
                    };
                }
            }
            catch (Exception ex)
            {
                return new PawnIoSetupResult
                {
                    Outcome = PawnIoSetupOutcome.LaunchFailed,
                    FailureStage = PawnIoSetupFailureStage.PostInstallCheck,
                    InstallationState = preCheck,
                    Compatibility = preCompat,
                    FailureReason = $"Could not acquire setup lock: {ex.Message}",
                    RequestedAt = requestedAt,
                    CompletedAt = DateTimeOffset.UtcNow,
                    RebootRequired = false,
                };
            }

            try
            {
                // Step 3: Re-check inside the lock to avoid redundant work
                // if another caller already installed since our pre-check.
                var lockedCheck = _installationChecker.Check();
                var lockedCompat = PawnIoCompatibility.Evaluate(lockedCheck);

                if (lockedCheck.Status == PawnIoInstallationStatus.Installed &&
                    lockedCompat.Status == PawnIoCompatibilityStatus.Compatible)
                {
                    return new PawnIoSetupResult
                    {
                        Outcome = PawnIoSetupOutcome.AlreadyConfigured,
                        InstallationState = lockedCheck,
                        Compatibility = lockedCompat,
                        FailureReason = null,
                        RequestedAt = requestedAt,
                        CompletedAt = DateTimeOffset.UtcNow,
                        RebootRequired = false,
                    };
                }

                // Step 4: Launch installer (download → verify → launch elevated)
                var launchResult = await _installer.LaunchAsync(
                    string.Empty, // The installer manages its own download path internally
                    cancellationToken);

                if (launchResult.Outcome is PawnIoInstallerOutcome.LaunchFailed)
                {
                    // Distinguish download-stage failure from launch-stage failure.
                    // MapDownloadFailure maps DownloadError → LaunchFailed,
                    // but we preserve the stage via DownloadOutcome.
                    if (launchResult.DownloadOutcome == PawnIoDownloadOutcome.DownloadError)
                    {
                        return new PawnIoSetupResult
                        {
                            Outcome = PawnIoSetupOutcome.DownloadFailed,
                            FailureStage = PawnIoSetupFailureStage.Download,
                            InstallationState = lockedCheck,
                            Compatibility = lockedCompat,
                            FailureReason = launchResult.FailureReason,
                            RequestedAt = requestedAt,
                            CompletedAt = DateTimeOffset.UtcNow,
                            RebootRequired = false,
                        };
                    }

                    return new PawnIoSetupResult
                    {
                        Outcome = PawnIoSetupOutcome.LaunchFailed,
                        FailureStage = PawnIoSetupFailureStage.Launch,
                        InstallationState = lockedCheck,
                        Compatibility = lockedCompat,
                        FailureReason = launchResult.FailureReason,
                        RequestedAt = requestedAt,
                        CompletedAt = DateTimeOffset.UtcNow,
                        RebootRequired = false,
                    };
                }

                if (launchResult.Outcome is PawnIoInstallerOutcome.Indeterminate &&
                    launchResult.ExitCode is null)
                {
                    // Indeterminate with no exit code means verification failed
                    // (hash mismatch or signature rejection). The installer
                    // was never launched.
                    return new PawnIoSetupResult
                    {
                        Outcome = PawnIoSetupOutcome.VerificationFailed,
                        FailureStage = PawnIoSetupFailureStage.Verification,
                        InstallationState = lockedCheck,
                        Compatibility = lockedCompat,
                        FailureReason = launchResult.FailureReason,
                        RequestedAt = requestedAt,
                        CompletedAt = DateTimeOffset.UtcNow,
                        RebootRequired = false,
                    };
                }

                if (launchResult.WasCancelled)
                {
                    return new PawnIoSetupResult
                    {
                        Outcome = PawnIoSetupOutcome.Cancelled,
                        InstallationState = null,
                        Compatibility = null,
                        FailureReason = launchResult.FailureReason,
                        RequestedAt = requestedAt,
                        CompletedAt = DateTimeOffset.UtcNow,
                        RebootRequired = launchResult.RebootRequired,
                    };
                }

                // Step 5: Re-check dependency state after installer exits
                var postCheck = _installationChecker.Check();
                var postCompat = PawnIoCompatibility.Evaluate(postCheck);

                if (postCheck.Status != PawnIoInstallationStatus.Installed ||
                    postCompat.Status != PawnIoCompatibilityStatus.Compatible)
                {
                    return new PawnIoSetupResult
                    {
                        Outcome = PawnIoSetupOutcome.DependencyStillMissing,
                        FailureStage = PawnIoSetupFailureStage.PostInstallCheck,
                        InstallationState = postCheck,
                        Compatibility = postCompat,
                        FailureReason = $"Installer completed but PawnIO not detected as installed+compatible. Status: {postCheck.Status}, Compatibility: {postCompat.Status}",
                        RequestedAt = requestedAt,
                        CompletedAt = DateTimeOffset.UtcNow,
                        RebootRequired = launchResult.RebootRequired,
                    };
                }

                // Step 6: Reinitialize backend if available
                if (_sensorSource is not null)
                {
                    var reinitResult = await _sensorSource.ReinitializeAsync(cancellationToken);

                    if (reinitResult.Outcome == ReinitializationOutcome.TemperatureUnavailable)
                    {
                        return new PawnIoSetupResult
                        {
                            Outcome = PawnIoSetupOutcome.DependencyInstalledSensorUnavailable,
                            FailureStage = PawnIoSetupFailureStage.PostInstallCheck,
                            InstallationState = postCheck,
                            Compatibility = postCompat,
                            FailureReason = reinitResult.FailureReason ?? "PawnIO installed but CPU temperature sensors unavailable",
                            RequestedAt = requestedAt,
                            CompletedAt = DateTimeOffset.UtcNow,
                            RebootRequired = launchResult.RebootRequired,
                        };
                    }

                    if (reinitResult.Outcome == ReinitializationOutcome.Success)
                    {
                        return new PawnIoSetupResult
                        {
                            Outcome = PawnIoSetupOutcome.Success,
                            FailureStage = PawnIoSetupFailureStage.None,
                            InstallationState = postCheck,
                            Compatibility = postCompat,
                            FailureReason = null,
                            RequestedAt = requestedAt,
                            CompletedAt = DateTimeOffset.UtcNow,
                            RebootRequired = launchResult.RebootRequired,
                        };
                    }

                    // Other reinit outcomes (InitializationFailed, etc.)
                    return new PawnIoSetupResult
                    {
                        Outcome = PawnIoSetupOutcome.DependencyInstalledSensorUnavailable,
                        FailureStage = PawnIoSetupFailureStage.PostInstallCheck,
                        InstallationState = postCheck,
                        Compatibility = postCompat,
                        FailureReason = $"Backend reinit failed: {reinitResult.FailureReason}",
                        RequestedAt = requestedAt,
                        CompletedAt = DateTimeOffset.UtcNow,
                        RebootRequired = launchResult.RebootRequired,
                    };
                }

                // No sensor source to reinitialize — report success with reboot flag
                return new PawnIoSetupResult
                {
                    Outcome = PawnIoSetupOutcome.DependencyStillMissing,
                    FailureStage = PawnIoSetupFailureStage.PostInstallCheck,
                    InstallationState = postCheck,
                    Compatibility = postCompat,
                    FailureReason = "No sensor source to reinitialize — manual backend restart needed after reboot",
                    RequestedAt = requestedAt,
                    CompletedAt = DateTimeOffset.UtcNow,
                    RebootRequired = launchResult.RebootRequired,
                };
            }
            finally
            {
                // Always release the mutex.
                if (acquired)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch (ApplicationException)
                    {
                        // Already released or not owned — safe to ignore.
                    }
                }
            }
        }
    }
}

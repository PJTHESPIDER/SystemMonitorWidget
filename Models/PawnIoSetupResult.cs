using System;

namespace System_Monitor.Models
{
    /// <summary>
    /// The outcome of the PawnIO setup flow, for the coordinator's state machine.
    /// These are the terminal states the coordinator reports to the caller.
    /// </summary>
    public enum PawnIoSetupOutcome
    {
        /// <summary>Setup has not been requested or not yet started.</summary>
        NotStarted,

        /// <summary>User cancelled the setup (UAC declined, installer cancelled, or coordinator cancelled).</summary>
        Cancelled,

        /// <summary>Download succeeded, verification failed — installer was not launched.</summary>
        VerificationFailed,

        /// <summary>Download or network error prevented reaching the installer.</summary>
        DownloadFailed,

        /// <summary>Installer process could not be started or crashed before producing output.</summary>
        LaunchFailed,

        /// <summary>Installer completed but PawnIO is not installed/detected afterward.</summary>
        DependencyStillMissing,

        /// <summary>PawnIO driver is installed and at a compatible version, but sensors remain unreadable.</summary>
        DependencyInstalledSensorUnavailable,

        /// <summary>PawnIO driver installed, compatible, and CPU temperature sensors are available.</summary>
        Success,

        /// <summary>Already installed and compatible — no setup action taken.</summary>
        AlreadyConfigured,

        /// <summary>Setup is in progress (concurrent call).</summary>
        InProgress,
    }

    /// <summary>
    /// Structured result of the PawnIO setup flow. Carries the terminal
    /// outcome, the installation/compatibility state detected after the
    /// flow ran, and the reason when the outcome is not successful.
    /// </summary>
    public sealed record PawnIoSetupResult
    {
        /// <summary>Terminal outcome of the setup flow.</summary>
        public PawnIoSetupOutcome Outcome { get; init; }

        /// <summary>Installation state as detected post-setup (may differ from pre-setup).</summary>
        public PawnIoInstallationState? InstallationState { get; init; }

        /// <summary>Compatibility result as evaluated post-setup.</summary>
        public PawnIoCompatibilityResult? Compatibility { get; init; }

        /// <summary>Human-readable reason when the outcome is not Success or AlreadyConfigured.</summary>
        public string? FailureReason { get; init; }

        /// <summary>When the setup flow was requested.</summary>
        public DateTimeOffset RequestedAt { get; init; }

        /// <summary>When the setup flow completed.</summary>
        public DateTimeOffset CompletedAt { get; init; }

        /// <summary>True when a system reboot is required for the driver to be fully active.</summary>
        public bool RebootRequired { get; init; }

        /// <summary>
        /// Structured stage at which a failure occurred, when the outcome is not
        /// Success or AlreadyConfigured. Allows callers to distinguish failure
        /// cause without parsing <see cref="FailureReason"/>.
        /// </summary>
        public PawnIoSetupFailureStage FailureStage { get; init; } = PawnIoSetupFailureStage.None;
    }

    /// <summary>
    /// The stage at which a setup failure occurred. Used by <see cref="PawnIoSetupResult"/>
    /// to provide structured, non-message-based failure attribution.
    /// </summary>
    public enum PawnIoSetupFailureStage
    {
        /// <summary>No specific stage — used when the outcome is not a failure.</summary>
        None,

        /// <summary>Failure during download transport (HTTP error, IO, cancellation).</summary>
        Download,

        /// <summary>Failure during hash or Authenticode signature verification of the downloaded file.</summary>
        Verification,

        /// <summary>Failure when launching the elevated installer process.</summary>
        Launch,

        /// <summary>Failure during post-install dependency verification.</summary>
        PostInstallCheck,
    }
}

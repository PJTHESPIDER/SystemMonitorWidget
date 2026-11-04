using System;

namespace System_Monitor.Models
{
    /// <summary>
    /// Outcome of launching the PawnIO installer process.
    /// </summary>
    public enum PawnIoInstallerOutcome
    {
        /// <summary>Installer process started and exited.</summary>
        Exited,

        /// <summary>Installer could not be started (e.g., file not found, access denied).</summary>
        LaunchFailed,

        /// <summary>Installer was cancelled by the user (UAC declined, process killed).</summary>
        Cancelled,

        /// <summary>Outcome is indeterminate — installer exited abnormally with no clear result.</summary>
        Indeterminate,

        /// <summary>Installer completed and reports a reboot is required.</summary>
        RebootRequired,
    }

    /// <summary>
    /// Result of launching and waiting for the PawnIO installer process.
    /// </summary>
    public sealed record PawnIoInstallerLaunchResult
    {
        /// <summary>Outcome of the installer launch.</summary>
        public PawnIoInstallerOutcome Outcome { get; init; }

        /// <summary>The exit code of the installer process, if available.</summary>
        public int? ExitCode { get; init; }

        /// <summary>Human-readable reason for non-success outcomes.</summary>
        public string? FailureReason { get; init; }

        /// <summary>
        /// Whether the installer explicitly indicated a reboot is required.
        /// This may be derived from exit codes, installer output, or documentation.
        /// </summary>
        public bool RebootRequired { get; init; }

        /// <summary>When the installer process completed (or failed to start).</summary>
        public DateTimeOffset CompletedAt { get; init; }

        /// <summary>True when the user cancelled (UAC declined).</summary>
        public bool WasCancelled { get; init; }

        /// <summary>
        /// The download outcome that preceded launch, if any. Allows the
        /// coordinator to distinguish download-stage failures from
        /// launch-stage failures without parsing <see cref="FailureReason"/>.
        /// </summary>
        public PawnIoDownloadOutcome? DownloadOutcome { get; init; }
    }
}

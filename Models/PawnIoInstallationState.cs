using System;

namespace System_Monitor.Models
{
    /// <summary>
    /// The installation presence status of the PawnIO kernel driver.
    /// This is separate from version compatibility and sensor availability.
    /// </summary>
    public enum PawnIoInstallationStatus
    {
        /// <summary>Check not yet performed.</summary>
        Unknown,

        /// <summary>Async check in progress.</summary>
        Checking,

        /// <summary>
        /// No PawnIO service, no driver loaded, no registry metadata found.
        /// The driver is not installed on this machine.
        /// </summary>
        NotInstalled,

        /// <summary>
        /// PawnIO service is running AND driver is loaded.
        /// Version is captured separately (may be null if unreadable).
        /// </summary>
        Installed,

        /// <summary>WMI/service/registry query threw an exception.</summary>
        CheckFailed,

        /// <summary>
        /// OS/architecture not supported (e.g., ARM64 without driver support).
        /// </summary>
        Unsupported,
    }

    /// <summary>
    /// Read-only installation presence state of PawnIO.
    /// Does not include version compatibility judgment — that is a separate concern.
    /// </summary>
    public sealed record PawnIoInstallationState
    {
        /// <summary>Installation presence status.</summary>
        public PawnIoInstallationStatus Status { get; init; }

        /// <summary>
        /// Raw version string if detected (e.g., "2.2.0", "2.1.0").
        /// Null when version could not be read or Status != Installed.
        /// </summary>
        public string? Version { get; init; }

        /// <summary>Failure reason when Status == CheckFailed.</summary>
        public string? FailureReason { get; init; }

        /// <summary>When this check was performed.</summary>
        public DateTimeOffset CheckedAt { get; init; }

        /// <summary>Creates an Unknown state (check not yet performed).</summary>
        public static PawnIoInstallationState Unknown() => new()
        {
            Status = PawnIoInstallationStatus.Unknown,
            Version = null,
            FailureReason = null,
            CheckedAt = DateTimeOffset.MinValue,
        };

        /// <summary>Creates a Checking state (async check in progress).</summary>
        public static PawnIoInstallationState Checking() => new()
        {
            Status = PawnIoInstallationStatus.Checking,
            Version = null,
            FailureReason = null,
            CheckedAt = DateTimeOffset.UtcNow,
        };
    }
}
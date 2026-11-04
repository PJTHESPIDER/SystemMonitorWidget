using System;

namespace System_Monitor.Models
{
    /// <summary>
    /// Version compatibility status of an installed PawnIO driver against the tested baseline.
    /// This is separate from installation presence and sensor availability.
    /// </summary>
    public enum PawnIoCompatibilityStatus
    {
        /// <summary>Installed version is exactly the tested baseline (2.2.0).</summary>
        Compatible,

        /// <summary>Installed version is provably outside the tested baseline (e.g., 2.1.0 known incompatible).</summary>
        Incompatible,

        /// <summary>Installed version is not the tested baseline, but no evidence establishes compatibility or incompatibility.</summary>
        Unknown,

        /// <summary>PawnIO is installed but version could not be determined.</summary>
        UnknownVersion,

        /// <summary>Installation check returned NotInstalled.</summary>
        NotInstalled,
    }

    /// <summary>
    /// Result of evaluating PawnIO version compatibility against the explicit tested baseline.
    /// Only version 2.2.0 is the tested baseline (validated with LibreHardwareMonitorLib 0.9.6
    /// via PR #2174 and #2222). 2.2.0 = Compatible. Versions explicitly known to differ from
    /// the baseline are Unknown (not Incomcompatible) unless evidence establishes incompatibility.
    /// </summary>
    public sealed record PawnIoCompatibilityResult
    {
        /// <summary>Compatibility status.</summary>
        public PawnIoCompatibilityStatus Status { get; init; }

        /// <summary>The version string detected from the installed PawnIO (if readable).</summary>
        public string? DetectedVersion { get; init; }

        /// <summary>
        /// The explicit tested baseline version. Hardcoded to "2.2.0" per design.
        /// This is not inferred — it is the version validated with LHM 0.9.6.
        /// </summary>
        public string TestedBaseline { get; init; } = "2.2.0";

        /// <summary>Human-readable reason for Incompatible, Unknown, or UnknownVersion status.</summary>
        public string? Reason { get; init; }

        /// <summary>Creates a Compatible result.</summary>
        public static PawnIoCompatibilityResult Compatible(string detectedVersion) => new()
        {
            Status = PawnIoCompatibilityStatus.Compatible,
            DetectedVersion = detectedVersion,
            TestedBaseline = "2.2.0",
            Reason = null,
        };

        /// <summary>Creates an Incompatible result with reason (only for versions with proven incompatibility).</summary>
        public static PawnIoCompatibilityResult Incompatible(string detectedVersion, string reason) => new()
        {
            Status = PawnIoCompatibilityStatus.Incompatible,
            DetectedVersion = detectedVersion,
            TestedBaseline = "2.2.0",
            Reason = reason,
        };

        /// <summary>Creates an Unknown result (version not baseline, but no incompatibility evidence).</summary>
        public static PawnIoCompatibilityResult UnknownVersion(string detectedVersion) => new()
        {
            Status = PawnIoCompatibilityStatus.Unknown,
            DetectedVersion = detectedVersion,
            TestedBaseline = "2.2.0",
            Reason = $"Version {detectedVersion} is not the tested baseline (\"2.2.0\"); compatibility not established",
        };

        /// <summary>Creates an UnknownVersion result (installed but version unreadable).</summary>
        public static PawnIoCompatibilityResult UnknownVersion() => new()
        {
            Status = PawnIoCompatibilityStatus.UnknownVersion,
            DetectedVersion = null,
            TestedBaseline = "2.2.0",
            Reason = "PawnIO is installed but version could not be read",
        };

        /// <summary>Creates a NotInstalled result.</summary>
        public static PawnIoCompatibilityResult NotInstalled() => new()
        {
            Status = PawnIoCompatibilityStatus.NotInstalled,
            DetectedVersion = null,
            TestedBaseline = "2.2.0",
            Reason = "PawnIO is not installed",
        };
    }
}

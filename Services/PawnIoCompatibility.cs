using System;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// Evaluates PawnIO version compatibility against the explicit tested baseline.
    /// Only version 2.2.0 is the tested baseline (validated with LibreHardwareMonitorLib 0.9.6
    /// via PR #2174 and #2222). 2.2.0 = Compatible. Other versions are Unknown (not Incompatible)
    /// unless evidence establishes incompatibility. Malformed/unavailable version = UnknownVersion.
    /// </summary>
    public static class PawnIoCompatibility
    {
        /// <summary>
        /// The explicit tested baseline version. Hardcoded per design — not inferred.
        /// Validated with LibreHardwareMonitorLib 0.9.6.
        /// </summary>
        public const string TestedBaseline = "2.2.0";

        /// <summary>
        /// Evaluates compatibility of an installation state against the tested baseline.
        /// </summary>
        /// <param name="installationState">The installation state to evaluate.</param>
        /// <returns>Compatibility result with status, detected version, and reason.</returns>
        public static PawnIoCompatibilityResult Evaluate(PawnIoInstallationState installationState)
        {
            ArgumentNullException.ThrowIfNull(installationState);

            return installationState.Status switch
            {
                PawnIoInstallationStatus.Installed => EvaluateVersion(installationState.Version),
                PawnIoInstallationStatus.NotInstalled => PawnIoCompatibilityResult.NotInstalled(),
                PawnIoInstallationStatus.CheckFailed => PawnIoCompatibilityResult.UnknownVersion(),
                PawnIoInstallationStatus.Unsupported => PawnIoCompatibilityResult.Incompatible(
                    installationState.Version ?? "unknown",
                    "PawnIO is not supported on this platform"),
                PawnIoInstallationStatus.Unknown => PawnIoCompatibilityResult.UnknownVersion(),
                PawnIoInstallationStatus.Checking => PawnIoCompatibilityResult.UnknownVersion(),
                _ => PawnIoCompatibilityResult.UnknownVersion(),
            };
        }

        private static PawnIoCompatibilityResult EvaluateVersion(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                return PawnIoCompatibilityResult.UnknownVersion();
            }

            var normalized = NormalizeVersion(version);
            var baseline = NormalizeVersion(TestedBaseline);

            if (string.Equals(normalized, baseline, StringComparison.OrdinalIgnoreCase))
            {
                return PawnIoCompatibilityResult.Compatible(version);
            }

            // Version is not the tested baseline. Per design, we do not label every untested
            // version Incompatible. Instead, classify as Unknown with a diagnostic detail.
            return PawnIoCompatibilityResult.UnknownVersion(version);
        }

        /// <summary>
        /// Normalizes a version string for comparison (e.g., "2.2.0.0" -> "2.2.0").
        /// </summary>
        private static string NormalizeVersion(string version)
        {
            var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3)
            {
                return $"{parts[0]}.{parts[1]}.{parts[2]}";
            }
            return version;
        }
    }
}

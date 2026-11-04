using System;
using System.Management;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// Read-only detection of PawnIO kernel driver installation presence and version.
    ///
    /// Installation presence is determined using the same registry metadata contract
    /// that LibreHardwareMonitorLib's PawnIo static constructor uses:
    ///   HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO\DisplayVersion
    ///
    /// This ensures that "Installed" aligns exactly with what the LHM library
    /// considers installed (PawnIo.IsInstalled => Version is not null).
    ///
    /// Service and driver-load state checks are supplementary diagnostics — they
    /// do not override the registry-derived installation state.
    /// </summary>
    public sealed class PawnIoInstallationChecker : IPawnIoInstallationChecker
    {
        /// <summary>
        /// Registry path used by LibreHardwareMonitorLib's PawnIo static constructor
        /// to read the DisplayVersion. Matches the LHM contract exactly.
        /// </summary>
        private const string UninstallRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO";

        /// <summary>
        /// Value name that LHM reads via Version.TryParse to populate PawnIo.Version.
        /// </summary>
        private const string VersionValueName = "DisplayVersion";

        /// <summary>
        /// Performs a non-elevated, read-only check of PawnIO installation state.
        ///
        /// Primary detection: reads HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO\DisplayVersion
        /// using the same contract as LibreHardwareMonitorLib's PawnIo static constructor.
        ///
        /// Supplementary diagnostics: queries WMI for service state and driver load state,
        /// which are reported in the installation state but do not override the registry-derived
        /// presence determination.
        /// </summary>
        /// <returns>Installation state with status, detected version (if readable), and timestamp.</returns>
        public PawnIoInstallationState Check()
        {
            var checkedAt = DateTimeOffset.UtcNow;

            try
            {
                // Primary detection: registry metadata matching LHM's PawnIo static constructor
                string? version = null;
                string? registryFailureReason = null;

                try
                {
                    // LHM first checks HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO
                    using var subKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(UninstallRegistryPath);
                    version = subKey?.GetValue(VersionValueName)?.ToString();

                    if (string.IsNullOrWhiteSpace(version))
                    {
                        // LHM falls back to Registry64 view (WOW64)
                        using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(
                            Microsoft.Win32.RegistryHive.LocalMachine,
                            Microsoft.Win32.RegistryView.Registry64);
                        using var subKey64 = baseKey.OpenSubKey(UninstallRegistryPath);
                        version = subKey64?.GetValue(VersionValueName)?.ToString();
                    }
                }
                catch (UnauthorizedAccessException uaEx)
                {
                    // Registry read failed due to permissions — keep version null, record reason
                    registryFailureReason = uaEx.Message;
                }
                catch (Exception ex)
                {
                    // Any other registry error — keep version null, record reason
                    registryFailureReason = ex.Message;
                }

                // Determine installation state based on registry metadata (LHM contract)
                var isInstalled = Version.TryParse(version, out var parsedVersion) && parsedVersion != new Version(0, 0);

                if (isInstalled)
                {
                    // PawnIO is installed with a parseable version
                    // Collect supplementary diagnostics (service state, driver load)
                    var diagnostics = CollectSupplementaryDiagnostics();

                    return new PawnIoInstallationState
                    {
                        Status = PawnIoInstallationStatus.Installed,
                        Version = version,
                        FailureReason = diagnostics.Length == 0 ? null : diagnostics.ToString(),
                        CheckedAt = checkedAt,
                    };
                }

                if (!string.IsNullOrWhiteSpace(registryFailureReason))
                {
                    // Registry key was present but we couldn't read it — CheckFailed
                    return new PawnIoInstallationState
                    {
                        Status = PawnIoInstallationStatus.CheckFailed,
                        Version = null,
                        FailureReason = $"Registry read failed: {registryFailureReason}",
                        CheckedAt = checkedAt,
                    };
                }

                // No registry metadata and no registry error — NotInstalled
                return new PawnIoInstallationState
                {
                    Status = PawnIoInstallationStatus.NotInstalled,
                    Version = null,
                    FailureReason = null,
                    CheckedAt = checkedAt,
                };
            }
            catch (ManagementException ex)
            {
                // WMI failure during supplementary diagnostics should not mask installation state
                return new PawnIoInstallationState
                {
                    Status = PawnIoInstallationStatus.CheckFailed,
                    Version = null,
                    FailureReason = $"WMI error: {ex.Message}",
                    CheckedAt = checkedAt,
                };
            }
            catch (Exception ex)
            {
                return new PawnIoInstallationState
                {
                    Status = PawnIoInstallationStatus.CheckFailed,
                    Version = null,
                    FailureReason = $"Unexpected error: {ex.Message}",
                    CheckedAt = checkedAt,
                };
            }
        }

        /// <summary>
        /// Collects supplementary diagnostics: service state and driver load state.
        /// These provide additional context but do not override the registry-derived
        /// installation presence determination.
        /// </summary>
        private static string CollectSupplementaryDiagnostics()
        {
            var diagnostics = new System.Text.StringBuilder();

            // Check service state via WMI (supplementary — does not affect installation status)
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "root\\CIMV2",
                    "SELECT State, StartMode FROM Win32_Service WHERE Name = 'PawnIO'");
                using var collection = searcher.Get();

                foreach (ManagementObject mo in collection)
                {
                    var state = mo["State"]?.ToString();
                    var startMode = mo["StartMode"]?.ToString();
                    diagnostics.Append($"Service: {state ?? "unknown"} (start mode: {startMode ?? "unknown"}); ");
                    break;
                }
            }
            catch (ManagementException)
            {
                // WMI query failed — this is supplementary, not primary
                diagnostics.Append("Service: WMI query failed; ");
            }

            // Check driver load state via WMI (supplementary)
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "root\\CIMV2",
                    "SELECT Name, State FROM Win32_SystemDriver WHERE Name = 'PawnIO.sys' OR PathName LIKE '%PawnIO.sys%'");
                using var collection = searcher.Get();

                foreach (ManagementObject mo in collection)
                {
                    var state = mo["State"]?.ToString();
                    diagnostics.Append($"Driver: {state ?? "unknown"}; ");
                    break;
                }
            }
            catch (ManagementException)
            {
                diagnostics.Append("Driver: WMI query failed; ");
            }

            var result = diagnostics.ToString().Trim();
            return result.EndsWith("; ") ? result.Substring(0, result.Length - 2) : result;
        }
    }
}

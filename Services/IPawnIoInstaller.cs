using System.Threading;
using System.Threading.Tasks;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// Launches the PawnIO installer as Administrator, waits for completion,
    /// and determines the outcome (success, cancellation, reboot-required, etc.).
    ///
    /// This is the installer launch abstraction — separated from download/verification
    /// so each step can be tested independently. The installer is a GUI wizard with
    /// no documented silent switches, so the user must select "Official edition"
    /// manually in the UI.
    /// </summary>
    public interface IPawnIoInstaller
    {
        /// <summary>
        /// Launches the installer at <paramref name="installerPath"/> with
        /// Administrator elevation, waits for the user to complete the GUI
        /// wizard, and returns the outcome.
        /// </summary>
        /// <param name="installerPath">Path to the verified installer executable.</param>
        /// <param name="cancellationToken">Cancellation token (user cancelling setup).</param>
        /// <returns>The installer launch result.</returns>
        Task<PawnIoInstallerLaunchResult> LaunchAsync(
            string installerPath,
            CancellationToken cancellationToken = default);
    }
}

using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// Read-only detection of PawnIO kernel driver installation presence and version.
    /// Abstracted so the coordinator can be tested with fakes — no registry access
    /// or hardware required in unit tests.
    /// </summary>
    public interface IPawnIoInstallationChecker
    {
        /// <summary>
        /// Performs a non-elevated, read-only check of PawnIO installation state.
        /// </summary>
        PawnIoInstallationState Check();
    }
}

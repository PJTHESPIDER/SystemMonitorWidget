using System;
using System.Threading;
using System.Threading.Tasks;

namespace System_Monitor.Services
{
    /// <summary>
    /// The result of running a command.
    /// </summary>
    /// <param name="ExitCode">The process exit code, or -1 when the process could not be started or was killed.</param>
    /// <param name="StandardOutput">Everything the process wrote to stdout.</param>
    /// <param name="StandardError">Everything the process wrote to stderr.</param>
    /// <param name="TimedOut">True when the process was killed for exceeding its timeout.</param>
    /// <param name="ChildTerminated">
    /// True when a child that was killed is no longer running. False when
    /// the OS refused to terminate it, in which case the child survives.
    /// True for every run that did not time out, because the process
    /// exited on its own.
    /// </param>
    public readonly record struct ProcessRunResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool TimedOut,
        bool ChildTerminated = true)
    {
        /// <summary>
        /// True when the command ran and reported success.
        /// </summary>
        public bool Succeeded => !TimedOut && ExitCode == 0;
    }

    /// <summary>
    /// The result of running an elevated command.
    /// </summary>
    /// <param name="ExitCode">The process exit code, or null when the process could not be started.</param>
    /// <param name="FailureReason">Reason for failure when the process could not start or was cancelled.</param>
    /// <param name="WasCancelled">True when the user cancelled (e.g., UAC declined).</param>
    /// <param name="RebootRequired">True when the installer indicates a reboot is required (e.g., exit code 3010).</param>
    public sealed record ElevatedRunResult(
        int? ExitCode,
        string? FailureReason = null,
        bool WasCancelled = false,
        bool RebootRequired = false);

    /// <summary>
    /// Runs an external command and captures its output.
    ///
    /// Injected so that the nvidia-smi fallback can be tested against fakes,
    /// without an NVIDIA driver and without spawning real processes.
    /// </summary>
    public interface IProcessRunner
    {
        /// <summary>
        /// Runs <paramref name="fileName"/> with <paramref name="arguments"/>
        /// and waits for it to exit, bounded by <paramref name="timeout"/>.
        ///
        /// Implementations must not throw for an expected failure such as the
        /// executable not existing, a nonzero exit code, or a timeout. They
        /// must return the captured output in that case.
        /// </summary>
        Task<ProcessRunResult> RunAsync(
            string fileName,
            string arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Launches <paramref name="fileName"/> as Administrator (elevated),
        /// waits for the user to complete interaction, and returns the outcome.
        ///
        /// This is used for installers that require elevation, such as the
        /// PawnIO driver installer. The process is launched with "runas" verb
        /// so the OS prompts for UAC consent.
        ///
        /// Must report cancellation (UAC declined or user quit) distinctly from
        /// a process crash or non-zero exit.
        /// </summary>
        Task<ElevatedRunResult> RunElevatedAsync(
            string fileName,
            string arguments,
            CancellationToken cancellationToken = default);
    }
}

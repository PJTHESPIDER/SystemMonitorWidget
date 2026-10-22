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
    public readonly record struct ProcessRunResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool TimedOut)
    {
        /// <summary>
        /// True when the command ran and reported success.
        /// </summary>
        public bool Succeeded => !TimedOut && ExitCode == 0;
    }

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
    }
}

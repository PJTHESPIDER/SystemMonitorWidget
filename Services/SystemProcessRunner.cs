using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System_Monitor.Services
{
    /// <summary>
    /// Runs external commands in a child process, bounded by a timeout.
    ///
    /// Three properties matter here, because the caller is a metric collector
    /// running on the pipeline's single collection thread:
    ///
    ///   - **Bounded.** Every wait — the start, the output drain, and the
    ///     exit — is bounded. A hung child can therefore never hold the
    ///     pipeline's drain boundary open indefinitely.
    ///   - **Deadlock-free.** Standard output and standard error are read
    ///     concurrently on separate tasks. Reading one stream to completion
    ///     while the child fills the other would otherwise block forever.
    ///   - **Clean.** The child is killed and its handle released when the
    ///     timeout expires, and the <see cref="Process"/> instance is always
    ///     disposed.
    /// </summary>
    public sealed class SystemProcessRunner : IProcessRunner
    {
        /// <summary>
        /// How long the readers are given to observe the closed pipes after
        /// the child has exited, or has been killed. The drain is bounded by
        /// this, so a grandchild that inherited the pipe handles cannot
        /// hold the collection open.
        /// </summary>
        private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(1);

        public async Task<ProcessRunResult> RunAsync(
            string fileName,
            string arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = new Process { StartInfo = startInfo };

            try
            {
                if (!process.Start())
                {
                    return new ProcessRunResult(-1, string.Empty, "Process did not start.", false);
                }
            }
            catch (Exception ex)
            {
                // The executable is missing, or the OS refused to start it.
                return new ProcessRunResult(-1, string.Empty, ex.Message, false);
            }

            // Drain both streams concurrently. If one were read to completion
            // before the other, a child that fills the unread stream's pipe
            // buffer would block forever while we wait for it.
            var stdoutTask = Task.Run(() => process.StandardOutput.ReadToEndAsync(), CancellationToken.None);
            var stderrTask = Task.Run(() => process.StandardError.ReadToEndAsync(), CancellationToken.None);

            // A cancellation request means the caller no longer wants the
            // result. The child is killed so the wait can end immediately,
            // rather than running the full timeout for a result nobody will
            // read.
            using var cancellationRegistration = cancellationToken.CanBeCanceled
                ? cancellationToken.Register(() => TryKill(process))
                : default;

            var exited = process.WaitForExit((int)timeout.TotalMilliseconds);

            if (!exited)
            {
                var killed = TryKill(process);

                // Give the readers a moment to observe the closed pipes, then
                // stop waiting on them regardless.
                await Task.WhenAny(
                    Task.WhenAll(stdoutTask, stderrTask),
                    Task.Delay(DrainGrace, CancellationToken.None)).ConfigureAwait(false);

                return new ProcessRunResult(
                    -1,
                    SafeResult(stdoutTask),
                    SafeResult(stderrTask),
                    TimedOut: true,
                    ChildTerminated: killed);
            }

            // The process has exited, so both pipes are closed and the reads
            // complete. Await them so the full output is captured — but
            // bound the wait, because a grandchild that inherited the pipe
            // handles can keep the write end open long after the child has
            // gone. Without this bound the drain would be the one unbounded
            // wait in an otherwise bounded runner.
            await Task.WhenAny(
                Task.WhenAll(stdoutTask, stderrTask),
                Task.Delay(DrainGrace, CancellationToken.None)).ConfigureAwait(false);

            return new ProcessRunResult(
                process.ExitCode,
                SafeResult(stdoutTask),
                SafeResult(stderrTask),
                TimedOut: false);
        }

        /// <summary>
        /// Terminates the child process. A process that has already exited
        /// throws, which is not an error here.
        /// </summary>
        /// <param name="process">The child to terminate.</param>
        /// <returns>
        /// True when the child was terminated — or had already exited. False
        /// when the OS refused to terminate it, in which case the child is
        /// still running and the caller must say so.
        /// </returns>
        /// <summary>
        /// Launches a process as Administrator via the "runas" verb and waits
        /// for it to exit. Used for installers that require elevation.
        ///
        /// Because the elevated process is a GUI wizard, standard output and
        /// error are not captured (UseShellExecute = true). Only the exit code
        /// matters.
        ///
        /// Cancellation: since the user interacts with a GUI, cancellation here
        /// means the caller (e.g., the coordinate) gave up — but the elevated
        /// process may still be running. We cannot force-kill an elevated process
        /// from a non-elevated parent, so we report cancellation without killing.
        /// </summary>
        public async Task<ElevatedRunResult> RunElevatedAsync(
            string fileName,
            string arguments,
            CancellationToken cancellationToken = default)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas", // Elevate via UAC prompt
                // With UseShellExecute=true and Verb="runas", there is no
                // stdout/stderr redirect — the OS handles the elevation prompt.
            };

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            try
            {
                if (!process.Start())
                {
                    return new ElevatedRunResult(
                        ExitCode: null,
                        FailureReason: "Process did not start.",
                        WasCancelled: false,
                        RebootRequired: false);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                // The user declined the UAC prompt.
                return new ElevatedRunResult(
                    ExitCode: null,
                    FailureReason: $"Elevation denied or unavailable: {ex.Message}",
                    WasCancelled: true,
                    RebootRequired: false);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // Win32 error 1223 = ERROR_CANCELLED_BY_USER — the user
                // declined the UAC elevation dialog. This is cancellation,
                // not a launch failure.
                return new ElevatedRunResult(
                    ExitCode: null,
                    FailureReason: "User declined the UAC elevation prompt",
                    WasCancelled: true,
                    RebootRequired: false);
            }
            catch (Exception ex)
            {
                process.Dispose();
                return new ElevatedRunResult(
                    ExitCode: null,
                    FailureReason: $"Failed to launch installer: {ex.Message}",
                    WasCancelled: false,
                    RebootRequired: false);
            }

            // Wait for the process to exit. For a GUI installer, this blocks
            // until the user dismisses the wizard.
            using var registration = cancellationToken.Register(() =>
            {
                // Cannot force-kill an elevated child — report cancellation
                // without killing. The elevated process may still be running.
            });

            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The caller cancelled. We cannot kill the elevated process,
                // but we report the cancellation. The ExitCode may or may not
                // be available depending on whether the process exits after
                // our wait is cancelled.
                var exitCode = process.HasExited ? process.ExitCode : (int?)null;
                process.Dispose();
                return new ElevatedRunResult(
                    ExitCode: exitCode,
                    FailureReason: "Setup was cancelled by the user while waiting for the installer",
                    WasCancelled: true,
                    RebootRequired: false);
            }
            finally
            {
                if (!process.HasExited)
                {
                    // The process is still running (e.g., a long-running wizard).
                    // We've been cancelled but cannot kill an elevated child.
                    // Dispose will not kill it.
                }
            }

            var finalExitCode = process.ExitCode;
            var reboot = finalExitCode == 3010;

            process.Dispose();

            return new ElevatedRunResult(
                ExitCode: finalExitCode,
                FailureReason: null,
                WasCancelled: false,
                RebootRequired: reboot);
        }

        private static bool TryKill(Process process)
        {
            try
            {
                if (process.HasExited)
                    return true;

                process.Kill(entireProcessTree: true);
                return true;
            }
            catch (InvalidOperationException)
            {
                // The process exited between the check and the kill. There is
                // nothing left running.
                return true;
            }
            catch (Exception ex)
            {
                // The OS refused to terminate it — an access-denied child, or
                // a process that has already re-parented. The child is still
                // running. This is reported rather than swallowed, because a
                // surviving child accumulates: the collector runs once per
                // cycle, so an unkillable child would leak one process per
                // cycle with nothing in the log to show for it.
                DiagnosticLog.Shared.Warning(
                    "process",
                    "kill",
                    "failed",
                    detail: DiagnosticDetail.Describe(ex));
                return false;
            }
        }

        /// <summary>
        /// Returns a reader's result, or an empty string when the reader
        /// faulted.
        /// </summary>
        private static string SafeResult(Task<string> reader)
        {
            try
            {
                return reader.IsCompletedSuccessfully ? reader.Result : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}

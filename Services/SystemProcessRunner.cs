using System;
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

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

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

            var exited = process.WaitForExit((int)timeout.TotalMilliseconds);

            if (!exited)
            {
                TryKill(process);

                // Give the readers a moment to observe the closed pipes, then
                // stop waiting on them regardless.
                await Task.WhenAny(
                    Task.WhenAll(stdoutTask, stderrTask),
                    Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None)).ConfigureAwait(false);

                return new ProcessRunResult(
                    -1,
                    SafeResult(stdoutTask),
                    SafeResult(stderrTask),
                    TimedOut: true);
            }

            // The process has exited, so both pipes are closed and the reads
            // complete. Await them so the full output is captured.
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

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
        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process exited between the check and the kill, or the
                // OS refused to terminate it. Either way there is nothing
                // further to do: the handle is released by the using block.
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

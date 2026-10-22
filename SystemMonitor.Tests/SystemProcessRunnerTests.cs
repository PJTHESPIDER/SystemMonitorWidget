using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the real process runner.
///
/// These start real child processes, but only trivial ones: the .NET host
/// itself, echoing text. No NVIDIA driver and no NVIDIA hardware is
/// involved.
/// </summary>
public class SystemProcessRunnerTests
{
    /// <summary>
    /// The shell used to run a trivial command. It exists on every Windows
    /// machine and takes no arguments beyond the ones under test.
    /// </summary>
    private const string Shell = "cmd.exe";

    private static string Quote(string argument) => $"\"{argument}\"";

    [Fact]
    public async Task CapturesStandardOutput()
    {
        var runner = new SystemProcessRunner();

        var result = await runner.RunAsync(
            Shell,
            "/c echo hello-from-the-runner",
            TimeSpan.FromSeconds(30));

        Assert.True(result.Succeeded, result.StandardError);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("hello-from-the-runner", result.StandardOutput);
    }

    [Fact]
    public async Task ReportsANonZeroExitCode()
    {
        var runner = new SystemProcessRunner();

        var result = await runner.RunAsync(
            Shell,
            "/c exit 3",
            TimeSpan.FromSeconds(30));

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task ReportsAMissingExecutableWithoutThrowing()
    {
        var runner = new SystemProcessRunner();

        var result = await runner.RunAsync(
            @"C:\definitely\not\here\nvidia-smi.exe",
            "--query-gpu=uuid",
            TimeSpan.FromSeconds(30));

        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task BoundsACommandThatRunsTooLong()
    {
        var runner = new SystemProcessRunner();

        var started = Stopwatch.StartNew();
        var result = await runner.RunAsync(
            Shell,
            "/c ping -n 60 127.0.0.1 > nul",
            TimeSpan.FromSeconds(2));
        started.Stop();

        // The runner must return on its own rather than waiting for the
        // child. A child that ignores the kill would still be visible here
        // as a long wait.
        Assert.True(result.TimedOut);
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(30), $"waited {started.Elapsed}");
    }

    [Fact]
    public async Task DoesNotDeadlockWhenTheChildFillsBothPipes()
    {
        // A child that writes far more to stdout and stderr than a pipe
        // buffer holds. A runner that reads one stream to completion before
        // the other would block here forever.
        //
        // The order of the two loops is what gives the test its teeth. The
        // child fills stderr *first*, while a sequential runner would still
        // be blocked reading stdout. The stderr pipe then fills up and the
        // child blocks writing to it, so it never reaches the stdout writes
        // that would release the reader. A sequential reader therefore never
        // finishes, while a concurrent reader drains both and returns.
        //
        // The command is written to a batch file rather than passed on the
        // command line, because cmd.exe treats "&" as a command separator,
        // which would split the two loops apart.
        var script = Path.Combine(Path.GetTempPath(), $"fill-pipes-{Guid.NewGuid():N}.cmd");
        await File.WriteAllTextAsync(script, string.Join(Environment.NewLine, new[]
        {
            "@echo off",
            "echo stdout-head",
            "for /L %%i in (1,1,20000) do @echo stderr-line-%%i-padding-padding-padding 1>&2",
            "for /L %%i in (1,1,20000) do @echo stdout-line-%%i-padding-padding-padding",
        }));

        try
        {
            var runner = new SystemProcessRunner();

            var started = Stopwatch.StartNew();
            var result = await runner.RunAsync(
                Shell,
                $"/c \"{script}\"",
                TimeSpan.FromSeconds(30));
            started.Stop();

            Assert.False(result.TimedOut, "the child should not have been killed");
            Assert.True(result.Succeeded, result.StandardError);
            Assert.Equal(0, result.ExitCode);

            // Both streams must be captured in full, including the last line
            // of each flood, which is only reachable if neither pipe was
            // left unread.
            Assert.Contains("stdout-head", result.StandardOutput);
            Assert.Contains("stdout-line-20000", result.StandardOutput);
            Assert.Contains("stderr-line-20000", result.StandardError);

            // A sequential reader deadlocks here and is only released by the
            // timeout. The concurrent reader finishes in a couple of seconds,
            // so a wall-clock bound catches a regression to sequential reads.
            Assert.True(
                started.Elapsed < TimeSpan.FromSeconds(20),
                $"took {started.Elapsed}, which suggests the streams were read sequentially");
        }
        finally
        {
            try { File.Delete(script); } catch { /* best effort */ }
        }
    }
}

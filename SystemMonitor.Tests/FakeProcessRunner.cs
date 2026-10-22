using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System_Monitor.Services;

namespace SystemMonitor.Tests;

/// <summary>
/// A fake <see cref="IProcessRunner"/>, so the nvidia-smi fallback can be
/// tested without an NVIDIA driver and without spawning real processes.
///
/// The fake records every invocation, so a test can assert both the parsed
/// result and the *number* of processes that were started — which is how
/// the "one invocation per cycle" and "no invocation when the backend
/// covered the metric" requirements are verified.
/// </summary>
public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Queue<ProcessRunResult> _results = new();

    public FakeProcessRunner(params ProcessRunResult[] results)
    {
        foreach (var result in results)
            _results.Enqueue(result);
    }

    /// <summary>One entry per invocation, in order.</summary>
    public List<FakeInvocation> Invocations { get; } = new();

    /// <summary>
    /// The result returned when the queue is empty. Defaults to a successful
    /// run with no output, i.e. "nvidia-smi reported nothing".
    /// </summary>
    public ProcessRunResult DefaultResult { get; set; } = new(0, string.Empty, string.Empty, false);

    public Task<ProcessRunResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Invocations.Add(new FakeInvocation(fileName, arguments, timeout));

        var result = _results.Count > 0 ? _results.Dequeue() : DefaultResult;

        return Task.FromResult(result);
    }

    /// <summary>Queues a successful run that writes <paramref name="stdout"/>.</summary>
    public FakeProcessRunner WithOutput(string stdout)
    {
        _results.Enqueue(new ProcessRunResult(0, stdout, string.Empty, false));
        return this;
    }

    /// <summary>Queues a run that exits with <paramref name="exitCode"/>.</summary>
    public FakeProcessRunner WithExitCode(int exitCode, string stderr = "")
    {
        _results.Enqueue(new ProcessRunResult(exitCode, string.Empty, stderr, false));
        return this;
    }

    /// <summary>Queues a run that exceeds its timeout.</summary>
    public FakeProcessRunner WithTimeout()
    {
        _results.Enqueue(new ProcessRunResult(-1, string.Empty, string.Empty, true));
        return this;
    }
}

/// <summary>One recorded call to the fake runner.</summary>
public sealed record FakeInvocation(string FileName, string Arguments, TimeSpan Timeout);

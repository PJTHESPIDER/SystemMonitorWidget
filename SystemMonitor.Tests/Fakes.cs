using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System_Monitor.Models;
using System_Monitor.Services;

namespace SystemMonitor.Tests;

/// <summary>
/// Shared fakes used by multiple test classes, so they don't need to
/// duplicate or nest helper types.
/// </summary>

/// <summary>
/// A fake <see cref="ITrustedDownloader"/> that returns pre-queued results.
/// Records every invocation for assertions.
/// </summary>
internal sealed class FakeTrustedDownloader : ITrustedDownloader
{
    private readonly Queue<PawnIoDownloadResult> _results = new();

    public FakeTrustedDownloader(params PawnIoDownloadResult[] results)
    {
        foreach (var r in results)
            _results.Enqueue(r);
    }

    public string? LastUrl { get; private set; }
    public string? LastExpectedHash { get; private set; }
    public SignerValidationPolicy? LastSignerPolicy { get; private set; }
    public string? LastExpectedSignerSubject { get; private set; }
    public int InvocationCount { get; private set; }

    /// <summary>
    /// Queues a successful verified download result pointing to a real temp file.
    /// </summary>
    public FakeTrustedDownloader Verified(string filePath = null)
    {
        _results.Enqueue(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.Verified,
            FilePath = filePath ?? Path.GetTempFileName(),
            BytesDownloaded = 1024,
            CompletedAt = DateTimeOffset.UtcNow,
            WasCancelled = false,
        });
        return this;
    }

    /// <summary>
    /// Queues a download error result.
    /// </summary>
    public FakeTrustedDownloader DownloadError(string reason = "Network unreachable")
    {
        _results.Enqueue(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.DownloadError,
            FailureReason = reason,
            CompletedAt = DateTimeOffset.UtcNow,
            WasCancelled = false,
        });
        return this;
    }

    /// <summary>
    /// Queues a hash mismatch result.
    /// </summary>
    public FakeTrustedDownloader HashMismatch(string reason = "SHA-256 mismatch")
    {
        _results.Enqueue(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.HashMismatch,
            FailureReason = reason,
            CompletedAt = DateTimeOffset.UtcNow,
            WasCancelled = false,
        });
        return this;
    }

    /// <summary>
    /// Queues a signature rejection result.
    /// </summary>
    public FakeTrustedDownloader SignatureRejected(string reason = "Signer mismatch")
    {
        _results.Enqueue(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.SignatureRejected,
            FailureReason = reason,
            CompletedAt = DateTimeOffset.UtcNow,
            WasCancelled = false,
        });
        return this;
    }

    /// <summary>
    /// Queues a cancelled result.
    /// </summary>
    public FakeTrustedDownloader Cancelled(string reason = "Download cancelled by user")
    {
        _results.Enqueue(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.Cancelled,
            FailureReason = reason,
            CompletedAt = DateTimeOffset.UtcNow,
            WasCancelled = true,
        });
        return this;
    }

    public Task<PawnIoDownloadResult> DownloadAndVerifyAsync(
        string url,
        string expectedSha256,
        SignerValidationPolicy signerPolicy,
        string? expectedSignerSubject,
        CancellationToken cancellationToken = default)
    {
        LastUrl = url;
        LastExpectedHash = expectedSha256;
        LastSignerPolicy = signerPolicy;
        LastExpectedSignerSubject = expectedSignerSubject;
        InvocationCount++;

        var result = _results.Count > 0 ? _results.Dequeue() : new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.DownloadError,
            FailureReason = "No more queued results",
            CompletedAt = DateTimeOffset.UtcNow,
        };

        return Task.FromResult(result);
    }
}

/// <summary>
/// A fake <see cref="IPawnIoInstallationChecker"/> that returns pre-queued states in order.
/// </summary>
internal sealed class FakeInstallationChecker : IPawnIoInstallationChecker
{
    private readonly Queue<PawnIoInstallationState> _states = new();

    public FakeInstallationChecker(params PawnIoInstallationState[] states)
    {
        foreach (var s in states)
            _states.Enqueue(s);
    }

    public int CheckCount { get; private set; }

    public PawnIoInstallationState Check()
    {
        CheckCount++;
        if (_states.Count > 0)
            return _states.Dequeue();
        // Default: not installed
        return new PawnIoInstallationState
        {
            Status = PawnIoInstallationStatus.NotInstalled,
            Version = null,
            CheckedAt = DateTimeOffset.UtcNow,
        };
    }
}

/// <summary>
/// A fake trusted downloader that delays before returning, simulating a slow download.
/// </summary>
internal sealed class SlowFakeTrustedDownloader : ITrustedDownloader
{
    private readonly int _delayMs;

    public int InvocationCount { get; private set; }

    public SlowFakeTrustedDownloader(int delayMs)
    {
        _delayMs = delayMs;
    }

    public async Task<PawnIoDownloadResult> DownloadAndVerifyAsync(
        string url,
        string expectedSha256,
        SignerValidationPolicy signerPolicy,
        string? expectedSignerSubject,
        CancellationToken cancellationToken = default)
    {
        InvocationCount++;
        await Task.Delay(_delayMs, cancellationToken);
        return new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.Verified,
            FilePath = Path.GetTempFileName(),
            CompletedAt = DateTimeOffset.UtcNow,
        };
    }
}

/// <summary>
/// A fake trusted downloader that blocks on a TaskCompletionSource until
/// the test explicitly releases it, enabling deterministic concurrency test
/// synchronization without time-based delays.
/// </summary>
internal sealed class SignalControlledDownloader : ITrustedDownloader
{
    private readonly TaskCompletionSource<PawnIoDownloadResult?> _downloadTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Completes the download with a verified result.
    /// </summary>
    public void ReleaseDownload(string filePath = null)
    {
        _downloadTcs.SetResult(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.Verified,
            FilePath = filePath ?? Path.GetTempFileName(),
            CompletedAt = DateTimeOffset.UtcNow,
        });
    }

    /// <summary>
    /// Completes the download as cancelled.
    /// </summary>
    public void ReleaseCancelled()
    {
        _downloadTcs.SetResult(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.Cancelled,
            FailureReason = "Cancelled by test",
            CompletedAt = DateTimeOffset.UtcNow,
            WasCancelled = true,
        });
    }

    public int InvocationCount { get; private set; }

    public Task<PawnIoDownloadResult> DownloadAndVerifyAsync(
        string url,
        string expectedSha256,
        SignerValidationPolicy signerPolicy,
        string? expectedSignerSubject,
        CancellationToken cancellationToken = default)
    {
        InvocationCount++;
        var tcs = _downloadTcs;
        if (cancellationToken.IsCancellationRequested && !tcs.Task.IsCompleted)
        {
            tcs.SetResult(new PawnIoDownloadResult
            {
                Outcome = PawnIoDownloadOutcome.Cancelled,
                FailureReason = "Cancelled by test",
                CompletedAt = DateTimeOffset.UtcNow,
                WasCancelled = true,
            });
        }
        return tcs.Task.ContinueWith(t => t.Result ?? new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.DownloadError,
            FailureReason = "Download TCS was not completed",
            CompletedAt = DateTimeOffset.UtcNow,
        }, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}

/// <summary>
/// A fake sensor source that simulates reinitialization behavior for tests.
/// Records all calls and supports configurable outcomes.
/// </summary>
internal sealed class FakeSensorSource : ILhmSensorSource
{
    private readonly object _lock = new();
    private bool _disposed;

    public FakeSensorSource(ReinitializeResult? reinitResult = null)
    {
        _reinitResult = reinitResult ?? new ReinitializeResult
        {
            Outcome = ReinitializationOutcome.Success,
            WasAvailableBefore = true,
            CompletedAt = DateTimeOffset.UtcNow,
        };
        IsAvailable = true;
    }

    public bool IsAvailable { get; set; }
    public string? UnavailableReason { get; set; }
    public bool IsDisposedValue => _disposed;
    public int ReadCount { get; private set; }
    public int ReinitCount { get; private set; }

    private readonly ReinitializeResult _reinitResult;
    public IReadOnlyList<LhmDeviceDescriptor>? Devices { get; set; }

    public IReadOnlyList<LhmDeviceDescriptor> Read()
    {
        lock (_lock)
        {
            ReadCount++;
            if (_disposed) return Array.Empty<LhmDeviceDescriptor>();
        }
        return Devices ?? Array.Empty<LhmDeviceDescriptor>();
    }

    public Task<ReinitializeResult> ReinitializeAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            ReinitCount++;
            if (_disposed)
            {
                return Task.FromResult(new ReinitializeResult
                {
                    Outcome = ReinitializationOutcome.DisposedDuringReinit,
                    WasAvailableBefore = IsAvailable,
                    CompletedAt = DateTimeOffset.UtcNow,
                    FailureReason = "Source was disposed during reinitialization",
                });
            }

            if (ct.IsCancellationRequested)
            {
                return Task.FromResult(new ReinitializeResult
                {
                    Outcome = ReinitializationOutcome.Cancelled,
                    WasAvailableBefore = IsAvailable,
                    CompletedAt = DateTimeOffset.UtcNow,
                    FailureReason = "Reinitialization cancelled before close",
                });
            }
        }
        return Task.FromResult(_reinitResult);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
        }
    }
}

/// <summary>
/// A fake installation checker whose Check() throws, to simulate check failures.
/// </summary>
internal sealed class ThrowingInstallationChecker : IPawnIoInstallationChecker
{
    private readonly Exception _exception;

    public ThrowingInstallationChecker(Exception ex)
    {
        _exception = ex;
    }

    public PawnIoInstallationState Check() => throw _exception;
}

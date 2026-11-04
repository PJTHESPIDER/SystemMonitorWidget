using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Unit tests for PawnIoInstaller — the download → verify → launch elevator.
/// Uses fakes for ITrustedDownloader and IProcessRunner. No live network,
/// no real processes, no elevation.
/// </summary>
public sealed class PawnIoInstallerTests
{
    private static TrustedPawnIoInstaller MakeDescriptor() => new()
    {
        Version = "2.2.0",
        Url = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe",
        ExpectedSha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032",
        ExpectedSignerSubject = "E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU",
        SignerPolicy = SignerValidationPolicy.RequireSigned,
    };

    private static string TempFilePath() =>
        Path.Combine(Path.GetTempPath(), $"PawnIO_test_{Guid.NewGuid():N}.exe");

    [Fact]
    public async Task DownloadFails_ReturnsLaunchFailed()
    {
        var downloader = new FakeTrustedDownloader(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.DownloadError,
            FailureReason = "Network unreachable",
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var runner = new FakeProcessRunner();
        var installer = new PawnIoInstaller(MakeDescriptor(), downloader, runner);

        var result = await installer.LaunchAsync(string.Empty);

        Assert.Equal(PawnIoInstallerOutcome.LaunchFailed, result.Outcome);
        Assert.Equal(PawnIoDownloadOutcome.DownloadError, result.DownloadOutcome);
        Assert.Equal("Network unreachable", result.FailureReason);
        Assert.Null(result.ExitCode);
        Assert.False(result.RebootRequired);
        Assert.False(result.WasCancelled);
    }

    [Fact]
    public async Task HashMismatch_ReturnsIndeterminate()
    {
        var downloader = new FakeTrustedDownloader(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.HashMismatch,
            FailureReason = "SHA-256 mismatch",
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var runner = new FakeProcessRunner();
        var installer = new PawnIoInstaller(MakeDescriptor(), downloader, runner);

        var result = await installer.LaunchAsync(string.Empty);

        Assert.Equal(PawnIoInstallerOutcome.Indeterminate, result.Outcome);
        Assert.False(runner.ElevatedInvocations.Count > 0, "Installer must not be launched when verification fails");
    }

    [Fact]
    public async Task SignatureRejected_ReturnsIndeterminate()
    {
        var downloader = new FakeTrustedDownloader(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.SignatureRejected,
            FailureReason = "Signer mismatch",
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var runner = new FakeProcessRunner();
        var installer = new PawnIoInstaller(MakeDescriptor(), downloader, runner);

        var result = await installer.LaunchAsync(string.Empty);

        Assert.Equal(PawnIoInstallerOutcome.Indeterminate, result.Outcome);
        Assert.False(runner.ElevatedInvocations.Count > 0, "Installer must not be launched when signature is rejected");
    }

    [Fact]
    public async Task InstallerLaunchFailure_ReturnsLaunchFailed()
    {
        var tempFile = TempFilePath();
        File.WriteAllText(tempFile, "dummy");

        var downloader = new FakeTrustedDownloader(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.Verified,
            FilePath = tempFile,
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(
                ExitCode: null,
                FailureReason: "File not found",
                WasCancelled: false,
                RebootRequired: false));

        var installer = new PawnIoInstaller(MakeDescriptor(), downloader, runner);

        var result = await installer.LaunchAsync(string.Empty);

        Assert.Equal(PawnIoInstallerOutcome.LaunchFailed, result.Outcome);
        Assert.Equal("File not found", result.FailureReason);
        Assert.False(result.RebootRequired);
        Assert.False(result.WasCancelled);

        // Verify the downloaded file was cleaned up
        Assert.False(File.Exists(tempFile), "Downloaded file must be cleaned up after launch attempt");

        File.Delete(tempFile); // Safety net in case cleanup failed
    }

    [Fact]
    public async Task InstallerExitZero_ReturnsExited()
    {
        var tempFile = TempFilePath();
        File.WriteAllText(tempFile, "dummy");

        var downloader = new FakeTrustedDownloader(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.Verified,
            FilePath = tempFile,
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(0, null, false, false));

        var installer = new PawnIoInstaller(MakeDescriptor(), downloader, runner);

        var result = await installer.LaunchAsync(string.Empty);

        Assert.Equal(PawnIoInstallerOutcome.Exited, result.Outcome);
        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.FailureReason);
        Assert.False(result.RebootRequired);
        Assert.False(result.WasCancelled);

        // Verify the downloaded file was cleaned up
        Assert.False(File.Exists(tempFile), "Downloaded file must be cleaned up after successful launch");
    }

    [Fact]
    public async Task InstallerExit3010_ReturnsRebootRequired()
    {
        var tempFile = TempFilePath();
        File.WriteAllText(tempFile, "dummy");

        var downloader = new FakeTrustedDownloader(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.Verified,
            FilePath = tempFile,
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(3010, null, false, true));

        var installer = new PawnIoInstaller(MakeDescriptor(), downloader, runner);

        var result = await installer.LaunchAsync(string.Empty);

        Assert.Equal(PawnIoInstallerOutcome.RebootRequired, result.Outcome);
        Assert.Equal(3010, result.ExitCode);
        Assert.True(result.RebootRequired);
        Assert.False(result.WasCancelled);

        Assert.False(File.Exists(tempFile), "Downloaded file must be cleaned up");
    }

    [Fact]
    public async Task InstallerExit1602_ReturnsCancelled()
    {
        var tempFile = TempFilePath();
        File.WriteAllText(tempFile, "dummy");

        var downloader = new FakeTrustedDownloader(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.Verified,
            FilePath = tempFile,
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(1602, "User cancelled the installer", false, false));

        var installer = new PawnIoInstaller(MakeDescriptor(), downloader, runner);

        var result = await installer.LaunchAsync(string.Empty);

        Assert.Equal(PawnIoInstallerOutcome.Cancelled, result.Outcome);
        Assert.Equal(1602, result.ExitCode);
        Assert.True(result.WasCancelled);
        Assert.False(result.RebootRequired);
    }

    [Fact]
    public async Task InstallerExitNonZeroNonStandard_ReturnsIndeterminate()
    {
        var tempFile = TempFilePath();
        File.WriteAllText(tempFile, "dummy");

        var downloader = new FakeTrustedDownloader(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.Verified,
            FilePath = tempFile,
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(1, "Unexpected error", false, false));

        var installer = new PawnIoInstaller(MakeDescriptor(), downloader, runner);

        var result = await installer.LaunchAsync(string.Empty);

        Assert.Equal(PawnIoInstallerOutcome.Indeterminate, result.Outcome);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("exited with code 1", result.FailureReason!, StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(tempFile), "Downloaded file must be cleaned up");
    }

    [Fact]
    public async Task UserCancelledDownload_ReturnsCancelled()
    {
        var downloader = new FakeTrustedDownloader(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.Cancelled,
            FailureReason = "Download cancelled by user",
            CompletedAt = DateTimeOffset.UtcNow,
            WasCancelled = true,
        });

        var runner = new FakeProcessRunner();
        var installer = new PawnIoInstaller(MakeDescriptor(), downloader, runner);

        var result = await installer.LaunchAsync(string.Empty);

        Assert.Equal(PawnIoInstallerOutcome.Cancelled, result.Outcome);
        Assert.True(result.WasCancelled);
        Assert.False(runner.ElevatedInvocations.Count > 0, "Installer must not be launched when download is cancelled");
    }

    [Fact]
    public async Task UacDeclined_ReturnsCancelled()
    {
        var tempFile = TempFilePath();
        File.WriteAllText(tempFile, "dummy");

        var downloader = new FakeTrustedDownloader(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.Verified,
            FilePath = tempFile,
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(
                ExitCode: null,
                FailureReason: "Elevation denied or unavailable: UAC declined",
                WasCancelled: true,
                RebootRequired: false));

        var installer = new PawnIoInstaller(MakeDescriptor(), downloader, runner);

        var result = await installer.LaunchAsync(string.Empty);

        Assert.Equal(PawnIoInstallerOutcome.Cancelled, result.Outcome);
        Assert.True(result.WasCancelled);
        Assert.False(result.RebootRequired);

        Assert.False(File.Exists(tempFile), "Downloaded file must be cleaned up");
    }

    [Fact]
    public async Task InstallerLaunchException_ReturnsLaunchFailed()
    {
        var tempFile = TempFilePath();
        File.WriteAllText(tempFile, "dummy");

        var downloader = new FakeTrustedDownloader(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.Verified,
            FilePath = tempFile,
            CompletedAt = DateTimeOffset.UtcNow,
        });

        // Use a fake that throws on elevated launch
        var runner = new ThrowingElevatedProcessRunner();

        var installer = new PawnIoInstaller(MakeDescriptor(), downloader, runner);

        var result = await installer.LaunchAsync(string.Empty);

        Assert.Equal(PawnIoInstallerOutcome.LaunchFailed, result.Outcome);
        Assert.NotNull(result.FailureReason);

        Assert.False(File.Exists(tempFile), "Downloaded file must be cleaned up even when launch throws");
    }

    [Fact]
    public async Task DownloadCancelledDuringDownload_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Already cancelled before calling

        var downloader = new FakeTrustedDownloader(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.Cancelled,
            FailureReason = "Operation cancelled",
            CompletedAt = DateTimeOffset.UtcNow,
            WasCancelled = true,
        });

        var runner = new FakeProcessRunner();
        var installer = new PawnIoInstaller(MakeDescriptor(), downloader, runner);

        var result = await installer.LaunchAsync(string.Empty, cts.Token);

        Assert.Equal(PawnIoInstallerOutcome.Cancelled, result.Outcome);
        Assert.True(result.WasCancelled);
    }

    [Fact]
    public async Task DoesNotExecuteInstallerWhenHashMismatch()
    {
        var downloader = new FakeTrustedDownloader(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.HashMismatch,
            FailureReason = "SHA-256 mismatch",
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var runner = new FakeProcessRunner();
        var installer = new PawnIoInstaller(MakeDescriptor(), downloader, runner);

        await installer.LaunchAsync(string.Empty);

        Assert.Empty(runner.ElevatedInvocations);
    }

    [Fact]
    public async Task DownloadsToControlledLocation()
    {
        var tempFile = TempFilePath();
        File.WriteAllText(tempFile, "dummy");

        var downloader = new FakeTrustedDownloader(new PawnIoDownloadResult
        {
            Outcome = PawnIoDownloadOutcome.Verified,
            FilePath = tempFile,
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(0, null, false, false));

        var installer = new PawnIoInstaller(MakeDescriptor(), downloader, runner);

        await installer.LaunchAsync(string.Empty);

        // The downloader was called with the descriptor's URL and hash
        Assert.Equal(MakeDescriptor().Url, downloader.LastUrl);
        Assert.Equal(MakeDescriptor().ExpectedSha256, downloader.LastExpectedHash);
        Assert.Equal(1, downloader.InvocationCount);
    }
}

/// <summary>
/// A fake process runner whose RunElevatedAsync throws an exception,
/// simulating a process launch failure.
/// </summary>
internal sealed class ThrowingElevatedProcessRunner : IProcessRunner
{
    public Task<ProcessRunResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new ProcessRunResult(0, string.Empty, string.Empty, false));

    public Task<ElevatedRunResult> RunElevatedAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("Simulated launch failure");
}

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Unit tests for CpuTemperatureSetupCoordinator — the state machine that
/// serializes setup across processes via a named mutex.
///
/// Uses fakes for ITrustedDownloader, IProcessRunner, IPawnIoInstallationChecker,
/// and ILhmSensorSource. No live registry, network, elevation, or driver
/// installation.
/// </summary>
public sealed class CpuTemperatureSetupCoordinatorTests
{
    private static TrustedPawnIoInstaller MakeDescriptor() => new()
    {
        Version = "2.2.0",
        Url = "https://github.com/namazso/PawnIO.Setup/releases/download/2.2.0/PawnIO_setup.exe",
        ExpectedSha256 = "1F519A22E47187F70A1379A48CA604981C4FCF694F4E65B734AAA74A9FBA3032",
        ExpectedSignerSubject = "E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU",
        SignerPolicy = SignerValidationPolicy.RequireSigned,
    };

    private static PawnIoInstallationState NotInstalled() => new()
    {
        Status = PawnIoInstallationStatus.NotInstalled,
        Version = null,
        CheckedAt = DateTimeOffset.UtcNow,
    };

    private static PawnIoInstallationState InstalledCompatible() => new()
    {
        Status = PawnIoInstallationStatus.Installed,
        Version = "2.2.0",
        CheckedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task AlreadyConfigured_PreCheckDetectsInstalledAndCompatible()
    {
        var state = InstalledCompatible();
        // Pre-check returns AlreadyConfigured; locked-check also returns it
        var checker = new FakeInstallationChecker(state, state);
        var coordinator = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(),
            new FakeTrustedDownloader(),
            new FakeProcessRunner(),
            checker,
            null);

        var result = await coordinator.RequestSetupAsync();

        Assert.Equal(PawnIoSetupOutcome.AlreadyConfigured, result.Outcome);
        Assert.Equal("2.2.0", result.Compatibility!.DetectedVersion);
        Assert.Null(result.FailureReason);
        Assert.False(result.RebootRequired);
    }

    [Fact]
    public async Task DownloadFails_ReturnsDownloadFailed()
    {
        var checker = new FakeInstallationChecker(); // NotInstalled pre + locked checks
        var downloader = new FakeTrustedDownloader().DownloadError("Network unreachable");
        var runner = new FakeProcessRunner();
        var coordinator = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(), downloader, runner, checker, null);

        var result = await coordinator.RequestSetupAsync();

        Assert.Equal(PawnIoSetupOutcome.DownloadFailed, result.Outcome);
        Assert.Equal(PawnIoSetupFailureStage.Download, result.FailureStage);
        Assert.Contains("Network unreachable", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(runner.ElevatedInvocations);
    }

    [Fact]
    public async Task HashMismatch_ReturnsVerificationFailed()
    {
        var checker = new FakeInstallationChecker();
        var downloader = new FakeTrustedDownloader().HashMismatch("SHA-256 mismatch");
        var runner = new FakeProcessRunner();
        var coordinator = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(), downloader, runner, checker, null);

        var result = await coordinator.RequestSetupAsync();

        Assert.Equal(PawnIoSetupOutcome.VerificationFailed, result.Outcome);
        Assert.Equal(PawnIoSetupFailureStage.Verification, result.FailureStage);
        Assert.Empty(runner.ElevatedInvocations);
    }

    [Fact]
    public async Task SignatureRejected_ReturnsVerificationFailed()
    {
        var checker = new FakeInstallationChecker();
        var downloader = new FakeTrustedDownloader().SignatureRejected("Signer mismatch");
        var runner = new FakeProcessRunner();
        var coordinator = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(), downloader, runner, checker, null);

        var result = await coordinator.RequestSetupAsync();

        Assert.Equal(PawnIoSetupOutcome.VerificationFailed, result.Outcome);
        Assert.Equal(PawnIoSetupFailureStage.Verification, result.FailureStage);
        Assert.Empty(runner.ElevatedInvocations);
    }

    [Fact]
    public async Task UserCancelledDownload_ReturnsCancelled()
    {
        var checker = new FakeInstallationChecker();
        var downloader = new FakeTrustedDownloader().Cancelled("Download cancelled by user");
        var runner = new FakeProcessRunner();
        var coordinator = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(), downloader, runner, checker, null);

        var result = await coordinator.RequestSetupAsync();

        Assert.Equal(PawnIoSetupOutcome.Cancelled, result.Outcome);
        Assert.NotNull(result.FailureReason);
    }

    [Fact]
    public async Task UacDeclined_ReturnsCancelled()
    {
        var tempFile = Path.GetTempFileName();
        var checker = new FakeInstallationChecker(
            // Pre-check: not installed
            NotInstalled(),
            // Locked-check: not installed
            NotInstalled(),
            // Post-check: still not installed
            NotInstalled());

        var downloader = new FakeTrustedDownloader().Verified(tempFile);

        var runner = new FakeProcessRunner().WithElevatedResult(new ElevatedRunResult(
            ExitCode: null,
            FailureReason: "User declined the UAC elevation prompt",
            WasCancelled: true,
            RebootRequired: false));

        var coordinator = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(), downloader, runner, checker, null);

        var result = await coordinator.RequestSetupAsync();

        Assert.Equal(PawnIoSetupOutcome.Cancelled, result.Outcome);
        Assert.NotNull(result.FailureReason);
        Assert.False(File.Exists(tempFile));
    }

    [Fact]
    public async Task InstallerExitZero_PostCheckNotInstalled_ReturnsDependencyStillMissing()
    {
        var tempFile = Path.GetTempFileName();
        var checker = new FakeInstallationChecker(
            // Pre-check: not installed
            NotInstalled(),
            // Locked-check: not installed
            NotInstalled(),
            // Post-check: still not installed
            NotInstalled());

        var downloader = new FakeTrustedDownloader().Verified(tempFile);
        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(0, null, false, false));

        var coordinator = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(), downloader, runner, checker, null);

        var result = await coordinator.RequestSetupAsync();

        Assert.Equal(PawnIoSetupOutcome.DependencyStillMissing, result.Outcome);
        Assert.Equal(PawnIoSetupFailureStage.PostInstallCheck, result.FailureStage);
        Assert.Contains("not detected as installed", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallerExitZero_PostCheckCompatible_NoBackend_ReturnsDependencyStillMissing()
    {
        var tempFile = Path.GetTempFileName();
        var checker = new FakeInstallationChecker(
            // Pre-check: not installed
            NotInstalled(),
            // Locked-check: not installed
            NotInstalled(),
            // Post-check: installed and compatible
            InstalledCompatible());

        var downloader = new FakeTrustedDownloader().Verified(tempFile);
        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(0, null, false, false));

        // No sensor source — should report "no sensor source to reinitialize"
        var coordinator = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(), downloader, runner, checker, null);

        var result = await coordinator.RequestSetupAsync();

        Assert.Equal(PawnIoSetupOutcome.DependencyStillMissing, result.Outcome);
        Assert.Equal(PawnIoSetupFailureStage.PostInstallCheck, result.FailureStage);
        Assert.Contains("manual backend restart", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostCheckInstalledCompatible_WithSensorSource_Success()
    {
        var tempFile = Path.GetTempFileName();
        var checker = new FakeInstallationChecker(
            NotInstalled(),
            NotInstalled(),
            InstalledCompatible());

        var downloader = new FakeTrustedDownloader().Verified(tempFile);
        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(0, null, false, false));

        var sensorSource = new FakeSensorSource(new ReinitializeResult
        {
            Outcome = ReinitializationOutcome.Success,
            WasAvailableBefore = false,
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var coordinator = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(), downloader, runner, checker, sensorSource);

        var result = await coordinator.RequestSetupAsync();

        Assert.Equal(PawnIoSetupOutcome.Success, result.Outcome);
        Assert.Equal("2.2.0", result.Compatibility!.DetectedVersion);
        Assert.False(result.RebootRequired);
        Assert.Equal(1, sensorSource.ReinitCount);
    }

    [Fact]
    public async Task PostCheckInstalledCompatible_WithSensorSource_TemperatureUnavailable()
    {
        var tempFile = Path.GetTempFileName();
        var checker = new FakeInstallationChecker(
            NotInstalled(),
            NotInstalled(),
            InstalledCompatible());

        var downloader = new FakeTrustedDownloader().Verified(tempFile);
        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(0, null, false, false));

        var sensorSource = new FakeSensorSource(new ReinitializeResult
        {
            Outcome = ReinitializationOutcome.TemperatureUnavailable,
            WasAvailableBefore = false,
            FailureReason = "No CPU temperature sensors found",
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var coordinator = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(), downloader, runner, checker, sensorSource);

        var result = await coordinator.RequestSetupAsync();

        Assert.Equal(PawnIoSetupOutcome.DependencyInstalledSensorUnavailable, result.Outcome);
        Assert.Contains("sensor", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostCheckInstalledCompatible_WithSensorSource_InitFailed()
    {
        var tempFile = Path.GetTempFileName();
        var checker = new FakeInstallationChecker(
            NotInstalled(),
            NotInstalled(),
            InstalledCompatible());

        var downloader = new FakeTrustedDownloader().Verified(tempFile);
        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(0, null, false, false));

        var sensorSource = new FakeSensorSource(new ReinitializeResult
        {
            Outcome = ReinitializationOutcome.InitializationFailed,
            WasAvailableBefore = true,
            FailureReason = "Driver initialization failed",
            CompletedAt = DateTimeOffset.UtcNow,
        });

        var coordinator = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(), downloader, runner, checker, sensorSource);

        var result = await coordinator.RequestSetupAsync();

        Assert.Equal(PawnIoSetupOutcome.DependencyInstalledSensorUnavailable, result.Outcome);
        Assert.Contains("reinit failed", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallerExit3010_RebootRequiredPropagated()
    {
        var tempFile = Path.GetTempFileName();
        var checker = new FakeInstallationChecker(
            NotInstalled(),
            NotInstalled(),
            InstalledCompatible());

        var downloader = new FakeTrustedDownloader().Verified(tempFile);
        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(3010, null, false, true));

        var coordinator = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(), downloader, runner, checker, null);

        var result = await coordinator.RequestSetupAsync();

        Assert.True(result.RebootRequired);
    }

    [Fact]
    public async Task InstallerExit1602_ReturnsCancelled()
    {
        var tempFile = Path.GetTempFileName();
        var checker = new FakeInstallationChecker(
            NotInstalled(),
            NotInstalled(),
            NotInstalled());

        var downloader = new FakeTrustedDownloader().Verified(tempFile);
        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(1602, "User cancelled the installer", false, false));

        var coordinator = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(), downloader, runner, checker, null);

        var result = await coordinator.RequestSetupAsync();

        Assert.Equal(PawnIoSetupOutcome.Cancelled, result.Outcome);
        Assert.True(result.FailureReason is not null);
    }

    [Fact]
    public async Task InstallerLaunchFailure_ReturnsLaunchFailed()
    {
        var tempFile = Path.GetTempFileName();
        var checker = new FakeInstallationChecker(
            NotInstalled(),
            NotInstalled(),
            NotInstalled());

        var downloader = new FakeTrustedDownloader().Verified(tempFile);
        // Elevated result with null exit code → LaunchFailed
        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(null, "File not found", false, false));

        var coordinator = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(), downloader, runner, checker, null);

        var result = await coordinator.RequestSetupAsync();

        Assert.Equal(PawnIoSetupOutcome.LaunchFailed, result.Outcome);
        Assert.Equal(PawnIoSetupFailureStage.Launch, result.FailureStage);
    }

    [Fact]
    public async Task InstallerExitNonStandard_PostCheckNotInstalled_ReturnsIndeterminateResult_FallsThroughToDependencyStillMissing()
    {
        var tempFile = Path.GetTempFileName();
        var checker = new FakeInstallationChecker(
            NotInstalled(),
            NotInstalled(),
            NotInstalled()); // Post-check still not installed

        var downloader = new FakeTrustedDownloader().Verified(tempFile);
        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(1, "Unexpected error", false, false));

        var coordinator = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(), downloader, runner, checker, null);

        var result = await coordinator.RequestSetupAsync();

        // The installer returned Indeterminate (exit code 1), not Cancelled or
        // LaunchFailed. The coordinator proceeds to post-check, which finds
        // the dependency still missing.
        Assert.Equal(PawnIoSetupOutcome.DependencyStillMissing, result.Outcome);
        Assert.Equal(PawnIoSetupFailureStage.PostInstallCheck, result.FailureStage);
    }

    [Fact]
    public async Task CancelledWhileWaitingForMutex_ReturnsCancelled()
    {
        var checker = new FakeInstallationChecker(NotInstalled());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var coordinator = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(),
            new FakeTrustedDownloader().Verified(Path.GetTempFileName()),
            new FakeProcessRunner(),
            checker,
            null);

        var result = await coordinator.RequestSetupAsync(cts.Token);

        Assert.Equal(PawnIoSetupOutcome.Cancelled, result.Outcome);
        Assert.Contains("cancelled", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentSetupRequests_SecondReturnsInProgress()
    {
        // Deterministic concurrency test using explicit signals:
        // 1. First request starts and acquires the mutex.
        // 2. A signal confirms the first request owns the mutex.
        // 3. Second request starts — should see mutex held and return InProgress.
        // 4. Release the first request and verify it completes normally.

        var downloader = new SignalControlledDownloader();
        var runner = new FakeProcessRunner().WithElevatedResult(
            new ElevatedRunResult(1, "simulated", false, false));

        // Checker for first request: pre-check (NotInstalled), locked-check (NotInstalled), post-check (NotInstalled)
        var checker = new FakeInstallationChecker(NotInstalled(), NotInstalled(), NotInstalled());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Start first request — it will acquire the mutex and block on the download signal.
        var firstTask = Task.Run(async () =>
        {
            var coord = new CpuTemperatureSetupCoordinator(
                MakeDescriptor(), downloader, runner, checker, null);
            return await coord.RequestSetupAsync(cts.Token);
        });

        // Wait for the first request's download to be invoked (confirms mutex acquisition).
        // The SignalControlledDownloader blocks until ReleaseDownload is called.
        var downloadWait = Task.Delay(TimeSpan.FromSeconds(10));
        while (downloader.InvocationCount == 0 && !downloadWait.IsCompleted)
        {
            await Task.Delay(10);
        }
        Assert.True(downloader.InvocationCount > 0,
            "First request should have invoked download (i.e., acquired the mutex)");

        // Now the first request holds the mutex. Start the second request.
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var secondCoord = new CpuTemperatureSetupCoordinator(
            MakeDescriptor(),
            new FakeTrustedDownloader().Verified(Path.GetTempFileName()),
            new FakeProcessRunner(),
            new FakeInstallationChecker(NotInstalled()),
            null);

        var secondResult = await secondCoord.RequestSetupAsync(cts2.Token);

        Assert.Equal(PawnIoSetupOutcome.InProgress, secondResult.Outcome);
        Assert.Contains("another setup operation", secondResult.FailureReason!,
            StringComparison.OrdinalIgnoreCase);

        // Release the first request's download — it should complete normally.
        downloader.ReleaseDownload();

        // Wait for first request to finish (with bounded timeout as a safety guard).
        await Task.WhenAny(firstTask, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(firstTask.IsCompleted, "First request should complete after download is released");
        var firstResult = await firstTask;
        Assert.NotNull(firstResult);
    }

    [Fact]
    public void MutexName_IsGlobalCrossProcess()
    {
        Assert.Equal(@"Global\SystemMonitor_PawnIoSetup", CpuTemperatureSetupCoordinator.MutexName);
    }

    [Fact]
    public void MutexName_StartsWithGlobalPrefix()
    {
        Assert.True(CpuTemperatureSetupCoordinator.MutexName.StartsWith("Global\\"));
    }
}

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the NVIDIA command-line fallback's process behaviour. Every
/// test drives a fake process runner, so none of them need NVIDIA hardware
/// and none of them start a real process.
/// </summary>
public class NvidiaGpuFallbackTests
{
    private const string SingleRow =
        "GPU-a5e273b9, NVIDIA GeForce RTX 2070 Super, 00000000:01:00.0, 0x1E9110DE, 18, 60, 871, 8192";

    /// <summary>
    /// A locator that resolves to a fixed path, so the fallback always has
    /// an executable to run.
    /// </summary>
    private static NvidiaSmiLocator Locator(string path = @"C:\fake\nvidia-smi.exe") =>
        new(_ => true, new[] { path });

    [Fact]
    public async Task QueriesEveryMetricInOneInvocation()
    {
        var runner = new FakeProcessRunner().WithOutput(SingleRow);
        using var fallback = new NvidiaGpuFallback(runner, Locator());

        var samples = await fallback.ReadAsync();

        Assert.Single(runner.Invocations);
        Assert.Single(samples);

        // One invocation must request identity, utilization, temperature and
        // both memory figures.
        var arguments = runner.Invocations[0].Arguments;
        Assert.Contains("uuid", arguments);
        Assert.Contains("utilization.gpu", arguments);
        Assert.Contains("temperature.gpu", arguments);
        Assert.Contains("memory.used", arguments);
        Assert.Contains("memory.total", arguments);
    }

    [Fact]
    public async Task PassesTheTimeoutToTheProcessRunner()
    {
        var runner = new FakeProcessRunner().WithOutput(SingleRow);
        using var fallback = new NvidiaGpuFallback(runner, Locator(), TimeSpan.FromSeconds(2));

        await fallback.ReadAsync();

        Assert.Equal(TimeSpan.FromSeconds(2), runner.Invocations[0].Timeout);
    }

    [Fact]
    public async Task ReturnsNoSamplesWhenTheExecutableIsMissing()
    {
        var runner = new FakeProcessRunner().WithOutput(SingleRow);
        var locator = new NvidiaSmiLocator(_ => false, new[] { @"C:\fake\nvidia-smi.exe" });
        using var fallback = new NvidiaGpuFallback(runner, locator);

        var samples = await fallback.ReadAsync();

        Assert.Empty(samples);
        // No process was started at all.
        Assert.Empty(runner.Invocations);
    }

    [Fact]
    public async Task ReturnsNoSamplesOnANonZeroExit()
    {
        var runner = new FakeProcessRunner().WithExitCode(2, "Field \"bogus\" is not a valid field to query.");
        using var fallback = new NvidiaGpuFallback(runner, Locator());

        var samples = await fallback.ReadAsync();

        Assert.Empty(samples);
        Assert.Single(runner.Invocations);
    }

    [Fact]
    public async Task ReturnsNoSamplesOnATimeout()
    {
        var runner = new FakeProcessRunner().WithTimeout();
        using var fallback = new NvidiaGpuFallback(runner, Locator());

        var samples = await fallback.ReadAsync();

        Assert.Empty(samples);
    }

    [Fact]
    public async Task CountsItsInvocations()
    {
        var runner = new FakeProcessRunner().WithOutput(SingleRow).WithOutput(SingleRow);
        using var fallback = new NvidiaGpuFallback(runner, Locator());

        await fallback.ReadAsync();
        await fallback.ReadAsync();

        Assert.Equal(2, fallback.InvocationCount);
        Assert.Equal(2, runner.Invocations.Count);
    }

    [Fact]
    public async Task ReadsOneRowPerGpu()
    {
        var output = string.Join(Environment.NewLine, new[]
        {
            "GPU-a, NVIDIA A, 00000000:01:00.0, 0x1E9110DE, 5, 40, 100, 24576",
            "GPU-b, NVIDIA B, 00000000:41:00.0, 0x220410DE, 90, 75, 9000, 10240",
        });

        var runner = new FakeProcessRunner().WithOutput(output);
        using var fallback = new NvidiaGpuFallback(runner, Locator());

        var samples = await fallback.ReadAsync();

        Assert.Equal(2, samples.Count);
        Assert.Equal(2, runner.Invocations.Count == 1 ? 2 : 1);
    }

    [Fact]
    public async Task ReadsSynchronouslyToo()
    {
        var runner = new FakeProcessRunner().WithOutput(SingleRow);
        using var fallback = new NvidiaGpuFallback(runner, Locator());

        var samples = fallback.Read();

        Assert.Single(samples);
    }

    [Fact]
    public async Task ReturnsNothingAfterDisposal()
    {
        var runner = new FakeProcessRunner().WithOutput(SingleRow);
        var fallback = new NvidiaGpuFallback(runner, Locator());

        fallback.Dispose();

        var samples = await fallback.ReadAsync();

        Assert.Empty(samples);
        Assert.Empty(runner.Invocations);
    }
}

/// <summary>
/// Tests for the rule that decides whether the fallback is worth running at
/// all.
/// </summary>
public class NvidiaGpuFallbackPolicyTests
{
    [Fact]
    public void IsNotInvokedWhenTheBackendSuppliedEveryMetric()
    {
        // The backend read the temperature and both VRAM figures, so the
        // fallback has nothing to contribute and no subprocess is started.
        Assert.False(NvidiaGpuFallbackPolicy.ShouldInvoke(55d, 100UL, 200UL));
    }

    [Fact]
    public void IsInvokedWhenTheBackendCouldNotReadTheTemperature()
    {
        Assert.True(NvidiaGpuFallbackPolicy.ShouldInvoke(null, 100UL, 200UL));
    }

    [Fact]
    public void IsInvokedWhenTheBackendCouldNotReadTheVramUsed()
    {
        Assert.True(NvidiaGpuFallbackPolicy.ShouldInvoke(55d, null, 200UL));
    }

    [Fact]
    public void IsInvokedWhenTheBackendCouldNotReadTheVramTotal()
    {
        Assert.True(NvidiaGpuFallbackPolicy.ShouldInvoke(55d, 100UL, null));
    }

    [Fact]
    public void IsInvokedWhenTheBackendReadNothingAtAll()
    {
        Assert.True(NvidiaGpuFallbackPolicy.ShouldInvoke(null, null, null));
    }
}

/// <summary>
/// Tests for the executable locator: it resolves once, and it does not
/// re-probe the filesystem on every cycle when the executable is absent.
/// </summary>
public class NvidiaSmiLocatorTests
{
    [Fact]
    public void ResolvesTheExecutableFromTheConfiguredLocations()
    {
        var locator = new NvidiaSmiLocator(
            path => path == @"C:\second\nvidia-smi.exe",
            new[] { @"C:\first\nvidia-smi.exe", @"C:\second\nvidia-smi.exe" });

        Assert.Equal(@"C:\second\nvidia-smi.exe", locator.Resolve());
        Assert.True(locator.IsAvailable);
    }

    [Fact]
    public void ResolvesTheExecutableOnlyOnce()
    {
        var lookups = 0;
        var locator = new NvidiaSmiLocator(
            _ => { lookups++; return true; },
            new[] { @"C:\fake\nvidia-smi.exe" });

        locator.Resolve();
        locator.Resolve();
        locator.Resolve();

        // The path is remembered, so the filesystem is not walked again.
        Assert.Equal(1, lookups);
    }

    [Fact]
    public void StopsLookingForTheExecutableUntilTheRetryIntervalElapses()
    {
        var lookups = 0;
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var locator = new NvidiaSmiLocator(
            _ => { lookups++; return false; },
            new[] { @"C:\fake\nvidia-smi.exe" },
            retryInterval: TimeSpan.FromMinutes(15),
            timeProvider: timeProvider);

        Assert.Null(locator.Resolve());
        Assert.Equal(1, lookups);

        // A second cycle must not probe the filesystem again.
        Assert.Null(locator.Resolve());
        Assert.Equal(1, lookups);

        // Nor a third.
        Assert.Null(locator.Resolve());
        Assert.Equal(1, lookups);
    }

    [Fact]
    public void LooksAgainAfterTheRetryIntervalHasElapsed()
    {
        var timeProvider = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var locator = new NvidiaSmiLocator(
            _ => true,
            new[] { @"C:\fake\nvidia-smi.exe" },
            retryInterval: TimeSpan.FromMinutes(15),
            timeProvider: timeProvider);

        // Not found yet: the file check is what decides.
        var notFound = new NvidiaSmiLocator(
            _ => false,
            new[] { @"C:\fake\nvidia-smi.exe" },
            retryInterval: TimeSpan.FromMinutes(15),
            timeProvider: timeProvider);

        Assert.Null(notFound.Resolve());
        notFound.Resolve();

        timeProvider.Advance(TimeSpan.FromMinutes(16));

        // The interval has passed, so a fresh lookup happens.
        var lookups = 0;
        var counting = new NvidiaSmiLocator(
            _ => { lookups++; return false; },
            new[] { @"C:\fake\nvidia-smi.exe" },
            retryInterval: TimeSpan.FromMinutes(15),
            timeProvider: timeProvider);

        counting.Resolve();
        counting.Resolve();
        Assert.Equal(1, lookups);

        timeProvider.Advance(TimeSpan.FromMinutes(16));
        counting.Resolve();
        Assert.Equal(2, lookups);
    }

    [Fact]
    public void RediscoverForcesAFreshLookup()
    {
        var present = false;
        var lookups = 0;
        var locator = new NvidiaSmiLocator(
            _ => { lookups++; return present; },
            new[] { @"C:\fake\nvidia-smi.exe" },
            retryInterval: TimeSpan.FromMinutes(15));

        Assert.Null(locator.Resolve());
        Assert.Equal(1, lookups);

        // The driver is installed.
        present = true;

        // Without an explicit rediscovery, the locator stays quiet.
        Assert.Null(locator.Resolve());
        Assert.Equal(1, lookups);

        // With one, it looks again and finds it.
        locator.Rediscover();
        Assert.Equal(@"C:\fake\nvidia-smi.exe", locator.Resolve());
        Assert.Equal(2, lookups);
    }

    [Fact]
    public void SurvivesAFailedLookupWithoutThrowing()
    {
        var locator = new NvidiaSmiLocator(
            _ => throw new UnauthorizedAccessException("denied"),
            new[] { @"C:\fake\nvidia-smi.exe" });

        Assert.Null(locator.Resolve());
    }
}

/// <summary>
/// Tests for the rule that decides which fallback reading may be used.
/// </summary>
public class NvidiaGpuFallbackSelectorTests
{
    private static readonly BackendGpuIdentity NvidiaDevice =
        new("/gpu-nvidia/0", "VEN_10DE&DEV_1E91");

    private static NvidiaGpuSample Sample(
        string uuid = "GPU-a5e273b9",
        string pciDeviceId = "0x1E9110DE",
        double? utilization = 40,
        double? temperature = 55,
        ulong? vramUsed = 100,
        ulong? vramTotal = 200)
    {
        return new NvidiaGpuSample
        {
            Uuid = uuid,
            Name = "NVIDIA GeForce RTX 2070 Super",
            PciDeviceId = pciDeviceId,
            PciDeviceKey = NvidiaGpuIdentity.NormalizePciDeviceKey(pciDeviceId),
            UtilizationPercent = utilization,
            TemperatureC = temperature,
            VramUsedBytes = vramUsed,
            VramTotalBytes = vramTotal,
        };
    }

    [Fact]
    public void SelectsTheMetricsOfTheReconciledDevice()
    {
        var metrics = NvidiaGpuFallbackSelector.Select(
            new[] { Sample() },
            new[] { NvidiaDevice });

        Assert.Equal(40d, metrics.UtilizationPercent);
        Assert.Equal(55d, metrics.TemperatureC);
        Assert.Equal(100UL, metrics.VramUsedBytes);
        Assert.Equal(200UL, metrics.VramTotalBytes);
        Assert.Equal("/gpu-nvidia/0", metrics.DeviceId);
    }

    [Fact]
    public void SelectsNothingWhenTheReadingCannotBeReconciled()
    {
        var metrics = NvidiaGpuFallbackSelector.Select(
            new[] { Sample(pciDeviceId: "0x268410DE") },
            new[] { NvidiaDevice });

        Assert.Null(metrics.UtilizationPercent);
        Assert.Null(metrics.TemperatureC);
        Assert.Null(metrics.VramUsedBytes);
        Assert.Null(metrics.VramTotalBytes);
        Assert.Null(metrics.DeviceId);
    }

    [Fact]
    public void SelectsNothingWhenTheIdentityIsAmbiguous()
    {
        var metrics = NvidiaGpuFallbackSelector.Select(
            new[]
            {
                Sample(uuid: "GPU-a"),
                Sample(uuid: "GPU-b"),
            },
            new[] { NvidiaDevice });

        Assert.Null(metrics.TemperatureC);
        Assert.Null(metrics.DeviceId);
    }

    [Fact]
    public void SelectsNothingWhenThereAreNoSamples()
    {
        var metrics = NvidiaGpuFallbackSelector.Select(
            Array.Empty<NvidiaGpuSample>(),
            new[] { NvidiaDevice });

        Assert.Null(metrics.TemperatureC);
    }

    [Fact]
    public void SelectsNothingWhenTheBackendReportedNoDevice()
    {
        // Without a device identity there is nothing to reconcile against,
        // and a reading must never be attributed to a device chosen by
        // position.
        var metrics = NvidiaGpuFallbackSelector.Select(
            new[] { Sample() },
            Array.Empty<BackendGpuIdentity>());

        Assert.Null(metrics.TemperatureC);
        Assert.Null(metrics.DeviceId);
    }
}

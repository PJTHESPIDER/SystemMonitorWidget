using System;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the precedence between the sources that may supply one
/// flattened metric.
///
/// <para>
/// A metric can be offered by the device-identified backend, by the
/// reconciled vendor fallback, and by the Windows counters. They are not
/// equivalent, and the order they are tried in is what decides whether a
/// figure describes the card the widget is presenting.
/// </para>
/// </summary>
public class ScalarMergePolicyTests
{
    private static readonly DateTimeOffset ReadAt = new(2026, 10, 21, 6, 0, 0, TimeSpan.Zero);

    private static ScalarCandidate Candidate(
        double value,
        string source,
        string? deviceId,
        DateTimeOffset? readAt = null) =>
        new(value, "unit", source, readAt ?? ReadAt, deviceId);

    [Fact]
    public void TheBackendWins_WhenItSuppliedTheMetric()
    {
        // The backend names both the device and the sensor. It is the
        // primary source, and nothing else may displace it.
        var backend = Candidate(50, "LibreHardwareMonitor", "/gpu-nvidia/0");
        var fallback = Candidate(70, "nvidia-smi", "/gpu-nvidia/0");

        var winner = ScalarMergePolicy.Resolve(backend, fallback);

        Assert.Same(backend, winner);
    }

    [Fact]
    public void TheFallbackFillsIn_WhenTheBackendCouldNotReadTheMetric()
    {
        var fallback = Candidate(70, "nvidia-smi", "/gpu-nvidia/0");

        var winner = ScalarMergePolicy.Resolve(null, fallback);

        Assert.Same(fallback, winner);
    }

    [Fact]
    public void TheWindowsCountersAreUsed_WhenNeitherOtherSourceCould()
    {
        // The counters name no device. Their figure is still a real
        // reading, and it is better than showing nothing — but it is
        // never presented for a card, because it carries no device id.
        var counters = Candidate(1024, "Windows GPU counters", null);

        var winner = ScalarMergePolicy.Resolve(null, null, counters);

        Assert.Same(counters, winner);
        Assert.Null(winner!.DeviceId);
    }

    [Fact]
    public void TheFallbackBeatsTheWindowsCounters()
    {
        // The fallback names a device; the counters do not. The fallback
        // is therefore the better figure even when both are available.
        var fallback = Candidate(2048, "nvidia-smi", "/gpu-nvidia/0");
        var counters = Candidate(1024, "Windows GPU counters", null);

        var winner = ScalarMergePolicy.Resolve(null, fallback, counters);

        Assert.Same(fallback, winner);
    }

    [Fact]
    public void NoSource_ProducesNoValueRatherThanZero()
    {
        var winner = ScalarMergePolicy.Resolve(null, null, null);

        Assert.Null(winner);
    }

    [Fact]
    public void TheWinnerKeepsItsOwnDeviceId()
    {
        // The id belongs to the source that supplied the value. A figure
        // must never inherit the device id of a source that lost the
        // merge: that is exactly how one card's capacity ends up
        // presented as another's.
        var backend = Candidate(50, "LibreHardwareMonitor", "/gpu-nvidia/0");
        var fallback = Candidate(70, "nvidia-smi", "/gpu-intel/1");

        var winner = ScalarMergePolicy.Resolve(backend, fallback);

        Assert.Equal("/gpu-nvidia/0", winner!.DeviceId);
    }

    [Fact]
    public void TheReadTimeIsCarriedThrough()
    {
        var readAt = ReadAt.AddSeconds(-30);
        var backend = Candidate(50, "LibreHardwareMonitor", "/gpu-nvidia/0", readAt);

        var winner = ScalarMergePolicy.Resolve(backend, null);

        Assert.Equal(readAt, winner!.ReadAt);
    }

    [Fact]
    public void AMeasuredZeroFromTheBackendIsStillAWinner()
    {
        // Zero is a legitimate reading. It must not be mistaken for "no
        // value", which would let a less authoritative source displace it.
        var backend = Candidate(0, "LibreHardwareMonitor", "/gpu-nvidia/0");
        var fallback = Candidate(70, "nvidia-smi", "/gpu-nvidia/0");

        var winner = ScalarMergePolicy.Resolve(backend, fallback);

        Assert.Same(backend, winner);
        Assert.Equal(0, winner!.Value);
    }

    [Fact]
    public void TheFallbacksUtilizationFillsIn_WhenTheBackendCouldNotReadTheLoad()
    {
        // The backend could not read the GPU load, but the reconciled
        // fallback did. The load figure must come from the fallback and
        // carry the fallback's device id — the fallback's device is not
        // necessarily the backend's primary GPU, so the attribution follows
        // the figure.
        var fallback = Candidate(40, "nvidia-smi", "/gpu-nvidia/0");

        var winner = ScalarMergePolicy.Resolve(null, fallback);

        Assert.Same(fallback, winner);
        Assert.Equal("/gpu-nvidia/0", winner!.DeviceId);
    }

    [Fact]
    public void AMeasuredZeroFromTheFallbackIsStillAValue()
    {
        // An idle GPU reads zero utilization. That is a measurement, not an
        // absence, so it must survive the merge as a value.
        var fallback = Candidate(0, "nvidia-smi", "/gpu-nvidia/0");

        var winner = ScalarMergePolicy.Resolve(null, fallback);

        Assert.NotNull(winner);
        Assert.Equal(0, winner!.Value);
    }
}

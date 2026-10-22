using System;
using System.Collections.Generic;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the rule that decides whether a Windows GPU memory counter
/// reading may be attributed to a single device.
///
/// <para>
/// The Windows counters report one figure per adapter, keyed by an LUID,
/// and carry no domain-model device identity. The widget may be presenting
/// any one of several adapters, so a reading that describes more than one
/// adapter cannot be attributed to the device being presented.
/// </para>
///
/// <para>
/// The specific failures these tests guard against are:
/// </para>
///
/// <list type="bullet">
/// <item><description>
/// Summing the used figures across adapters, which reports the machine's
/// total GPU memory in use as one card's usage.
/// </description></item>
/// <item><description>
/// Taking the first adapter's capacity, which reports the integrated
/// part's capacity as the discrete card's.
/// </description></item>
/// </list>
/// </summary>
public class GpuMemoryFallbackPolicyTests
{
    private static GpuAdapterMemorySample Sample(string name, ulong used) =>
        new(name, used);

    [Fact]
    public void ASingleAdapter_ProducesItsUsedBytes()
    {
        // One adapter: the figure is unambiguously that adapter's.
        var readings = new GpuMemoryCounterReadings(
            new[] { Sample("luid_0x0_phys_0", 512 * 1024 * 1024) },
            new[] { 8UL * 1024 * 1024 * 1024 });

        var (used, total) = GpuMemoryFallbackPolicy.Resolve(readings);

        Assert.Equal(512UL * 1024 * 1024, used);
        Assert.Equal(8UL * 1024 * 1024 * 1024, total);
    }

    [Fact]
    public void AMultipleAdapterSystem_ProducesNoUsedBytes()
    {
        // Two adapters: a sum would report one card's usage as the other's.
        var readings = new GpuMemoryCounterReadings(
            new[]
            {
                Sample("luid_0x1_phys_0", 1024UL * 1024 * 1024),
                Sample("luid_0x2_phys_0", 256UL * 1024 * 1024),
            },
            new[] { 8UL * 1024 * 1024 * 1024, 4UL * 1024 * 1024 * 1024 });

        var (used, total) = GpuMemoryFallbackPolicy.Resolve(readings);

        Assert.Null(used);
        Assert.Null(total);
    }

    [Fact]
    public void TheUsedBytes_AreNeverSummedAcrossAdapters()
    {
        // The sum of the two adapters' usage is the machine's total, not
        // any one card's. It must never appear as a per-device reading.
        var readings = new GpuMemoryCounterReadings(
            new[]
            {
                Sample("luid_0x1_phys_0", 1024UL * 1024 * 1024),
                Sample("luid_0x2_phys_0", 256UL * 1024 * 1024),
            },
            Array.Empty<ulong>());

        var (used, _) = GpuMemoryFallbackPolicy.Resolve(readings);

        Assert.Null(used);
    }

    [Fact]
    public void TheTotalBytes_AreNeverTakenFromTheFirstAdapter()
    {
        // The first adapter is an enumeration accident, not a fact about
        // the device the widget presents. On a hybrid machine it is very
        // often the integrated part, whose capacity bears no relation to
        // the discrete card's.
        var readings = new GpuMemoryCounterReadings(
            new[] { Sample("luid_0x1_phys_0", 0) },
            new[] { 1UL * 1024 * 1024 * 1024, 8UL * 1024 * 1024 * 1024 });

        var (_, total) = GpuMemoryFallbackPolicy.Resolve(readings);

        Assert.Null(total);
    }

    [Fact]
    public void NoAdapters_ProduceNoReadings()
    {
        var readings = new GpuMemoryCounterReadings(
            Array.Empty<GpuAdapterMemorySample>(),
            Array.Empty<ulong>());

        var (used, total) = GpuMemoryFallbackPolicy.Resolve(readings);

        Assert.Null(used);
        Assert.Null(total);
    }

    [Fact]
    public void ACapacitySmallerThanTheUsage_ProducesNoTotal()
    {
        // The two classes cannot be correlated to each other, so they may
        // have described different adapters. A capacity below the amount in
        // use would produce a usage percentage above 100, which is not a
        // number any device reports.
        var readings = new GpuMemoryCounterReadings(
            new[] { Sample("luid_0x1_phys_0", 8UL * 1024 * 1024 * 1024) },
            new[] { 4UL * 1024 * 1024 * 1024 });

        var (used, total) = GpuMemoryFallbackPolicy.Resolve(readings);

        Assert.Equal(8UL * 1024 * 1024 * 1024, used);
        Assert.Null(total);
    }

    [Fact]
    public void AnAmbiguousUsedFigure_WithAnUnambiguousTotal_KeepsTheUsedFigure()
    {
        // Each class is judged on its own. The used figure is ambiguous,
        // but the total is not, and the total still describes the only
        // adapter the video-controller class saw.
        var readings = new GpuMemoryCounterReadings(
            new[]
            {
                Sample("luid_0x1_phys_0", 1024UL * 1024 * 1024),
                Sample("luid_0x2_phys_0", 0),
            },
            new[] { 8UL * 1024 * 1024 * 1024 });

        var (used, total) = GpuMemoryFallbackPolicy.Resolve(readings);

        Assert.Null(used);
        Assert.Equal(8UL * 1024 * 1024 * 1024, total);
    }

    [Fact]
    public void ANullReading_ProducesNoReadings()
    {
        var (used, total) = GpuMemoryFallbackPolicy.Resolve(null!);

        Assert.Null(used);
        Assert.Null(total);
    }

    [Fact]
    public void AnEmptyAdapterList_ProducesNoUsedBytes()
    {
        Assert.Null(GpuMemoryFallbackPolicy.UnambiguousUsedBytes(
            Array.Empty<GpuAdapterMemorySample>()));
    }

    [Fact]
    public void AnEmptyCapacityList_ProducesNoTotalBytes()
    {
        Assert.Null(GpuMemoryFallbackPolicy.UnambiguousTotalBytes(
            Array.Empty<ulong>()));
    }
}

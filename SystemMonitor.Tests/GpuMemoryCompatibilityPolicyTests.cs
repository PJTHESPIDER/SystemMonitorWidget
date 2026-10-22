using System;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the rule that decides whether a used-memory figure and a
/// total-memory figure may be presented as one ratio.
///
/// <para>
/// A "used / total (percent)" figure is a claim that the two numbers
/// describe the same memory pool of the same device. These tests pin the
/// three ways that claim can be false — attribution, memory semantics and
/// coherence — and the fact that a display name is never treated as
/// evidence of anything.
/// </para>
/// </summary>
public class GpuMemoryCompatibilityPolicyTests
{
    private const string Device = "/gpu-nvidia/0";
    private const string OtherDevice = "/gpu-intel/1";

    private static readonly DateTimeOffset ReadAt =
        new DateTimeOffset(2026, 10, 21, 6, 0, 0, TimeSpan.Zero);

    // --- Provenance labels used by the real collection path ---------------

    private const string BackendPrefix = "LibreHardwareMonitor:";
    private const string DedicatedUsed = BackendPrefix + "GPU Memory Used";
    private const string DedicatedTotal = BackendPrefix + "GPU Memory Total";
    private const string GenericUsed = BackendPrefix + "Memory Used";
    private const string GenericTotal = BackendPrefix + "Memory Total";
    private const string SharedUsed = BackendPrefix + "D3D Shared Memory Used";
    private const string SharedTotal = BackendPrefix + "D3D Shared Memory Total";

    private static Measurement Figure(double bytes, string source) =>
        Measurement.AvailableValue(bytes, "B", source, ReadAt);

    // =====================================================================
    // Attribution
    // =====================================================================

    [Fact]
    public void TheSameDeviceFromTwoSources_MayBeCombined()
    {
        // The used figure came from the vendor-neutral fallback and the
        // capacity from the device-identified backend. They are different
        // sources, but they name the same device and the same pool, so the
        // ratio they produce is a fact about that device.
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(4_000_000_000.0, GpuMemoryCompatibilityPolicy.NvidiaSource),
            Device,
            Figure(8_000_000_000.0, DedicatedTotal),
            Device,
            Device);

        Assert.True(canCombine);
    }

    [Fact]
    public void TwoDifferentDevices_MayNotBeCombined()
    {
        // One card's usage paired with another card's capacity produces a
        // percentage no device reports.
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(4_000_000_000.0, DedicatedUsed),
            Device,
            Figure(8_000_000_000.0, DedicatedTotal),
            OtherDevice,
            Device);

        Assert.False(canCombine);
    }

    [Fact]
    public void AnUnattributedTotal_MayNotBeCombinedWithAnAttributedUsed()
    {
        // The capacity names no device, so it may be any card's capacity.
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(4_000_000_000.0, DedicatedUsed),
            Device,
            Figure(8_000_000_000.0, DedicatedTotal),
            null,
            Device);

        Assert.False(canCombine);
    }

    [Fact]
    public void AnUnattributedUsed_MayNotBeCombinedWithAnAttributedTotal()
    {
        // The mirror image: the usage names no device, so it may be any
        // card's usage.
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(4_000_000_000.0, DedicatedUsed),
            null,
            Figure(8_000_000_000.0, DedicatedTotal),
            Device,
            Device);

        Assert.False(canCombine);
    }

    [Fact]
    public void AFigureForAnotherDevice_MayNotBeCombinedWithThePresentedOne()
    {
        // Both figures name the same device, but it is not the one being
        // presented. The ratio belongs to a card the widget is not showing.
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(4_000_000_000.0, DedicatedUsed),
            OtherDevice,
            Figure(8_000_000_000.0, DedicatedTotal),
            OtherDevice,
            Device);

        Assert.False(canCombine);
    }

    [Fact]
    public void FiguresForNoPresentedDevice_MayNotBeCombined()
    {
        // Nothing is being presented, so no figure is presented for it.
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(4_000_000_000.0, DedicatedUsed),
            Device,
            Figure(8_000_000_000.0, DedicatedTotal),
            Device,
            null);

        Assert.False(canCombine);
    }

    // =====================================================================
    // Memory semantics
    // =====================================================================

    [Fact]
    public void DedicatedMemoryAndSharedMemory_MayNotBeCombined()
    {
        // The card's dedicated VRAM on one side, shared system memory on
        // the other. The ratio of those two numbers is a coincidence, not a
        // usage percentage.
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(4_000_000_000.0, DedicatedUsed),
            Device,
            Figure(8_000_000_000.0, SharedTotal),
            Device,
            Device);

        Assert.False(canCombine);
    }

    [Fact]
    public void SharedMemoryAndDedicatedMemory_MayNotBeCombined()
    {
        // The mirror image: the shared figure is the used one.
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(4_000_000_000.0, SharedUsed),
            Device,
            Figure(8_000_000_000.0, DedicatedTotal),
            Device,
            Device);

        Assert.False(canCombine);
    }

    [Fact]
    public void TwoSharedMemoryFigures_MayNotBeCombined()
    {
        // Shared system memory is not VRAM, so a ratio over it would be
        // presented as the card's VRAM usage.
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(4_000_000_000.0, SharedUsed),
            Device,
            Figure(8_000_000_000.0, SharedTotal),
            Device,
            Device);

        Assert.False(canCombine);
    }

    [Fact]
    public void TwoFiguresFromTheSameFamily_MayBeCombined()
    {
        // Both figures come from the generic "Memory" sensors, whose names
        // say nothing about which pool they describe. What they do share
        // is a family, and two figures of one family on one device are two
        // figures of one pool, whatever that pool is. The pair is
        // therefore combined: the ratio is a fact about the card, even
        // though the pool is unnamed.
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(4_000_000_000.0, GenericUsed),
            Device,
            Figure(8_000_000_000.0, GenericTotal),
            Device,
            Device);

        Assert.True(canCombine);
    }

    [Fact]
    public void TwoFiguresFromDifferentFamilies_MayNotBeCombined()
    {
        // One figure names the dedicated pool and the other names nothing.
        // They are two different families, and nothing says they describe
        // the same pool, so the pairing is a claim neither figure
        // supports.
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(4_000_000_000.0, DedicatedUsed),
            Device,
            Figure(8_000_000_000.0, GenericTotal),
            Device,
            Device);

        Assert.False(canCombine);
    }

    [Fact]
    public void AFigureWithNoFamily_MayNotBeCombinedWithAnyOther()
    {
        // The Windows counters' figures establish no pool and no family,
        // so nothing can place them in the other figure's pool.
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(4_000_000_000.0, "Windows GPU counters"),
            Device,
            Figure(8_000_000_000.0, DedicatedTotal),
            Device,
            Device);

        Assert.False(canCombine);
    }

    [Fact]
    public void ThePoolOfAFigure_IsReadFromItsOwnProvenance()
    {
        Assert.Equal(
            GpuMemorySpace.Dedicated,
            GpuMemoryCompatibilityPolicy.SpaceOf(Figure(1, DedicatedUsed)));

        Assert.Equal(
            GpuMemorySpace.Shared,
            GpuMemoryCompatibilityPolicy.SpaceOf(Figure(1, SharedUsed)));

        // A generic name says nothing about its pool.
        Assert.Equal(
            GpuMemorySpace.Unknown,
            GpuMemoryCompatibilityPolicy.SpaceOf(Figure(1, GenericUsed)));

        // The vendor-neutral fallback reports the card's own dedicated
        // video memory.
        Assert.Equal(
            GpuMemorySpace.Dedicated,
            GpuMemoryCompatibilityPolicy.SpaceOf(
                Figure(1, GpuMemoryCompatibilityPolicy.NvidiaSource)));

        // The Windows counters establish no pool.
        Assert.Equal(
            GpuMemorySpace.Unknown,
            GpuMemoryCompatibilityPolicy.SpaceOf(Figure(1, "Windows GPU counters")));

        // A figure with no provenance at all establishes nothing.
        Assert.Equal(
            GpuMemorySpace.Unknown,
            GpuMemoryCompatibilityPolicy.SpaceOf(Measurement.AvailableValue(1, "B", null, ReadAt)));
    }

    [Fact]
    public void TheFamilyOfAFigure_IsReadFromItsOwnProvenance()
    {
        // The family is what a used figure and a total figure share when
        // neither of them names a pool.
        Assert.Equal(
            "GPU Memory",
            GpuMemoryCompatibilityPolicy.FamilyOf(Figure(1, DedicatedUsed)));

        Assert.Equal(
            "Memory",
            GpuMemoryCompatibilityPolicy.FamilyOf(Figure(1, GenericUsed)));

        // The vendor-neutral fallback's two figures are one pool.
        Assert.Equal(
            GpuMemoryCompatibilityPolicy.NvidiaSource,
            GpuMemoryCompatibilityPolicy.FamilyOf(
                Figure(1, GpuMemoryCompatibilityPolicy.NvidiaSource)));

        // The Windows counters' figures establish no family.
        Assert.Null(
            GpuMemoryCompatibilityPolicy.FamilyOf(Figure(1, "Windows GPU counters")));

        Assert.Null(
            GpuMemoryCompatibilityPolicy.FamilyOf(Figure(1, "test")));
    }

    [Fact]
    public void TheGenericMemorySensors_AreNotDedicatedMemorySensors()
    {
        // The backend's generic candidates are deliberately not treated as
        // dedicated memory: a driver may name shared system memory
        // "Memory Total" on one card.
        Assert.False(LhmSensorSelection.IsDedicatedMemorySensor("Memory Used"));
        Assert.False(LhmSensorSelection.IsDedicatedMemorySensor("Memory Total"));
        Assert.True(LhmSensorSelection.IsGenericMemorySensor("Memory Used"));
        Assert.True(LhmSensorSelection.IsGenericMemorySensor("Memory Total"));
    }

    [Fact]
    public void TheSharedMemorySensors_AreRecognised()
    {
        Assert.True(LhmSensorSelection.IsSharedMemorySensor("D3D Shared Memory Used"));
        Assert.True(LhmSensorSelection.IsSharedMemorySensor("D3D Shared Memory Total"));
        Assert.False(LhmSensorSelection.IsSharedMemorySensor("GPU Memory Used"));

        // A shared sensor is never a dedicated one.
        Assert.False(LhmSensorSelection.IsDedicatedMemorySensor("D3D Shared Memory Used"));
    }

    // =====================================================================
    // Coherence
    // =====================================================================

    [Fact]
    public void AZeroTotal_MayNotBeCombined()
    {
        // A capacity of zero cannot produce a ratio at all.
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(0.0, DedicatedUsed),
            Device,
            Figure(0.0, DedicatedTotal),
            Device,
            Device);

        Assert.False(canCombine);
    }

    [Fact]
    public void AMissingTotal_MayNotBeCombined()
    {
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(4_000_000_000.0, DedicatedUsed),
            Device,
            null,
            Device,
            Device);

        Assert.False(canCombine);
    }

    [Fact]
    public void AFailedTotal_MayNotBeCombined()
    {
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(4_000_000_000.0, DedicatedUsed),
            Device,
            Measurement.Failed(DedicatedTotal),
            Device,
            Device);

        Assert.False(canCombine);
    }

    [Fact]
    public void AUsageAboveTheCapacity_MayNotBeCombined()
    {
        // More in use than there is: the two numbers cannot describe one
        // pool, so the ratio would be a number no device reports.
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(8_000_000_000.0, DedicatedUsed),
            Device,
            Figure(4_000_000_000.0, DedicatedTotal),
            Device,
            Device);

        Assert.False(canCombine);
    }

    [Fact]
    public void AMeasuredZeroUsage_WithARealCapacity_MayBeCombined()
    {
        // An idle card uses none of its memory. That is a measurement, and
        // "0.0%" is a fact about it.
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(0.0, DedicatedUsed),
            Device,
            Figure(8_000_000_000.0, DedicatedTotal),
            Device,
            Device);

        Assert.True(canCombine);
    }

    // =====================================================================
    // What compatibility is never inferred from
    // =====================================================================

    [Fact]
    public void AMatchingDeviceName_IsNeverEvidenceOfCompatibility()
    {
        // Two identical cards have identical display names and are
        // different devices. The policy never sees a display name at all:
        // it is decided on device ids, so the two are not compatible.
        var canCombine = GpuMemoryCompatibilityPolicy.CanCombine(
            Figure(4_000_000_000.0, DedicatedUsed),
            Device,
            Figure(8_000_000_000.0, DedicatedTotal),
            OtherDevice,
            Device);

        Assert.False(canCombine);
    }

    [Fact]
    public void AFigureWithNoProvenance_EstablishesNoPool()
    {
        // A source that says nothing about the pool leaves the figure
        // unclassified rather than guessing.
        Assert.Equal(
            GpuMemorySpace.Unknown,
            GpuMemoryCompatibilityPolicy.SpaceOf(Figure(1, "test")));

        Assert.Equal(
            GpuMemorySpace.Unknown,
            GpuMemoryCompatibilityPolicy.SpaceOf(Figure(1, "")));
    }

    [Fact]
    public void SharedMemoryIsNeverDescribedByADedicatedFigure()
    {
        Assert.True(GpuMemoryCompatibilityPolicy.DescribesSharedMemory(
            Figure(1, SharedUsed)));

        Assert.False(GpuMemoryCompatibilityPolicy.DescribesSharedMemory(
            Figure(1, DedicatedUsed)));

        Assert.False(GpuMemoryCompatibilityPolicy.DescribesSharedMemory(null));
    }
}

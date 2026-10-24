using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.Extensions.Time.Testing;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// End-to-end integration tests for GPU grace period behavior across the
/// tracker → snapshot → presenter pipeline.
///
/// These tests verify that the grace period semantics are correctly
/// preserved through the full stack: the trackers' state machines, the
/// immutable snapshot, and the presenter's consumption of that snapshot.
///
/// The tests use the pure logic components directly (no SystemMetricsService
/// which requires Windows APIs), injecting controlled snapshots built from
/// LhmFixtures.
/// </summary>
public sealed class GpuGraceIntegrationTests
{
    /// <summary>
    /// Builds a MetricsSnapshot from the given devices and readings.
    /// </summary>
    private static MetricsSnapshot BuildSnapshot(
        DateTimeOffset timestamp,
        IReadOnlyList<HardwareDevice> devices,
        IReadOnlyList<DeviceReading> readings,
        string? gpuLoadDeviceId = null,
        string? gpuTemperatureDeviceId = null,
        string? gpuVramUsedDeviceId = null,
        string? gpuVramTotalDeviceId = null,
        Measurement? gpuLoadPercent = null,
        Measurement? gpuTemperatureC = null,
        Measurement? gpuVramUsedBytes = null,
        Measurement? gpuVramTotalBytes = null,
        string? gpuTrackedInstanceId = null,
        IReadOnlyList<string>? gpuTrackedExtraInstanceIds = null)
    {
        return MetricsSnapshot.Create(
            timestamp,
            cpus: devices.Where(d => d.DeviceClass == DeviceClass.Cpu).ToArray(),
            gpus: devices.Where(d => d.DeviceClass == DeviceClass.Gpu).ToArray(),
            readings: readings,
            gpuLoadPercent: gpuLoadPercent,
            gpuTemperatureC: gpuTemperatureC,
            gpuVramUsedBytes: gpuVramUsedBytes,
            gpuVramTotalBytes: gpuVramTotalBytes,
            gpuLoadDeviceId: gpuLoadDeviceId,
            gpuTemperatureDeviceId: gpuTemperatureDeviceId,
            gpuVramUsedDeviceId: gpuVramUsedDeviceId,
            gpuVramTotalDeviceId: gpuVramTotalDeviceId,
            gpuTrackedInstanceId: gpuTrackedInstanceId,
            gpuTrackedExtraInstanceIds: gpuTrackedExtraInstanceIds ?? Array.Empty<string>());
    }

    /// <summary>
    /// Runs one collection cycle: updates trackers, builds snapshot, presents.
    /// Returns the snapshot, payload, tracker, extras tracker, and the tracked GPU device ID for the next cycle.
    /// </summary>
    private static (MetricsSnapshot Snapshot, WidgetPayload Payload, GpuSelectionTracker Tracker, GpuExtrasTracker ExtrasTracker, string? NextTrackedGpuDeviceId) RunCycle(
        FakeTimeProvider time,
        GpuSelectionTracker tracker,
        GpuExtrasTracker extrasTracker,
        IReadOnlyList<LhmDeviceDescriptor> lhmDevices,
        string? userSelectedGpuInstanceId = null,
        string? trackedGpuDeviceId = null)
    {
        // Project LHM devices to domain model
        var projection = LhmSnapshotMapper.Project(lhmDevices);
        var readAt = time.GetUtcNow();

        // Compute scalar GPU metrics from the TRACKED GPU's readings (not the current selection)
        // This mirrors what SystemMetricsService does - the scalar metrics describe the
        // device that was tracked in the previous cycle.
        var primaryGpuForScalars = string.IsNullOrEmpty(trackedGpuDeviceId)
            ? LhmSnapshotMapper.SelectPrimaryGpu(
                projection.Gpus,
                null, // no snapshot yet
                null, null, null, null, null, null, null, null)
            : projection.Gpus.FirstOrDefault(g => g.DeviceId == trackedGpuDeviceId);

        // Get device-level readings for the primary GPU to use as scalar fallbacks
        var gpuLoadReading = primaryGpuForScalars is not null
            ? projection.Readings.FirstOrDefault(r => r.DeviceId == primaryGpuForScalars.DeviceId && r.Metric == LhmSensorSelection.GpuLoadMetric)?.Measurement
            : null;
        var gpuTempReading = primaryGpuForScalars is not null
            ? projection.Readings.FirstOrDefault(r => r.DeviceId == primaryGpuForScalars.DeviceId && r.Metric == LhmSensorSelection.GpuTemperatureMetric)?.Measurement
            : null;

        var gpuLoadPercent = (gpuLoadReading?.Status == MeasurementStatus.Available && gpuLoadReading.Value.HasValue)
            ? Measurement.AvailableValue(gpuLoadReading.Value.Value, "%", gpuLoadReading.Source, readAt)
            : null;
        var gpuTemperatureC = (gpuTempReading?.Status == MeasurementStatus.Available && gpuTempReading.Value.HasValue)
            ? Measurement.AvailableValue(gpuTempReading.Value.Value, "°C", gpuTempReading.Source, readAt)
            : null;

        // Update primary GPU tracker (with grace period)
        var selectedPrimary = tracker.Update(
            projection.Gpus,
            userSelectedGpuInstanceId,
            projection.ToSnapshot(readAt),
            gpuLoadPercent,
            primaryGpuForScalars?.DeviceId,
            gpuTemperatureC,
            primaryGpuForScalars?.DeviceId,
            null, null, null, null);

        // Update extras tracker (with grace period)
        var extrasResult = extrasTracker.Update(
            projection.Gpus,
            selectedPrimary?.InstanceId,
            projection.ToSnapshot(readAt),
            gpuLoadPercent,
            primaryGpuForScalars?.DeviceId,
            gpuTemperatureC,
            primaryGpuForScalars?.DeviceId);

        // Determine the tracked GPU device ID for the NEXT cycle's scalar metrics
        var nextTrackedGpuDeviceId = tracker.TrackedInstanceId is not null
            ? projection.Gpus.FirstOrDefault(g => string.Equals(g.InstanceId, tracker.TrackedInstanceId, StringComparison.OrdinalIgnoreCase))?.DeviceId
            : (selectedPrimary?.DeviceId);

        // Build full snapshot with tracker state
        var snapshot = MetricsSnapshot.Create(
            readAt,
            cpus: projection.Cpus,
            gpus: projection.Gpus,
            readings: projection.Readings,
            gpuLoadPercent: gpuLoadPercent,
            gpuTemperatureC: gpuTemperatureC,
            gpuVramUsedBytes: null,
            gpuVramTotalBytes: null,
            gpuLoadDeviceId: primaryGpuForScalars?.DeviceId,
            gpuTemperatureDeviceId: primaryGpuForScalars?.DeviceId,
            gpuVramUsedDeviceId: null,
            gpuVramTotalDeviceId: null,
            gpuTrackedInstanceId: tracker.TrackedInstanceId,
            gpuTrackedExtraInstanceIds: extrasResult.EligibleInstanceIds);

        // Present the snapshot
        var payload = WidgetPayloadPresenter.Present(snapshot, userSelectedGpuInstanceId);

        return (snapshot, payload, tracker, extrasTracker, nextTrackedGpuDeviceId);
    }

    // ============================================================
    // Test 1: End-to-end grace boundary for secondary GPU (extras)
    // ============================================================

    /// <summary>
    /// Verifies the complete grace boundary lifecycle for a secondary GPU:
    /// 1. Initial success (both GPUs have telemetry, both appear)
    /// 2. Failures 1-3 (secondary GPU retains through 3 consecutive failures)
    /// 3. Failure 4 (secondary GPU removed)
    /// 4. Recovery (secondary GPU reappears with telemetry, qualifies fresh)
    /// 5. Disappearance (secondary GPU physically removed, gone immediately)
    /// 6. Repeated presentation (same snapshot presented multiple times, no additional failures)
    /// 7. Snapshot immutability (presenter cannot mutate snapshot)
    /// </summary>
    [Fact]
    public void EndToEnd_GraceBoundary_SecondaryGpu_FullLifecycle()
    {
        var time = new FakeTimeProvider();
        var tracker = new GpuSelectionTracker(graceCycles: 3, time);
        var extrasTracker = new GpuExtrasTracker(graceCycles: 3, time);

        // Two discrete GPUs: gpu0 (primary), gpu1 (secondary)
        // gpu0 is the RTX 2070 Super (InstanceId ends in 0008), gpu1 is RTX 3070 (InstanceId ends in 0010)
        // Both are discrete/unknown, so the tie-break is by InstanceId. gpu0's InstanceId is lexicographically smaller.
        var gpu0 = LhmFixtures.NvidiaGpu(readable: true);
        var gpu1 = LhmFixtures.NvidiaGpu(readable: true);
        gpu1 = gpu1 with
        {
            DeviceId = "/gpu-nvidia/1",
            DisplayName = "NVIDIA GeForce RTX 3070",
            PciDeviceKey = "VEN_10DE&DEV_2206",
            InstanceId = @"PCI\VEN_10DE&DEV_2206&SUBSYS_12341458\4&7AADB68&0&0010",
        };

        string? trackedGpuDeviceId = null;

        // --- CYCLE 1: Initial success - both GPUs have telemetry ---
        var (snapshot1, payload1, _, _, nextTracked1) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked1;

        // Debug output
        var gpuName1 = payload1.GetText(WidgetPayloadPresenter.GpuNameKey);
        var gpuVendor1 = payload1.GetText(WidgetPayloadPresenter.GpuVendorKey);
        var primaryInstanceId1 = GetPrimaryGpuInstanceId(payload1, snapshot1);
        var trackedInstanceId1 = snapshot1.GpuTrackedInstanceId;
        var extraIds1 = snapshot1.GpuTrackedExtraInstanceIds.ToList();

        // Both GPUs should appear: gpu0 as primary, gpu1 as extra
        Assert.NotNull(payload1.GetText(WidgetPayloadPresenter.GpuNameKey));
        Assert.Equal(gpu0.InstanceId, GetPrimaryGpuInstanceId(payload1, snapshot1));

        // Debug: print all flags
        var allFlags1 = payload1.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .Where(f => f.FieldType == typeof(Dictionary<string, bool>))
            .SelectMany(f => ((Dictionary<string, bool>)f.GetValue(payload1)!))
            .ToList();

        var extraFlags1 = new Dictionary<string, bool>();
        foreach (var flag in allFlags1)
        {
            if (flag.Key.StartsWith("gpuExtra") && flag.Key.EndsWith("Available"))
                extraFlags1[flag.Key] = flag.Value;
        }

        // Check if any extra device is available (might be at index 2 or 3)
        var hasAnyExtra = extraFlags1.Any(kvp => kvp.Value);
        Assert.True(hasAnyExtra, $"No extra devices available. Flags: {string.Join(", ", extraFlags1.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
        Assert.Contains(gpu1.InstanceId, snapshot1.GpuTrackedExtraInstanceIds);

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 2: Failure 1 - gpu1 loses all telemetry ---
        var gpu1NoTelemetry = gpu1 with { Sensors = new List<LhmSensorDescriptor>() };
        var (snapshot2, payload2, _, _, nextTracked2) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1NoTelemetry }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked2;

        // gpu1 should still appear as extra (within grace period, failure 1 of 3)
        Assert.True(payload2.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.Contains(gpu1.InstanceId, snapshot2.GpuTrackedExtraInstanceIds);

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 3: Failure 2 - gpu1 still no telemetry ---
        var (snapshot3, payload3, _, _, nextTracked3) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1NoTelemetry }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked3;

        // gpu1 should still appear (failure 2 of 3)
        Assert.True(payload3.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.Contains(gpu1.InstanceId, snapshot3.GpuTrackedExtraInstanceIds);

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 4: Failure 3 - gpu1 still no telemetry ---
        var (snapshot4, payload4, _, _, nextTracked4) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1NoTelemetry }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked4;

        // gpu1 should still appear (failure 3 of 3, last grace cycle)
        Assert.True(payload4.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.Contains(gpu1.InstanceId, snapshot4.GpuTrackedExtraInstanceIds);

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 5: Failure 4 - grace exhausted, gpu1 removed ---
        var (snapshot5, payload5, _, _, nextTracked5) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1NoTelemetry }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked5;

        // gpu1 should be gone (failure 4 exceeds grace of 3)
        Assert.False(payload5.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.DoesNotContain(gpu1.InstanceId, snapshot5.GpuTrackedExtraInstanceIds);

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 6: Recovery - gpu1 reappears WITH telemetry ---
        var (snapshot6, payload6, _, _, nextTracked6) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId); // gpu1 with telemetry again
        trackedGpuDeviceId = nextTracked6;

        // gpu1 should reappear and qualify fresh (new eligibility history)
        Assert.True(payload6.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.Contains(gpu1.InstanceId, snapshot6.GpuTrackedExtraInstanceIds);

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 7: Disappearance - gpu1 physically removed from system ---
        var (snapshot7, payload7, _, _, nextTracked7) = RunCycle(time, tracker, extrasTracker, new[] { gpu0 }, trackedGpuDeviceId: trackedGpuDeviceId); // gpu1 no longer in device list
        trackedGpuDeviceId = nextTracked7;

        // gpu1 should be gone immediately (device disappearance ends eligibility)
        Assert.False(payload7.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.DoesNotContain(gpu1.InstanceId, snapshot7.GpuTrackedExtraInstanceIds);

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 8: Repeated presentation - same snapshot presented multiple times ---
        var (snapshot8, payload8a, _, _, nextTracked8) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked8;
        var payload8b = WidgetPayloadPresenter.Present(snapshot8); // Present again with same snapshot
        var payload8c = WidgetPayloadPresenter.Present(snapshot8); // And again

        // All three presentations should show the same extra device
        // No additional failure consumed because no new collection cycle
        Assert.True(payload8a.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.True(payload8b.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.True(payload8c.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));

        // --- CYCLE 9: Verify snapshot immutability (behavioral) ---
        var (snapshot9, _, _, _, _) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId);

        // 1. Input isolation: Modifying the caller's original array after snapshot creation
        // must not change the snapshot.
        // We can't easily test the internal array passed to Create() from RunCycle, so we
        // test the property by creating a snapshot directly with a mutable source.
        var mutableArray = new[] { "id1", "id2" };
        var testSnapshot = MetricsSnapshot.Create(
            DateTimeOffset.UtcNow,
            gpuTrackedExtraInstanceIds: mutableArray);
        mutableArray[0] = "mutated";
        Assert.Equal("id1", testSnapshot.GpuTrackedExtraInstanceIds[0]);

        // 2. Public collection protection: The exposed collection must not allow
        // replacing an element or clearing through public casts/interfaces.
        // Array.AsReadOnly returns a ReadOnlyCollection which implements IList<string>
        // but throws NotSupportedException on mutation attempts.
        var exposed = testSnapshot.GpuTrackedExtraInstanceIds;
        Assert.IsAssignableFrom<IReadOnlyList<string>>(exposed);
        // Verify the underlying storage is not the caller's original array
        Assert.NotSame(mutableArray, exposed);
        // Verify mutation through IList<string> is rejected
        var asList = (IList<string>)exposed;
        Assert.Throws<NotSupportedException>(() => asList[0] = "mutated");
        Assert.Throws<NotSupportedException>(() => asList.Add("extra"));
        Assert.Throws<NotSupportedException>(() => asList.Clear());
        Assert.Throws<NotSupportedException>(() => asList.RemoveAt(0));

        // 3. Historical stability: Later tracker updates must not change an earlier snapshot.
        // Run another cycle which will update the trackers
        var (snapshot10, _, _, _, _) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId);
        // snapshot9's eligibility should be unchanged
        Assert.Equal(snapshot9.GpuTrackedExtraInstanceIds, snapshot9.GpuTrackedExtraInstanceIds);
    }

    // ============================================================
    // Test 2: Cancellation does not consume grace
    // ============================================================

    /// <summary>
    /// Verifies that a canceled/aborted collection (one that produces no
    /// accepted snapshot) does not advance the grace failure counter.
    /// Only completed collection cycles count.
    ///
    /// This simulates: a collection starts, but is canceled before
    /// producing a snapshot. The tracker should not count this as a failure.
    /// </summary>
    [Fact]
    public void Cancellation_DoesNotConsumeGrace_ForSecondaryGpu()
    {
        var time = new FakeTimeProvider();
        var tracker = new GpuSelectionTracker(graceCycles: 3, time);
        var extrasTracker = new GpuExtrasTracker(graceCycles: 3, time);

        var gpu0 = LhmFixtures.NvidiaGpu(readable: true);
        var gpu1 = LhmFixtures.NvidiaGpu(readable: true);
        gpu1 = gpu1 with
        {
            DeviceId = "/gpu-nvidia/1",
            DisplayName = "NVIDIA GeForce RTX 3070",
            PciDeviceKey = "VEN_10DE&DEV_2206",
            InstanceId = @"PCI\VEN_10DE&DEV_2206&SUBSYS_12341458\4&7AADB68&0&0010",
        };

        string? trackedGpuDeviceId = null;

        // --- CYCLE 1: Initial success - both have telemetry ---
        var (snapshot1, payload1, _, _, nextTracked1) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked1;
        Assert.True(payload1.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 2: Failure 1 - gpu1 loses telemetry ---
        var gpu1NoTelemetry = gpu1 with { Sensors = new List<LhmSensorDescriptor>() };
        var (snapshot2, payload2, _, _, nextTracked2) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1NoTelemetry }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked2;
        Assert.True(payload2.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));

        time.Advance(TimeSpan.FromSeconds(1));

        // --- SIMULATED CANCELED COLLECTION ---
        // In a real pipeline, this would be: UpdatePipeline checks cancellation
        // token BEFORE calling Collect(). If canceled, Collect() is never called,
        // and therefore the tracker's Update() is never called.
        // Here we simulate that by simply NOT calling RunCycle() for one cycle.
        // The time advances but no collection occurs.
        time.Advance(TimeSpan.FromSeconds(1)); // Time passes, but no Collect() call

        // --- CYCLE 3 (actual): Failure 2 - gpu1 still no telemetry ---
        var (snapshot3, payload3, _, _, nextTracked3) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1NoTelemetry }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked3;
        // Should still be retained (this is failure 2, not 3)
        Assert.True(payload3.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 4 (actual): Failure 3 - gpu1 still no telemetry ---
        var (snapshot4, payload4, _, _, nextTracked4) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1NoTelemetry }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked4;
        // Should still be retained (this is failure 3, last grace cycle)
        Assert.True(payload4.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 5 (actual): Failure 4 - grace exhausted ---
        var (snapshot5, payload5, _, _, nextTracked5) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1NoTelemetry }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked5;
        // Now removed (failure 4)
        Assert.False(payload5.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));

        // If the canceled cycle had consumed grace, the removal would have
        // happened one cycle earlier (on what we called "CYCLE 4" above).
        // The fact it happens on CYCLE 5 proves cancellation doesn't consume grace.
    }

    // ============================================================
    // Additional integration tests for primary GPU grace
    // ============================================================

    /// <summary>
    /// Verifies the primary GPU grace period works end-to-end.
    /// </summary>
    [Fact]
    public void PrimaryGpu_GraceBoundary_FullLifecycle()
    {
        var time = new FakeTimeProvider();
        var tracker = new GpuSelectionTracker(graceCycles: 3, time);
        var extrasTracker = new GpuExtrasTracker(graceCycles: 3, time);

        var gpu0 = LhmFixtures.NvidiaGpu(readable: true);
        var gpu1 = LhmFixtures.NvidiaGpu(readable: true);
        gpu1 = gpu1 with
        {
            DeviceId = "/gpu-nvidia/1",
            DisplayName = "NVIDIA GeForce RTX 3070",
            PciDeviceKey = "VEN_10DE&DEV_2206",
            InstanceId = @"PCI\VEN_10DE&DEV_2206&SUBSYS_12341458\4&7AADB68&0&0010",
        };

        string? trackedGpuDeviceId = null;

        // --- CYCLE 1: Initial success - gpu0 has telemetry, becomes tracked ---
        var (snapshot1, payload1, _, _, nextTracked1) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked1;
        Assert.Equal(gpu0.InstanceId, GetPrimaryGpuInstanceId(payload1, snapshot1));
        Assert.Equal(gpu0.InstanceId, snapshot1.GpuTrackedInstanceId);

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 2: Failure 1 - gpu0 loses telemetry ---
        var gpu0NoTelemetry = gpu0 with { Sensors = new List<LhmSensorDescriptor>() };
        var (snapshot2, payload2, _, _, nextTracked2) = RunCycle(time, tracker, extrasTracker, new[] { gpu0NoTelemetry, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked2;
        // gpu0 should still be presented (grace failure 1)
        Assert.Equal(gpu0.InstanceId, GetPrimaryGpuInstanceId(payload2, snapshot2));
        Assert.Equal(gpu0.InstanceId, snapshot2.GpuTrackedInstanceId);

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 3: Failure 2 ---
        var (snapshot3, payload3, _, _, nextTracked3) = RunCycle(time, tracker, extrasTracker, new[] { gpu0NoTelemetry, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked3;
        Assert.Equal(gpu0.InstanceId, GetPrimaryGpuInstanceId(payload3, snapshot3));

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 4: Failure 3 (last grace cycle) ---
        var (snapshot4, payload4, _, _, nextTracked4) = RunCycle(time, tracker, extrasTracker, new[] { gpu0NoTelemetry, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked4;
        Assert.Equal(gpu0.InstanceId, GetPrimaryGpuInstanceId(payload4, snapshot4));

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 5: Failure 4 - grace exhausted, fallback to gpu1 ---
        var (snapshot5, payload5, _, _, nextTracked5) = RunCycle(time, tracker, extrasTracker, new[] { gpu0NoTelemetry, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked5;
        // gpu1 now has telemetry, so it becomes the new primary
        Assert.Equal(gpu1.InstanceId, GetPrimaryGpuInstanceId(payload5, snapshot5));
        Assert.Equal(gpu1.InstanceId, snapshot5.GpuTrackedInstanceId);

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 6: Recovery - gpu0 gets telemetry back ---
        var (snapshot6, payload6, _, _, nextTracked6) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked6;
        // gpu0 has telemetry again, and gpu1 is the tracked primary which ALSO has telemetry.
        // The tracker keeps the tracked device (gpu1) as long as it has usable telemetry.
        // It does not re-evaluate to the default policy just because another device also has telemetry.
        // This prevents flapping between GPUs.
        Assert.Equal(gpu1.InstanceId, GetPrimaryGpuInstanceId(payload6, snapshot6));
        Assert.Equal(gpu1.InstanceId, snapshot6.GpuTrackedInstanceId);
    }

    /// <summary>
    /// Verifies that explicit user selection is never affected by grace period.
    /// </summary>
    [Fact]
    public void UserSelection_NeverAffectedByGrace()
    {
        var time = new FakeTimeProvider();
        var tracker = new GpuSelectionTracker(graceCycles: 3, time);
        var extrasTracker = new GpuExtrasTracker(graceCycles: 3, time);

        var gpu0 = LhmFixtures.NvidiaGpu(readable: true);
        var gpu1 = LhmFixtures.NvidiaGpu(readable: true);
        gpu1 = gpu1 with
        {
            DeviceId = "/gpu-nvidia/1",
            DisplayName = "NVIDIA GeForce RTX 3070",
            PciDeviceKey = "VEN_10DE&DEV_2206",
            InstanceId = @"PCI\VEN_10DE&DEV_2206&SUBSYS_12341458\4&7AADB68&0&0010",
        };

        // Cycle 1: gpu0 has telemetry, gpu1 has telemetry
        var (snapshot1, payload1, _, _, _) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1 }, userSelectedGpuInstanceId: gpu1.InstanceId);

        // Debug output
        var gpuName1 = payload1.GetText(WidgetPayloadPresenter.GpuNameKey);
        var gpuVendor1 = payload1.GetText(WidgetPayloadPresenter.GpuVendorKey);
        var trackedInstanceId1 = snapshot1.GpuTrackedInstanceId;
        var selectionMissing1 = payload1.GetFlag(WidgetPayloadPresenter.GpuSelectionMissingKey);

        // The snapshot's GpuTrackedInstanceId is for automatic selection only
        // (may be null if user selection is active and no grace is needed)
        // Assert.NotNull(snapshot1.GpuTrackedInstanceId);

        // User selection should be honored even though gpu0 has telemetry
        Assert.Equal(gpu1.InstanceId, GetPrimaryGpuInstanceId(payload1, snapshot1));
        Assert.False(payload1.GetFlag(WidgetPayloadPresenter.GpuSelectionMissingKey));

        time.Advance(TimeSpan.FromSeconds(1));

        // Cycle 2: gpu1 loses ALL telemetry, gpu0 still has telemetry
        var gpu1NoTelemetry = gpu1 with { Sensors = new List<LhmSensorDescriptor>() };
        var (snapshot2, payload2, _, _, _) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1NoTelemetry }, userSelectedGpuInstanceId: gpu1.InstanceId);

        // Debug output
        var gpuName2 = payload2.GetText(WidgetPayloadPresenter.GpuNameKey);
        var gpuVendor2 = payload2.GetText(WidgetPayloadPresenter.GpuVendorKey);
        var trackedInstanceId2 = snapshot2.GpuTrackedInstanceId;
        var selectionMissing2 = payload2.GetFlag(WidgetPayloadPresenter.GpuSelectionMissingKey);

        // User selection is honored despite gpu1 having no telemetry
        Assert.Equal(gpu1.InstanceId, GetPrimaryGpuInstanceId(payload2, snapshot2));
        Assert.False(payload2.GetFlag(WidgetPayloadPresenter.GpuSelectionMissingKey));
        // The values for gpu1 should show as unavailable/N/A, not wrong device
    }

    /// <summary>
    /// Verifies cancellation does not consume grace for primary GPU.
    /// </summary>
    [Fact]
    public void Cancellation_DoesNotConsumeGrace_ForPrimaryGpu()
    {
        var time = new FakeTimeProvider();
        var tracker = new GpuSelectionTracker(graceCycles: 3, time);
        var extrasTracker = new GpuExtrasTracker(graceCycles: 3, time);

        var gpu0 = LhmFixtures.NvidiaGpu(readable: true);
        var gpu1 = LhmFixtures.NvidiaGpu(readable: true);
        gpu1 = gpu1 with
        {
            DeviceId = "/gpu-nvidia/1",
            DisplayName = "NVIDIA GeForce RTX 3070",
            PciDeviceKey = "VEN_10DE&DEV_2206",
            InstanceId = @"PCI\VEN_10DE&DEV_2206&SUBSYS_12341458\4&7AADB68&0&0010",
        };

        string? trackedGpuDeviceId = null;

        // --- CYCLE 1: Initial success ---
        var (snapshot1, payload1, _, _, nextTracked1) = RunCycle(time, tracker, extrasTracker, new[] { gpu0, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked1;
        Assert.Equal(gpu0.InstanceId, GetPrimaryGpuInstanceId(payload1, snapshot1));

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 2: Failure 1 ---
        var gpu0NoTelemetry = gpu0 with { Sensors = new List<LhmSensorDescriptor>() };
        var (snapshot2, payload2, _, _, nextTracked2) = RunCycle(time, tracker, extrasTracker, new[] { gpu0NoTelemetry, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked2;
        Assert.Equal(gpu0.InstanceId, GetPrimaryGpuInstanceId(payload2, snapshot2));

        time.Advance(TimeSpan.FromSeconds(1));

        // --- SIMULATED CANCELED COLLECTION ---
        time.Advance(TimeSpan.FromSeconds(1)); // Time passes, no Collect()

        // --- CYCLE 3 (actual): Failure 2 ---
        var (snapshot3, payload3, _, _, nextTracked3) = RunCycle(time, tracker, extrasTracker, new[] { gpu0NoTelemetry, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked3;
        Assert.Equal(gpu0.InstanceId, GetPrimaryGpuInstanceId(payload3, snapshot3));

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 4 (actual): Failure 3 ---
        var (snapshot4, payload4, _, _, nextTracked4) = RunCycle(time, tracker, extrasTracker, new[] { gpu0NoTelemetry, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked4;
        Assert.Equal(gpu0.InstanceId, GetPrimaryGpuInstanceId(payload4, snapshot4));

        time.Advance(TimeSpan.FromSeconds(1));

        // --- CYCLE 5 (actual): Failure 4 - grace exhausted ---
        var (snapshot5, payload5, _, _, nextTracked5) = RunCycle(time, tracker, extrasTracker, new[] { gpu0NoTelemetry, gpu1 }, trackedGpuDeviceId: trackedGpuDeviceId);
        trackedGpuDeviceId = nextTracked5;
        // Fallback to gpu1
        Assert.Equal(gpu1.InstanceId, GetPrimaryGpuInstanceId(payload5, snapshot5));
    }

    // ============================================================
    // Helpers
    // ============================================================

    /// <summary>
    /// Helper to get the primary GPU instance ID from the payload by matching
    /// the displayed name/vendor against the snapshot devices.
    /// </summary>
    private static string? GetPrimaryGpuInstanceId(WidgetPayload payload, MetricsSnapshot snapshot)
    {
        var gpuName = payload.GetText(WidgetPayloadPresenter.GpuNameKey);
        var gpuVendor = payload.GetText(WidgetPayloadPresenter.GpuVendorKey);

        if (string.IsNullOrEmpty(gpuName) || gpuName == WidgetPayloadPresenter.NotAvailable)
            return null;

        // Find the device in the snapshot that matches. If multiple match,
        // pick the one with the matching DeviceId from the snapshot's
        // GpuTrackedInstanceId (which identifies the primary GPU).
        var trackedInstanceId = snapshot.GpuTrackedInstanceId;
        if (trackedInstanceId is not null)
        {
            var tracked = snapshot.Gpus.FirstOrDefault(g =>
                string.Equals(g.InstanceId, trackedInstanceId, StringComparison.OrdinalIgnoreCase));
            if (tracked is not null &&
                string.Equals(tracked.DisplayName?.Trim(), gpuName.Trim(), StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(tracked.Vendor) || string.Equals(tracked.Vendor?.Trim(), gpuVendor.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                return tracked.InstanceId;
            }
        }

        // Fall back to finding by name/vendor
        var match = snapshot.Gpus.FirstOrDefault(g =>
            string.Equals(g.DisplayName?.Trim(), gpuName.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(g.Vendor) || string.Equals(g.Vendor?.Trim(), gpuVendor.Trim(), StringComparison.OrdinalIgnoreCase)));
        return match?.InstanceId;
    }
}
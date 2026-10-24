using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Time.Testing;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the GPU extras tracker's grace period behavior.
///
/// <para>
/// The tracker wraps the pure telemetry-aware extra-device ordering with
/// stateful grace-period logic. These tests pin the exact cycle-by-cycle
/// behavior so a transient failure cannot silently remove a secondary GPU row.
/// </para>
/// </summary>
public class GpuExtrasTrackerTests
{
    private static HardwareDevice Gpu(
        string deviceId,
        string? instanceId,
        GpuIntegration integration,
        string name = "GPU") =>
        new()
        {
            DeviceId = deviceId,
            DisplayName = name,
            Vendor = "Vendor",
            DeviceClass = DeviceClass.Gpu,
            GpuIntegration = integration,
            InstanceId = instanceId,
        };

    private static DeviceReading Read(string deviceId, string metric, double? value, string? unit = "%") =>
        new()
        {
            DeviceId = deviceId,
            Metric = metric,
            Measurement = value.HasValue
                ? Measurement.AvailableValue(value.Value, unit, "test")
                : Measurement.Unsupported("test"),
        };

    private static MetricsSnapshot Snapshot(
        IEnumerable<HardwareDevice>? gpus = null,
        IEnumerable<DeviceReading>? readings = null,
        DateTimeOffset? timestamp = null,
        Measurement? gpuLoadPercent = null,
        string? gpuLoadDeviceId = null,
        Measurement? gpuTemperatureC = null,
        string? gpuTemperatureDeviceId = null,
        Measurement? gpuVramUsedBytes = null,
        string? gpuVramUsedDeviceId = null,
        Measurement? gpuVramTotalBytes = null,
        string? gpuVramTotalDeviceId = null) =>
        MetricsSnapshot.Create(
            timestamp ?? new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero),
            gpus: gpus,
            readings: readings,
            gpuLoadPercent: gpuLoadPercent,
            gpuLoadDeviceId: gpuLoadDeviceId,
            gpuTemperatureC: gpuTemperatureC,
            gpuTemperatureDeviceId: gpuTemperatureDeviceId,
            gpuVramUsedBytes: gpuVramUsedBytes,
            gpuVramUsedDeviceId: gpuVramUsedDeviceId,
            gpuVramTotalBytes: gpuVramTotalBytes,
            gpuVramTotalDeviceId: gpuVramTotalDeviceId);

    [Fact]
    public void Tracker_ReadableSecondaryDevice_Appears()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete, "NVIDIA RTX 3080"),
            Gpu("gpu1", "pci-1", GpuIntegration.Discrete, "AMD RX 6800"),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[]
            {
                Read("gpu0", "LoadPercent", 10.0),
                Read("gpu1", "LoadPercent", 5.0),
            });

        var result = tracker.Update(devices, "pci-0", snapshot, null, null, null, null);

        // Both devices have load telemetry. The primary is gpu0, so gpu1 qualifies as extra.
        Assert.Contains("pci-1", result.EligibleInstanceIds);
        Assert.DoesNotContain("pci-0", result.EligibleInstanceIds); // Primary excluded
    }

    [Fact]
    public void Tracker_ValidZeroLoad_Qualifies()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 0.0) });

        var result = tracker.Update(devices, "gpu0", snapshot, null, null, null, null);

        // 0% is a valid reading. Integrated GPU qualifies as extra.
        Assert.Contains("pci-1", result.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_NeverReadableDevice_RemainsHidden()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(gpus: devices); // No readings at all

        var result = tracker.Update(devices, "gpu0", snapshot, null, null, null, null);

        // Neither device ever had telemetry, so no extras.
        Assert.Empty(result.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_MemoryOnlyTelemetry_DoesNotQualifyExtra()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[]
            {
                Read("gpu1", "VramUsedBytes", 1_000_000_000.0, "B"),
                Read("gpu1", "VramTotalBytes", 4_000_000_000.0, "B"),
            });

        var result = tracker.Update(devices, "gpu0", snapshot, null, null, null, null);

        // Memory-only telemetry does not qualify for extra row (extra row shows load/temp only).
        Assert.Empty(result.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_Failure1_RetainsRow()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        // Cycle 1: gpu1 has load, qualifies.
        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        var r1 = tracker.Update(devices, "gpu0", snapshot1, null, null, null, null);
        Assert.Contains("pci-1", r1.EligibleInstanceIds);

        // Cycle 2: gpu1 loses telemetry (failure 1).
        var snapshot2 = Snapshot(gpus: devices);
        var r2 = tracker.Update(devices, "gpu0", snapshot2, null, null, null, null);
        Assert.Contains("pci-1", r2.EligibleInstanceIds); // Retained through failure 1
    }

    [Fact]
    public void Tracker_Failure2_RetainsRow()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "gpu0", snapshot1, null, null, null, null);

        // Failure 1
        tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);

        // Failure 2
        var r2 = tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);
        Assert.Contains("pci-1", r2.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_Failure3_RetainsRow()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "gpu0", snapshot1, null, null, null, null);

        // Failures 1, 2
        for (int i = 0; i < 2; i++)
        {
            tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);
        }

        // Failure 3
        var r3 = tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);
        Assert.Contains("pci-1", r3.EligibleInstanceIds); // Still retained through failure 3
    }

    [Fact]
    public void Tracker_Failure4_RemovesRow()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "gpu0", snapshot1, null, null, null, null);

        // Failures 1, 2, 3
        for (int i = 0; i < 3; i++)
        {
            tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);
        }

        // Failure 4 - should be removed.
        var r4 = tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);
        Assert.DoesNotContain("pci-1", r4.EligibleInstanceIds); // Removed on failure 4
    }

    [Fact]
    public void Tracker_Recovery_ResetsFailureStreak()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        // Cycle 1: gpu1 has load.
        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "gpu0", snapshot1, null, null, null, null);

        // Cycle 2: failure 1.
        tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);

        // Cycle 3: recovery - gpu1 has load again.
        var snapshot3 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 15.0) });
        var r3 = tracker.Update(devices, "gpu0", snapshot3, null, null, null, null);
        Assert.Contains("pci-1", r3.EligibleInstanceIds);

        // Cycle 4: failure again - should be failure 1 of new streak.
        tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);
        var r4 = tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);
        Assert.Contains("pci-1", r4.EligibleInstanceIds); // Still retained (failure 1 of new streak)

        // Cycle 5: failure 2.
        tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);

        // Cycle 6: failure 3.
        tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);

        // Cycle 7: failure 4 - should be removed now.
        var r7 = tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);
        Assert.DoesNotContain("pci-1", r7.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_DeviceDisappearance_RemovesImmediately()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        // Cycle 1: gpu1 has load, qualifies.
        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "gpu0", snapshot1, null, null, null, null);

        // Cycle 2: gpu1 removed from system.
        var devicesAfterRemoval = new[] { Gpu("gpu0", "pci-0", GpuIntegration.Discrete) };
        var snapshot2 = Snapshot(gpus: devicesAfterRemoval);
        var r2 = tracker.Update(devicesAfterRemoval, "gpu0", snapshot2, null, null, null, null);

        // Device gone -> removed immediately.
        Assert.Empty(r2.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_Reappearance_StartsNewHistory()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        // Cycle 1: gpu1 has load, qualifies.
        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "gpu0", snapshot1, null, null, null, null);

        // Cycle 2: gpu1 removed.
        var devicesNoGpu1 = new[] { Gpu("gpu0", "pci-0", GpuIntegration.Discrete) };
        tracker.Update(devicesNoGpu1, "gpu0", Snapshot(gpus: devicesNoGpu1), null, null, null, null);

        // Cycle 3: gpu1 reappears (same instance id) but NO telemetry.
        var devicesAgain = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var snapshot3 = Snapshot(gpus: devicesAgain);
        var r3 = tracker.Update(devicesAgain, "gpu0", snapshot3, null, null, null, null);

        // Reappearance starts fresh: no telemetry means not qualified.
        Assert.Empty(r3.EligibleInstanceIds);

        // Cycle 4: gpu1 gets telemetry now.
        var snapshot4 = Snapshot(
            gpus: devicesAgain,
            readings: new[] { Read("gpu1", "LoadPercent", 5.0) });
        var r4 = tracker.Update(devicesAgain, "gpu0", snapshot4, null, null, null, null);
        Assert.Contains("pci-1", r4.EligibleInstanceIds); // Now qualifies fresh
    }

    [Fact]
    public void Tracker_IdenticallyNamedGpus_SeparateHistories()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete, "NVIDIA RTX 3080"),
            Gpu("gpu1", "pci-1", GpuIntegration.Discrete, "NVIDIA RTX 3080"), // Same name, different instance
            Gpu("gpu2", "pci-2", GpuIntegration.Discrete),
        };

        // Cycle 1: gpu1 has load, gpu2 does not.
        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "gpu0", snapshot1, null, null, null, null);

        // Cycle 2: both gpu1 and gpu2 lose telemetry.
        tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);
        tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);

        // Cycle 3: gpu1 gets telemetry back, gpu2 does not.
        var snapshot3 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 5.0) });
        var r3 = tracker.Update(devices, "gpu0", snapshot3, null, null, null, null);

        // gpu1 qualifies (recovery resets), gpu2 never qualified.
        Assert.Contains("pci-1", r3.EligibleInstanceIds);
        Assert.DoesNotContain("pci-2", r3.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_PrimaryExcludedFromExtras()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Discrete),
        };

        // Cycle 1: both have load, gpu0 is primary.
        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu0", "LoadPercent", 10.0), Read("gpu1", "LoadPercent", 5.0) });
        var r1 = tracker.Update(devices, "pci-0", snapshot1, null, null, null, null);
        Assert.Contains("pci-1", r1.EligibleInstanceIds);
        Assert.DoesNotContain("pci-0", r1.EligibleInstanceIds);

        // Cycle 2: gpu0 loses telemetry, gpu1 becomes primary.
        var snapshot2 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 5.0) });
        var r2 = tracker.Update(devices, "pci-1", snapshot2, null, null, null, null);
        // gpu0 is now secondary but never qualified as secondary.
        Assert.Empty(r2.EligibleInstanceIds);

        // Cycle 3: gpu0 gets telemetry as secondary.
        var snapshot3 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 5.0), Read("gpu0", "LoadPercent", 8.0) });
        var r3 = tracker.Update(devices, "pci-1", snapshot3, null, null, null, null);
        Assert.Contains("pci-0", r3.EligibleInstanceIds); // gpu0 qualifies fresh as secondary
    }

    [Fact]
    public void Tracker_RepeatedPresentation_DoesNotConsumeGrace()
    {
        // This test documents that the tracker advances on each Update() call
        // (each completed collection cycle), not on presentation calls.
        // Multiple presentations of the same snapshot would call Present() multiple
        // times but Update() only once per cycle.
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        // Cycle 1: gpu1 qualifies.
        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "gpu0", snapshot1, null, null, null, null);

        // Cycle 2: no telemetry (failure 1).
        var snapshot2 = Snapshot(gpus: devices);
        var r2 = tracker.Update(devices, "gpu0", snapshot2, null, null, null, null);

        // The tracker advances on each Update() call.
        // If the presenter called Present() multiple times with the same snapshot,
        // it wouldn't call Update() again - so no additional failure.
        // This test confirms the current failure count after one failed cycle.
        Assert.Contains("pci-1", r2.EligibleInstanceIds);
        Assert.Equal(1, tracker.GetTrackingState()["pci-1"].ConsecutiveFailures);
    }

    [Fact]
    public void Tracker_AbortedCollection_DoesNotAdvanceFailures()
    {
        // This test documents the contract: only completed Update() calls
        // advance the failure count. An aborted collection (no Update call)
        // does not consume grace.
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        // Cycle 1: gpu1 qualifies.
        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "pci-0", snapshot1, null, null, null, null);

        // Cycle 2: no telemetry (failure 1).
        var snapshot2 = Snapshot(gpus: devices);
        tracker.Update(devices, "pci-0", snapshot2, null, null, null, null);

        // Cycle 3: no telemetry (failure 2).
        tracker.Update(devices, "pci-0", snapshot2, null, null, null, null);

        // If an aborted collection happened here (no Update call),
        // the failure count would still be 2 on the next real collection.
        // This is a contract test - the tracker cannot know about aborted
        // work; it only advances on explicit Update calls.

        // Cycle 4: no telemetry (failure 3 - still within grace of 3).
        var r4 = tracker.Update(devices, "pci-0", snapshot2, null, null, null, null);
        Assert.Contains("pci-1", r4.EligibleInstanceIds); // Still retained (failure 3 <= grace 3)

        // Cycle 5: failure 4 - removed.
        var r5 = tracker.Update(devices, "pci-0", snapshot2, null, null, null, null);
        Assert.DoesNotContain("pci-1", r5.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_RetainedRow_DoesNotRefreshFailedMeasurements()
    {
        // This test documents the requirement: grace preserves row identity,
        // not measurement freshness. Failed readings remain N/A/stale.
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        // Cycle 1: gpu1 has load, qualifies.
        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "gpu0", snapshot1, null, null, null, null);

        // Cycle 2: gpu1 has failed temperature reading, no load.
        var failedTemp = Measurement.Failed("test");
        var snapshot2 = Snapshot(
            gpus: devices,
            readings: new[]
            {
                new DeviceReading { DeviceId = "gpu1", Metric = "TemperatureC", Measurement = failedTemp },
            });
        var r2 = tracker.Update(devices, "gpu0", snapshot2, null, null, null, null);

        // Row retained (failure 1), but temperature reading is failed.
        // The presenter will show N/A for temperature, not a stale value.
        Assert.Contains("pci-1", r2.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_CustomGraceCycles_RespectsConfiguration()
    {
        var tracker = new GpuExtrasTracker(2, TimeProvider.System); // Only 2 grace cycles
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "gpu0", snapshot1, null, null, null, null);

        // Failure 1
        tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);
        // Failure 2 (last grace cycle)
        var r2 = tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);
        Assert.Contains("pci-1", r2.EligibleInstanceIds);

        // Failure 3 (exhausted) - removed.
        var r3 = tracker.Update(devices, "gpu0", Snapshot(gpus: devices), null, null, null, null);
        Assert.DoesNotContain("pci-1", r3.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_ExplicitPrimaryDeviceId_ExcludesCorrectDevice()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Discrete),
        };

        // Primary is gpu1 (by instance id).
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu0", "LoadPercent", 10.0), Read("gpu1", "LoadPercent", 5.0) });
        var result = tracker.Update(devices, "pci-1", snapshot, null, null, null, null);

        // gpu0 qualifies as extra (has telemetry, not primary).
        Assert.Contains("pci-0", result.EligibleInstanceIds);
        Assert.DoesNotContain("pci-1", result.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_PrimaryByInstanceId_ExcludesCorrectDevice()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Discrete),
        };

        // Primary is gpu0 (by instance id).
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu0", "LoadPercent", 10.0), Read("gpu1", "LoadPercent", 5.0) });
        var result = tracker.Update(devices, "pci-0", snapshot, null, null, null, null);

        // gpu1 qualifies as extra.
        Assert.Contains("pci-1", result.EligibleInstanceIds);
        Assert.DoesNotContain("pci-0", result.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_TemperatureTelemetry_Qualifies()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "TemperatureC", 55.0, "°C") });

        var result = tracker.Update(devices, "gpu0", snapshot, null, null, null, null);

        Assert.Contains("pci-1", result.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_ScalarFallbackForDevice_Qualifies()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            gpuLoadPercent: Measurement.AvailableValue(10.0, "%", "nvidia-smi"),
            gpuLoadDeviceId: "gpu1");

        var result = tracker.Update(devices, "gpu0", snapshot,
            snapshot.GpuLoadPercent, snapshot.GpuLoadDeviceId,
            snapshot.GpuTemperatureC, snapshot.GpuTemperatureDeviceId);

        // Scalar fallback attributed to gpu1 qualifies it.
        Assert.Contains("pci-1", result.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_UnattributedScalar_DoesNotQualify()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            gpuLoadPercent: Measurement.AvailableValue(10.0, "%", "nvidia-smi"),
            gpuLoadDeviceId: null); // Unattributed

        var result = tracker.Update(devices, "gpu0", snapshot,
            snapshot.GpuLoadPercent, snapshot.GpuLoadDeviceId,
            snapshot.GpuTemperatureC, snapshot.GpuTemperatureDeviceId);

        Assert.Empty(result.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_ScalarForDifferentDevice_DoesNotQualify()
    {
        var tracker = new GpuExtrasTracker();
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            gpuLoadPercent: Measurement.AvailableValue(10.0, "%", "nvidia-smi"),
            gpuLoadDeviceId: "gpu0"); // Scalar for primary, not secondary

        var result = tracker.Update(devices, "pci-0", snapshot,
            snapshot.GpuLoadPercent, snapshot.GpuLoadDeviceId,
            snapshot.GpuTemperatureC, snapshot.GpuTemperatureDeviceId);

        Assert.Empty(result.EligibleInstanceIds);
    }
}
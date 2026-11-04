using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Time.Testing;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Regression tests for GpuExtrasTracker enforcing load-only qualification
/// (load >= 5%, zero excluded) and a 6-second time-based retention window.
/// Uses FakeTimeProvider for deterministic time advancement.
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

    // ------------------------------------------------------------------
    // Qualification rules: only load >= 5% qualifies
    // ------------------------------------------------------------------

    [Fact]
    public void Tracker_QualifyingLoad_AboveThreshold_Qualifies()
    {
        var tracker = new GpuExtrasTracker(6, new FakeTimeProvider());
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });

        var result = tracker.Update(devices, "pci-0", snapshot, null, null, null, null);
        Assert.Contains("pci-1", result.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_QualifyingLoad_AtThreshold_Qualifies()
    {
        var tracker = new GpuExtrasTracker(6, new FakeTimeProvider());
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 5.0) });

        var result = tracker.Update(devices, "pci-0", snapshot, null, null, null, null);
        Assert.Contains("pci-1", result.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_ZeroLoad_DoesNotQualify()
    {
        var tracker = new GpuExtrasTracker(6, new FakeTimeProvider());
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 0.0) });

        var result = tracker.Update(devices, "pci-0", snapshot, null, null, null, null);
        Assert.Empty(result.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_LoadBelowThreshold_DoesNotQualify()
    {
        var tracker = new GpuExtrasTracker(6, new FakeTimeProvider());
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 3.0) });

        var result = tracker.Update(devices, "pci-0", snapshot, null, null, null, null);
        Assert.Empty(result.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_TemperatureOnly_DoesNotQualify()
    {
        var tracker = new GpuExtrasTracker(6, new FakeTimeProvider());
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "TemperatureC", 55.0, "°C") });

        var result = tracker.Update(devices, "pci-0", snapshot, null, null, null, null);
        Assert.Empty(result.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_MemoryOnly_DoesNotQualify()
    {
        var tracker = new GpuExtrasTracker(6, new FakeTimeProvider());
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

        var result = tracker.Update(devices, "pci-0", snapshot, null, null, null, null);
        Assert.Empty(result.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_DiscreteGpu_LowLoad_DoesNotQualify()
    {
        // Idle suppression applies to all devices: load < 5% does not qualify.
        var tracker = new GpuExtrasTracker(6, new FakeTimeProvider());
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Discrete),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 3.0) });

        var result = tracker.Update(devices, "pci-0", snapshot, null, null, null, null);
        Assert.Empty(result.EligibleInstanceIds);
    }

    // ------------------------------------------------------------------
    // 6-second time-based retention (not cycle-based)
    // ------------------------------------------------------------------

    [Fact]
    public void Tracker_Within6Seconds_RetainsRow()
    {
        var time = new FakeTimeProvider();
        var tracker = new GpuExtrasTracker(6, time);
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        // Qualify at T=0
        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "pci-0", snapshot1, null, null, null, null);

        // Advance 3 seconds, no telemetry
        time.Advance(TimeSpan.FromSeconds(3));
        var r2 = tracker.Update(devices, "pci-0", Snapshot(gpus: devices), null, null, null, null);
        Assert.Contains("pci-1", r2.EligibleInstanceIds); // Within 6s window
    }

    [Fact]
    public void Tracker_Exactly6Seconds_StillRetained()
    {
        var time = new FakeTimeProvider();
        var tracker = new GpuExtrasTracker(6, time);
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "pci-0", snapshot1, null, null, null, null);

        time.Advance(TimeSpan.FromSeconds(6));
        var r2 = tracker.Update(devices, "pci-0", Snapshot(gpus: devices), null, null, null, null);
        Assert.Contains("pci-1", r2.EligibleInstanceIds); // Still within <= 6s
    }

    [Fact]
    public void Tracker_Over6Seconds_RemovesRow()
    {
        var time = new FakeTimeProvider();
        var tracker = new GpuExtrasTracker(6, time);
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "pci-0", snapshot1, null, null, null, null);

        time.Advance(TimeSpan.FromSeconds(6.1));
        var r2 = tracker.Update(devices, "pci-0", Snapshot(gpus: devices), null, null, null, null);
        Assert.Empty(r2.EligibleInstanceIds); // Beyond 6s window
    }

    [Fact]
    public void Tracker_NewQualifyingLoad_RefreshesWindow()
    {
        var time = new FakeTimeProvider();
        var tracker = new GpuExtrasTracker(6, time);
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        var s1 = Snapshot(gpus: devices, readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "pci-0", s1, null, null, null, null);

        time.Advance(TimeSpan.FromSeconds(5));
        // At 5s, another qualifying load refreshes the window
        var s2 = Snapshot(gpus: devices, readings: new[] { Read("gpu1", "LoadPercent", 12.0) });
        tracker.Update(devices, "pci-0", s2, null, null, null, null);

        // Now advance another 5s from the refresh (total 10s from original)
        time.Advance(TimeSpan.FromSeconds(5));
        var r3 = tracker.Update(devices, "pci-0", Snapshot(gpus: devices), null, null, null, null);
        Assert.Contains("pci-1", r3.EligibleInstanceIds); // Window refreshed at 5s

        time.Advance(TimeSpan.FromSeconds(1));
        var r4 = tracker.Update(devices, "pci-0", Snapshot(gpus: devices), null, null, null, null);
        Assert.Empty(r4.EligibleInstanceIds); // 6s after refresh = 11s total
    }

    [Fact]
    public void Tracker_WithinWindow_NoTelemetry_DoesNotRefreshWindow()
    {
        // Temperature/memory must NOT refresh LastQualifiedAt
        var time = new FakeTimeProvider();
        var tracker = new GpuExtrasTracker(6, time);
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        var s1 = Snapshot(gpus: devices, readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "pci-0", s1, null, null, null, null);

        time.Advance(TimeSpan.FromSeconds(3));
        // Temperature reading only — should NOT extend retention window
        var s2 = Snapshot(gpus: devices, readings: new[] { Read("gpu1", "TemperatureC", 45.0, "°C") });
        tracker.Update(devices, "pci-0", s2, null, null, null, null);

        // Advance to 6.5s after ORIGINAL qualification (not refreshed)
        time.Advance(TimeSpan.FromSeconds(3.5));
        var r3 = tracker.Update(devices, "pci-0", Snapshot(gpus: devices), null, null, null, null);
        Assert.Empty(r3.EligibleInstanceIds); // Window not refreshed by temp
    }

    [Fact]
    public void Tracker_NeverQualified_NoRetentionWindow()
    {
        var time = new FakeTimeProvider();
        var tracker = new GpuExtrasTracker(6, time);
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };

        // No telemetry ever — should never be eligible
        var result = tracker.Update(devices, "pci-0", Snapshot(gpus: devices), null, null, null, null);
        Assert.Empty(result.EligibleInstanceIds);
    }

    // ------------------------------------------------------------------
    // Role transition and identity
    // ------------------------------------------------------------------

    [Fact]
    public void Tracker_PrimaryExcluded_FromExtras()
    {
        var tracker = new GpuExtrasTracker(6, new FakeTimeProvider());
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Discrete),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[]
            {
                Read("gpu0", "LoadPercent", 10.0),
                Read("gpu1", "LoadPercent", 5.0),
            });
        var result = tracker.Update(devices, "pci-1", snapshot, null, null, null, null);
        Assert.Contains("pci-0", result.EligibleInstanceIds);
        Assert.DoesNotContain("pci-1", result.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_DeviceDisappearance_RemovesImmediately()
    {
        var tracker = new GpuExtrasTracker(6, new FakeTimeProvider());
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var s1 = Snapshot(gpus: devices, readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "pci-0", s1, null, null, null, null);

        var devicesAfter = new[] { Gpu("gpu0", "pci-0", GpuIntegration.Discrete) };
        var r2 = tracker.Update(devicesAfter, "pci-0", Snapshot(gpus: devicesAfter), null, null, null, null);
        Assert.Empty(r2.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_Reappearance_RequiresFreshQualification()
    {
        var tracker = new GpuExtrasTracker(6, new FakeTimeProvider());
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var s1 = Snapshot(gpus: devices, readings: new[] { Read("gpu1", "LoadPercent", 10.0) });
        tracker.Update(devices, "pci-0", s1, null, null, null, null);

        var noGpu1 = new[] { Gpu("gpu0", "pci-0", GpuIntegration.Discrete) };
        tracker.Update(noGpu1, "pci-0", Snapshot(gpus: noGpu1), null, null, null, null);

        var again = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        // Reappearance without telemetry — should NOT qualify
        var r3 = tracker.Update(again, "pci-0", Snapshot(gpus: again), null, null, null, null);
        Assert.Empty(r3.EligibleInstanceIds);

        // Fresh qualification
        var s4 = Snapshot(gpus: again, readings: new[] { Read("gpu1", "LoadPercent", 5.0) });
        var r4 = tracker.Update(again, "pci-0", s4, null, null, null, null);
        Assert.Contains("pci-1", r4.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_SecondaryToPrimaryToSecondary_ResetsHistory()
    {
        var tracker = new GpuExtrasTracker(6, new FakeTimeProvider());
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Discrete),
        };

        // Cycle 1: primary gpu0, secondary gpu1 with telemetry -> qualifies
        var s1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu0", "LoadPercent", 10.0), Read("gpu1", "LoadPercent", 5.0) });
        tracker.Update(devices, "pci-0", s1, null, null, null, null);

        // Cycle 4: gpu1 becomes primary, excluded from extras
        var s4 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 5.0) });
        tracker.Update(devices, "pci-1", s4, null, null, null, null);

        // Cycle 5: gpu0 primary again, gpu1 secondary with telemetry -> fresh qualification
        var s5 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu0", "LoadPercent", 8.0), Read("gpu1", "LoadPercent", 5.0) });
        var r5 = tracker.Update(devices, "pci-0", s5, null, null, null, null);
        Assert.Contains("pci-1", r5.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_SecondaryToPrimaryToSecondary_NoTelemetry_DoesNotCarryOver()
    {
        var tracker = new GpuExtrasTracker(6, new FakeTimeProvider());
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Discrete),
        };
        var s1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu0", "LoadPercent", 10.0), Read("gpu1", "LoadPercent", 5.0) });
        tracker.Update(devices, "pci-0", s1, null, null, null, null);

        // gpu1 becomes primary
        var sPrimary = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu1", "LoadPercent", 5.0) });
        tracker.Update(devices, "pci-1", sPrimary, null, null, null, null);

        // Back to secondary with no telemetry — should NOT be eligible
        var s5 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu0", "LoadPercent", 8.0) });
        var r5 = tracker.Update(devices, "pci-0", s5, null, null, null, null);
        Assert.Empty(r5.EligibleInstanceIds);
    }

    // ------------------------------------------------------------------
    // Ordering with telemetry and grace devices
    // ------------------------------------------------------------------

    [Fact]
    public void Tracker_ReadableAndGrace_OrderedCorrectly()
    {
        var time = new FakeTimeProvider();
        var tracker = new GpuExtrasTracker(6, time);
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete, "Primary GPU"),
            Gpu("gpu1", "pci-1", GpuIntegration.Discrete, "Secondary With Telemetry"),
            Gpu("gpu2", "pci-2", GpuIntegration.Discrete, "Secondary In Grace"),
        };

        var s1 = Snapshot(
            gpus: devices,
            readings: new[]
            {
                Read("gpu0", "LoadPercent", 10.0),
                Read("gpu1", "LoadPercent", 5.0),
                Read("gpu2", "LoadPercent", 3.0),
            });
        tracker.Update(devices, "pci-0", s1, null, null, null, null);

        // Remove telemetry from gpu2
        var s2 = Snapshot(
            gpus: devices,
            readings: new[]
            {
                Read("gpu0", "LoadPercent", 10.0),
                Read("gpu1", "LoadPercent", 5.0),
            });
        var r2 = tracker.Update(devices, "pci-0", s2, null, null, null, null);
        Assert.Equal(2, r2.EligibleInstanceIds.Count);
        Assert.Equal("pci-1", r2.EligibleInstanceIds[0]); // telemetry first
        Assert.Equal("pci-2", r2.EligibleInstanceIds[1]); // grace second
    }

    // ------------------------------------------------------------------
    // Scalar fallback
    // ------------------------------------------------------------------

    [Fact]
    public void Tracker_ScalarFallback_QualifiesWhenAttributed()
    {
        var tracker = new GpuExtrasTracker(6, new FakeTimeProvider());
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            gpuLoadPercent: Measurement.AvailableValue(10.0, "%", "test"),
            gpuLoadDeviceId: "gpu1");
        var result = tracker.Update(devices, "pci-0", snapshot,
            snapshot.GpuLoadPercent, snapshot.GpuLoadDeviceId,
            snapshot.GpuTemperatureC, snapshot.GpuTemperatureDeviceId);
        Assert.Contains("pci-1", result.EligibleInstanceIds);
    }

    [Fact]
    public void Tracker_UnattributedScalar_DoesNotQualify()
    {
        var tracker = new GpuExtrasTracker(6, new FakeTimeProvider());
        var devices = new[]
        {
            Gpu("gpu0", "pci-0", GpuIntegration.Discrete),
            Gpu("gpu1", "pci-1", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            gpuLoadPercent: Measurement.AvailableValue(10.0, "%", "test"),
            gpuLoadDeviceId: null);
        var result = tracker.Update(devices, "pci-0", snapshot,
            snapshot.GpuLoadPercent, snapshot.GpuLoadDeviceId,
            snapshot.GpuTemperatureC, snapshot.GpuTemperatureDeviceId);
        Assert.Empty(result.EligibleInstanceIds);
    }
}

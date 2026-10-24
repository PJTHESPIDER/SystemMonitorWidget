using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Time.Testing;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the GPU selection tracker's grace period behavior.
///
/// <para>
/// The tracker wraps the pure telemetry-aware ranking with stateful grace
/// period logic. These tests pin the exact cycle-by-cycle behavior so a
/// transient failure cannot silently switch the presented GPU.
/// </para>
/// </summary>
public class GpuSelectionTrackerTests
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
    public void Tracker_WithNoSnapshot_UsesStaticOrdering()
    {
        var tracker = new GpuSelectionTracker();
        var devices = new[]
        {
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
        };

        var selected = tracker.Update(devices, null, null, null, null, null, null, null, null, null, null);

        Assert.Equal("gpu-d", selected!.DeviceId);
    }

    [Fact]
    public void Tracker_PrefersDeviceWithUsableTelemetry()
    {
        var tracker = new GpuSelectionTracker();
        var devices = new[]
        {
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu-i", "LoadPercent", 10.0) });

        var selected = tracker.Update(devices, null, snapshot, null, null, null, null, null, null, null, null);

        // The integrated GPU has a load reading, so it wins despite being
        // integrated (telemetry availability beats integration class).
        Assert.Equal("gpu-i", selected!.DeviceId);
        Assert.Equal("pci-i", tracker.TrackedInstanceId);
    }

    [Fact]
    public void Tracker_ValidZeroLoadIsUsableTelemetry()
    {
        var tracker = new GpuSelectionTracker();
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu-i", "LoadPercent", 0.0) });

        var selected = tracker.Update(devices, null, snapshot, null, null, null, null, null, null, null, null);

        // 0% is a valid reading, not unavailable. The integrated GPU has
        // usable telemetry and wins.
        Assert.Equal("gpu-i", selected!.DeviceId);
        Assert.Equal("pci-i", tracker.TrackedInstanceId);
    }

    [Fact]
    public void Tracker_StaleReadingDoesNotCountAsUsable()
    {
        var tracker = new GpuSelectionTracker();
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };
        var stale = Measurement.Stale(DateTimeOffset.UtcNow.AddMinutes(-5), "test");
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[]
            {
                new DeviceReading { DeviceId = "gpu-i", Metric = "LoadPercent", Measurement = stale },
            });

        var selected = tracker.Update(devices, null, snapshot, null, null, null, null, null, null, null, null);

        // Stale reading does not count as usable. Discrete GPU wins by
        // integration class (no telemetry on either). Neither has usable
        // telemetry, so no device is tracked.
        Assert.Equal("gpu-d", selected!.DeviceId);
        Assert.Null(tracker.TrackedInstanceId);
    }

    [Fact]
    public void Tracker_FailedReadingDoesNotCountAsUsable()
    {
        var tracker = new GpuSelectionTracker();
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[] { Failed("gpu-i", "LoadPercent") });

        var selected = tracker.Update(devices, null, snapshot, null, null, null, null, null, null, null, null);

        // Failed reading does not count as usable.
        Assert.Equal("gpu-d", selected!.DeviceId);
    }

    [Fact]
    public void Tracker_UnsupportedReadingDoesNotCountAsUsable()
    {
        var tracker = new GpuSelectionTracker();
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu-i", "LoadPercent", null) });

        var selected = tracker.Update(devices, null, snapshot, null, null, null, null, null, null, null, null);

        Assert.Equal("gpu-d", selected!.DeviceId);
    }

    [Fact]
    public void Tracker_NullReadingDoesNotCountAsUsable()
    {
        var tracker = new GpuSelectionTracker();
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(gpus: devices); // No readings at all

        var selected = tracker.Update(devices, null, snapshot, null, null, null, null, null, null, null, null);

        Assert.Equal("gpu-d", selected!.DeviceId);
    }

    [Fact]
    public void Tracker_NonFiniteReadingDoesNotCountAsUsable()
    {
        var tracker = new GpuSelectionTracker();
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };
        var nanMeasurement = Measurement.AvailableValue(double.NaN, "%", "test");
        var infMeasurement = Measurement.AvailableValue(double.PositiveInfinity, "%", "test");

        var snapshotNan = Snapshot(
            gpus: devices,
            readings: new[] { new DeviceReading { DeviceId = "gpu-i", Metric = "LoadPercent", Measurement = nanMeasurement } });

        var snapshotInf = Snapshot(
            gpus: devices,
            readings: new[] { new DeviceReading { DeviceId = "gpu-i", Metric = "LoadPercent", Measurement = infMeasurement } });

        var selectedNan = tracker.Update(devices, null, snapshotNan, null, null, null, null, null, null, null, null);
        var selectedInf = tracker.Update(devices, null, snapshotInf, null, null, null, null, null, null, null, null);

        Assert.Equal("gpu-d", selectedNan!.DeviceId);
        Assert.Equal("gpu-d", selectedInf!.DeviceId);
    }

    [Fact]
    public void Tracker_ScalarFromDifferentDeviceDoesNotQualify()
    {
        var tracker = new GpuSelectionTracker();
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };
        // Scalar load describes the discrete GPU.
        var snapshot = Snapshot(
            gpus: devices,
            gpuLoadPercent: Measurement.AvailableValue(10.0, "%", "test"),
            gpuLoadDeviceId: "gpu-d");

        var selected = tracker.Update(devices, null, snapshot,
            snapshot.GpuLoadPercent, snapshot.GpuLoadDeviceId,
            snapshot.GpuTemperatureC, snapshot.GpuTemperatureDeviceId,
            snapshot.GpuVramUsedBytes, snapshot.GpuVramUsedDeviceId,
            snapshot.GpuVramTotalBytes, snapshot.GpuVramTotalDeviceId);

        // The scalar belongs to the discrete GPU, so it qualifies the discrete GPU.
        // Discrete has usable telemetry (via scalar) and wins by integration class.
        // The discrete GPU should be tracked because it has usable telemetry.
        Assert.Equal("gpu-d", selected!.DeviceId);
        Assert.Equal("pci-d", tracker.TrackedInstanceId);
    }

    [Fact]
    public void Tracker_UnattributedScalarDoesNotQualifyAnyDevice()
    {
        var tracker = new GpuSelectionTracker();
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            gpuLoadPercent: Measurement.AvailableValue(10.0, "%", "test"),
            gpuLoadDeviceId: null);

        var selected = tracker.Update(devices, null, snapshot, null, null, null, null, null, null, null, null);

        // Unattributed scalar qualifies neither device. Discrete wins by
        // integration class but has no usable telemetry, so no device is tracked.
        Assert.Equal("gpu-d", selected!.DeviceId);
        Assert.Null(tracker.TrackedInstanceId);
    }

    [Fact]
    public void Tracker_GracePeriodRetainsDeviceOnTransientFailure()
    {
        var clock = new FakeTimeProvider();
        var tracker = new GpuSelectionTracker(GpuSelectionTracker.DefaultGraceCycles, clock);
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };

        // Cycle 1: Discrete GPU has telemetry, becomes tracked.
        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu-d", "LoadPercent", 10.0) });
        var c1 = tracker.Update(devices, null, snapshot1, null, null, null, null, null, null, null, null);
        Assert.Equal("gpu-d", c1!.DeviceId);
        Assert.Equal("pci-d", tracker.TrackedInstanceId);
        Assert.Equal(0, tracker.ConsecutiveFailures);

        // Cycle 2: Discrete GPU loses telemetry (transient failure).
        var snapshot2 = Snapshot(gpus: devices);
        var c2 = tracker.Update(devices, null, snapshot2, null, null, null, null, null, null, null, null);
        Assert.Equal("gpu-d", c2!.DeviceId); // Still tracked during grace
        Assert.Equal("pci-d", tracker.TrackedInstanceId);
        Assert.Equal(1, tracker.ConsecutiveFailures);

        // Cycle 3: Still no telemetry, still in grace (default grace = 3).
        var snapshot3 = Snapshot(gpus: devices);
        var c3 = tracker.Update(devices, null, snapshot3, null, null, null, null, null, null, null, null);
        Assert.Equal("gpu-d", c3!.DeviceId);
        Assert.Equal(2, tracker.ConsecutiveFailures);

        // Cycle 4: Still no telemetry, still in grace (last grace cycle).
        var snapshot4 = Snapshot(gpus: devices);
        var c4 = tracker.Update(devices, null, snapshot4, null, null, null, null, null, null, null, null);
        Assert.Equal("gpu-d", c4!.DeviceId);
        Assert.Equal(3, tracker.ConsecutiveFailures);

        // Cycle 5: Grace exhausted. Tracker clears and re-evaluates.
        // Neither device has telemetry, so discrete wins by integration class.
        // But since neither has usable telemetry, no device is tracked.
        var snapshot5 = Snapshot(gpus: devices);
        var c5 = tracker.Update(devices, null, snapshot5, null, null, null, null, null, null, null, null);
        Assert.Equal("gpu-d", c5!.DeviceId);
        Assert.Null(tracker.TrackedInstanceId); // Tracker cleared after grace exhaustion
        Assert.Equal(0, tracker.ConsecutiveFailures); // Counter reset after clear
    }

    [Fact]
    public void Tracker_GracePeriodExhausted_AllowsFallbackWhenAlternativeHasTelemetry()
    {
        var clock = new FakeTimeProvider();
        var tracker = new GpuSelectionTracker(GpuSelectionTracker.DefaultGraceCycles, clock);
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };

        // Cycle 1: Discrete has telemetry.
        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu-d", "LoadPercent", 10.0) });
        tracker.Update(devices, null, snapshot1, null, null, null, null, null, null, null, null);

        // Cycles 2-4: Discrete loses telemetry (3 grace cycles).
        for (int i = 0; i < 3; i++)
        {
            var snapshot = Snapshot(gpus: devices);
            tracker.Update(devices, null, snapshot, null, null, null, null, null, null, null, null);
        }

        // Cycle 5: Grace exhausted. Now integrated GETS telemetry.
        var snapshot5 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu-i", "LoadPercent", 5.0) });
        var selected = tracker.Update(devices, null, snapshot5, null, null, null, null, null, null, null, null);

        // Integrated now has telemetry, discrete doesn't. Integrated wins.
        Assert.Equal("gpu-i", selected!.DeviceId);
        Assert.Equal("pci-i", tracker.TrackedInstanceId);
    }

    [Fact]
    public void Tracker_RecoveryResetsFailureCounterImmediately()
    {
        var clock = new FakeTimeProvider();
        var tracker = new GpuSelectionTracker(GpuSelectionTracker.DefaultGraceCycles, clock);
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };

        // Cycle 1: Discrete has telemetry.
        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu-d", "LoadPercent", 10.0) });
        tracker.Update(devices, null, snapshot1, null, null, null, null, null, null, null, null);

        // Cycle 2: Discrete loses telemetry.
        var snapshot2 = Snapshot(gpus: devices);
        tracker.Update(devices, null, snapshot2, null, null, null, null, null, null, null, null);
        Assert.Equal(1, tracker.ConsecutiveFailures);

        // Cycle 3: Discrete RECOVERS telemetry.
        var snapshot3 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu-d", "LoadPercent", 15.0) });
        var selected = tracker.Update(devices, null, snapshot3, null, null, null, null, null, null, null, null);

        Assert.Equal("gpu-d", selected!.DeviceId);
        Assert.Equal(0, tracker.ConsecutiveFailures); // Counter reset immediately
    }

    [Fact]
    public void Tracker_DeviceDisappeared_ClearsGraceImmediately()
    {
        var clock = new FakeTimeProvider();
        var tracker = new GpuSelectionTracker(GpuSelectionTracker.DefaultGraceCycles, clock);
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };

        // Cycle 1: Discrete has telemetry.
        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu-d", "LoadPercent", 10.0) });
        tracker.Update(devices, null, snapshot1, null, null, null, null, null, null, null, null);

        // Cycle 2: Discrete loses telemetry.
        var snapshot2 = Snapshot(gpus: devices);
        tracker.Update(devices, null, snapshot2, null, null, null, null, null, null, null, null);
        Assert.Equal("pci-d", tracker.TrackedInstanceId);

        // Cycle 3: Discrete GPU REMOVED from system.
        var devicesAfterRemoval = new[] { Gpu("gpu-i", "pci-i", GpuIntegration.Integrated) };
        var snapshot3 = Snapshot(gpus: devicesAfterRemoval);
        var selected = tracker.Update(devicesAfterRemoval, null, snapshot3, null, null, null, null, null, null, null, null);

        // Device gone -> tracker cleared immediately, integrated presented.
        Assert.Equal("gpu-i", selected!.DeviceId);
        Assert.Null(tracker.TrackedInstanceId);
        Assert.Equal(0, tracker.ConsecutiveFailures);
    }

    [Fact]
    public void Tracker_ExplicitUserSelection_BypassesGracePeriod()
    {
        var clock = new FakeTimeProvider();
        var tracker = new GpuSelectionTracker(GpuSelectionTracker.DefaultGraceCycles, clock);
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };

        // Cycle 1: Discrete has telemetry, tracked automatically.
        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu-d", "LoadPercent", 10.0) });
        tracker.Update(devices, null, snapshot1, null, null, null, null, null, null, null, null);
        Assert.Equal("pci-d", tracker.TrackedInstanceId);

        // Cycle 2: User explicitly selects integrated GPU (which has no telemetry).
        var snapshot2 = Snapshot(gpus: devices);
        var selected = tracker.Update(devices, "pci-i", snapshot2, null, null, null, null, null, null, null, null);

        Assert.Equal("gpu-i", selected!.DeviceId);
        Assert.Null(tracker.TrackedInstanceId); // User selection clears tracking
    }

    [Fact]
    public void Tracker_MemoryOnlyTelemetry_QualifiesPrimaryButNotExtra()
    {
        var tracker = new GpuSelectionTracker();
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };
        // Only VRAM readings on integrated GPU (no load/temp).
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[]
            {
                Read("gpu-i", "VramUsedBytes", 1_000_000_000.0, "B"),
                Read("gpu-i", "VramTotalBytes", 4_000_000_000.0, "B"),
            });

        var selected = tracker.Update(devices, null, snapshot, null, null, null, null, null, null, null, null);

        // Memory telemetry qualifies for primary selection.
        Assert.Equal("gpu-i", selected!.DeviceId);
        Assert.Equal("pci-i", tracker.TrackedInstanceId);
    }

    [Fact]
    public void Tracker_CollectionAndPresentationAgreeOnPrimary_WithFallbackScalar()
    {
        // The collection tracker selects the same device the presenter would
        // resolve, even when fallback scalars are involved.
        var clock = new FakeTimeProvider();
        var tracker = new GpuSelectionTracker(GpuSelectionTracker.DefaultGraceCycles, clock);
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete, "NVIDIA RTX 3080"),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated, "Intel UHD"),
        };

        // NVIDIA fallback provides load for discrete GPU; backend provides
        // nothing for integrated.
        var snapshot = Snapshot(
            gpus: devices,
            gpuLoadPercent: Measurement.AvailableValue(25.0, "%", "nvidia-smi"),
            gpuLoadDeviceId: "gpu-d");

        var selected = tracker.Update(devices, null, snapshot,
            snapshot.GpuLoadPercent, snapshot.GpuLoadDeviceId,
            snapshot.GpuTemperatureC, snapshot.GpuTemperatureDeviceId,
            snapshot.GpuVramUsedBytes, snapshot.GpuVramUsedDeviceId,
            snapshot.GpuVramTotalBytes, snapshot.GpuVramTotalDeviceId);

        // Collection tracker chooses discrete (has fallback scalar).
        Assert.Equal("gpu-d", selected!.DeviceId);
        Assert.Equal("pci-d", tracker.TrackedInstanceId);
        Assert.Equal("gpu-d", snapshot.GpuLoadDeviceId);

        // Presenter would resolve the same device.
        var presenterSelection = GpuSelectionPolicy.Resolve(
            devices, null, snapshot, snapshot.GpuLoadPercent, snapshot.GpuLoadDeviceId,
            snapshot.GpuTemperatureC, snapshot.GpuTemperatureDeviceId,
            snapshot.GpuVramUsedBytes, snapshot.GpuVramUsedDeviceId,
            snapshot.GpuVramTotalBytes, snapshot.GpuVramTotalDeviceId);

        Assert.Equal(selected.DeviceId, presenterSelection.Device!.DeviceId);
    }

    [Fact]
    public void Tracker_CustomGraceCycles_RespectsConfiguration()
    {
        var clock = new FakeTimeProvider();
        var tracker = new GpuSelectionTracker(2, clock); // Only 2 grace cycles
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };

        // Cycle 1: Discrete has telemetry.
        var snapshot1 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu-d", "LoadPercent", 10.0) });
        tracker.Update(devices, null, snapshot1, null, null, null, null, null, null, null, null);

        // Cycle 2: No telemetry (1st grace cycle).
        tracker.Update(devices, null, Snapshot(gpus: devices), null, null, null, null, null, null, null, null);
        Assert.Equal(1, tracker.ConsecutiveFailures);

        // Cycle 3: No telemetry (2nd grace cycle - last).
        tracker.Update(devices, null, Snapshot(gpus: devices), null, null, null, null, null, null, null, null);
        Assert.Equal(2, tracker.ConsecutiveFailures);

        // Cycle 4: Grace exhausted (3rd failure with 2 grace cycles = exhausted).
        var snapshot4 = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu-i", "LoadPercent", 5.0) });
        var selected = tracker.Update(devices, null, snapshot4, null, null, null, null, null, null, null, null);

        // Integrated now has telemetry and wins.
        Assert.Equal("gpu-i", selected!.DeviceId);
        Assert.Equal("pci-i", tracker.TrackedInstanceId);
    }

    [Fact]
    public void Tracker_TemperatureTelemetryAlsoCounts()
    {
        var tracker = new GpuSelectionTracker();
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[] { Read("gpu-i", "TemperatureC", 55.0, "°C") });

        var selected = tracker.Update(devices, null, snapshot, null, null, null, null, null, null, null, null);

        Assert.Equal("gpu-i", selected!.DeviceId);
        Assert.Equal("pci-i", tracker.TrackedInstanceId);
    }

    [Fact]
    public void Tracker_VramTelemetryAlsoCounts()
    {
        var tracker = new GpuSelectionTracker();
        var devices = new[]
        {
            Gpu("gpu-d", "pci-d", GpuIntegration.Discrete),
            Gpu("gpu-i", "pci-i", GpuIntegration.Integrated),
        };
        var snapshot = Snapshot(
            gpus: devices,
            readings: new[]
            {
                Read("gpu-i", "VramUsedBytes", 2_000_000_000.0, "B"),
            });

        var selected = tracker.Update(devices, null, snapshot, null, null, null, null, null, null, null, null);

        Assert.Equal("gpu-i", selected!.DeviceId);
        Assert.Equal("pci-i", tracker.TrackedInstanceId);
    }

    private static DeviceReading Failed(string deviceId, string metric) =>
        new() { DeviceId = deviceId, Metric = metric, Measurement = Measurement.Failed("test") };
}
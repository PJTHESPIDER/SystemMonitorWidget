using System;
using System.Collections.Generic;
using System.Linq;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

public class HardwareModelsTests
{
    // Helpers to build snapshots in test scenarios.

    private static HardwareDevice Device(string id, string name, string? vendor, DeviceClass deviceClass,
        GpuIntegration gpuIntegration = GpuIntegration.Unknown) =>
        new()
        {
            DeviceId = id,
            DisplayName = name,
            Vendor = vendor,
            DeviceClass = deviceClass,
            GpuIntegration = gpuIntegration,
        };

    private static Measurement Available(double? value, string? unit = null, string? source = null) =>
        Measurement.AvailableValue(value, unit, source);

    private static Measurement Unavailable(string? source = null) =>
        Measurement.Unsupported(source);

    private static DeviceReading Reading(string deviceId, string metric, Measurement measurement) =>
        new() { DeviceId = deviceId, Metric = metric, Measurement = measurement };

    private static MetricsSnapshot Snapshot(
        IEnumerable<HardwareDevice>? cpus = null,
        IEnumerable<HardwareDevice>? gpus = null,
        IEnumerable<DeviceReading>? readings = null) =>
        MetricsSnapshot.Create(
            new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero),
            cpus: cpus,
            gpus: gpus,
            readings: readings);

    [Fact]
    public void IntelCpu_AmdGpu_SnapshotContainsBothDevices()
    {
        var cpu = Device("CPU0", "Intel(R) Core(TM) i7", "Intel", DeviceClass.Cpu);
        var gpu = Device("GPU0", "AMD Radeon RX 6800", "AMD", DeviceClass.Gpu, GpuIntegration.Discrete);
        var snapshot = Snapshot(
            cpus: new[] { cpu },
            gpus: new[] { gpu });

        Assert.Single(snapshot.Cpus);
        Assert.Equal("Intel(R) Core(TM) i7", snapshot.Cpus[0].DisplayName);
        Assert.Equal("Intel", snapshot.Cpus[0].Vendor);
        Assert.Equal(DeviceClass.Cpu, snapshot.Cpus[0].DeviceClass);

        Assert.Single(snapshot.Gpus);
        Assert.Equal("AMD Radeon RX 6800", snapshot.Gpus[0].DisplayName);
        Assert.Equal("AMD", snapshot.Gpus[0].Vendor);
        Assert.Equal(DeviceClass.Gpu, snapshot.Gpus[0].DeviceClass);
        Assert.Equal(GpuIntegration.Discrete, snapshot.Gpus[0].GpuIntegration);
    }

    [Fact]
    public void AmdCpu_NvidiaGpu_SnapshotContainsBothDevices()
    {
        var cpu = Device("CPU0", "AMD Ryzen 9 5950X", "AMD", DeviceClass.Cpu);
        var gpu = Device("GPU0", "NVIDIA GeForce RTX 3080", "NVIDIA", DeviceClass.Gpu, GpuIntegration.Discrete);
        var snapshot = Snapshot(cpus: new[] { cpu }, gpus: new[] { gpu });

        Assert.Equal("AMD", snapshot.Cpus[0].Vendor);
        Assert.Equal("NVIDIA", snapshot.Gpus[0].Vendor);
    }

    [Fact]
    public void IntelIntegratedGpu_Plus_NvidiaDiscreteGpu_TwoGpusClassified()
    {
        var igpu = Device("GPU0", "Intel UHD Graphics 630", "Intel", DeviceClass.Gpu, GpuIntegration.Integrated);
        var dgpu = Device("GPU1", "NVIDIA GeForce RTX 3080", "NVIDIA", DeviceClass.Gpu, GpuIntegration.Discrete);
        var snapshot = Snapshot(gpus: new[] { igpu, dgpu });

        Assert.Equal(2, snapshot.Gpus.Count);
        Assert.Equal(GpuIntegration.Integrated, snapshot.Gpus[0].GpuIntegration);
        Assert.Equal(GpuIntegration.Discrete, snapshot.Gpus[1].GpuIntegration);
    }

    [Fact]
    public void TwoGpusIdenticalNames_DistinctDeviceIds_PreservedSeparately()
    {
        var gpuA = Device("GPU-PCI-0001", "NVIDIA GeForce RTX 3080", "NVIDIA", DeviceClass.Gpu, GpuIntegration.Discrete);
        var gpuB = Device("GPU-PCI-0002", "NVIDIA GeForce RTX 3080", "NVIDIA", DeviceClass.Gpu, GpuIntegration.Discrete);
        var snapshot = Snapshot(gpus: new[] { gpuA, gpuB });

        Assert.Equal(2, snapshot.Gpus.Count);
        Assert.NotSame(snapshot.Gpus[0], snapshot.Gpus[1]);
        Assert.Equal("GPU-PCI-0001", snapshot.Gpus[0].DeviceId);
        Assert.Equal("GPU-PCI-0002", snapshot.Gpus[1].DeviceId);
    }

    [Fact]
    public void DeviceWithNoReadableSensors_DeviceExistsReadingsUnavailable()
    {
        var gpu = Device("GPU0", "Unknown GPU", null, DeviceClass.Gpu, GpuIntegration.Unknown);
        var snapshot = Snapshot(gpus: new[] { gpu });

        // Device is detected even though no measurements exist.
        Assert.Single(snapshot.Gpus);
        Assert.Equal(GpuIntegration.Unknown, snapshot.Gpus[0].GpuIntegration);
        Assert.Null(snapshot.Gpus[0].Vendor);

        // No readings means no measurements — value is absent, never zero.
        Assert.Empty(snapshot.Readings);
    }

    [Fact]
    public void UnknownVendor_UnknownClassification_Allowed()
    {
        var device = Device("DEV0", "Unknown Device", null, DeviceClass.Unknown, GpuIntegration.Unknown);
        var snapshot = Snapshot(cpus: new[] { device });

        Assert.Single(snapshot.Cpus);
        Assert.Null(snapshot.Cpus[0].Vendor);
        Assert.Equal(DeviceClass.Unknown, snapshot.Cpus[0].DeviceClass);
        Assert.Equal(GpuIntegration.Unknown, snapshot.Cpus[0].GpuIntegration);
    }

    [Fact]
    public void MeasurementNeverSubstitutesZero_UnavailableStatusUsed()
    {
        var m = Measurement.Unsupported("WMI");
        Assert.False(m.Value.HasValue);
        Assert.Equal(MeasurementStatus.Unsupported, m.Status);
    }

    [Fact]
    public void SnapshotCollectionsAreImmutable_CallerCannotMutate()
    {
        var snapshot = Snapshot();
        var cpus = snapshot.Cpus;
        var gpus = snapshot.Gpus;
        var readings = snapshot.Readings;

        // IReadOnlyList exposes no Add; collections are read-only copies.
        Assert.Throws<NotSupportedException>(() => ((System.Collections.IList)cpus).Add(Device("x", "x", null, DeviceClass.Cpu)));
        Assert.Throws<NotSupportedException>(() => ((System.Collections.IList)gpus).Add(Device("x", "x", null, DeviceClass.Gpu)));
        Assert.Throws<NotSupportedException>(() => ((System.Collections.IList)readings).Add(new DeviceReading()));
        Assert.Empty(cpus);
        Assert.Empty(gpus);
        Assert.Empty(readings);
    }

    [Fact]
    public void LegacyPayloadPresenter_PopulatesLegacyKeys()
    {
        var cpu = Device("CPU0", "Intel CPU", "Intel", DeviceClass.Cpu);
        var gpu = Device("GPU0", "AMD GPU", "AMD", DeviceClass.Gpu, GpuIntegration.Discrete);
        var snapshot = Snapshot(
            cpus: new[] { cpu },
            gpus: new[] { gpu },
            readings: new[]
            {
                Reading("CPU0", "LoadPercent", Available(42.5, "%", "WMI")),
                Reading("CPU0", "ClockMHz", Available(3500.0, "MHz", "WMI")),
                Reading("CPU0", "TemperatureC", Available(55.0, "°C", "WMI")),
                Reading("GPU0", "LoadPercent", Available(30.0, "%", "WMI")),
                Reading("GPU0", "TemperatureC", Available(65.0, "°C", "nvidia-smi")),
                Reading("GPU0", "VramUsedBytes", Available(4_000_000_000.0, "bytes", "nvidia-smi")),
                Reading("GPU0", "VramTotalBytes", Available(8_000_000_000.0, "bytes", "nvidia-smi")),
            });

        var payload = LegacyPayloadPresenter.Present(snapshot);

        Assert.Equal("42.5%", payload["cpu"]);
        Assert.Equal("3.50 GHz", payload["cpuClock"]);
        Assert.Equal("55.0°C", payload["cpuTemp"]);
        Assert.Equal("30.0%", payload["gpu"]);
        Assert.Equal("65.0°C", payload["gpuTemp"]);
        Assert.Contains("3.7 GB / 7.5 GB (50.0%)", payload["gpuVram"]);
        Assert.Equal("N/A", payload["igpu"]);
        Assert.Equal("30.0%", payload["dgpu"]);
        Assert.Equal("12:00:00 PM", payload["lastUpdated"]);
    }

    [Fact]
    public void LegacyPayloadPresenter_MissingSensors_NeverZero()
    {
        var cpu = Device("CPU0", "CPU", null, DeviceClass.Cpu);
        var snapshot = Snapshot(cpus: new[] { cpu });

        var payload = LegacyPayloadPresenter.Present(snapshot);

        Assert.Equal("N/A", payload["cpu"]);
        Assert.Equal("N/A", payload["cpuTemp"]);
        Assert.Equal("N/A", payload["gpu"]);
    }

    [Fact]
    public void LegacyPayloadPresenter_MultipleGpus_BestAvailableLoadUsed_DoesNotAverage()
    {
        var gpuA = Device("GPU-A", "GPU A", "VendorA", DeviceClass.Gpu, GpuIntegration.Discrete);
        var gpuB = Device("GPU-B", "GPU B", "VendorB", DeviceClass.Gpu, GpuIntegration.Discrete);
        var snapshot = Snapshot(
            gpus: new[] { gpuA, gpuB },
            readings: new[]
            {
                Reading("GPU-A", "LoadPercent", Available(10.0, "%", "WMI")),
                Reading("GPU-B", "LoadPercent", Available(90.0, "%", "WMI")),
            });

        var payload = LegacyPayloadPresenter.Present(snapshot);

        // Best available (first in order) — not the average of 50%.
        Assert.Equal("10.0%", payload["gpu"]);
    }

    [Fact]
    public void MetricsSnapshot_TimestampIsImmutable()
    {
        var ts = new DateTimeOffset(2026, 10, 19, 13, 30, 0, TimeSpan.Zero);
        var snapshot = new MetricsSnapshot { Timestamp = ts };
        Assert.Equal(ts, snapshot.Timestamp);
    }
}

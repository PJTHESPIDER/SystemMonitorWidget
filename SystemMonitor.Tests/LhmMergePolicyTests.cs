using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the precedence rules used when combining the device-identified
/// backend readings with the legacy vendor-specific collector values.
///
/// The merge itself lives in SystemMetricsService, which is not linked into
/// the test project (it depends on the Windows App SDK). These tests assert
/// the same policy at the level of the pure projection: which reading wins,
/// and what happens when a sensor is blocked.
/// </summary>
public class LhmMergePolicyTests
{
    private static DeviceReading Reading(LhmProjectionResult result, string deviceId, string metric) =>
        result.Readings.First(r => r.DeviceId == deviceId && r.Metric == metric);

    [Fact]
    public void BackendReadingWins_AndCarriesDeviceProvenance()
    {
        var gpu = LhmFixtures.NvidiaGpu();

        var result = LhmSnapshotMapper.Project(new[] { gpu });

        var temp = Reading(result, gpu.DeviceId, LhmSensorSelection.GpuTemperatureMetric).Measurement;

        Assert.Equal(MeasurementStatus.Available, temp.Status);
        Assert.Equal(61.0, temp.Value);
        // The provenance names the sensor, so a reader can tell which device
        // and which sensor the value came from.
        Assert.Equal("LibreHardwareMonitor:GPU Core", temp.Source);
    }

    [Fact]
    public void BlockedBackendSensor_DoesNotProduceAValue()
    {
        // A blocked sensor must not fall back to a fabricated zero. The
        // legacy collector may still supply a value, but the backend's own
        // reading stays unavailable.
        var gpu = LhmFixtures.NvidiaGpu(readable: false);

        var result = LhmSnapshotMapper.Project(new[] { gpu });

        var temp = Reading(result, gpu.DeviceId, LhmSensorSelection.GpuTemperatureMetric).Measurement;

        Assert.Equal(MeasurementStatus.Failed, temp.Status);
        Assert.Null(temp.Value);
    }

    [Fact]
    public void MetricsWithoutASensor_AreUnsupportedRatherThanFailed()
    {
        // The Intel integrated GPU has no temperature sensor at all. That is
        // "unsupported", which is different from "attempted and failed".
        var gpu = LhmFixtures.IntelIntegratedGpu();

        var result = LhmSnapshotMapper.Project(new[] { gpu });

        var temp = Reading(result, gpu.DeviceId, LhmSensorSelection.GpuTemperatureMetric).Measurement;

        Assert.Equal(MeasurementStatus.Unsupported, temp.Status);
        Assert.Null(temp.Value);
    }

    [Fact]
    public void DifferentMetricsOnTheSameDevice_FailIndependently()
    {
        // The integrated GPU has a readable load but no temperature, clock or
        // dedicated memory. Each metric is judged on its own.
        var gpu = LhmFixtures.IntelIntegratedGpu();

        var result = LhmSnapshotMapper.Project(new[] { gpu });

        var load = Reading(result, gpu.DeviceId, LhmSensorSelection.GpuLoadMetric).Measurement;
        var temp = Reading(result, gpu.DeviceId, LhmSensorSelection.GpuTemperatureMetric).Measurement;
        var vram = Reading(result, gpu.DeviceId, LhmSensorSelection.GpuMemoryUsedMetric).Measurement;

        Assert.Equal(MeasurementStatus.Available, load.Status);
        Assert.Equal(7.0, load.Value);
        Assert.Equal(MeasurementStatus.Unsupported, temp.Status);
        Assert.Equal(MeasurementStatus.Unsupported, vram.Status);
    }

    [Fact]
    public void VramValuesAreNeverZeroWhenUnavailable()
    {
        var gpu = LhmFixtures.GpuWithoutSensors();

        var result = LhmSnapshotMapper.Project(new[] { gpu });

        var used = Reading(result, gpu.DeviceId, LhmSensorSelection.GpuMemoryUsedMetric).Measurement;

        Assert.Equal(MeasurementStatus.Unsupported, used.Status);
        Assert.Null(used.Value);
    }

    [Fact]
    public void SnapshotReadingSourcesAreDistinctPerDevice()
    {
        // Two GPUs of the same vendor must still be distinguishable by
        // identity, not by their position in a list.
        var first = LhmFixtures.NvidiaGpu();
        var second = LhmFixtures.NvidiaGpu() with { DeviceId = "/gpu-nvidia/1" };

        var result = LhmSnapshotMapper.Project(new[] { first, second });

        var firstTemp = Reading(result, first.DeviceId, LhmSensorSelection.GpuTemperatureMetric);
        var secondTemp = Reading(result, second.DeviceId, LhmSensorSelection.GpuTemperatureMetric);

        Assert.NotEqual(firstTemp.DeviceId, secondTemp.DeviceId);
        Assert.Equal(2, result.Gpus.Count);
    }
}

using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

public class LhmSnapshotMapperTests
{
    private static Measurement? MeasurementOf(LhmProjectionResult result, string deviceId, string metric) =>
        result.Readings.FirstOrDefault(r => r.DeviceId == deviceId && r.Metric == metric)?.Measurement;

    [Fact]
    public void IntelCpuWithAmdGpu_ProducesOneCpuAndOneGpu()
    {
        var devices = new[] { LhmFixtures.IntelCpu(), LhmFixtures.AmdGpu() };

        var result = LhmSnapshotMapper.Project(devices);

        Assert.Single(result.Cpus);
        Assert.Single(result.Gpus);
        Assert.Equal("Intel", result.Cpus[0].Vendor);
        Assert.Equal("AMD", result.Gpus[0].Vendor);
        Assert.Equal(DeviceClass.Cpu, result.Cpus[0].DeviceClass);
        Assert.Equal(DeviceClass.Gpu, result.Gpus[0].DeviceClass);
    }

    [Fact]
    public void AmdCpuWithNvidiaGpu_ProducesCorrectVendors()
    {
        var devices = new[] { LhmFixtures.AmdCpu(), LhmFixtures.NvidiaGpu() };

        var result = LhmSnapshotMapper.Project(devices);

        Assert.Equal("AMD", result.Cpus[0].Vendor);
        Assert.Equal("NVIDIA", result.Gpus[0].Vendor);
    }

    [Fact]
    public void IntelIntegratedWithNvidiaDiscrete_ClassifiesIntegration()
    {
        var devices = new[] { LhmFixtures.IntelIntegratedGpu(), LhmFixtures.NvidiaGpu() };

        var result = LhmSnapshotMapper.Project(devices);

        var integrated = result.Gpus.Single(g => g.Vendor == "Intel");
        var nvidia = result.Gpus.Single(g => g.Vendor == "NVIDIA");

        Assert.Equal(GpuIntegration.Integrated, integrated.GpuIntegration);
        // The library does not expose NVIDIA's integration class, so it must
        // remain Unknown rather than being guessed.
        Assert.Equal(GpuIntegration.Unknown, nvidia.GpuIntegration);
    }

    [Fact]
    public void TwoGpusWithIdenticalNames_KeepDistinctIdentities()
    {
        var first = LhmFixtures.NvidiaGpu();
        var second = LhmFixtures.NvidiaGpu() with { DeviceId = "/gpu-nvidia/1" };

        var result = LhmSnapshotMapper.Project(new[] { first, second });

        Assert.Equal(2, result.Gpus.Count);
        Assert.NotEqual(result.Gpus[0].DeviceId, result.Gpus[1].DeviceId);
        Assert.Equal(result.Gpus[0].DisplayName, result.Gpus[1].DisplayName);
    }

    [Fact]
    public void DeviceWithNoReadableSensors_IsStillReportedAsADevice()
    {
        var result = LhmSnapshotMapper.Project(new[] { LhmFixtures.GpuWithoutSensors() });

        var gpu = Assert.Single(result.Gpus);
        Assert.Equal("Intel(R) Arc A770", gpu.DisplayName);

        var temp = MeasurementOf(result, gpu.DeviceId, LhmSensorSelection.GpuTemperatureMetric);
        Assert.NotNull(temp);
        Assert.Equal(MeasurementStatus.Unsupported, temp!.Status);
        Assert.Null(temp.Value);
    }

    [Fact]
    public void BlockedSensors_AreFailedNotZero()
    {
        // A sensor that exists but cannot be read (no elevation) is Failed,
        // never Available with a zero value.
        var cpu = LhmFixtures.BlockedCpu();

        var result = LhmSnapshotMapper.Project(new[] { cpu });

        var temp = MeasurementOf(result, cpu.DeviceId, LhmSensorSelection.CpuTemperatureMetric);
        Assert.NotNull(temp);
        Assert.Equal(MeasurementStatus.Failed, temp!.Status);
        Assert.Null(temp.Value);
    }

    [Fact]
    public void AvailableReading_CarriesValueUnitAndSource()
    {
        var cpu = LhmFixtures.IntelCpu();

        var result = LhmSnapshotMapper.Project(new[] { cpu });

        var temp = MeasurementOf(result, cpu.DeviceId, LhmSensorSelection.CpuTemperatureMetric);
        Assert.NotNull(temp);
        Assert.Equal(MeasurementStatus.Available, temp!.Status);
        Assert.Equal(84.0, temp.Value);
        Assert.Equal("°C", temp.Unit);
        Assert.Equal("LibreHardwareMonitor:CPU Package", temp.Source);
        Assert.NotNull(temp.LastSuccessfulUpdate);
    }

    [Fact]
    public void VramMemoryIsConvertedFromMegabytesToBytes()
    {
        var gpu = LhmFixtures.NvidiaGpu();

        var result = LhmSnapshotMapper.Project(new[] { gpu });

        var used = MeasurementOf(result, gpu.DeviceId, LhmSensorSelection.GpuMemoryUsedMetric);
        var total = MeasurementOf(result, gpu.DeviceId, LhmSensorSelection.GpuMemoryTotalMetric);

        Assert.Equal(1233.0 * 1024 * 1024, used!.Value);
        Assert.Equal(8192.0 * 1024 * 1024, total!.Value);
        Assert.Equal("B", used.Unit);
    }

    [Fact]
    public void EveryMetricIsPresentForEveryDevice()
    {
        // A device is reported with all its metrics, so a missing sensor is
        // distinguishable from a device that was never seen.
        var result = LhmSnapshotMapper.Project(new[] { LhmFixtures.IntelIntegratedGpu() });

        var gpu = Assert.Single(result.Gpus);
        var metrics = result.Readings
            .Where(r => r.DeviceId == gpu.DeviceId)
            .Select(r => r.Metric)
            .ToList();

        Assert.Contains(LhmSensorSelection.GpuLoadMetric, metrics);
        Assert.Contains(LhmSensorSelection.GpuTemperatureMetric, metrics);
        Assert.Contains(LhmSensorSelection.GpuClockMetric, metrics);
        Assert.Contains(LhmSensorSelection.GpuMemoryUsedMetric, metrics);
        Assert.Contains(LhmSensorSelection.GpuMemoryTotalMetric, metrics);
    }

    [Fact]
    public void DuplicateDeviceIdsAreCollapsedToOneDevice()
    {
        var result = LhmSnapshotMapper.Project(new[] { LhmFixtures.NvidiaGpu(), LhmFixtures.NvidiaGpu() });

        Assert.Single(result.Gpus);
    }

    [Fact]
    public void DeviceWithoutIdIsSkipped()
    {
        var device = LhmFixtures.NvidiaGpu() with { DeviceId = "" };

        var result = LhmSnapshotMapper.Project(new[] { device });

        Assert.Empty(result.Gpus);
        Assert.Empty(result.Readings);
    }

    [Fact]
    public void NullDeviceListProducesEmptyProjection()
    {
        var result = LhmSnapshotMapper.Project(null!);

        Assert.True(result.IsEmpty);
        Assert.Empty(result.Readings);
    }

    [Fact]
    public void PrimaryGpuPrefersDiscreteThenStableDeviceId()
    {
        var integrated = LhmFixtures.IntelIntegratedGpu();
        var discrete = LhmFixtures.NvidiaGpu();

        var result = LhmSnapshotMapper.Project(new[] { integrated, discrete });

        var primary = LhmSnapshotMapper.SelectPrimaryGpu(result.Gpus);

        Assert.NotNull(primary);
        Assert.Equal(discrete.DeviceId, primary!.DeviceId);
    }

    [Fact]
    public void PrimaryGpuIsStableRegardlessOfDeviceOrder()
    {
        var first = LhmFixtures.IntelIntegratedGpu();
        var second = LhmFixtures.NvidiaGpu();

        var a = LhmSnapshotMapper.SelectPrimaryGpu(LhmSnapshotMapper.Project(new[] { first, second }).Gpus);
        var b = LhmSnapshotMapper.SelectPrimaryGpu(LhmSnapshotMapper.Project(new[] { second, first }).Gpus);

        Assert.Equal(a!.DeviceId, b!.DeviceId);
    }

    [Fact]
    public void PrimaryGpuFallsBackToUnknownIntegration()
    {
        // When neither GPU has a known integration class, the choice is still
        // deterministic (lowest device id).
        var a = LhmFixtures.NvidiaGpu();
        var b = LhmFixtures.AmdGpu();

        var result = LhmSnapshotMapper.Project(new[] { b, a });

        var primary = LhmSnapshotMapper.SelectPrimaryGpu(result.Gpus);
        Assert.NotNull(primary);
        Assert.Equal(result.Gpus.OrderBy(g => g.DeviceId, StringComparer.Ordinal).First().DeviceId, primary!.DeviceId);
    }

    [Fact]
    public void PrimaryCpuIsSelectedByStableDeviceId()
    {
        var result = LhmSnapshotMapper.Project(new[] { LhmFixtures.IntelCpu(), LhmFixtures.AmdCpu() });

        var primary = LhmSnapshotMapper.SelectPrimaryCpu(result.Cpus);

        Assert.NotNull(primary);
        Assert.Equal(result.Cpus.OrderBy(c => c.DeviceId, StringComparer.Ordinal).First().DeviceId, primary!.DeviceId);
    }

    [Fact]
    public void PrimarySelectionOnEmptyListReturnsNull()
    {
        Assert.Null(LhmSnapshotMapper.SelectPrimaryGpu(Array.Empty<HardwareDevice>()));
        Assert.Null(LhmSnapshotMapper.SelectPrimaryCpu(Array.Empty<HardwareDevice>()));
    }

    [Fact]
    public void ProjectionToSnapshotKeepsSystemLevelMeasurements()
    {
        var result = LhmSnapshotMapper.Project(new[] { LhmFixtures.IntelCpu() });

        var snapshot = result.ToSnapshot(
            DateTimeOffset.UtcNow,
            ramUsedBytes: Measurement.AvailableValue(1.0, "B", "windows"),
            ramTotalBytes: Measurement.AvailableValue(2.0, "B", "windows"),
            ramPercent: Measurement.AvailableValue(50.0, "%", "windows"),
            diskUsedBytes: Measurement.AvailableValue(3.0, "B", "windows"),
            diskTotalBytes: Measurement.AvailableValue(4.0, "B", "windows"),
            diskPercent: Measurement.AvailableValue(75.0, "%", "windows"),
            networkDownloadBytesPerSec: Measurement.AvailableValue(5.0, "B/s", "windows"),
            networkUploadBytesPerSec: Measurement.AvailableValue(6.0, "B/s", "windows"));

        Assert.Single(snapshot.Cpus);
        Assert.Equal(1.0, snapshot.RamUsedBytes!.Value);
        Assert.Equal(75.0, snapshot.DiskPercent!.Value);
        Assert.Equal(5.0, snapshot.NetworkDownloadBytesPerSec!.Value);
    }
}

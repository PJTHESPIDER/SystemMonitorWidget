using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the backend contract itself, exercised through a fake source
/// so no hardware access is needed.
/// </summary>
public class LhmSensorSourceTests
{
    /// <summary>
    /// A source that returns a fixed set of devices, standing in for the
    /// LibreHardwareMonitor adapter.
    /// </summary>
    private sealed class FakeSource : ILhmSensorSource
    {
        private readonly IReadOnlyList<LhmDeviceDescriptor> _devices;

        public FakeSource(IReadOnlyList<LhmDeviceDescriptor> devices)
        {
            _devices = devices;
        }

        public bool IsAvailable { get; set; } = true;
        public string? UnavailableReason { get; set; }
        public bool IsDisposed { get; private set; }
        public int ReadCount { get; private set; }

        public IReadOnlyList<LhmDeviceDescriptor> Read()
        {
            ReadCount++;
            return _devices;
        }

        public void Dispose() => IsDisposed = true;
    }

    [Fact]
    public void PartialPermissionFailure_KeepsWorkingDevicesAndMarksBlockedOnesFailed()
    {
        // The CPU's MSR-backed temperature and clock sensors are blocked, but
        // its scheduler-backed load sensor still reads and the GPU reads fine.
        // The blocked metrics must be Failed while the readable ones stay
        // Available.
        var cpu = LhmFixtures.PartiallyBlockedCpu();
        var gpu = LhmFixtures.NvidiaGpu();

        var source = new FakeSource(new[] { cpu, gpu });
        var result = LhmSnapshotMapper.Project(source.Read());

        var cpuTemp = result.Readings
            .First(r => r.DeviceId == cpu.DeviceId && r.Metric == LhmSensorSelection.CpuTemperatureMetric)
            .Measurement;
        var cpuClock = result.Readings
            .First(r => r.DeviceId == cpu.DeviceId && r.Metric == LhmSensorSelection.CpuClockMetric)
            .Measurement;
        var cpuLoad = result.Readings
            .First(r => r.DeviceId == cpu.DeviceId && r.Metric == LhmSensorSelection.CpuLoadMetric)
            .Measurement;
        var gpuTemp = result.Readings
            .First(r => r.DeviceId == gpu.DeviceId && r.Metric == LhmSensorSelection.GpuTemperatureMetric)
            .Measurement;

        Assert.Equal(MeasurementStatus.Failed, cpuTemp.Status);
        Assert.Null(cpuTemp.Value);
        Assert.Equal(MeasurementStatus.Failed, cpuClock.Status);
        Assert.Equal(MeasurementStatus.Available, cpuLoad.Status);
        Assert.Equal(19.44, cpuLoad.Value);
        Assert.Equal(MeasurementStatus.Available, gpuTemp.Status);
    }

    [Fact]
    public void BlockedPreferredSensor_FallsBackToReadableAlternative()
    {
        // "CPU Package" is blocked but "Core Average" reads, so the metric
        // stays populated rather than becoming unavailable.
        var cpu = LhmFixtures.PartiallyBlockedCpu();
        var withFallback = cpu with
        {
            Sensors = new List<LhmSensorDescriptor>
            {
                LhmFixtures.Load("CPU Total", 19.44),
                LhmFixtures.Temp("CPU Package", null),
                LhmFixtures.Temp("Core Average", 71.5),
                LhmFixtures.Clock("CPU Core #1", null),
            },
        };

        var result = LhmSnapshotMapper.Project(new[] { withFallback });

        var temp = result.Readings
            .First(r => r.DeviceId == cpu.DeviceId && r.Metric == LhmSensorSelection.CpuTemperatureMetric)
            .Measurement;

        Assert.Equal(MeasurementStatus.Available, temp.Status);
        Assert.Equal(71.5, temp.Value);
        Assert.Equal("LibreHardwareMonitor:Core Average", temp.Source);
    }

    [Fact]
    public void BlockedCpuWithNoReadableSensors_ReportsEveryMetricAsFailedNotZero()
    {
        var cpu = LhmFixtures.IntelCpu(readable: false);

        var result = LhmSnapshotMapper.Project(new[] { cpu });

        Assert.NotEmpty(result.Readings);
        foreach (var reading in result.Readings)
        {
            Assert.Equal(MeasurementStatus.Failed, reading.Measurement.Status);
            Assert.Null(reading.Measurement.Value);
        }
    }

    [Fact]
    public void UnavailableSource_ProducesNoDevicesAndNoReadings()
    {
        var source = new FakeSource(Array.Empty<LhmDeviceDescriptor>())
        {
            IsAvailable = false,
            UnavailableReason = "access denied",
        };

        Assert.False(source.IsAvailable);
        Assert.Equal("access denied", source.UnavailableReason);

        var result = LhmSnapshotMapper.Project(source.Read());

        Assert.True(result.IsEmpty);
        Assert.Empty(result.Readings);
    }

    [Fact]
    public void RepeatedReads_AreSafeAndDoNotAccumulateReadings()
    {
        var source = new FakeSource(new[] { LhmFixtures.IntelCpu() });

        var first = LhmSnapshotMapper.Project(source.Read());
        var second = LhmSnapshotMapper.Project(source.Read());

        Assert.Equal(2, source.ReadCount);
        Assert.Equal(first.Readings.Count, second.Readings.Count);
        Assert.Single(second.Cpus);
    }

    [Fact]
    public void SourceIsDisposable()
    {
        var source = new FakeSource(Array.Empty<LhmDeviceDescriptor>());

        source.Dispose();

        Assert.True(source.IsDisposed);
    }

    [Fact]
    public void SnapshotCollectionsAreNotMutable()
    {
        var result = LhmSnapshotMapper.Project(new[] { LhmFixtures.IntelCpu() });
        var snapshot = result.ToSnapshot(DateTimeOffset.UtcNow);

        Assert.Throws<NotSupportedException>(
            () => ((IList<HardwareDevice>)snapshot.Cpus).Add(new HardwareDevice()));
        Assert.Throws<NotSupportedException>(
            () => ((IList<DeviceReading>)snapshot.Readings).Add(new DeviceReading()));
    }
}

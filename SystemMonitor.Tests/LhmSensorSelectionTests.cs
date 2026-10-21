using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

public class LhmSensorSelectionTests
{
    [Fact]
    public void CpuTemperature_OnIntel_SelectsPackageSensor()
    {
        var sensors = LhmFixtures.IntelCpu().Sensors;

        var selected = LhmSensorSelection.Select(sensors, LhmSensorKind.CpuTemperatureC);

        Assert.NotNull(selected);
        Assert.Equal("CPU Package", selected!.Name);
    }

    [Fact]
    public void CpuTemperature_OnAmd_SelectsTctlTdieSensor()
    {
        var sensors = LhmFixtures.AmdCpu().Sensors;

        var selected = LhmSensorSelection.Select(sensors, LhmSensorKind.CpuTemperatureC);

        Assert.NotNull(selected);
        Assert.Equal("Core (Tctl/Tdie)", selected!.Name);
    }

    [Fact]
    public void CpuLoad_SelectsTotalNotCoreMax()
    {
        // "CPU Core Max" is a Load sensor too, and it would be a wrong choice:
        // it is the busiest single core, not the whole-package load.
        var sensors = LhmFixtures.IntelCpu().Sensors;

        var selected = LhmSensorSelection.Select(sensors, LhmSensorKind.CpuLoadPercent);

        Assert.NotNull(selected);
        Assert.Equal("CPU Total", selected!.Name);
    }

    [Fact]
    public void CpuClock_SelectsFirstCoreClockNotTemperatureOfSameName()
    {
        // "CPU Core #1" exists as both a Temperature and a Clock sensor.
        var sensors = LhmFixtures.IntelCpu().Sensors;

        var selected = LhmSensorSelection.Select(sensors, LhmSensorKind.CpuClockMHz);

        Assert.NotNull(selected);
        Assert.Equal("CPU Core #1", selected!.Name);
        Assert.Equal("Clock", selected.SensorType);
    }

    [Fact]
    public void CpuClock_SelectsLowestCoreIndexForDeterminism()
    {
        var sensors = new List<LhmSensorDescriptor>
        {
            LhmFixtures.Clock("CPU Core #3", 3000),
            LhmFixtures.Clock("CPU Core #1", 2600),
            LhmFixtures.Clock("CPU Core #2", 2700),
        };

        var selected = LhmSensorSelection.Select(sensors, LhmSensorKind.CpuClockMHz);

        Assert.NotNull(selected);
        Assert.Equal("CPU Core #1", selected!.Name);
    }

    [Fact]
    public void GpuTemperature_SelectsCoreOverHotSpot()
    {
        var sensors = LhmFixtures.NvidiaGpu().Sensors;

        var selected = LhmSensorSelection.Select(sensors, LhmSensorKind.GpuTemperatureC);

        Assert.NotNull(selected);
        Assert.Equal("GPU Core", selected!.Name);
    }

    [Fact]
    public void GpuLoad_DoesNotPickTemperatureNamedGpuCore()
    {
        // "GPU Core" exists as Temperature, Clock and Load. The Load must win.
        var sensors = LhmFixtures.NvidiaGpu().Sensors;

        var selected = LhmSensorSelection.Select(sensors, LhmSensorKind.GpuLoadPercent);

        Assert.NotNull(selected);
        Assert.Equal("GPU Core", selected!.Name);
        Assert.Equal("Load", selected.SensorType);
    }

    [Fact]
    public void GpuLoad_OnIntelIntegrated_SelectsD3d3d()
    {
        var sensors = LhmFixtures.IntelIntegratedGpu().Sensors;

        var selected = LhmSensorSelection.Select(sensors, LhmSensorKind.GpuLoadPercent);

        Assert.NotNull(selected);
        Assert.Equal("D3D 3D", selected!.Name);
    }

    [Fact]
    public void GpuMemory_IgnoresSharedMemorySensors()
    {
        // Shared system memory is not VRAM. "D3D Dedicated Memory Used" is
        // the dedicated-memory sensor and may be used, but "D3D Shared
        // Memory Used" must never be.
        var sensors = LhmFixtures.IntelIntegratedGpu().Sensors;

        var selected = LhmSensorSelection.Select(sensors, LhmSensorKind.GpuMemoryUsedBytes);

        Assert.Null(selected);
    }

    [Fact]
    public void GpuMemoryUsed_SelectsDedicatedMemoryOverShared()
    {
        var sensors = LhmFixtures.NvidiaGpu().Sensors;

        var selected = LhmSensorSelection.Select(sensors, LhmSensorKind.GpuMemoryUsedBytes);

        Assert.NotNull(selected);
        Assert.Equal("GPU Memory Used", selected!.Name);
    }

    [Fact]
    public void MissingSensor_ReturnsNull()
    {
        var sensors = LhmFixtures.GpuWithoutSensors().Sensors;

        Assert.Null(LhmSensorSelection.Select(sensors, LhmSensorKind.GpuTemperatureC));
        Assert.Null(LhmSensorSelection.Select(sensors, LhmSensorKind.GpuLoadPercent));
    }

    [Fact]
    public void NameMatchOfWrongTypeIsRejected()
    {
        // A "GPU Memory" Load sensor must not be selected as a memory metric,
        // and a "GPU Core" Clock must not be selected as a temperature.
        var sensors = new List<LhmSensorDescriptor>
        {
            LhmFixtures.Clock("GPU Core", 330),
            LhmFixtures.Load("GPU Memory", 15),
        };

        Assert.Null(LhmSensorSelection.Select(sensors, LhmSensorKind.GpuTemperatureC));
        Assert.Null(LhmSensorSelection.Select(sensors, LhmSensorKind.GpuMemoryUsedBytes));
    }

    [Fact]
    public void AmbiguousNames_PreferTypedMatchOverPrefixMatch()
    {
        // "Core" (Temperature) should be reachable when "GPU Core" (Temperature)
        // is absent, but "GPU Hot Spot" outranks the bare "Core".
        var sensors = LhmFixtures.AmbiguousNamesGpu().Sensors;

        var selected = LhmSensorSelection.Select(sensors, LhmSensorKind.GpuTemperatureC);

        Assert.NotNull(selected);
        Assert.Equal("GPU Hot Spot", selected!.Name);
    }

    [Fact]
    public void PreferReadableValue_FallsBackWhenPreferredSensorHasNoValue()
    {
        // The preferred sensor exists but is blocked; the fallback carries a
        // value, so the metric stays populated.
        var sensors = new List<LhmSensorDescriptor>
        {
            LhmFixtures.Temp("CPU Package", null),
            LhmFixtures.Temp("Core Average", 71.5),
        };

        var strict = LhmSensorSelection.Select(sensors, LhmSensorKind.CpuTemperatureC);
        var fallback = LhmSensorSelection.Select(sensors, LhmSensorKind.CpuTemperatureC, preferReadableValue: true);

        Assert.Equal("CPU Package", strict!.Name);
        Assert.Equal("Core Average", fallback!.Name);
    }

    [Fact]
    public void PreferReadableValue_KeepsPreferredSensorWhenItReads()
    {
        var sensors = new List<LhmSensorDescriptor>
        {
            LhmFixtures.Temp("CPU Package", 84.0),
            LhmFixtures.Temp("Core Average", 71.5),
        };

        var selected = LhmSensorSelection.Select(sensors, LhmSensorKind.CpuTemperatureC, preferReadableValue: true);

        Assert.Equal("CPU Package", selected!.Name);
    }

    [Fact]
    public void NullSensorList_ReturnsNull()
    {
        Assert.Null(LhmSensorSelection.Select(null!, LhmSensorKind.CpuTemperatureC));
    }
}

using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

public class ThermalZoneSourceTests
{
    [Fact]
    public void AcpiUnidentified_SourceValue_IsDefined()
    {
        Assert.Equal(ThermalZoneSource.AcpiUnidentified, ThermalZoneSource.AcpiUnidentified);
    }

    [Fact]
    public void NvidiaSmiGpu_SourceValue_IsDefined()
    {
        Assert.Equal(ThermalZoneSource.NvidiaSmiGpu, ThermalZoneSource.NvidiaSmiGpu);
    }

    [Fact]
    public void ThermalZoneReading_Record_HasExpectedProperties()
    {
        var reading = new ThermalZoneReading("ACPI\\THERMALZONE\\TZ01", 42.5, ThermalZoneSource.AcpiUnidentified);
        Assert.Equal("ACPI\\THERMALZONE\\TZ01", reading.InstanceName);
        Assert.Equal(42.5, reading.TemperatureC);
        Assert.Equal(ThermalZoneSource.AcpiUnidentified, reading.Source);
    }
}

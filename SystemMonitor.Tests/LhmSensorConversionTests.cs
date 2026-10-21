using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

public class LhmSensorConversionTests
{
    [Fact]
    public void MemoryMegabytes_ConvertToBytes()
    {
        // The library reports memory in megabytes: an 8 GiB card reads 8192.
        Assert.True(LhmSensorSelection.TryConvert(
            LhmSensorKind.GpuMemoryTotalBytes, 8192.0, LhmSensorUnit.Megabyte, out var bytes));

        Assert.Equal(8192.0 * 1024 * 1024, bytes);
    }

    [Fact]
    public void MemoryWithWrongUnit_IsRejectedNotReinterpreted()
    {
        // A value already in bytes must not be multiplied again.
        Assert.False(LhmSensorSelection.TryConvert(
            LhmSensorKind.GpuMemoryUsedBytes, 1233.0, LhmSensorUnit.Byte, out _));
    }

    [Fact]
    public void UnknownUnit_IsRejected()
    {
        Assert.False(LhmSensorSelection.TryConvert(
            LhmSensorKind.GpuMemoryUsedBytes, 1233.0, LhmSensorUnit.Unknown, out _));
    }

    [Fact]
    public void TemperatureWithWrongUnit_IsRejected()
    {
        Assert.False(LhmSensorSelection.TryConvert(
            LhmSensorKind.CpuTemperatureC, 84.0, LhmSensorUnit.Megahertz, out _));
    }

    [Fact]
    public void MissingValue_IsUnavailableNotZero()
    {
        Assert.False(LhmSensorSelection.TryConvert(
            LhmSensorKind.CpuTemperatureC, null, LhmSensorUnit.Celsius, out var converted));

        Assert.Equal(0.0, converted);
    }

    [Fact]
    public void NaN_IsUnavailable()
    {
        Assert.False(LhmSensorSelection.TryConvert(
            LhmSensorKind.CpuTemperatureC, double.NaN, LhmSensorUnit.Celsius, out _));
    }

    [Fact]
    public void Infinity_IsUnavailable()
    {
        Assert.False(LhmSensorSelection.TryConvert(
            LhmSensorKind.CpuLoadPercent, double.PositiveInfinity, LhmSensorUnit.Percent, out _));

        Assert.False(LhmSensorSelection.TryConvert(
            LhmSensorKind.CpuLoadPercent, double.NegativeInfinity, LhmSensorUnit.Percent, out _));
    }

    [Fact]
    public void PercentageAndTemperaturePassThroughUnchanged()
    {
        Assert.True(LhmSensorSelection.TryConvert(
            LhmSensorKind.GpuLoadPercent, 42.5, LhmSensorUnit.Percent, out var load));
        Assert.Equal(42.5, load);

        Assert.True(LhmSensorSelection.TryConvert(
            LhmSensorKind.GpuTemperatureC, 61.25, LhmSensorUnit.Celsius, out var temp));
        Assert.Equal(61.25, temp);
    }

    [Fact]
    public void ClockPassesThroughUnchanged()
    {
        Assert.True(LhmSensorSelection.TryConvert(
            LhmSensorKind.CpuClockMHz, 2600.0, LhmSensorUnit.Megahertz, out var mhz));
        Assert.Equal(2600.0, mhz);
    }

    [Fact]
    public void CanonicalUnitsAreStable()
    {
        Assert.Equal("%", LhmSensorSelection.CanonicalUnit(LhmSensorKind.CpuLoadPercent));
        Assert.Equal("°C", LhmSensorSelection.CanonicalUnit(LhmSensorKind.CpuTemperatureC));
        Assert.Equal("MHz", LhmSensorSelection.CanonicalUnit(LhmSensorKind.CpuClockMHz));
        Assert.Equal("B", LhmSensorSelection.CanonicalUnit(LhmSensorKind.GpuMemoryUsedBytes));
    }

    [Fact]
    public void RequiredSensorTypesAreDistinctPerQuantity()
    {
        Assert.Equal("Temperature", LhmSensorSelection.RequiredSensorType(LhmSensorKind.CpuTemperatureC));
        Assert.Equal("Clock", LhmSensorSelection.RequiredSensorType(LhmSensorKind.CpuClockMHz));
        Assert.Equal("Load", LhmSensorSelection.RequiredSensorType(LhmSensorKind.CpuLoadPercent));
        Assert.Equal("SmallData", LhmSensorSelection.RequiredSensorType(LhmSensorKind.GpuMemoryUsedBytes));
    }
}

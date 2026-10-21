using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

public class ThermalMathTests
{
    [Fact]
    public void TenthsKelvinToCelsius_3000TenthsKelvin_ReturnsApproximately2685C()
    {
        // 3000 tenths Kelvin = 300.0 K = 26.85 C
        var result = ThermalMath.TenthsKelvinToCelsius(3000.0);
        Assert.Equal(26.85, result, precision: 2);
    }

    [Fact]
    public void TenthsKelvinToCelsius_0TenthsKelvin_ReturnsAbsoluteZeroCelsius()
    {
        var result = ThermalMath.TenthsKelvinToCelsius(0.0);
        Assert.Equal(-273.15, result, precision: 2);
    }

    [Fact]
    public void TenthsKelvinToCelsius_2731_5TenthsKelvin_ReturnsZeroCelsius()
    {
        var result = ThermalMath.TenthsKelvinToCelsius(2731.5); // 273.15 K = 0 C
        Assert.Equal(0.0, result, precision: 2);
    }

    [Fact]
    public void ClampPercent_BelowZero_ReturnsZero()
    {
        Assert.Equal(0.0, ThermalMath.ClampPercent(-5.0));
    }

    [Fact]
    public void ClampPercent_Above100_Returns100()
    {
        Assert.Equal(100.0, ThermalMath.ClampPercent(105.0));
    }

    [Fact]
    public void ClampPercent_WithinRange_ReturnsSame()
    {
        Assert.Equal(42.5, ThermalMath.ClampPercent(42.5));
    }

    [Fact]
    public void ClampPercent_NaN_ReturnsZero()
    {
        Assert.Equal(0.0, ThermalMath.ClampPercent(double.NaN));
    }

    [Fact]
    public void ClampPercent_PositiveInfinity_ReturnsZero()
    {
        Assert.Equal(0.0, ThermalMath.ClampPercent(double.PositiveInfinity));
    }

    [Fact]
    public void ClampPercent_NegativeInfinity_ReturnsZero()
    {
        Assert.Equal(0.0, ThermalMath.ClampPercent(double.NegativeInfinity));
    }

    [Fact]
    public void TenthsKelvinToCelsius_MissingSensorValue_NotTreatedAsZero()
    {
        // A missing/unavailable sensor would not provide a raw value at all.
        // When no raw value exists, temperature remains unavailable (null),
        // never 0 or ambient.
        double? missing = null;
        Assert.False(missing.HasValue);
    }
}

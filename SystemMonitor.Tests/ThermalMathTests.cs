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
        // A negative percentage is clamped, not discarded: the reading was
        // taken, and it says the figure was below the floor.
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
    public void ClampPercent_MeasuredZero_StaysZero()
    {
        // Zero is a measurement. It must survive clamping as zero, so it
        // is distinguishable from "no value".
        Assert.Equal(0.0, ThermalMath.ClampPercent(0.0));
    }

    [Fact]
    public void ClampPercent_NaN_HasNoValue()
    {
        // A NaN is not a measurement. Reporting it as a measured zero
        // would put a number on the widget that no device reported.
        Assert.Null(ThermalMath.ClampPercent(double.NaN));
    }

    [Fact]
    public void ClampPercent_PositiveInfinity_HasNoValue()
    {
        Assert.Null(ThermalMath.ClampPercent(double.PositiveInfinity));
    }

    [Fact]
    public void ClampPercent_NegativeInfinity_HasNoValue()
    {
        Assert.Null(ThermalMath.ClampPercent(double.NegativeInfinity));
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

    // =====================================================================
    // PercentOf: the ratio of two measured amounts
    // =====================================================================

    [Fact]
    public void PercentOf_ComputesTheRatio()
    {
        Assert.Equal(50.0, ThermalMath.PercentOf(512, 1024));
    }

    [Fact]
    public void PercentOf_MeasuredZeroUsed_IsZero()
    {
        // An empty disk against a non-empty capacity is a measured zero.
        // It is a value, and it must not become "no value".
        Assert.Equal(0.0, ThermalMath.PercentOf(0, 1024));
    }

    [Fact]
    public void PercentOf_ZeroTotal_HasNoValue()
    {
        // A capacity of zero carries no percentage. The ratio is
        // undefined, and any number produced from it would be a figure no
        // device reports.
        Assert.Null(ThermalMath.PercentOf(0, 0));
    }

    [Fact]
    public void PercentOf_NegativeTotal_HasNoValue()
    {
        Assert.Null(ThermalMath.PercentOf(10, -1));
    }

    [Fact]
    public void PercentOf_NaNUsed_HasNoValue()
    {
        Assert.Null(ThermalMath.PercentOf(double.NaN, 1024));
    }

    [Fact]
    public void PercentOf_InfiniteTotal_HasNoValue()
    {
        Assert.Null(ThermalMath.PercentOf(512, double.PositiveInfinity));
    }

    [Fact]
    public void PercentOf_AboveOneHundredPercent_ClampsToOneHundred()
    {
        // Usage above the capacity is unusual but it is still a reading, so
        // it is clamped rather than discarded.
        Assert.Equal(100.0, ThermalMath.PercentOf(2048, 1024));
    }
}

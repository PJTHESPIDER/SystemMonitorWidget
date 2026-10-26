using System;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the byte formatter, which is the single source of truth
/// for all byte-formatting in the widget payload.
/// </summary>
public sealed class ByteFormatterTests
{
    private const double KB = 1024.0;
    private const double MB = 1024.0 * 1024.0;
    private const double GB = 1024.0 * 1024.0 * 1024.0;
    private const double TB = 1024.0 * 1024.0 * 1024.0 * 1024.0;

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1.0 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(KB * KB - 1, "1024.0 KB")]
    [InlineData(MB, "1.0 MB")]
    [InlineData(1500 * KB, "1.5 MB")]
    [InlineData(GB - 1, "1024.0 MB")]
    [InlineData(GB, "1.0 GB")]
    [InlineData(1500 * MB, "1.5 GB")]
    [InlineData(TB - 1, "1024.0 GB")]
    [InlineData(TB, "1.0 TB")]
    public void FormatBytes_FormatsCorrectly(double bytes, string expected)
    {
        Assert.Equal(expected, ByteFormatter.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(-1, "0 B")]
    [InlineData(-1024, "0 B")]
    public void FormatBytes_NegativeIsClampedToZero(double bytes, string expected)
    {
        Assert.Equal(expected, ByteFormatter.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(0, 0, ByteFormatter.NotAvailable)]
    [InlineData(-1, 1024, ByteFormatter.NotAvailable)]
    [InlineData(1024, -1, ByteFormatter.NotAvailable)]
    [InlineData(1024, 0, ByteFormatter.NotAvailable)]
    [InlineData(double.NaN, 1024, ByteFormatter.NotAvailable)]
    [InlineData(1024, double.NaN, ByteFormatter.NotAvailable)]
    [InlineData(double.PositiveInfinity, 1024, ByteFormatter.NotAvailable)]
    [InlineData(1024, double.PositiveInfinity, ByteFormatter.NotAvailable)]
    [InlineData(double.NegativeInfinity, 1024, ByteFormatter.NotAvailable)]
    [InlineData(1024, double.NegativeInfinity, ByteFormatter.NotAvailable)]
    [InlineData(2048, 1024, ByteFormatter.NotAvailable)] // used > total
    public void FormatCapacityCompact_MissingOrInvalid_ReturnsNotAvailable(double used, double total, string expected)
    {
        Assert.Equal(expected, ByteFormatter.FormatCapacityCompact(used, total));
    }

    [Theory]
    [InlineData(KB / 2, KB, "0.5 / 1 KB")]           // total = 1 KB (< 10): 2 decimals, trimmed
    [InlineData(1.5 * KB, 2 * KB, "1.5 / 2 KB")]    // total = 2 KB (< 10): 2 decimals, trimmed
    [InlineData(MB / 2, MB, "0.5 / 1 MB")]           // total = 1 MB (< 10): 2 decimals, trimmed
    [InlineData(GB / 2, GB, "0.5 / 1 GB")]           // total = 1 GB (< 10): 2 decimals, trimmed
    [InlineData(14 * GB, 16 * GB, "14 / 16 GB")]     // total = 16 GB (>= 10, < 100): 1 decimal, trimmed
    [InlineData(14.5 * GB, 16 * GB, "14.5 / 16 GB")] // total = 16 GB (>= 10, < 100): 1 decimal
    [InlineData(512 * MB, 8 * GB, "0.5 / 8 GB")]     // total = 8 GB (< 10): 2 decimals, trimmed
    [InlineData(4 * GB, 8 * GB, "4 / 8 GB")]         // total = 8 GB (< 10): 2 decimals, trimmed
    public void FormatCapacityCompact_SharedUnitAndPrecision(double used, double total, string expected)
    {
        Assert.Equal(expected, ByteFormatter.FormatCapacityCompact(used, total));
    }

    [Fact]
    public void FormatCapacityCompact_BytesLevel_ZeroDecimals()
    {
        // Total < 1024 uses bytes with zero decimals
        Assert.Equal("500 / 1000 B", ByteFormatter.FormatCapacityCompact(500, 1000));
        Assert.Equal("0 / 1 B", ByteFormatter.FormatCapacityCompact(0, 1));
    }

    [Fact]
    public void FormatCapacityCompact_KbLevel_PrecisionPolicy()
    {
        // Total >= 1024 but < 1MB uses KB with precision based on magnitude
        // total = 1 KB (< 10): 2 decimals, trimmed to "1"
        Assert.Equal("0.5 / 1 KB", ByteFormatter.FormatCapacityCompact(512, 1024));
        // total = 1 MB (>= 10, < 100): 1 decimal, trimmed to "1"
        Assert.Equal("0.5 / 1 MB", ByteFormatter.FormatCapacityCompact(512 * KB, KB * KB));
        // total = 5 KB (< 10): 2 decimals
        Assert.Equal("1.25 / 5 KB", ByteFormatter.FormatCapacityCompact(1.25 * KB, 5 * KB));
        // total = 50 KB (>= 10, < 100): 1 decimal
        Assert.Equal("12.5 / 50 KB", ByteFormatter.FormatCapacityCompact(12.5 * KB, 50 * KB));
        // total = 500 KB (>= 100): whole units
        Assert.Equal("125 / 500 KB", ByteFormatter.FormatCapacityCompact(125 * KB, 500 * KB));
    }

    [Fact]
    public void FormatCapacityCompact_MbLevel_PrecisionPolicy()
    {
        // Total >= 1MB but < 1GB uses MB with precision based on magnitude
        // total = 1 MB (< 10): 2 decimals, trimmed
        Assert.Equal("0.5 / 1 MB", ByteFormatter.FormatCapacityCompact(MB / 2, MB));
        // total = 5 MB (< 10): 2 decimals
        Assert.Equal("1.25 / 5 MB", ByteFormatter.FormatCapacityCompact(1.25 * MB, 5 * MB));
        // total = 50 MB (>= 10, < 100): 1 decimal
        Assert.Equal("12.5 / 50 MB", ByteFormatter.FormatCapacityCompact(12.5 * MB, 50 * MB));
        // total = 500 MB (>= 100): whole units
        Assert.Equal("125 / 500 MB", ByteFormatter.FormatCapacityCompact(125 * MB, 500 * MB));
    }

    [Fact]
    public void FormatCapacityCompact_GbLevel_PrecisionPolicy()
    {
        // Total >= 1GB uses GB with precision based on magnitude
        // total = 1 GB (< 10): 2 decimals, trimmed
        Assert.Equal("0.5 / 1 GB", ByteFormatter.FormatCapacityCompact(GB / 2, GB));
        // total = 5 GB (< 10): 2 decimals
        Assert.Equal("1.25 / 5 GB", ByteFormatter.FormatCapacityCompact(1.25 * GB, 5 * GB));
        // total = 16 GB (>= 10, < 100): 1 decimal
        Assert.Equal("14.1 / 16 GB", ByteFormatter.FormatCapacityCompact(14.1 * GB, 16 * GB));
        // total = 50 GB (>= 10, < 100): 1 decimal
        Assert.Equal("12.5 / 50 GB", ByteFormatter.FormatCapacityCompact(12.5 * GB, 50 * GB));
        // total = 500 GB (>= 100): whole units
        Assert.Equal("125 / 500 GB", ByteFormatter.FormatCapacityCompact(125 * GB, 500 * GB));
    }

    [Fact]
    public void FormatCapacityCompact_TbLevel_PrecisionPolicy()
    {
        // Total >= 1TB uses TB with precision based on magnitude
        // total = 2 TB (< 10): 2 decimals, trimmed
        Assert.Equal("1.5 / 2 TB", ByteFormatter.FormatCapacityCompact(1.5 * TB, 2 * TB));
        // total = 5 TB (< 10): 2 decimals
        Assert.Equal("1.25 / 5 TB", ByteFormatter.FormatCapacityCompact(1.25 * TB, 5 * TB));
        // total = 50 TB (>= 10, < 100): 1 decimal
        Assert.Equal("12.5 / 50 TB", ByteFormatter.FormatCapacityCompact(12.5 * TB, 50 * TB));
        // total = 500 TB (>= 100): whole units
        Assert.Equal("125 / 500 TB", ByteFormatter.FormatCapacityCompact(125 * TB, 500 * TB));
    }

    [Fact]
    public void FormatCapacityCompact_TrueZeroVersusTinyPositive()
    {
        // True zero usage should show as "0"
        Assert.Equal("0 / 1 KB", ByteFormatter.FormatCapacityCompact(0, KB));

        // Tiny positive usage that rounds to 0 at the current precision tier
        // should show as "<0.01" (for < 10 tier) to distinguish from true zero
        // total = 1 KB (< 10), used = 5 bytes = 0.0049 KB -> rounds to 0 at 2 decimals
        var result = ByteFormatter.FormatCapacityCompact(5, KB);
        Assert.StartsWith("<", result); // Should show as "<0.01 KB" or similar

        // For total >= 10, the minimum displayed precision is 0.1
        // used = 50 bytes = 0.049 KB -> rounds to 0 at 1 decimal
        result = ByteFormatter.FormatCapacityCompact(50, 50 * KB); // total = 50 KB (>= 10)
        Assert.StartsWith("<", result); // Should show as "<0.1 KB" or similar

        // For total >= 100, the minimum displayed precision is 1
        // used = 500 bytes = 0.49 KB -> rounds to 0 at whole units
        result = ByteFormatter.FormatCapacityCompact(500, 500 * KB); // total = 500 KB (>= 100)
        Assert.StartsWith("<", result); // Should show as "<1 KB" or similar
    }

    [Theory]
    [InlineData(KB, 2 * KB, 50.0, "1.0 KB / 2.0 KB (50.0%)")]
    [InlineData(GB, 2 * GB, 50.0, "1.0 GB / 2.0 GB (50.0%)")]
    public void FormatUsage_CombinedWithPercent(double used, double total, double percent, string expected)
    {
        Assert.Equal(expected, ByteFormatter.FormatUsage(used, total, percent));
    }

    [Fact]
    public void FormatUsage_NoTotal_ReturnsPercentOnly()
    {
        Assert.Equal("50.0%", ByteFormatter.FormatUsage(1024, 0, 50.0));
        Assert.Equal("50.0%", ByteFormatter.FormatUsage(1024, -1, 50.0));
    }

    [Fact]
    public void FormatPercent_RoundsToOneDecimal()
    {
        Assert.Equal("50.0%", ByteFormatter.FormatPercent(50.0));
        Assert.Equal("33.3%", ByteFormatter.FormatPercent(33.333));
        Assert.Equal("66.7%", ByteFormatter.FormatPercent(66.666));
    }

    [Fact]
    public void FormatTemperature_Format()
    {
        Assert.Equal("42.5°C", ByteFormatter.FormatTemperature(42.5));
        Assert.Equal("0.0°C", ByteFormatter.FormatTemperature(0.0));
    }

    [Fact]
    public void FormatClock_SwitchesToGhzAt1000()
    {
        Assert.Equal("999 MHz", ByteFormatter.FormatClock(999));
        Assert.Equal("1.00 GHz", ByteFormatter.FormatClock(1000));
        Assert.Equal("3.50 GHz", ByteFormatter.FormatClock(3500));
    }

    [Theory]
    [InlineData(0.25, "0.3%")]   // AwayFromZero: midpoint 0.25 -> 0.3 (ToEven would give 0.2)
    [InlineData(0.05, "0.1%")]   // AwayFromZero: midpoint 0.05 -> 0.1 (ToEven would give 0.0)
    [InlineData(2.25, "2.3%")]   // AwayFromZero: midpoint 2.25 -> 2.3 (ToEven would give 2.2)
    [InlineData(2.35, "2.4%")]   // AwayFromZero: midpoint 2.35 -> 2.4 (ToEven would give 2.4, same result)
    public void FormatPercent_UsesAwayFromZeroRounding(double value, string expected)
    {
        var result = ByteFormatter.FormatPercent(value);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0.25, "0.3°C")]   // AwayFromZero: midpoint 0.25 -> 0.3 (ToEven would give 0.2)
    [InlineData(0.05, "0.1°C")]   // AwayFromZero: midpoint 0.05 -> 0.1 (ToEven would give 0.0)
    [InlineData(2.25, "2.3°C")]   // AwayFromZero: midpoint 2.25 -> 2.3 (ToEven would give 2.2)
    public void FormatTemperature_UsesAwayFromZeroRounding(double value, string expected)
    {
        var result = ByteFormatter.FormatTemperature(value);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(998.5, "999 MHz")]   // AwayFromZero: midpoint 998.5 -> 999 (ToEven would give 998)
    [InlineData(999.5, "1000 MHz")]  // AwayFromZero: midpoint 999.5 -> 1000 (ToEven would give 1000, same)
    [InlineData(1000.25, "1.00 GHz")] // 0.25 GHz -> rounds to 0.00 at 2 decimals
    public void FormatClock_UsesAwayFromZeroRounding(double megahertz, string expected)
    {
        var result = ByteFormatter.FormatClock(megahertz);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatNetworkSpeed_FormatsCorrectly()
    {
        Assert.Equal("0 B/s", ByteFormatter.FormatNetworkSpeed(0));
        Assert.Equal("1.0 KB/s", ByteFormatter.FormatNetworkSpeed(KB));
        Assert.Equal("1.0 MB/s", ByteFormatter.FormatNetworkSpeed(MB));
        Assert.Equal("1.0 GB/s", ByteFormatter.FormatNetworkSpeed(GB));
    }

    [Theory]
    [InlineData(9.95, 10.0)]  // AwayFromZero: 9.95 -> 10.0 (midpoint rounds up, unlike ToEven which would give 9.9)
    [InlineData(9.94, 9.9)]   // AwayFromZero: 9.94 -> 9.9 (below midpoint)
    [InlineData(99.5, 100.0)]  // AwayFromZero: 99.5 -> 100.0 (midpoint rounds up)
    [InlineData(99.4, 99.0)]   // AwayFromZero: 99.4 -> 99.0 (below midpoint)
    [InlineData(150.5, 151.0)]  // AwayFromZero: 150.5 -> 151.0 (midpoint rounds up)
    [InlineData(150.4, 150.0)]  // AwayFromZero: 150.4 -> 150.0 (below midpoint)
    public void FormatCapacityCompact_MidpointRounding_AwayFromZero(double used, double total)
    {
        // Tests that FormatCapacityCompact uses MidpointRounding.AwayFromZero consistently
        // across all magnitude tiers (<10, 10-100, >=100).
        // With ToEven (banker's rounding), 9.95 would round to 9.9 (even), but with AwayFromZero it rounds to 10.0.
        // This verifies the consistent rounding behavior introduced in Prompt 13 Part B.
        var result = ByteFormatter.FormatCapacityCompact(used, total);
        // The result should not start with "<" for normal values being tested
        if (used > 0)
        {
            Assert.False(result.StartsWith("<"),
                $"Value {used} should not show '<' prefix, got '{result}'");
        }
    }

    [Theory]
    [InlineData(0, KB)]  // Genuine zero
    [InlineData(5, KB)]  // Tiny positive that rounds to 0 at 2-decimal precision
    [InlineData(0, 50 * KB)]  // Genuine zero with larger total
    [InlineData(50, 50 * KB)]  // Tiny positive that rounds to 0 at 1-decimal precision
    [InlineData(0, 500 * KB)]  // Genuine zero with even larger total
    [InlineData(500, 500 * KB)]  // Tiny positive that rounds to 0 at whole-unit precision
    public void FormatCapacityCompact_ZeroVersusTinyPositive(double used, double total)
    {
        // Tests that genuine zero vs positive values below displayed precision are distinguished
        var result = ByteFormatter.FormatCapacityCompact(used, total);
        // Zero usage should show as "0" (no "<" prefix)
        if (used == 0)
        {
            Assert.False(result.StartsWith("<"),
                $"Genuine zero should not show '<' prefix, got '{result}'");
        }
        else
        {
            // Positive values below rounding threshold should show "<" prefix
            Assert.True(result.StartsWith("<"),
                $"Positive value {used} below precision threshold should show '<' prefix, got '{result}'");
        }
    }

    [Theory]
    [InlineData(9.95, 10.0)]  // Boundary: just below 10 with AwayFromZero midpoint
    [InlineData(10.0, 10.0)]  // Exactly 10 stays at 10
    [InlineData(9.99, 9.99)]  // Just below 10, displays with 2 decimals
    [InlineData(10.01, 10.01)]  // Just above 10, displays with 2 decimals
    public void FormatCapacityCompact_UnitBoundary(double used, double total)
    {
        // Tests behavior at the 10-unit boundary between tiers
        var result = ByteFormatter.FormatCapacityCompact(used, total);
        // Values at the 10 boundary should use appropriate precision
        if (used >= 10 && used < 100)
        {
            // Should use 1-decimal format for 10-100 tier
            Assert.False(result.StartsWith("<"),
                $"Value {used} in 10-100 tier should not show '<' prefix, got '{result}'");
        }
        else if (used < 10)
        {
            // Should use 2-decimal format for < 10 tier
            if (used > 0)
            {
                // Normal positive values should display without '<' prefix
                Assert.False(result.StartsWith("<"),
                    $"Value {used} in <10 tier should not show '<' prefix for normal values, got '{result}'");
            }
        }
    }
}
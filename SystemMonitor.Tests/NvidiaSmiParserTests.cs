using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the nvidia-smi output parser. The parser is pure, so these
/// tests need no NVIDIA hardware and spawn no processes.
/// </summary>
public class NvidiaSmiParserTests
{
    /// <summary>
    /// A row in the exact shape the query produces: uuid, name, pci.bus_id,
    /// pci.device_id, utilization, temperature, memory used, memory total.
    /// </summary>
    private const string SingleRow =
        "GPU-a5e273b9-529a-7905-ac22-613726981f5d, NVIDIA GeForce RTX 2070 Super with Max-Q Design, " +
        "00000000:01:00.0, 0x1E9110DE, 18, 60, 871, 8192";

    [Fact]
    public void ParsesEveryRequestedFieldFromOneRow()
    {
        var samples = NvidiaSmiParser.Parse(SingleRow);

        var sample = Assert.Single(samples);
        Assert.Equal("GPU-a5e273b9-529a-7905-ac22-613726981f5d", sample.Uuid);
        Assert.Equal("NVIDIA GeForce RTX 2070 Super with Max-Q Design", sample.Name);
        Assert.Equal("00000000:01:00.0", sample.PciBusId);
        Assert.Equal("0x1E9110DE", sample.PciDeviceId);
        Assert.Equal(18d, sample.UtilizationPercent);
        Assert.Equal(60d, sample.TemperatureC);
        Assert.Equal(871UL * 1024 * 1024, sample.VramUsedBytes);
        Assert.Equal(8192UL * 1024 * 1024, sample.VramTotalBytes);
    }

    [Fact]
    public void NormalizesThePciDeviceIdIntoAnIdentityKey()
    {
        var sample = Assert.Single(NvidiaSmiParser.Parse(SingleRow));

        // 0x1E9110DE packs the device id in the high half and the vendor id
        // in the low half.
        Assert.Equal("VEN_10DE&DEV_1E91", sample.PciDeviceKey);
        Assert.True(sample.HasIdentity);
    }

    [Fact]
    public void ParsesOneRowPerGpuForMultipleDevices()
    {
        var output = string.Join(Environment.NewLine, new[]
        {
            "GPU-aaaa, NVIDIA GeForce RTX 4090, 00000000:01:00.0, 0x268410DE, 5, 40, 100, 24576",
            "GPU-bbbb, NVIDIA GeForce RTX 3080, 00000000:41:00.0, 0x220410DE, 90, 75, 9000, 10240",
        });

        var samples = NvidiaSmiParser.Parse(output);

        Assert.Equal(2, samples.Count);
        Assert.Equal("VEN_10DE&DEV_2684", samples[0].PciDeviceKey);
        Assert.Equal("VEN_10DE&DEV_2204", samples[1].PciDeviceKey);
        Assert.Equal(90d, samples[1].UtilizationPercent);
    }

    [Fact]
    public void HandlesANameContainingAComma()
    {
        // A display name may legitimately contain a comma. The row is split
        // from the right so the name does not shift the numeric fields.
        var output =
            "GPU-a5e273b9, NVIDIA GeForce RTX 2070 Super, Max-Q Design, 00000000:01:00.0, 0x1E9110DE, 18, 60, 871, 8192";

        var sample = Assert.Single(NvidiaSmiParser.Parse(output));

        Assert.Equal("NVIDIA GeForce RTX 2070 Super, Max-Q Design", sample.Name);
        Assert.Equal(18d, sample.UtilizationPercent);
        Assert.Equal(60d, sample.TemperatureC);
        Assert.Equal(871UL * 1024 * 1024, sample.VramUsedBytes);
        Assert.Equal(8192UL * 1024 * 1024, sample.VramTotalBytes);
    }

    [Theory]
    [InlineData("[N/A]")]
    [InlineData("[Not Supported]")]
    [InlineData("<Not Supported>")]
    [InlineData("")]
    [InlineData("   ")]
    public void TreatsUnsupportedValuesAsUnavailableRatherThanZero(string unsupported)
    {
        var output =
            $"GPU-a5e273b9, NVIDIA GeForce RTX 2070 Super, 00000000:01:00.0, 0x1E9110DE, {unsupported}, {unsupported}, {unsupported}, {unsupported}";

        var sample = Assert.Single(NvidiaSmiParser.Parse(output));

        Assert.Null(sample.UtilizationPercent);
        Assert.Null(sample.TemperatureC);
        Assert.Null(sample.VramUsedBytes);
        Assert.Null(sample.VramTotalBytes);
    }

    [Fact]
    public void KeepsReadableFieldsWhenOneFieldIsUnsupported()
    {
        var output =
            "GPU-a5e273b9, NVIDIA GeForce RTX 2070 Super, 00000000:01:00.0, 0x1E9110DE, 18, [N/A], 871, 8192";

        var sample = Assert.Single(NvidiaSmiParser.Parse(output));

        Assert.Equal(18d, sample.UtilizationPercent);
        Assert.Null(sample.TemperatureC);
        Assert.Equal(871UL * 1024 * 1024, sample.VramUsedBytes);
    }

    [Fact]
    public void SkipsMalformedRowsRatherThanFailing()
    {
        var output = string.Join(Environment.NewLine, new[]
        {
            "GPU-a5e273b9, NVIDIA GeForce RTX 2070 Super, 00000000:01:00.0, 0x1E9110DE, 18, 60, 871, 8192",
            "this row is not csv at all",
            "too, few, fields",
        });

        var samples = NvidiaSmiParser.Parse(output);

        var sample = Assert.Single(samples);
        Assert.Equal("GPU-a5e273b9", sample.Uuid);
    }

    [Fact]
    public void RejectsUtilizationOutsideTheValidRange()
    {
        var output =
            "GPU-a5e273b9, NVIDIA GeForce RTX 2070 Super, 00000000:01:00.0, 0x1E9110DE, 120, 60, 871, 8192";

        var sample = Assert.Single(NvidiaSmiParser.Parse(output));

        // A sensor reporting 120 % is reporting something this parser does
        // not understand. It is dropped, not clamped.
        Assert.Null(sample.UtilizationPercent);
    }

    [Fact]
    public void RejectsImplausibleTemperatures()
    {
        var output =
            "GPU-a5e273b9, NVIDIA GeForce RTX 2070 Super, 00000000:01:00.0, 0x1E9110DE, 18, 900, 871, 8192";

        var sample = Assert.Single(NvidiaSmiParser.Parse(output));

        Assert.Null(sample.TemperatureC);
    }

    [Fact]
    public void ReturnsNothingForEmptyOutput()
    {
        Assert.Empty(NvidiaSmiParser.Parse(null));
        Assert.Empty(NvidiaSmiParser.Parse(string.Empty));
        Assert.Empty(NvidiaSmiParser.Parse("   \n  \n"));
    }

    [Fact]
    public void ParsesNumbersWithTheInvariantCultureRegardlessOfTheCurrentCulture()
    {
        // The previous culture is switched to one that uses a comma as the
        // decimal separator. A parser that used the current culture would
        // misread "18.5" as 185.
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            var output =
                "GPU-a5e273b9, NVIDIA GeForce RTX 2070 Super, 00000000:01:00.0, 0x1E9110DE, 18.5, 60.5, 871, 8192";

            var sample = Assert.Single(NvidiaSmiParser.Parse(output));

            Assert.Equal(18.5d, sample.UtilizationPercent);
            Assert.Equal(60.5d, sample.TemperatureC);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void HandlesWindowsAndUnixLineEndings()
    {
        var output = "GPU-a, NVIDIA A, 00000000:01:00.0, 0x1E9110DE, 1, 2, 3, 4\r\nGPU-b, NVIDIA B, 00000000:02:00.0, 0x1E9110DE, 5, 6, 7, 8";

        Assert.Equal(2, NvidiaSmiParser.Parse(output).Count);
    }
}

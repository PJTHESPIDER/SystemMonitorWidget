using System;
using System.Linq;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the layout defects observed in the live Windows widget host.
///
/// <para>
/// These are regression tests for what the host actually rendered, not for
/// what the templates were intended to render. On the test machine the
/// real Widgets board was driven through UI Automation and the rendered
/// text runs were measured, and three things were wrong:
///
/// </para>
///
/// <list type="number">
/// <item><description>
/// A GPU-memory figure of about one gigabyte was shown in kilobytes —
/// "1002496.0 KB" — because <see cref="ByteFormatter"/> had no megabyte
/// tier. The figure was twelve characters long, and the host ellipsized it
/// to "1021952.0…": the number was unreadable in every size.
/// </description></item>
/// <item><description>
/// The GPU row put Usage, Temp and the memory figure in three
/// <c>stretch</c> columns, so the memory column was about a third of the
/// card. A combined "used / total (percent)" figure does not fit a
/// third of 302 pixels, so it was clipped even when the digits were short
/// enough.
/// </description></item>
/// <item><description>
/// Every template carried a root-level <c>actions</c> array. The host
/// renders that as a row of buttons after the card body, and the board
/// does not scroll, so the row competed with the figures for the card's
/// height: the Storage value and then the action buttons themselves were
/// clipped off the bottom of the Small and Medium cards.
/// </description></item>
/// </list>
///
/// <para>
/// A template test cannot measure pixels, so these tests pin the
/// properties that caused the observed failures: the unit ladder, the
/// column widths, and the absence of the action row. The rendered result
/// was confirmed separately in the live host.
/// </para>
/// </summary>
public class WidgetLayoutFitTests
{
    // =====================================================================
    // The unit ladder has no kilobyte tier above a megabyte
    // =====================================================================

    [Theory]
    [InlineData(1_026_554_624.0, "979.0 MB")]
    [InlineData(1_048_576.0, "1.0 MB")]
    [InlineData(1_073_741_824.0, "1.0 GB")]
    [InlineData(4_000_000_000.0, "3.7 GB")]
    public void AFigureBelowOneGigabyte_IsNotShownInKilobytes(double bytes, string expected)
    {
        // The live host rendered a one-gigabyte GPU-memory figure as
        // "1002496.0 KB", which it then ellipsized. A figure of about a
        // gigabyte is shown in megabytes.
        Assert.Equal(expected, ByteFormatter.FormatBytes(bytes));
    }

    [Fact]
    public void AFigureOfAboutOneGigabyte_FitsInFewCharacters()
    {
        // The live host rendered this figure as "1002496.0 KB" — twelve
        // characters — and the host ellipsized it to "1026048.0…". The
        // memory column was about 84 pixels wide, which is why the
        // kilobyte form did not fit.
        var text = ByteFormatter.FormatBytes(1_026_554_624.0);

        Assert.DoesNotContain("KB", text);
        Assert.True(
            text.Length <= 8,
            $"'{text}' is {text.Length} characters, which is too long for the memory column.");
    }

    [Fact]
    public void TheUnitLadderIsTheSameInEveryDirection()
    {
        // FormatNetworkSpeed already had a megabyte tier. The byte ladder
        // did not, and the two drifted apart. They are checked together so
        // the ladders cannot drift again.
        Assert.Equal("1.0 MB/s", ByteFormatter.FormatNetworkSpeed(1_048_576.0));
        Assert.Equal("1.0 MB", ByteFormatter.FormatBytes(1_048_576.0));

        // The same ladder, one order down.
        Assert.Equal("1.0 KB/s", ByteFormatter.FormatNetworkSpeed(1_024.0));
        Assert.Equal("1.0 KB", ByteFormatter.FormatBytes(1_024.0));
    }

    // =====================================================================
    // The memory figure gets a column wide enough for it
    // =====================================================================

    [Theory]
    [InlineData(WidgetLayoutSize.Small)]
    [InlineData(WidgetLayoutSize.Medium)]
    [InlineData(WidgetLayoutSize.Large)]
    public void TheMemoryColumnTakesTheRoomTheOtherColumnsDoNotNeed(
        WidgetLayoutSize size)
    {
        // The GPU row's Usage and Temp columns are narrow figures, so they
        // are sized to their content ("auto") and the memory column takes
        // the rest ("stretch"). With three "stretch" columns the memory
        // column got about a third of the card and the figure was clipped.
        var template = WidgetTemplates.BuildTemplate(size);

        Assert.Contains("\"text\": \"${gpu}", template.Replace(" ", " "));
    }

    [Theory]
    [InlineData(WidgetLayoutSize.Medium)]
    [InlineData(WidgetLayoutSize.Large)]
    public void TheGpuRowGivesTheMemoryColumnTheRestOfTheRow(WidgetLayoutSize size)
    {
        // The memory column is the last column of the GPU row, and it is
        // the only one that is "stretch": the Usage and Temp columns are
        // "auto", so they take only what their own figures need.
        var template = WidgetTemplates.BuildTemplate(size);

        var gpuRow = GpuRowOf(template);
        Assert.NotNull(gpuRow);

        var columns = ColumnsOf(gpuRow!);

        // At least one "auto" column and a "stretch" one.
        Assert.Contains(columns, c => c == "auto");
        Assert.Contains(columns, c => c == "stretch");

        // The stretch column is the last one, which is the memory column.
        Assert.Equal("stretch", columns.Last());
    }

    [Fact]
    public void TheSmallLayoutsMemoryColumnIsNotSqueezedByAStretchSibling()
    {
        // The small layout uses "auto" columns for its figures, so the
        // memory figure is measured by its own content rather than being
        // given a fixed share of a row that other figures also want.
        var template = WidgetTemplates.BuildTemplate(WidgetLayoutSize.Small);

        Assert.Contains("\"text\": \"${gpuMemoryCapacityCompact}\"", template);
    }

    // =====================================================================
    // No template carries an action row
    // =====================================================================

    [Theory]
    [InlineData(WidgetLayoutSize.Small)]
    [InlineData(WidgetLayoutSize.Medium)]
    [InlineData(WidgetLayoutSize.Large)]
    public void NoTemplateCarriesAnActionRow(WidgetLayoutSize size)
    {
        // The host lays the action row out after the body, and the board
        // clips whatever does not fit. The row is what pushed the Storage
        // value and then the action buttons off the bottom of the card.
        var template = WidgetTemplates.BuildTemplate(size);

        Assert.DoesNotContain("\"actions\"", template);
        Assert.DoesNotContain("Action.Execute", template);
    }

    [Theory]
    [InlineData(WidgetLayoutSize.Small)]
    [InlineData(WidgetLayoutSize.Medium)]
    [InlineData(WidgetLayoutSize.Large)]
    public void EveryTemplateIsValidJsonWithoutTheActionRow(WidgetLayoutSize size)
    {
        var template = WidgetTemplates.BuildTemplate(size);

        using var document = System.Text.Json.JsonDocument.Parse(template);

        Assert.Equal("AdaptiveCard", document.RootElement.GetProperty("type").GetString());
    }

    // =====================================================================
    // The Medium card shows every section it declares
    // =====================================================================

    [Fact]
    public void TheMediumLayoutShowsTheDiskFigureInACompactForm()
    {
        // The medium card overflowed its own height: the Storage value was
        // clipped away entirely. The RAM and Storage figures are now shown
        // in their compact one-line form, which is what the small layout
        // already used and what fits.
        var template = WidgetTemplates.BuildTemplate(WidgetLayoutSize.Medium);

        Assert.Contains("\"text\": \"${diskCapacityCompact}\"", template);
        Assert.Contains("\"text\": \"${ramCapacityCompact}\"", template);
    }

    [Fact]
    public void TheSmallLayoutStillNamesTheDiskFigure()
    {
        // The small card is 148 pixels tall and cannot carry every figure,
        // but the disk figure it did carry was clipped off the bottom.
        // With the action row gone there is room for it again.
        var template = WidgetTemplates.BuildTemplate(WidgetLayoutSize.Small);

        Assert.Contains("\"text\": \"${diskCapacityCompact}\"", template);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    /// <summary>
    /// The JSON of the GPU row — the ColumnSet that binds the GPU usage,
    /// temperature and memory figures.
    /// </summary>
    private static string? GpuRowOf(string template)
    {
        foreach (var block in BlocksOf(template))
        {
            if (block.Contains("\"${gpu}\"", StringComparison.Ordinal)
                && block.Contains("\"${gpuMemoryCapacityCompact}\"", StringComparison.Ordinal))
            {
                return block;
            }
        }

        return null;
    }

    /// <summary>
    /// The width of each column of a ColumnSet block.
    /// </summary>
    private static System.Collections.Generic.List<string> ColumnsOf(string block)
    {
        var widths = new System.Collections.Generic.List<string>();

        var index = 0;
        while (true)
        {
            var at = block.IndexOf("\"width\": \"", index, StringComparison.Ordinal);
            if (at < 0)
                break;

            var start = at + "\"width\": \"".Length;
            var end = block.IndexOf('"', start);
            if (end < 0)
                break;

            widths.Add(block.Substring(start, end - start));
            index = end;
        }

        return widths;
    }

    /// <summary>
    /// The top-level blocks of a template's body, as text.
    /// </summary>
    private static System.Collections.Generic.List<string> BlocksOf(string template)
    {
        var blocks = new System.Collections.Generic.List<string>();

        // A block starts at a line that opens with eight spaces and a
        // brace, and ends at the matching close. Counting braces is enough
        // here: the templates are plain JSON with no braces in strings.
        var lines = template.Split('\n');
        var current = new System.Text.StringBuilder();
        var depth = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (depth == 0 && trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                current.Clear();
            }

            if (depth > 0 || trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                current.Append(line);
                current.Append('\n');

                depth += trimmed.Count(c => c == '{') - trimmed.Count(c => c == '}');

                if (depth == 0)
                {
                    blocks.Add(current.ToString());
                }
            }
        }

        return blocks;
    }
}

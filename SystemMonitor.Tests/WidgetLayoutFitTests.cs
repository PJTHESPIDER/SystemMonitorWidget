using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

        // Root-level "actions" array is forbidden (it renders as a separate
        // row after the body). ActionSet elements inside the body are allowed.
        using var document = System.Text.Json.JsonDocument.Parse(template);
        Assert.False(document.RootElement.TryGetProperty("actions", out _),
            "Template has a root-level actions array which will be rendered as a separate row and compete for height.");
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

    [Fact]
    public void SmallLayoutKeepsItsExistingSectionGap()
    {
        AssertSizeLayout(
            WidgetLayoutSize.Small,
            "Small",
            new[] { "${cpu}", "${gpu}", "${ramCapacityCompact}", "${diskCapacityCompact}" });
    }

    [Fact]
    public void TheSmallGpuMetricGroupsAreSeparatedFromTheirPeers()
    {
        // The small GPU row keeps its own ColumnSet; the separation between
        // the GPU metric groups is carried by the columns. Only the gap
        // before the Temp and memory groups is widened here, so the GPU
        // label-to-first-value gap and the row-level ColumnSet spacing are
        // left exactly as they were.
        using var document = System.Text.Json.JsonDocument.Parse(WidgetTemplates.BuildTemplate(WidgetLayoutSize.Small));
        var body = document.RootElement.GetProperty("body");
        var gpuRow = FindBodyElement(body, "${gpuMemoryCapacityCompact}");
        Assert.Equal("Small", gpuRow.GetProperty("spacing").GetString());
        var columns = gpuRow.GetProperty("columns").EnumerateArray().ToArray();
        Assert.Equal(4, columns.Length);
        Assert.False(columns[0].TryGetProperty("spacing", out _));
        Assert.False(columns[1].TryGetProperty("spacing", out _));
        Assert.Equal("Large", columns[2].GetProperty("spacing").GetString());
        Assert.Equal("Large", columns[3].GetProperty("spacing").GetString());
    }

    [Fact]
    public void TheSmallCpuMetricGroupsAreSeparatedFromTheirPeers()
    {
        // The small CPU row keeps its own ColumnSet; separation between the
        // CPU peer metric groups uses the same treatment as the small GPU row,
        // while the CPU label-to-first-value gap stays unchanged. The
        // conditional temperature column keeps its own $when.
        using var document = System.Text.Json.JsonDocument.Parse(WidgetTemplates.BuildTemplate(WidgetLayoutSize.Small));
        var body = document.RootElement.GetProperty("body");
        var cpuRow = FindBodyElement(body, "${cpuTemp}");
        Assert.Equal("Small", cpuRow.GetProperty("spacing").GetString());
        var columns = cpuRow.GetProperty("columns").EnumerateArray().ToArray();
        Assert.Equal(4, columns.Length);
        Assert.False(columns[0].TryGetProperty("spacing", out _));
        Assert.False(columns[1].TryGetProperty("spacing", out _));
        Assert.Equal("Medium", columns[2].GetProperty("spacing").GetString());
        Assert.Equal("Medium", columns[3].GetProperty("spacing").GetString());
    }

    [Theory]
    [InlineData(WidgetLayoutSize.Medium)]
    [InlineData(WidgetLayoutSize.Large)]
    public void SectionMetricsStayTightToTheirHeadings(WidgetLayoutSize size)
    {
        using var document = System.Text.Json.JsonDocument.Parse(WidgetTemplates.BuildTemplate(size));
        var body = document.RootElement.GetProperty("body");
        var metricBindings = new List<string>
        {
            "${cpu}",
            "${gpu}",
            "${ramCapacityCompact}",
            "${diskCapacityCompact}",
        };

        if (size == WidgetLayoutSize.Large)
        {
            metricBindings.Add("${netDown}");
            metricBindings.Add("${netUp}");
        }

        foreach (var binding in metricBindings)
        {
            var metrics = FindNestedTextElement(body, binding);
            Assert.Equal("None", metrics.GetProperty("spacing").GetString());
        }
    }

    [Theory]
    [InlineData(WidgetLayoutSize.Medium)]
    [InlineData(WidgetLayoutSize.Large)]
    public void TheGpuMetricGroupsAreSeparatedFromTheirPeers(WidgetLayoutSize size)
    {
        // The GPU section renders its peer metric groups — Usage, Temp and
        // the memory figure — as the columns of one ColumnSet. Spacing
        // between those groups is the per-column "spacing" property: each
        // column carries the gap to the column before it, and the first
        // column in a set carries none. The groups were packed with no gap,
        // which read as crowded, so the group-to-group spacing is now set
        // explicitly as inter-column GPU metric spacing. Only the
        // group-to-group spacing changes; the ColumnSet's own "spacing" (the
        // gap to the heading that precedes it) and each group's own value,
        // which stays tight to its label, are unchanged.
        using var document = System.Text.Json.JsonDocument.Parse(WidgetTemplates.BuildTemplate(size));
        var body = document.RootElement.GetProperty("body");
        var gpuMetrics = Descendants(body).First(element =>
            element.ValueKind == System.Text.Json.JsonValueKind.Object
            && element.TryGetProperty("type", out var type)
            && type.GetString() == "ColumnSet"
            && ContainsText(element, "${gpuMemoryCapacityCompact}"));
        Assert.Equal("None", gpuMetrics.GetProperty("spacing").GetString());
        var columns = gpuMetrics.GetProperty("columns").EnumerateArray().ToArray();
        Assert.Equal(3, columns.Length);
        Assert.False(columns[0].TryGetProperty("spacing", out _));
        Assert.Equal("Large", columns[1].GetProperty("spacing").GetString());
        Assert.Equal("Large", columns[2].GetProperty("spacing").GetString());
    }

    [Theory]
    [InlineData(
        WidgetLayoutSize.Medium,
        new[] { "layoutGapLeading", "layoutGapBeforeGpu", "layoutGapBeforeMemory", "layoutGapBeforeStorage", "layoutGapTrailing" },
        4)]
    [InlineData(
        WidgetLayoutSize.Large,
        new[] { "layoutGapLeading", "layoutGapBeforeGpu", "layoutGapBeforeMemory", "layoutGapBeforeStorage", "layoutGapBeforeNetwork", "layoutGapTrailing" },
        5)]
    public void MediumAndLargeDistributeAvailableHeightIntoStretchGaps(
        WidgetLayoutSize size,
        string[] expectedGapIds,
        int expectedCategoryCount)
    {
        using var document = System.Text.Json.JsonDocument.Parse(WidgetTemplates.BuildTemplate(size));
        var root = document.RootElement;
        var body = root.GetProperty("body");
        var bodyElements = body.EnumerateArray().ToArray();
        var categories = bodyElements
            .Where(element =>
                element.TryGetProperty("type", out var type)
                && type.GetString() == "Container"
                && element.TryGetProperty("items", out var items)
                && items.GetArrayLength() > 0)
            .ToArray();
        var gaps = bodyElements
            .Where(element =>
                element.TryGetProperty("type", out var type)
                && type.GetString() == "Container"
                && element.TryGetProperty("id", out var id)
                && id.GetString()!.StartsWith("layoutGap", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(expectedCategoryCount, categories.Length);
        Assert.Equal("Center", root.GetProperty("verticalContentAlignment").GetString());
        Assert.Equal(
            expectedGapIds,
            gaps.Select(gap => gap.GetProperty("id").GetString()).ToArray());

        foreach (var gap in gaps)
        {
            Assert.Equal("stretch", gap.GetProperty("height").GetString());
            Assert.Equal("None", gap.GetProperty("spacing").GetString());
            Assert.Empty(gap.GetProperty("items").EnumerateArray());
            Assert.False(gap.TryGetProperty("padding", out _));
        }

        foreach (var category in categories)
        {
            Assert.Equal("None", category.GetProperty("spacing").GetString());
            Assert.False(category.TryGetProperty("padding", out _));

            foreach (var item in category.GetProperty("items").EnumerateArray())
            {
                // Every item in a category stays tight to its content: the GPU
                // metric groups carry their group-to-group gap on the columns
                // of their ColumnSet, not on the ColumnSet itself.
                Assert.Equal("None", item.GetProperty("spacing").GetString());
            }
        }

        var conditionalGpuGap = gaps.First(gap =>
            gap.GetProperty("id").GetString() == "layoutGapBeforeGpu");
        Assert.Equal(
            "${gpuSectionPresent}",
            conditionalGpuGap.GetProperty("$when").GetString());

        var gpuCategory = categories.First(category =>
            ContainsText(category, size == WidgetLayoutSize.Medium
                ? "GPU — ${gpuNameCompact}"
                : "Graphics (GPU) — ${gpuNameCompact}"));
        Assert.Equal("${gpuSectionPresent}", gpuCategory.GetProperty("$when").GetString());
    }

    [Theory]
    [InlineData(WidgetLayoutSize.Small)]
    [InlineData(WidgetLayoutSize.Medium)]
    [InlineData(WidgetLayoutSize.Large)]
    public void ConditionalRowsAreRemovedWithoutLeavingLayoutPlaceholders(WidgetLayoutSize size)
    {
        using var document = System.Text.Json.JsonDocument.Parse(WidgetTemplates.BuildTemplate(size));
        var root = document.RootElement;
        var body = root.GetProperty("body");

        Assert.Equal("Center", root.GetProperty("verticalContentAlignment").GetString());

        var staleNotice = FindBodyElement(body, "Telemetry is stale");
        Assert.Equal("${sampleIsStale}", staleNotice.GetProperty("$when").GetString());

        Assert.DoesNotContain(Descendants(body), element =>
            element.ValueKind == System.Text.Json.JsonValueKind.Object
            && element.TryGetProperty("type", out var type)
            && type.GetString() == "Spacer");
        Assert.DoesNotContain(Descendants(body), element =>
            element.ValueKind == System.Text.Json.JsonValueKind.Object
            && element.TryGetProperty("type", out var type)
            && type.GetString() == "TextBlock"
            && element.TryGetProperty("text", out var text)
            && string.IsNullOrWhiteSpace(text.GetString()));

        var cpuTemperatureColumn = Descendants(body).First(element =>
            element.ValueKind == System.Text.Json.JsonValueKind.Object
            && element.TryGetProperty("type", out var type)
            && type.GetString() == "Column"
            && ContainsText(element, "${cpuTemp}"));
        Assert.Equal("${cpuTempShow}", cpuTemperatureColumn.GetProperty("$when").GetString());

        if (size == WidgetLayoutSize.Small)
        {
            var gpuRow = FindBodyElement(body, "${gpuMemoryCapacityCompact}");
            Assert.Equal("${gpuDevicePresent}", gpuRow.GetProperty("$when").GetString());
        }
        else
        {
            var gpuHeading = FindNestedTextElement(
                body,
                size == WidgetLayoutSize.Medium
                    ? "GPU — ${gpuNameCompact}"
                    : "Graphics (GPU) — ${gpuNameCompact}");
            Assert.Equal("${gpuDevicePresent}", gpuHeading.GetProperty("$when").GetString());

            var gpuMetrics = Descendants(body).First(element =>
                element.ValueKind == System.Text.Json.JsonValueKind.Object
                && element.TryGetProperty("type", out var type)
                && type.GetString() == "ColumnSet"
                && ContainsText(element, "${gpuMemoryCapacityCompact}"));
            Assert.Equal("${gpuDevicePresent}", gpuMetrics.GetProperty("$when").GetString());
        }

        var extraCount = size == WidgetLayoutSize.Large ? 3 : size == WidgetLayoutSize.Medium ? 1 : 0;
        foreach (var extraIndex in Enumerable.Range(1, extraCount))
        {
            var extra = FindNestedTextElement(body, $"Other GPU — ${{gpuExtra{extraIndex}}}");
            Assert.Equal($"${{gpuExtra{extraIndex}Available}}", extra.GetProperty("$when").GetString());
            Assert.Equal("None", extra.GetProperty("spacing").GetString());
        }
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

    private static void AssertSizeLayout(
        WidgetLayoutSize size,
        string peerGap,
        string[] peerSections)
    {
        using var document = System.Text.Json.JsonDocument.Parse(WidgetTemplates.BuildTemplate(size));
        var root = document.RootElement;
        var body = root.GetProperty("body");

        Assert.Equal("Center", root.GetProperty("verticalContentAlignment").GetString());
        if (size == WidgetLayoutSize.Medium)
        {
            var cpuHeading = FindNestedTextElement(body, "CPU — ${cpuNameCompact}");
            Assert.Equal("None", cpuHeading.GetProperty("spacing").GetString());
        }
        else if (size == WidgetLayoutSize.Large)
        {
            var cpuHeading = FindNestedTextElement(body, "Processor (CPU) — ${cpuNameCompact}");
            Assert.Equal("None", cpuHeading.GetProperty("spacing").GetString());
        }

        foreach (var section in peerSections)
        {
            var element = FindBodyElement(body, section);
            Assert.Equal(peerGap, element.GetProperty("spacing").GetString());
        }
    }

    private static System.Text.Json.JsonElement FindBodyElement(
        System.Text.Json.JsonElement body,
        string text) =>
        body.EnumerateArray().First(element => ContainsText(element, text));

    private static System.Text.Json.JsonElement FindNestedTextElement(
        System.Text.Json.JsonElement element,
        string text) =>
        Descendants(element).First(candidate =>
            candidate.ValueKind == System.Text.Json.JsonValueKind.Object
            && candidate.TryGetProperty("text", out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String
            && value.GetString()!.Contains(text, StringComparison.Ordinal));

    private static bool ContainsText(System.Text.Json.JsonElement element, string text)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("text")
                    && property.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    && property.Value.GetString()!.Contains(text, StringComparison.Ordinal))
                {
                    return true;
                }

                if (ContainsText(property.Value, text))
                    return true;
            }
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            return element.EnumerateArray().Any(child => ContainsText(child, text));
        }

        return false;
    }

    private static IEnumerable<System.Text.Json.JsonElement> Descendants(
        System.Text.Json.JsonElement element)
    {
        yield return element;

        if (element.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                foreach (var descendant in Descendants(property.Value))
                    yield return descendant;
            }
        }
        else if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                foreach (var descendant in Descendants(child))
                    yield return descendant;
            }
        }
    }

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

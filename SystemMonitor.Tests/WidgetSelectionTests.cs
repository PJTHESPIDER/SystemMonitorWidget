using System;
using System.Linq;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the per-widget device selection: the registry's storage of
/// the selection, and the templates that carry no action row.
/// </summary>
public class WidgetSelectionTests
{
    // --- Registry --------------------------------------------------------

    [Fact]
    public void ANewWidgetHasNoSelection()
    {
        var registry = new WidgetRegistry<int>();
        registry.Set(new WidgetEntry<int> { WidgetId = "w1", Size = 1, IsActive = true });

        Assert.True(registry.TryGet("w1", out var entry));
        Assert.Null(entry!.SelectedGpuInstanceId);
    }

    [Fact]
    public void ASelectionIsStoredAgainstTheInstanceId()
    {
        var registry = new WidgetRegistry<int>();
        registry.Set(new WidgetEntry<int> { WidgetId = "w1" });

        Assert.True(registry.TrySetSelectedGpu("w1", @"PCI\VEN_10DE&DEV_1E91\4&1&0"));

        Assert.True(registry.TryGet("w1", out var entry));
        Assert.Equal(@"PCI\VEN_10DE&DEV_1E91\4&1&0", entry!.SelectedGpuInstanceId);
    }

    [Fact]
    public void ASelectionIsScopedToOneWidget()
    {
        var registry = new WidgetRegistry<int>();
        registry.Set(new WidgetEntry<int> { WidgetId = "w1" });
        registry.Set(new WidgetEntry<int> { WidgetId = "w2" });

        registry.TrySetSelectedGpu("w1", "pci-a");

        Assert.True(registry.TryGet("w2", out var other));
        Assert.Null(other!.SelectedGpuInstanceId);
    }

    [Fact]
    public void ASelectionCanBeCleared()
    {
        var registry = new WidgetRegistry<int>();
        registry.Set(new WidgetEntry<int> { WidgetId = "w1" });
        registry.TrySetSelectedGpu("w1", "pci-a");

        Assert.True(registry.TrySetSelectedGpu("w1", null));

        Assert.True(registry.TryGet("w1", out var entry));
        Assert.Null(entry!.SelectedGpuInstanceId);
    }

    [Fact]
    public void ABlankSelectionIsStoredAsNoSelection()
    {
        var registry = new WidgetRegistry<int>();
        registry.Set(new WidgetEntry<int> { WidgetId = "w1" });
        registry.TrySetSelectedGpu("w1", "pci-a");

        registry.TrySetSelectedGpu("w1", "   ");

        Assert.True(registry.TryGet("w1", out var entry));
        Assert.Null(entry!.SelectedGpuInstanceId);
    }

    [Fact]
    public void ASelectionIsTrimmed()
    {
        var registry = new WidgetRegistry<int>();
        registry.Set(new WidgetEntry<int> { WidgetId = "w1" });

        registry.TrySetSelectedGpu("w1", "  pci-a  ");

        Assert.True(registry.TryGet("w1", out var entry));
        Assert.Equal("pci-a", entry!.SelectedGpuInstanceId);
    }

    [Fact]
    public void SettingASelectionOnAnUnknownWidgetFails()
    {
        var registry = new WidgetRegistry<int>();

        Assert.False(registry.TrySetSelectedGpu("nope", "pci-a"));
    }

    [Fact]
    public void SettingASelectionKeepsTheOtherWidgetState()
    {
        var registry = new WidgetRegistry<int>();
        registry.Set(new WidgetEntry<int> { WidgetId = "w1", Size = 2, IsActive = true });

        registry.TrySetSelectedGpu("w1", "pci-a");

        Assert.True(registry.TryGet("w1", out var entry));
        Assert.Equal(2, entry!.Size);
        Assert.True(entry.IsActive);
    }

    [Fact]
    public void ASelectionSurvivesAnotherSelection()
    {
        var registry = new WidgetRegistry<int>();
        registry.Set(new WidgetEntry<int> { WidgetId = "w1" });

        registry.TrySetSelectedGpu("w1", "pci-a");
        registry.TrySetSelectedGpu("w1", "pci-b");

        Assert.True(registry.TryGet("w1", out var entry));
        Assert.Equal("pci-b", entry!.SelectedGpuInstanceId);
    }

    // --- The action row is gone ------------------------------------------

    [Theory]
    [InlineData(WidgetLayoutSize.Small)]
    [InlineData(WidgetLayoutSize.Medium)]
    [InlineData(WidgetLayoutSize.Large)]
    public void TheTemplateCarriesNoActionRow(WidgetLayoutSize size)
    {
        // The host renders a root-level "actions" array as a row of buttons
        // after the card body. The board does not scroll, so that row
        // competes with the figures for the card's height and the overflow
        // is clipped — which is what pushed the action buttons off the
        // bottom of every size in the live host.
        //
        // The templates therefore carry no action row at all.
        var template = WidgetTemplates.BuildTemplate(size);

        using var document = System.Text.Json.JsonDocument.Parse(template);

        Assert.False(
            document.RootElement.TryGetProperty("actions", out _),
            $"The {size} template still carries an actions array.");
    }

    [Theory]
    [InlineData(WidgetLayoutSize.Small)]
    [InlineData(WidgetLayoutSize.Medium)]
    [InlineData(WidgetLayoutSize.Large)]
    public void TheTemplateIsValidJsonWithoutTheActionRow(WidgetLayoutSize size)
    {
        var template = WidgetTemplates.BuildTemplate(size);

        using var document = System.Text.Json.JsonDocument.Parse(template);

        Assert.Equal("AdaptiveCard", document.RootElement.GetProperty("type").GetString());
        Assert.Equal(
            System.Text.Json.JsonValueKind.Array,
            document.RootElement.GetProperty("body").ValueKind);
    }
}

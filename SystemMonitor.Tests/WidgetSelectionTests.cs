using System;
using System.Linq;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the per-widget device selection: the registry's storage of
/// the selection, and the device-picker actions the templates offer.
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

    // --- Picker actions --------------------------------------------------

    [Fact]
    public void TheTemplateOffersAnActionPerSelectableDevice()
    {
        var choices = new[]
        {
            new WidgetPayloadPresenter.GpuChoiceOption("pci-a", "NVIDIA GeForce RTX 3080", true),
            new WidgetPayloadPresenter.GpuChoiceOption("pci-b", "AMD Radeon RX 6700 XT", false),
        };

        var template = WidgetTemplates.BuildTemplate(WidgetLayoutSize.Medium, choices);

        using var document = System.Text.Json.JsonDocument.Parse(template);

        var verbs = document.RootElement.GetProperty("actions")
            .EnumerateArray()
            .Select(a => a.GetProperty("verb").GetString())
            .ToList();

        Assert.Contains(WidgetTemplates.RefreshVerb, verbs);
        Assert.Contains(WidgetTemplates.SelectGpuVerbPrefix + "pci-a", verbs);
        Assert.Contains(WidgetTemplates.SelectGpuVerbPrefix + "pci-b", verbs);
    }

    [Fact]
    public void ADeviceWithNoInstanceIdentityIsNotOfferedAsAChoice()
    {
        // A selection has to be resolvable after a restart, so a device
        // without a per-instance identity is described but not selectable.
        var choices = new[]
        {
            new WidgetPayloadPresenter.GpuChoiceOption(null, "Unknown GPU", false),
        };

        var template = WidgetTemplates.BuildTemplate(WidgetLayoutSize.Medium, choices);

        using var document = System.Text.Json.JsonDocument.Parse(template);

        var verbs = document.RootElement.GetProperty("actions")
            .EnumerateArray()
            .Select(a => a.GetProperty("verb").GetString())
            .ToList();

        Assert.Single(verbs);
        Assert.Equal(WidgetTemplates.RefreshVerb, verbs[0]);
    }

    [Fact]
    public void TheChoiceTitleNamesTheDevice()
    {
        var choices = new[]
        {
            new WidgetPayloadPresenter.GpuChoiceOption("pci-a", "NVIDIA GeForce RTX 3080 (discrete)", false),
        };

        var template = WidgetTemplates.BuildTemplate(WidgetLayoutSize.Medium, choices);

        using var document = System.Text.Json.JsonDocument.Parse(template);

        var titles = document.RootElement.GetProperty("actions")
            .EnumerateArray()
            .Select(a => a.GetProperty("title").GetString())
            .ToList();

        Assert.Contains("NVIDIA GeForce RTX 3080 (discrete)", titles);
    }
}

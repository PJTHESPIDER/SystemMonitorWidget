using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the manufacturer-neutral widget payload.
///
/// <para>
/// These exercise the presenter against fixtures for every GPU vendor,
/// every device count, and every combination of missing sensors. A test
/// here proves the payload logic behaves correctly for a given input; it
/// does not prove that a real machine of that type produces those readings.
/// </para>
/// </summary>
public class WidgetPayloadPresenterTests
{
    private static readonly DateTimeOffset SampleTime =
        new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    // --- Fixture helpers -------------------------------------------------

    private static HardwareDevice Gpu(
        string deviceId,
        string name,
        string? vendor = null,
        GpuIntegration integration = GpuIntegration.Unknown,
        string? instanceId = null) =>
        new()
        {
            DeviceId = deviceId,
            DisplayName = name,
            Vendor = vendor,
            DeviceClass = DeviceClass.Gpu,
            GpuIntegration = integration,
            InstanceId = instanceId,
        };

    private static HardwareDevice Cpu(string deviceId, string name, string? vendor = null) =>
        new()
        {
            DeviceId = deviceId,
            DisplayName = name,
            Vendor = vendor,
            DeviceClass = DeviceClass.Cpu,
        };

    private static DeviceReading Read(string deviceId, string metric, double? value, string? unit = "%") =>
        new()
        {
            DeviceId = deviceId,
            Metric = metric,
            Measurement = value.HasValue
                ? Measurement.AvailableValue(value.Value, unit, "test")
                : Measurement.Unsupported("test"),
        };

    private static DeviceReading Failed(string deviceId, string metric) =>
        new() { DeviceId = deviceId, Metric = metric, Measurement = Measurement.Failed("test") };

    private static MetricsSnapshot Snapshot(
        IEnumerable<HardwareDevice>? cpus = null,
        IEnumerable<HardwareDevice>? gpus = null,
        IEnumerable<DeviceReading>? readings = null,
        DateTimeOffset? timestamp = null) =>
        MetricsSnapshot.Create(
            timestamp ?? SampleTime,
            cpus: cpus,
            gpus: gpus,
            readings: readings);

    // =====================================================================
    // Payload / template binding compatibility
    // =====================================================================

    /// <summary>
    /// Every template must bind only to names the payload produces. A
    /// template that references a name the payload does not carry would
    /// render as an empty string, which is indistinguishable from a real
    /// value.
    /// </summary>
    [Theory]
    [InlineData(WidgetLayoutSize.Small)]
    [InlineData(WidgetLayoutSize.Medium)]
    [InlineData(WidgetLayoutSize.Large)]
    public void Template_BindsOnlyToNamesThePayloadProduces(WidgetLayoutSize size)
    {
        var payload = WidgetPayloadPresenter.Present(HybridSnapshot());
        var template = WidgetTemplates.BuildTemplate(size, Array.Empty<WidgetPayloadPresenter.GpuChoiceOption>());

        var referenced = TemplateBindings.ExtractPropertyNames(template);

        Assert.NotEmpty(referenced);

        foreach (var name in referenced)
        {
            Assert.True(
                payload.Contains(name),
                $"The {size} template references '{name}', which the payload does not produce.");
        }
    }

    /// <summary>
    /// The same check for the empty payload: a template must still bind
    /// when there is nothing at all to show.
    /// </summary>
    [Theory]
    [InlineData(WidgetLayoutSize.Small)]
    [InlineData(WidgetLayoutSize.Medium)]
    [InlineData(WidgetLayoutSize.Large)]
    public void Template_BindsOnlyToNamesTheEmptyPayloadProduces(WidgetLayoutSize size)
    {
        var payload = WidgetPayloadPresenter.Present(null);
        var template = WidgetTemplates.BuildTemplate(size, Array.Empty<WidgetPayloadPresenter.GpuChoiceOption>());

        foreach (var name in TemplateBindings.ExtractPropertyNames(template))
        {
            Assert.True(
                payload.Contains(name),
                $"The {size} template references '{name}', which the empty payload does not produce.");
        }
    }

    /// <summary>
    /// Every size's template is valid JSON once the action set has been
    /// spliced in, and every one of them carries the refresh action.
    /// </summary>
    [Theory]
    [InlineData(WidgetLayoutSize.Small)]
    [InlineData(WidgetLayoutSize.Medium)]
    [InlineData(WidgetLayoutSize.Large)]
    public void Template_IsValidJsonAndCarriesTheRefreshAction(WidgetLayoutSize size)
    {
        var template = WidgetTemplates.BuildTemplate(
            size, Array.Empty<WidgetPayloadPresenter.GpuChoiceOption>());

        using var document = JsonDocument.Parse(template);

        var actions = document.RootElement.GetProperty("actions");
        Assert.Equal(JsonValueKind.Array, actions.ValueKind);

        var verbs = actions.EnumerateArray()
            .Select(a => a.GetProperty("verb").GetString())
            .ToList();

        Assert.Contains(WidgetTemplates.RefreshVerb, verbs);
    }

    /// <summary>
    /// The templates are valid JSON even when a device's name contains
    /// characters that have to be escaped inside a JSON string.
    /// </summary>
    [Theory]
    [InlineData(WidgetLayoutSize.Small)]
    [InlineData(WidgetLayoutSize.Medium)]
    [InlineData(WidgetLayoutSize.Large)]
    public void Template_IsValidJsonWithAwkwardDeviceNames(WidgetLayoutSize size)
    {
        var choices = new[]
        {
            new WidgetPayloadPresenter.GpuChoiceOption(
                "pci\\ven_10de&dev_1e91\\4&7aadb68&0&0008",
                "NVIDIA \"GeForce\" RTX \\ 3080",
                false),
        };

        var template = WidgetTemplates.BuildTemplate(size, choices);

        using var document = JsonDocument.Parse(template);

        var verbs = document.RootElement.GetProperty("actions")
            .EnumerateArray()
            .Select(a => a.GetProperty("verb").GetString())
            .ToList();

        Assert.Contains(
            WidgetTemplates.SelectGpuVerbPrefix + "pci\\ven_10de&dev_1e91\\4&7aadb68&0&0008",
            verbs);
    }

    /// <summary>
    /// The payload serializes to JSON whose flags are real booleans, so a
    /// template's <c>$when</c> condition can evaluate them.
    /// </summary>
    [Fact]
    public void Payload_FlagsAreJsonBooleans()
    {
        var payload = WidgetPayloadPresenter.Present(HybridSnapshot());
        using var document = JsonDocument.Parse(payload.ToJson());

        Assert.Equal(
            JsonValueKind.True,
            document.RootElement.GetProperty(WidgetPayloadPresenter.GpuDevicePresentKey).ValueKind);

        Assert.Equal(
            JsonValueKind.True,
            document.RootElement.GetProperty(WidgetPayloadPresenter.ExtraAvailableKey(1)).ValueKind);

        Assert.Equal(
            JsonValueKind.String,
            document.RootElement.GetProperty(WidgetPayloadPresenter.GpuKey).ValueKind);
    }

    // =====================================================================
    // Vendor fixtures
    // =====================================================================

    [Fact]
    public void AmdOnly_NamesTheAmdCard()
    {
        var snapshot = Snapshot(
            cpus: new[] { Cpu("cpu0", "AMD Ryzen 9 5950X", "AMD") },
            gpus: new[] { Gpu("gpu0", "AMD Radeon RX 6700 XT", "AMD", GpuIntegration.Discrete, "pci-amd-0") },
            readings: new[]
            {
                Read("gpu0", "LoadPercent", 12.0),
                Read("gpu0", "TemperatureC", 55.0, "°C"),
                Read("gpu0", "VramUsedBytes", 2_147_483_648.0, "B"),
                Read("gpu0", "VramTotalBytes", 12_884_901_888.0, "B"),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal("AMD Radeon RX 6700 XT", payload.GetText(WidgetPayloadPresenter.GpuNameKey));
        Assert.Equal("AMD", payload.GetText(WidgetPayloadPresenter.GpuVendorKey));
        Assert.Equal("discrete", payload.GetText(WidgetPayloadPresenter.GpuIntegrationKey));
        Assert.Equal("12.0%", payload.GetText(WidgetPayloadPresenter.GpuKey));
        Assert.Equal("55.0°C", payload.GetText(WidgetPayloadPresenter.GpuTempKey));
    }

    [Fact]
    public void NvidiaOnly_NamesTheNvidiaCard()
    {
        var snapshot = Snapshot(
            cpus: new[] { Cpu("cpu0", "Intel Core i7-10875H", "Intel") },
            gpus: new[] { Gpu("gpu0", "NVIDIA GeForce RTX 2070 Super", "NVIDIA", GpuIntegration.Unknown, "pci-nv-0") },
            readings: new[]
            {
                Read("gpu0", "LoadPercent", 25.0),
                Read("gpu0", "TemperatureC", 61.0, "°C"),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal("NVIDIA GeForce RTX 2070 Super", payload.GetText(WidgetPayloadPresenter.GpuNameKey));
        Assert.Equal("NVIDIA", payload.GetText(WidgetPayloadPresenter.GpuVendorKey));

        // The integration class is unknown for an NVIDIA card, so no
        // classification is shown at all.
        Assert.Equal(string.Empty, payload.GetText(WidgetPayloadPresenter.GpuIntegrationKey));
        Assert.DoesNotContain("integrated", payload.GetText(WidgetPayloadPresenter.GpuLabelKey));
        Assert.DoesNotContain("discrete", payload.GetText(WidgetPayloadPresenter.GpuLabelKey));
    }

    [Fact]
    public void IntelOnly_ShowsTheIntegratedClassBecauseItIsKnown()
    {
        var snapshot = Snapshot(
            gpus: new[] { Gpu("gpu0", "Intel(R) UHD Graphics", "Intel", GpuIntegration.Integrated, "pci-intel-0") },
            readings: new[] { Read("gpu0", "LoadPercent", 7.0) });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal("Intel(R) UHD Graphics", payload.GetText(WidgetPayloadPresenter.GpuNameKey));
        Assert.Equal("integrated", payload.GetText(WidgetPayloadPresenter.GpuIntegrationKey));
        Assert.Contains("integrated", payload.GetText(WidgetPayloadPresenter.GpuLabelKey));
    }

    [Fact]
    public void Hybrid_PresentsTheDiscreteCardAndListsTheOtherOne()
    {
        var payload = WidgetPayloadPresenter.Present(HybridSnapshot());

        // The default policy prefers the discrete card.
        Assert.Equal("NVIDIA GeForce RTX 3080", payload.GetText(WidgetPayloadPresenter.GpuNameKey));
        Assert.Equal("discrete", payload.GetText(WidgetPayloadPresenter.GpuIntegrationKey));

        // The integrated card is described as an additional device.
        Assert.True(payload.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.Contains("Intel(R) UHD Graphics 630", payload.GetText(WidgetPayloadPresenter.ExtraKey(1)));
        Assert.Contains("integrated", payload.GetText(WidgetPayloadPresenter.ExtraKey(1)));
    }

    [Fact]
    public void NoGpu_ReportsNoDeviceAndNoValues()
    {
        var snapshot = Snapshot(cpus: new[] { Cpu("cpu0", "Intel Core i7", "Intel") });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.False(payload.GetFlag(WidgetPayloadPresenter.GpuDevicePresentKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuTempKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuVramKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuNameKey));
        Assert.Equal(0, payload.GetText(WidgetPayloadPresenter.GpuExtrasKey).Length);
    }

    [Fact]
    public void CpuNameAndVendor_AreShownWhenKnown()
    {
        var payload = WidgetPayloadPresenter.Present(HybridSnapshot());

        Assert.Equal("Intel Core i7-10875H", payload.GetText(WidgetPayloadPresenter.CpuNameKey));
        Assert.Equal("Intel", payload.GetText(WidgetPayloadPresenter.CpuVendorKey));
    }

    [Fact]
    public void CpuNameAndVendor_AreUnavailableWhenUnknown()
    {
        var snapshot = Snapshot(cpus: new[] { Cpu("cpu0", "Unknown Processor", null) });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.CpuVendorKey));
        Assert.Equal("Unknown Processor", payload.GetText(WidgetPayloadPresenter.CpuNameKey));
    }

    // =====================================================================
    // Long device names
    // =====================================================================

    [Fact]
    public void LongDeviceName_IsTruncatedSoItCannotOverflowTheLayout()
    {
        const string longName =
            "NVIDIA GeForce RTX 4080 SUPER Founders Edition Triple Fan Edition";

        var snapshot = Snapshot(
            gpus: new[] { Gpu("gpu0", longName, "NVIDIA", GpuIntegration.Discrete, "pci-0") },
            readings: new[] { Read("gpu0", "LoadPercent", 10.0) });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        var label = payload.GetText(WidgetPayloadPresenter.GpuLabelKey);
        Assert.True(label.Length <= 40, $"The label was {label.Length} characters: '{label}'.");
        Assert.Contains("…", label);

        var compact = payload.GetText(WidgetPayloadPresenter.GpuLabelCompactKey);
        Assert.True(compact.Length <= 24, $"The compact label was {compact.Length} characters: '{compact}'.");

        // The full name is still available for the tooltip-style fields.
        Assert.Equal(longName, payload.GetText(WidgetPayloadPresenter.GpuNameKey));
    }

    [Fact]
    public void LongDeviceName_AdditionalDeviceLineIsTruncated()
    {
        const string longName =
            "AMD Radeon RX 7900 XTX Liquid Extreme 24GB Reference Edition";

        var snapshot = Snapshot(
            gpus: new[]
            {
                Gpu("gpu0", "NVIDIA RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0"),
                Gpu("gpu1", longName, "AMD", GpuIntegration.Discrete, "pci-1"),
            },
            readings: new[] { Read("gpu1", "LoadPercent", 10.0) });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        var extra = payload.GetText(WidgetPayloadPresenter.ExtraKey(1));
        Assert.Contains("…", extra);
        Assert.True(extra.Length < 80, $"The extra line was {extra.Length} characters: '{extra}'.");
    }

    [Fact]
    public void LongDeviceName_KeepsItsClassificationWithinTheLimit()
    {
        // The finished label must fit the space it was given, even when
        // the classification is appended after the name is shortened.
        var device = Gpu(
            "gpu0",
            "NVIDIA GeForce RTX 4080 SUPER Founders Edition Triple Fan Edition",
            "NVIDIA",
            GpuIntegration.Discrete,
            "pci-0");

        var label = WidgetPayloadPresenter.ComposeDeviceLabel(device, maxLength: 40);

        Assert.True(label.Length <= 40, $"The label was {label.Length} characters: '{label}'.");
        Assert.Contains("discrete", label);
    }

    // =====================================================================
    // Missing sensors
    // =====================================================================

    [Fact]
    public void MissingTemperatureAndVram_AreUnavailableNeverZero()
    {
        var snapshot = Snapshot(
            gpus: new[] { Gpu("gpu0", "AMD Radeon RX 6700 XT", "AMD", GpuIntegration.Discrete, "pci-0") },
            readings: new[] { Read("gpu0", "LoadPercent", 12.0) });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal("12.0%", payload.GetText(WidgetPayloadPresenter.GpuKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuTempKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuVramKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuVramUsedKey));
    }

    [Fact]
    public void FailedReading_IsUnavailableAndExposesNoExceptionText()
    {
        var snapshot = Snapshot(
            gpus: new[] { Gpu("gpu0", "AMD Radeon RX 6700 XT", "AMD", GpuIntegration.Discrete, "pci-0") },
            readings: new[]
            {
                Read("gpu0", "LoadPercent", 12.0),
                Failed("gpu0", "TemperatureC"),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuTempKey));
        AssertNoRawFailureText(payload);
    }

    [Fact]
    public void StaleReading_IsMarkedStaleNotPresentedAsCurrent()
    {
        var stale = Measurement.Stale(SampleTime.AddMinutes(-5), "test");

        var snapshot = Snapshot(
            gpus: new[] { Gpu("gpu0", "AMD Radeon RX 6700 XT", "AMD", GpuIntegration.Discrete, "pci-0") },
            readings: new[]
            {
                new DeviceReading { DeviceId = "gpu0", Metric = "LoadPercent", Measurement = stale },
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal(WidgetPayloadPresenter.StaleMarker, payload.GetText(WidgetPayloadPresenter.GpuKey));
    }

    [Fact]
    public void NoRawExceptionTextReachesThePayload()
    {
        var snapshot = Snapshot(
            gpus: new[] { Gpu("gpu0", "AMD Radeon RX 6700 XT", "AMD", GpuIntegration.Discrete, "pci-0") },
            readings: new[] { Failed("gpu0", "LoadPercent"), Failed("gpu0", "TemperatureC") });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        AssertNoRawFailureText(payload);
    }

    // =====================================================================
    // Selection: disappearing, unavailable, identical names
    // =====================================================================

    [Fact]
    public void SelectedDevice_PresentsThatDevice()
    {
        var payload = WidgetPayloadPresenter.Present(HybridSnapshot(), "pci-intel-0");

        Assert.Equal("Intel(R) UHD Graphics 630", payload.GetText(WidgetPayloadPresenter.GpuNameKey));
        Assert.Equal("integrated", payload.GetText(WidgetPayloadPresenter.GpuIntegrationKey));
        Assert.False(payload.GetFlag(WidgetPayloadPresenter.GpuSelectionMissingKey));
    }

    [Fact]
    public void SelectedDevice_ThatDisappears_FallsBackToTheDefaultAndSaysSo()
    {
        // The Intel card was removed from the machine.
        var payload = WidgetPayloadPresenter.Present(HybridSnapshot(), "pci-intel-that-was-removed");

        // The default policy takes over, so the widget keeps working.
        Assert.Equal("NVIDIA GeForce RTX 3080", payload.GetText(WidgetPayloadPresenter.GpuNameKey));
        Assert.True(payload.GetFlag(WidgetPayloadPresenter.GpuSelectionMissingKey));
        Assert.Contains("unavailable", payload.GetText(WidgetPayloadPresenter.GpuSelectionNoticeKey));
    }

    [Fact]
    public void SelectedDevice_ThatBecomesUnavailable_ShowsNoValuesForIt()
    {
        var snapshot = Snapshot(
            gpus: new[] { Gpu("gpu0", "AMD Radeon RX 6700 XT", "AMD", GpuIntegration.Discrete, "pci-0") },
            readings: new[] { Failed("gpu0", "LoadPercent") });

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-0");

        Assert.True(payload.GetFlag(WidgetPayloadPresenter.GpuDevicePresentKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuKey));
    }

    [Fact]
    public void SelectedDevice_ThatDisappearsAndNoGpuRemains_ReportsNoDevice()
    {
        var snapshot = Snapshot(cpus: new[] { Cpu("cpu0", "Intel Core i7", "Intel") });

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-intel-that-was-removed");

        Assert.False(payload.GetFlag(WidgetPayloadPresenter.GpuDevicePresentKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuKey));
    }

    [Fact]
    public void TwoGpusWithIdenticalNames_AreDistinguishedByInstanceId()
    {
        var snapshot = IdenticalNameSnapshot();

        // The names are identical, so only the instance id can say which
        // card is meant.
        var first = WidgetPayloadPresenter.Present(snapshot, "pci-a");
        var second = WidgetPayloadPresenter.Present(snapshot, "pci-b");

        Assert.Equal("10.0%", first.GetText(WidgetPayloadPresenter.GpuKey));
        Assert.Equal("90.0%", second.GetText(WidgetPayloadPresenter.GpuKey));

        // Both cards are still offered as choices.
        var choices = WidgetPayloadPresenter.BuildGpuChoices(snapshot, "pci-b");
        Assert.Equal(2, choices.Count);
        Assert.Contains(choices, c => c.InstanceId == "pci-a");
        Assert.Contains(choices, c => c.InstanceId == "pci-b");
        Assert.Single(choices.Where(c => c.IsSelected));
        Assert.Equal("pci-b", choices.Single(c => c.IsSelected).InstanceId);
    }

    [Fact]
    public void SelectionIsNeverResolvedByDisplayName()
    {
        // Two cards share a name; the selection names one of them by
        // instance id. Matching by name would pick the first one.
        var payload = WidgetPayloadPresenter.Present(IdenticalNameSnapshot(), "pci-b");

        Assert.Equal("90.0%", payload.GetText(WidgetPayloadPresenter.GpuKey));
    }

    // =====================================================================
    // Provenance
    // =====================================================================

    [Fact]
    public void SampleTime_IsTheMostRecentSuccessfulReadingNotThePublishTime()
    {
        var readAt = SampleTime.AddSeconds(-40);

        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[] { Gpu("gpu0", "AMD Radeon RX 6700 XT", "AMD", GpuIntegration.Discrete, "pci-0") },
            readings: new[]
            {
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "LoadPercent",
                    Measurement = new Measurement
                    {
                        Value = 12.0,
                        Unit = "%",
                        Status = MeasurementStatus.Available,
                        LastSuccessfulUpdate = readAt,
                    },
                },
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal(
            readAt.ToString("h:mm:ss tt", System.Globalization.CultureInfo.CurrentCulture),
            payload.GetText(WidgetPayloadPresenter.SampleTimeKey));

        // The reading is older than the sample, so the widget says so.
        Assert.True(payload.GetFlag(WidgetPayloadPresenter.SampleIsStaleKey));
    }

    [Fact]
    public void SampleTime_IsNotStaleWhenTheReadingIsFromThisCollection()
    {
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[] { Gpu("gpu0", "AMD Radeon RX 6700 XT", "AMD", GpuIntegration.Discrete, "pci-0") },
            readings: new[]
            {
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "LoadPercent",
                    Measurement = new Measurement
                    {
                        Value = 12.0,
                        Unit = "%",
                        Status = MeasurementStatus.Available,
                        LastSuccessfulUpdate = SampleTime.AddSeconds(-2),
                    },
                },
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.False(payload.GetFlag(WidgetPayloadPresenter.SampleIsStaleKey));
    }

    [Fact]
    public void LastUpdatedIsTheSampleTimeNotThePublishTime()
    {
        var payload = WidgetPayloadPresenter.Present(HybridSnapshot());

        Assert.Equal(
            payload.GetText(WidgetPayloadPresenter.SampleTimeKey),
            payload.GetText(WidgetPayloadPresenter.LastUpdatedKey));
    }

    [Fact]
    public void SampleTime_IsUnavailableWhenNothingHasEverRead()
    {
        var payload = WidgetPayloadPresenter.Present(Snapshot());

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.SampleTimeKey));
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static MetricsSnapshot HybridSnapshot() =>
        Snapshot(
            cpus: new[] { Cpu("cpu0", "Intel Core i7-10875H", "Intel") },
            gpus: new[]
            {
                Gpu("gpu0", "Intel(R) UHD Graphics 630", "Intel", GpuIntegration.Integrated, "pci-intel-0"),
                Gpu("gpu1", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
            },
            readings: new[]
            {
                Read("cpu0", "LoadPercent", 19.44),
                Read("cpu0", "ClockMHz", 2600.0, "MHz"),
                Read("cpu0", "TemperatureC", 84.0, "°C"),
                Read("gpu0", "LoadPercent", 7.0),
                Read("gpu1", "LoadPercent", 30.0),
                Read("gpu1", "TemperatureC", 65.0, "°C"),
                Read("gpu1", "VramUsedBytes", 4_000_000_000.0, "B"),
                Read("gpu1", "VramTotalBytes", 8_000_000_000.0, "B"),
            });

    private static MetricsSnapshot IdenticalNameSnapshot() =>
        Snapshot(
            gpus: new[]
            {
                Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-a"),
                Gpu("gpu1", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-b"),
            },
            readings: new[]
            {
                Read("gpu0", "LoadPercent", 10.0),
                Read("gpu1", "LoadPercent", 90.0),
            });

    private static void AssertNoRawFailureText(WidgetPayload payload)
    {
        foreach (var pair in payload.Text)
        {
            Assert.DoesNotContain("Exception", pair.Value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" at ", pair.Value, StringComparison.Ordinal);
            Assert.DoesNotContain("test", pair.Value, StringComparison.OrdinalIgnoreCase);
        }
    }
}

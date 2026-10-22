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
        var template = WidgetTemplates.BuildTemplate(size);

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
        var template = WidgetTemplates.BuildTemplate(size);

        foreach (var name in TemplateBindings.ExtractPropertyNames(template))
        {
            Assert.True(
                payload.Contains(name),
                $"The {size} template references '{name}', which the empty payload does not produce.");
        }
    }

    /// <summary>
    /// Every size's template is valid JSON, and none of them carries a
    /// root-level action row.
    ///
    /// <para>
    /// The host renders that row as buttons after the card body, and the
    /// board does not scroll, so the row competes with the figures for the
    /// card's height and the overflow is clipped. Removing the row gives
    /// the body the whole card.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(WidgetLayoutSize.Small)]
    [InlineData(WidgetLayoutSize.Medium)]
    [InlineData(WidgetLayoutSize.Large)]
    public void Template_IsValidJsonAndCarriesNoActionRow(WidgetLayoutSize size)
    {
        var template = WidgetTemplates.BuildTemplate(size);

        using var document = JsonDocument.Parse(template);

        Assert.Equal("AdaptiveCard", document.RootElement.GetProperty("type").GetString());
        Assert.False(
            document.RootElement.TryGetProperty("actions", out _),
            $"The {size} template still carries an actions array.");
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
    // System-level collectors: a failed read is not a measured zero
    // =====================================================================

    [Fact]
    public void AFailedSystemCollector_IsUnavailableNotZero()
    {
        // A system-level collector that could not read its metric must not
        // present the model's initial value. Zero is a legitimate
        // measurement — a genuinely idle network, a genuinely empty disk —
        // and a failed read must not be indistinguishable from one.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            cpuLoadPercent: Measurement.Failed("Windows CPU counters"),
            ramUsedBytes: Measurement.Failed("GlobalMemoryStatusEx"),
            diskPercent: Measurement.Failed("DriveInfo"),
            networkDownloadBytesPerSec: Measurement.Failed("NetworkInterface"));

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.CpuKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.RamUsedKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.DiskPercentKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.NetDownKey));
    }

    [Fact]
    public void AMeasuredZeroSystemCollector_IsPresentedAsAValue()
    {
        // The other half of the rule: a collector that read a genuine
        // zero must show it, not "N/A".
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            cpuLoadPercent: Measurement.AvailableValue(0, "%", "Windows CPU counters"),
            ramUsedBytes: Measurement.AvailableValue(0, "bytes", "GlobalMemoryStatusEx"),
            networkDownloadBytesPerSec: Measurement.AvailableValue(0, "bytes/sec", "NetworkInterface"));

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal("0.0%", payload.GetText(WidgetPayloadPresenter.CpuKey));
        Assert.Equal("0 B", payload.GetText(WidgetPayloadPresenter.RamUsedKey));
        Assert.Equal("0 B/s", payload.GetText(WidgetPayloadPresenter.NetDownKey));
    }

    [Fact]
    public void AStaleSystemCollector_IsMarkedStale()
    {
        // A value read in an earlier cycle and not refreshed is kept, but
        // it is marked stale so the widget does not imply it is current.
        var stale = Measurement.Stale(SampleTime.AddMinutes(-5), "GlobalMemoryStatusEx");

        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            ramUsedBytes: stale,
            ramTotalBytes: Measurement.AvailableValue(2048, "bytes", "GlobalMemoryStatusEx"));

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal(WidgetPayloadPresenter.StaleMarker, payload.GetText(WidgetPayloadPresenter.RamUsedKey));
    }

    [Fact]
    public void APercentageThatWasNotComputed_IsUnavailableRatherThanZero()
    {
        // A capacity of zero produces no percentage: the ratio is
        // undefined. The collector therefore reports the percentage as
        // unavailable, and the widget must show it as unavailable too —
        // not as "0.0%", which is a number the machine never reported.
        //
        // The used and total figures are still real readings, so they are
        // presented; only the percentage is absent.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            ramUsedBytes: Measurement.AvailableValue(0, "bytes", "GlobalMemoryStatusEx"),
            ramTotalBytes: Measurement.AvailableValue(0, "bytes", "GlobalMemoryStatusEx"),
            ramPercent: Measurement.Failed("GlobalMemoryStatusEx"),
            diskUsedBytes: Measurement.AvailableValue(0, "bytes", "DriveInfo"),
            diskTotalBytes: Measurement.AvailableValue(0, "bytes", "DriveInfo"),
            diskPercent: Measurement.Failed("DriveInfo"));

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.RamPercentKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.DiskPercentKey));

        // The combined figure falls back to the used and total figures
        // without a percentage, rather than inventing one.
        Assert.DoesNotContain("%", payload.GetText(WidgetPayloadPresenter.RamKey));
        Assert.DoesNotContain("%", payload.GetText(WidgetPayloadPresenter.DiskKey));
    }

    [Fact]
    public void AMeasuredZeroPercentage_IsPresentedAsZero()
    {
        // The other half of the rule, and the one the fix must not break:
        // a genuinely empty disk reads zero percent, and zero is a
        // measurement. It must reach the widget as "0.0%".
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            ramUsedBytes: Measurement.AvailableValue(0, "bytes", "GlobalMemoryStatusEx"),
            ramTotalBytes: Measurement.AvailableValue(2048, "bytes", "GlobalMemoryStatusEx"),
            ramPercent: Measurement.AvailableValue(0.0, "%", "GlobalMemoryStatusEx"));

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal("0.0%", payload.GetText(WidgetPayloadPresenter.RamPercentKey));
    }

    // =====================================================================
    // The ACPI thermal-zone average
    //
    // It comes from an unidentified sensor, so it is published as a scalar
    // measurement rather than as a device reading. The lookup must read
    // the scalar field, not go looking for a device reading that the
    // collector never produces.
    // =====================================================================

    [Fact]
    public void TheAcpiThermalZoneAverage_IsPresentedWhenItWasRead()
    {
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            acpiThermalZoneTemperatureC: Measurement.AvailableValue(41.5, "°C", "MSAcpi_ThermalZoneTemperature"));

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal("41.5°C", payload.GetText(WidgetPayloadPresenter.AcpiThermalZoneTempKey));
    }

    [Fact]
    public void TheAcpiThermalZoneAverage_IsUnavailableWhenItWasNotRead()
    {
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            acpiThermalZoneTemperatureC: Measurement.Failed("MSAcpi_ThermalZoneTemperature"));

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.AcpiThermalZoneTempKey));
    }

    [Fact]
    public void TheAcpiThermalZoneAverage_IsNeverPresentedAsACpuOrGpuTemperature()
    {
        // The zone names no component, so its reading must not leak into
        // the CPU or GPU temperature keys.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            cpus: new[] { Cpu("cpu0", "Intel Core i7", "Intel") },
            gpus: new[] { Gpu("gpu0", "AMD Radeon RX 6700 XT", "AMD", GpuIntegration.Discrete, "pci-0") },
            readings: new[]
            {
                Read("gpu0", "TemperatureC", 55.0, "°C"),
            },
            cpuTemperatureC: Measurement.AvailableValue(84.0, "°C", "LibreHardwareMonitor"),
            gpuTemperatureC: Measurement.AvailableValue(55.0, "°C", "LibreHardwareMonitor"),
            gpuTemperatureDeviceId: "gpu0",
            acpiThermalZoneTemperatureC: Measurement.AvailableValue(41.5, "°C", "MSAcpi_ThermalZoneTemperature"));

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal("84.0°C", payload.GetText(WidgetPayloadPresenter.CpuTempKey));
        Assert.Equal("55.0°C", payload.GetText(WidgetPayloadPresenter.GpuTempKey));
        Assert.Equal("41.5°C", payload.GetText(WidgetPayloadPresenter.AcpiThermalZoneTempKey));
    }

    [Fact]
    public void AFailedSystemCollector_DoesNotSetTheSampleTime()
    {
        // The sample time is the most recent *successful* reading. A
        // failed read carries no successful-update time, so it must not
        // move the timestamp forward.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            cpuLoadPercent: Measurement.Failed("Windows CPU counters"),
            ramUsedBytes: Measurement.AvailableValue(1024, "bytes", "GlobalMemoryStatusEx"));

        var sample = WidgetPayloadPresenter.MostRecentSuccessfulSample(snapshot);

        Assert.NotNull(sample);
        Assert.True(sample > SampleTime);
    }

    [Fact]
    public void NoSuccessfulReadingAtAll_LeavesTheSampleTimeUnavailable()
    {
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            cpuLoadPercent: Measurement.Failed("Windows CPU counters"),
            ramUsedBytes: Measurement.Failed("GlobalMemoryStatusEx"));

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.SampleTimeKey));
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
    // Cross-device attribution
    //
    // The snapshot's scalar GPU figures describe whichever device the
    // collection chose as primary. They are a valid fallback for the
    // default selection, but they must never be presented under a device
    // the user explicitly chose, nor under a different card's name.
    // =====================================================================

    [Fact]
    public void AScalarFromAnotherDevice_IsNotPresentedForTheSelectedDevice()
    {
        // The user selected the integrated card. The snapshot's scalar
        // load describes the discrete card, so it must not appear under
        // the integrated card's name.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "Intel(R) UHD Graphics 630", "Intel", GpuIntegration.Integrated, "pci-intel-0"),
                Gpu("gpu1", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
            },
            gpuLoadPercent: Measurement.AvailableValue(30.0, "%", "test", SampleTime),
            gpuLoadDeviceId: "gpu1");

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-intel-0");

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuKey));
    }

    [Fact]
    public void AScalarFromAnotherDevice_IsPresentedForTheDeviceItDescribes()
    {
        // The same snapshot, with the discrete card selected. The scalar
        // describes that card, so it is presented.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "Intel(R) UHD Graphics 630", "Intel", GpuIntegration.Integrated, "pci-intel-0"),
                Gpu("gpu1", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
            },
            gpuLoadPercent: Measurement.AvailableValue(30.0, "%", "test", SampleTime),
            gpuLoadDeviceId: "gpu1");

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        Assert.Equal("30.0%", payload.GetText(WidgetPayloadPresenter.GpuKey));
    }

    [Fact]
    public void AnUnattributedScalar_IsNotPresentedForAnyDevice()
    {
        // A figure with no device id describes no particular card, so it
        // is never presented under any card's name.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
            },
            gpuLoadPercent: Measurement.AvailableValue(30.0, "%", "test", SampleTime),
            gpuLoadDeviceId: null);

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuKey));
    }

    [Fact]
    public void TheFallbacksUtilization_IsPresentedForTheDeviceItDescribes()
    {
        // The backend could not read the load, but the reconciled nvidia-smi
        // fallback did, and it describes the discrete card. The scalar
        // carries that card's id, so it is presented under that card — and
        // only under that card.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "Intel(R) UHD Graphics 630", "Intel", GpuIntegration.Integrated, "pci-intel-0"),
                Gpu("gpu1", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
            },
            gpuLoadPercent: Measurement.AvailableValue(40.0, "%", "nvidia-smi", SampleTime),
            gpuLoadDeviceId: "gpu1");

        var forTheFallbackDevice = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");
        var forTheOtherDevice = WidgetPayloadPresenter.Present(snapshot, "pci-intel-0");

        Assert.Equal("40.0%", forTheFallbackDevice.GetText(WidgetPayloadPresenter.GpuKey));
        Assert.Equal(
            WidgetPayloadPresenter.NotAvailable,
            forTheOtherDevice.GetText(WidgetPayloadPresenter.GpuKey));
    }

    [Fact]
    public void AMeasuredZeroLoad_IsPresentedAsZeroRatherThanAsUnavailable()
    {
        // An idle card reads zero utilization. That is a measurement, and it
        // must reach the widget as "0.0%" rather than as "N/A".
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu1", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
            },
            gpuLoadPercent: Measurement.AvailableValue(0.0, "%", "nvidia-smi", SampleTime),
            gpuLoadDeviceId: "gpu1");

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        Assert.Equal("0.0%", payload.GetText(WidgetPayloadPresenter.GpuKey));
    }

    [Fact]
    public void TheUsedAndTotalMemoryFiguresAreAttributedSeparately()
    {
        // The used figure came from the reconciled NVIDIA fallback for the
        // discrete card; the capacity came from the Windows counters, which
        // name no device. The capacity must not be presented as the
        // discrete card's, because it may describe a different card
        // entirely.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
            },
            gpuVramUsedBytes: Measurement.AvailableValue(4_000_000_000.0, "B", "test", SampleTime),
            gpuVramTotalBytes: Measurement.AvailableValue(8_000_000_000.0, "B", "test", SampleTime),
            gpuVramUsedDeviceId: "gpu0",
            gpuVramTotalDeviceId: null);

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        // The used figure is presented. The combined key shows the used
        // amount alone, without a capacity: an unattributed capacity must
        // not be paired with this card's usage, because the percentage
        // that pairing produces is a number no device reports.
        Assert.Equal("3.7 GB", payload.GetText(WidgetPayloadPresenter.GpuVramUsedKey));
        Assert.Equal("3.7 GB", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
    }

    [Fact]
    public void ADeviceLevelReading_WinsOverTheScalarForTheSameDevice()
    {
        // The device reported its own load. The scalar also describes that
        // device, but the device's own reading is the more direct one.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
            },
            readings: new[]
            {
                Read("gpu0", "LoadPercent", 42.0),
            },
            gpuLoadPercent: Measurement.AvailableValue(30.0, "%", "test", SampleTime),
            gpuLoadDeviceId: "gpu0");

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        Assert.Equal("42.0%", payload.GetText(WidgetPayloadPresenter.GpuKey));
    }

    [Fact]
    public void AFailedDeviceReading_IsNotReplacedByAnotherDevicesScalar()
    {
        // The device reported the metric but the read failed. The scalar
        // describes a different card, so it must not be substituted: the
        // failed reading stands, and the widget says the value is
        // unavailable rather than showing another card's figure.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
            },
            readings: new[]
            {
                Failed("gpu0", "LoadPercent"),
            },
            gpuLoadPercent: Measurement.AvailableValue(30.0, "%", "test", SampleTime),
            gpuLoadDeviceId: "gpu1");

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuKey));
    }

    // =====================================================================
    // Combined GPU memory
    //
    // A "used / total (percent)" figure is a claim that the two numbers
    // describe the same memory pool of the same device. These tests pin
    // the cases in which that claim does not hold, and what the widget
    // shows instead.
    // =====================================================================

    /// <summary>
    /// The provenance label of a reading from the device-identified
    /// backend, naming the sensor it came from. The sensor's name is the
    /// source's own declaration of which memory pool it measures.
    /// </summary>
    private static string BackendSensor(string sensorName) =>
        $"{LhmSnapshotMapper.SourceName}:{sensorName}";

    [Fact]
    public void CompatibleMemoryFigures_AreCombinedWithAPercentage()
    {
        // The card reported its own dedicated memory usage and its own
        // dedicated memory capacity. The two describe one pool of one
        // device, so the ratio is presented.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
            },
            readings: new[]
            {
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "VramUsedBytes",
                    Measurement = Measurement.AvailableValue(
                        4_000_000_000.0, "B", BackendSensor("GPU Memory Used"), SampleTime),
                },
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "VramTotalBytes",
                    Measurement = Measurement.AvailableValue(
                        8_000_000_000.0, "B", BackendSensor("GPU Memory Total"), SampleTime),
                },
            });

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        Assert.Equal("3.7 GB", payload.GetText(WidgetPayloadPresenter.GpuVramUsedKey));
        Assert.Equal("3.7 GB / 7.5 GB (50.0%)", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
    }

    [Fact]
    public void TheSameDeviceFromTwoSources_IsCombinedWhenBothNameTheSamePool()
    {
        // The used figure came from the vendor-neutral fallback and the
        // capacity from the device-identified backend. They are different
        // sources, but they name the same device and both describe
        // dedicated video memory, so the ratio is a fact about that card.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
            },
            readings: new[]
            {
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "VramTotalBytes",
                    Measurement = Measurement.AvailableValue(
                        8_000_000_000.0, "B", BackendSensor("GPU Memory Total"), SampleTime),
                },
            },
            gpuVramUsedBytes: Measurement.AvailableValue(
                4_000_000_000.0, "B", "nvidia-smi", SampleTime),
            gpuVramUsedDeviceId: "gpu0",
            gpuVramTotalBytes: Measurement.AvailableValue(
                8_000_000_000.0, "B", BackendSensor("GPU Memory Total"), SampleTime),
            gpuVramTotalDeviceId: "gpu0");

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        Assert.Equal("3.7 GB / 7.5 GB (50.0%)", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
    }

    [Fact]
    public void MemoryFiguresForDifferentDevices_AreNotCombined()
    {
        // One card's usage paired with another card's capacity. The
        // percentage would be a number no device reports, so the used
        // amount is shown on its own.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
                Gpu("gpu1", "Intel(R) UHD Graphics 630", "Intel", GpuIntegration.Integrated, "pci-intel-0"),
            },
            readings: new[]
            {
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "VramUsedBytes",
                    Measurement = Measurement.AvailableValue(
                        4_000_000_000.0, "B", BackendSensor("GPU Memory Used"), SampleTime),
                },
            },
            gpuVramTotalBytes: Measurement.AvailableValue(
                8_000_000_000.0, "B", BackendSensor("GPU Memory Total"), SampleTime),
            gpuVramTotalDeviceId: "gpu1");

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        Assert.Equal("3.7 GB", payload.GetText(WidgetPayloadPresenter.GpuVramUsedKey));
        Assert.Equal("3.7 GB", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
        Assert.DoesNotContain("%", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
    }

    [Fact]
    public void AnUnattributedCapacity_IsNotCombinedWithAnAttributedUsage()
    {
        // The capacity names no device, so it may be any card's capacity —
        // very often the integrated part's, whose size bears no relation
        // to the discrete card's.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
            },
            readings: new[]
            {
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "VramUsedBytes",
                    Measurement = Measurement.AvailableValue(
                        4_000_000_000.0, "B", BackendSensor("GPU Memory Used"), SampleTime),
                },
            },
            gpuVramTotalBytes: Measurement.AvailableValue(
                8_000_000_000.0, "B", BackendSensor("GPU Memory Total"), SampleTime),
            gpuVramTotalDeviceId: null);

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        Assert.Equal("3.7 GB", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
        Assert.DoesNotContain("%", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
    }

    [Fact]
    public void DedicatedAndSharedMemory_AreNotCombined()
    {
        // The card's dedicated VRAM on one side, shared system memory on
        // the other. Their ratio is a coincidence, not a usage percentage.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "Intel(R) UHD Graphics 630", "Intel", GpuIntegration.Integrated, "pci-intel-0"),
            },
            readings: new[]
            {
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "VramUsedBytes",
                    Measurement = Measurement.AvailableValue(
                        4_000_000_000.0, "B", BackendSensor("GPU Memory Used"), SampleTime),
                },
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "VramTotalBytes",
                    Measurement = Measurement.AvailableValue(
                        8_000_000_000.0, "B", BackendSensor("D3D Shared Memory Total"), SampleTime),
                },
            });

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-intel-0");

        Assert.Equal("3.7 GB", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
        Assert.DoesNotContain("%", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
    }

    [Fact]
    public void AZeroCapacity_IsNotCombined()
    {
        // A capacity of zero cannot produce a ratio at all.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
            },
            readings: new[]
            {
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "VramUsedBytes",
                    Measurement = Measurement.AvailableValue(
                        4_000_000_000.0, "B", BackendSensor("GPU Memory Used"), SampleTime),
                },
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "VramTotalBytes",
                    Measurement = Measurement.AvailableValue(
                        0.0, "B", BackendSensor("GPU Memory Total"), SampleTime),
                },
            });

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        Assert.Equal("3.7 GB", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
        Assert.DoesNotContain("%", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
    }

    [Fact]
    public void AMissingCapacity_IsNotCombined()
    {
        // The card reported no capacity sensor at all.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
            },
            readings: new[]
            {
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "VramUsedBytes",
                    Measurement = Measurement.AvailableValue(
                        4_000_000_000.0, "B", BackendSensor("GPU Memory Used"), SampleTime),
                },
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "VramTotalBytes",
                    Measurement = Measurement.Unsupported(BackendSensor("GPU Memory Total")),
                },
            });

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        Assert.Equal("3.7 GB", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
        Assert.DoesNotContain("%", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
    }

    [Fact]
    public void TwoFiguresFromTheSameFamily_AreCombined()
    {
        // The card's drivers name their memory sensors plainly, without
        // saying which pool they describe. The two figures share a family
        // and describe one device, so the ratio is a fact about the card
        // and is presented.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
            },
            readings: new[]
            {
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "VramUsedBytes",
                    Measurement = Measurement.AvailableValue(
                        4_000_000_000.0, "B", BackendSensor("Memory Used"), SampleTime),
                },
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "VramTotalBytes",
                    Measurement = Measurement.AvailableValue(
                        8_000_000_000.0, "B", BackendSensor("Memory Total"), SampleTime),
                },
            });

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        Assert.Equal("3.7 GB / 7.5 GB (50.0%)", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
    }

    [Fact]
    public void TwoFiguresFromDifferentFamilies_AreNotCombined()
    {
        // The card named one figure's pool and the other's family. They
        // are two different families, and nothing says they describe the
        // same pool, so the ratio is not presented.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-nv-0"),
            },
            readings: new[]
            {
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "VramUsedBytes",
                    Measurement = Measurement.AvailableValue(
                        4_000_000_000.0, "B", BackendSensor("GPU Memory Used"), SampleTime),
                },
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "VramTotalBytes",
                    Measurement = Measurement.AvailableValue(
                        8_000_000_000.0, "B", BackendSensor("Memory Total"), SampleTime),
                },
            });

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        Assert.Equal("3.7 GB", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
        Assert.DoesNotContain("%", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
    }

    [Fact]
    public void TheBackendFiguresForOneCard_AreCombinedIntoARatio()
    {
        // End to end through the real mapping, not a hand-built snapshot:
        // the backend's sensors are projected onto the domain model, and
        // the resulting figures are presented. The sensor's own name
        // survives the projection, which is what lets the presenter know
        // the two figures describe one pool of one card.
        var projection = LhmSnapshotMapper.Project(new[] { LhmFixtures.NvidiaGpu() });
        var snapshot = projection.ToSnapshot(SampleTime);

        var payload = WidgetPayloadPresenter.Present(
            snapshot,
            @"PCI\VEN_10DE&DEV_1E91&SUBSYS_14581458\4&7AADB68&0&0008");

        // 1233 MB used of 8192 MB total: 15.05%, shown to one decimal.
        Assert.Equal("1.2 GB / 8.0 GB (15.1%)", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
    }

    [Fact]
    public void AnIntegratedPartWithOnlySharedMemory_ShowsNoCombinedFigure()
    {
        // The integrated part exposes only "D3D Shared Memory" sensors.
        // Shared system memory is not VRAM, so it is never selected as a
        // memory metric, and the widget shows nothing rather than a
        // shared-memory figure dressed up as the card's VRAM.
        var projection = LhmSnapshotMapper.Project(new[] { LhmFixtures.IntelIntegratedGpu() });
        var snapshot = projection.ToSnapshot(SampleTime);

        var payload = WidgetPayloadPresenter.Present(
            snapshot,
            @"PCI\VEN_8086&DEV_9BC4&SUBSYS_1A2117AA\4&2E1B0C4&0&0008");

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuVramKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuVramUsedKey));
    }

    [Fact]
    public void TwoCardsWithTheSameName_AreNotCombinedWithEachOther()
    {
        // Two identical cards have identical display names. Compatibility
        // is decided on device ids, never on the name, so one card's usage
        // is never paired with the other's capacity.
        var snapshot = MetricsSnapshot.Create(
            SampleTime,
            gpus: new[]
            {
                Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-a"),
                Gpu("gpu1", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-b"),
            },
            readings: new[]
            {
                new DeviceReading
                {
                    DeviceId = "gpu0",
                    Metric = "VramUsedBytes",
                    Measurement = Measurement.AvailableValue(
                        4_000_000_000.0, "B", BackendSensor("GPU Memory Used"), SampleTime),
                },
            },
            gpuVramTotalBytes: Measurement.AvailableValue(
                8_000_000_000.0, "B", BackendSensor("GPU Memory Total"), SampleTime),
            gpuVramTotalDeviceId: "gpu1");

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-a");

        Assert.Equal("3.7 GB", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
        Assert.DoesNotContain("%", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
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

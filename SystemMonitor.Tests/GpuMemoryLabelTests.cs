using System;
using System.Collections.Generic;
using System.Linq;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the heading a GPU-memory figure is shown under.
///
/// <para>
/// A heading is a claim about the figure beneath it. "Dedicated VRAM"
/// claims the figure is the card's own video memory; "GPU memory" claims
/// only that it is the card's memory. These tests pin the rule that the
/// widget makes the claim its data supports and no more.
/// </para>
///
/// <para>
/// The rule that matters most is that <b>pair compatibility and
/// memory-pool classification are separate decisions</b>. Accepting a
/// "used / total" pairing says the two figures describe one pool. It does
/// not say which pool. So a generic "Memory Used" / "Memory Total" pair
/// may be presented as a ratio and is still headed "GPU memory", never
/// "Dedicated VRAM".
/// </para>
/// </summary>
public class GpuMemoryLabelTests
{
    private const string Device = "/gpu-nvidia/0";
    private const string OtherDevice = "/gpu-intel/1";

    private static readonly DateTimeOffset ReadAt =
        new DateTimeOffset(2026, 10, 21, 6, 0, 0, TimeSpan.Zero);

    // --- Provenance labels used by the real collection path ---------------

    private const string DedicatedUsed = "LibreHardwareMonitor:GPU Memory Used";
    private const string DedicatedTotal = "LibreHardwareMonitor:GPU Memory Total";
    private const string GenericUsed = "LibreHardwareMonitor:Memory Used";
    private const string GenericTotal = "LibreHardwareMonitor:Memory Total";
    private const string SharedUsed = "LibreHardwareMonitor:D3D Shared Memory Used";
    private const string SharedTotal = "LibreHardwareMonitor:D3D Shared Memory Total";

    private static Measurement Figure(double bytes, string source) =>
        Measurement.AvailableValue(bytes, "B", source, ReadAt);

    private static HardwareDevice Gpu(
        string deviceId,
        string name,
        string? instanceId,
        GpuIntegration integration = GpuIntegration.Unknown) =>
        new()
        {
            DeviceId = deviceId,
            DisplayName = name,
            Vendor = "NVIDIA",
            DeviceClass = DeviceClass.Gpu,
            GpuIntegration = integration,
            InstanceId = instanceId,
        };

    private static DeviceReading MemoryReading(
        string deviceId,
        string metric,
        double bytes,
        string source) =>
        new()
        {
            DeviceId = deviceId,
            Metric = metric,
            Measurement = Figure(bytes, source),
        };

    // =====================================================================
    // A. A confirmed dedicated pair is headed as dedicated memory
    // =====================================================================

    [Fact]
    public void AConfirmedDedicatedPair_IsHeadedAsDedicatedGpuMemory()
    {
        var display = GpuMemoryPresentation.Resolve(
            Figure(4_000_000_000.0, DedicatedUsed), Device,
            Figure(8_000_000_000.0, DedicatedTotal), Device,
            Device);

        Assert.Equal(GpuMemoryLabel.Dedicated, display.Label);
        Assert.Equal("Dedicated GPU memory", GpuMemoryPresentation.LabelText(display.Label));
        Assert.Contains("%", display.Display);
    }

    [Fact]
    public void AConfirmedDedicatedPair_IsHeadedAsDedicatedMemoryInThePayload()
    {
        var snapshot = MetricsSnapshot.Create(
            ReadAt,
            gpus: new[] { Gpu(Device, "NVIDIA GeForce RTX 3080", "pci-nv-0", GpuIntegration.Discrete) },
            readings: new[]
            {
                MemoryReading(Device, "VramUsedBytes", 4_000_000_000.0, DedicatedUsed),
                MemoryReading(Device, "VramTotalBytes", 8_000_000_000.0, DedicatedTotal),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        Assert.Equal("3.7 GB / 7.5 GB (50.0%)", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
        Assert.Equal(
            GpuMemoryPresentation.DedicatedLabel,
            payload.GetText(WidgetPayloadPresenter.GpuVramLabelKey));
        Assert.Equal(
            GpuMemoryPresentation.DedicatedLabelCompact,
            payload.GetText(WidgetPayloadPresenter.GpuVramLabelCompactKey));
    }

    // =====================================================================
    // B. A confirmed shared pair is headed as shared memory
    // =====================================================================

    [Fact]
    public void AConfirmedSharedFigure_IsHeadedAsSharedGpuMemory()
    {
        // Shared system memory is not VRAM. The compatibility policy never
        // pairs it with anything, so the shared figure is shown on its own —
        // and it is headed as shared memory, which is what it is.
        var display = GpuMemoryPresentation.Resolve(
            Figure(4_000_000_000.0, SharedUsed), Device,
            Figure(8_000_000_000.0, SharedTotal), Device,
            Device);

        Assert.Equal(GpuMemoryLabel.Shared, display.Label);
        Assert.Equal("Shared GPU memory", GpuMemoryPresentation.LabelText(display.Label));
        Assert.DoesNotContain("%", display.Display);
    }

    [Fact]
    public void AConfirmedSharedFigure_IsHeadedAsSharedMemoryInThePayload()
    {
        var snapshot = MetricsSnapshot.Create(
            ReadAt,
            gpus: new[] { Gpu(Device, "Intel(R) UHD Graphics 630", "pci-intel-0", GpuIntegration.Integrated) },
            readings: new[]
            {
                MemoryReading(Device, "VramUsedBytes", 4_000_000_000.0, SharedUsed),
                MemoryReading(Device, "VramTotalBytes", 8_000_000_000.0, SharedTotal),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-intel-0");

        Assert.Equal(
            GpuMemoryPresentation.SharedLabel,
            payload.GetText(WidgetPayloadPresenter.GpuVramLabelKey));
        Assert.Equal(
            GpuMemoryPresentation.SharedLabelCompact,
            payload.GetText(WidgetPayloadPresenter.GpuVramLabelCompactKey));
    }

    // =====================================================================
    // C. A compatible generic pair is permitted, and headed neutrally
    // =====================================================================

    [Fact]
    public void AGenericFamilyPair_IsCombinedButNeverHeadedAsDedicatedVram()
    {
        // The two figures share a family, so the ratio is a fact about the
        // card. But their name says nothing about which pool they describe,
        // so the heading must not claim dedicated VRAM.
        var display = GpuMemoryPresentation.Resolve(
            Figure(4_000_000_000.0, GenericUsed), Device,
            Figure(8_000_000_000.0, GenericTotal), Device,
            Device);

        // The pairing is permitted by the compatibility policy.
        Assert.Contains("%", display.Display);

        // And the heading claims only what the data established.
        Assert.Equal(GpuMemoryLabel.Unspecified, display.Label);
        Assert.Equal("GPU memory", GpuMemoryPresentation.LabelText(display.Label));
        Assert.DoesNotContain("VRAM", GpuMemoryPresentation.LabelText(display.Label));
    }

    [Fact]
    public void AGenericFamilyPair_IsHeadedNeutrallyInThePayload()
    {
        var snapshot = MetricsSnapshot.Create(
            ReadAt,
            gpus: new[] { Gpu(Device, "NVIDIA GeForce RTX 3080", "pci-nv-0", GpuIntegration.Discrete) },
            readings: new[]
            {
                MemoryReading(Device, "VramUsedBytes", 4_000_000_000.0, GenericUsed),
                MemoryReading(Device, "VramTotalBytes", 8_000_000_000.0, GenericTotal),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        Assert.Equal("3.7 GB / 7.5 GB (50.0%)", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
        Assert.Equal(
            GpuMemoryPresentation.UnspecifiedLabel,
            payload.GetText(WidgetPayloadPresenter.GpuVramLabelKey));

        AssertNoHeadingClaimsDedicatedVram(payload.GetText(WidgetPayloadPresenter.GpuVramLabelKey));
        AssertNoHeadingClaimsDedicatedVram(payload.GetText(WidgetPayloadPresenter.GpuVramLabelCompactKey));
    }

    // =====================================================================
    // D. A conflicting pair is rejected, and the used figure's own label stands
    // =====================================================================

    [Fact]
    public void AConflictingPair_IsRejectedAndTheUsedFiguresOwnLabelStands()
    {
        // Dedicated on one side, shared on the other, on the same device.
        // The pair is rejected, so the used figure is shown alone — and the
        // heading comes from that figure alone.
        var display = GpuMemoryPresentation.Resolve(
            Figure(4_000_000_000.0, DedicatedUsed), Device,
            Figure(8_000_000_000.0, SharedTotal), Device,
            Device);

        Assert.DoesNotContain("%", display.Display);
        Assert.Equal(GpuMemoryLabel.Dedicated, display.Label);
    }

    [Fact]
    public void AConflictingPair_DoesNotBorrowTheRejectedTotalFiguresSemantics()
    {
        // The mirror image: the used figure is the shared one, and the
        // rejected dedicated total contributes nothing to the heading.
        var display = GpuMemoryPresentation.Resolve(
            Figure(4_000_000_000.0, SharedUsed), Device,
            Figure(8_000_000_000.0, DedicatedTotal), Device,
            Device);

        Assert.DoesNotContain("%", display.Display);
        Assert.Equal(GpuMemoryLabel.Shared, display.Label);
    }

    [Fact]
    public void AConflictingPair_InThePayloadShowsTheUsedFiguresOwnLabel()
    {
        var snapshot = MetricsSnapshot.Create(
            ReadAt,
            gpus: new[] { Gpu(Device, "Intel(R) UHD Graphics 630", "pci-intel-0", GpuIntegration.Integrated) },
            readings: new[]
            {
                MemoryReading(Device, "VramUsedBytes", 4_000_000_000.0, DedicatedUsed),
                MemoryReading(Device, "VramTotalBytes", 8_000_000_000.0, SharedTotal),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-intel-0");

        Assert.Equal("3.7 GB", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
        Assert.DoesNotContain("%", payload.GetText(WidgetPayloadPresenter.GpuVramKey));
        Assert.Equal(
            GpuMemoryPresentation.DedicatedLabel,
            payload.GetText(WidgetPayloadPresenter.GpuVramLabelKey));
    }

    // =====================================================================
    // E. A used figure with unknown semantics gets the neutral label
    // =====================================================================

    [Fact]
    public void AUsedFigureWithUnknownSemantics_IsHeadedNeutrally()
    {
        // A figure whose provenance names no pool establishes no pool.
        var display = GpuMemoryPresentation.Resolve(
            Figure(4_000_000_000.0, GenericUsed), Device,
            null, null,
            Device);

        Assert.Equal(GpuMemoryLabel.Unspecified, display.Label);
        Assert.Equal("GPU memory", GpuMemoryPresentation.LabelText(display.Label));
    }

    [Fact]
    public void AFigureWithNoProvenanceAtAll_IsHeadedNeutrally()
    {
        var display = GpuMemoryPresentation.Resolve(
            Figure(4_000_000_000.0, "Windows GPU counters"), Device,
            null, null,
            Device);

        Assert.Equal(GpuMemoryLabel.Unspecified, display.Label);
    }

    // =====================================================================
    // F. Attribution protections are intact
    // =====================================================================

    [Fact]
    public void FiguresForDifferentDevices_AreNotCombined()
    {
        var display = GpuMemoryPresentation.Resolve(
            Figure(4_000_000_000.0, DedicatedUsed), Device,
            Figure(8_000_000_000.0, DedicatedTotal), OtherDevice,
            Device);

        Assert.DoesNotContain("%", display.Display);
    }

    [Fact]
    public void AnUnattributedTotal_IsNotCombinedWithAnAttributedUsed()
    {
        var display = GpuMemoryPresentation.Resolve(
            Figure(4_000_000_000.0, DedicatedUsed), Device,
            Figure(8_000_000_000.0, DedicatedTotal), null,
            Device);

        Assert.DoesNotContain("%", display.Display);
    }

    [Fact]
    public void AnotherDevicesDedicatedFigure_ContributesNothingToTheHeading()
    {
        // The presented card has no memory reading of its own. The other
        // card's dedicated figure must not lend the presented card a
        // dedicated-VRAM heading.
        var display = GpuMemoryPresentation.Resolve(
            null, null,
            Figure(8_000_000_000.0, DedicatedTotal), OtherDevice,
            Device);

        Assert.Equal(GpuMemoryPresentation.NotAvailable, display.Display);
        Assert.Equal(GpuMemoryLabel.Unspecified, display.Label);
    }

    [Fact]
    public void AnUnavailableFigure_ShowsNoFigureAndSubstitutesNoZero()
    {
        var display = GpuMemoryPresentation.Resolve(
            Measurement.Unsupported(DedicatedUsed), Device,
            Measurement.Failed(DedicatedTotal), Device,
            Device);

        Assert.Equal(GpuMemoryPresentation.NotAvailable, display.Display);
        Assert.DoesNotContain("0", display.Display);
    }

    [Fact]
    public void AnUnavailableFigure_OnADeviceWithNoEstablishedCategory_KeepsTheNeutralHeading()
    {
        // Nothing was read, and the device's own figures establish no pool,
        // so the heading claims no pool.
        var snapshot = MetricsSnapshot.Create(
            ReadAt,
            gpus: new[] { Gpu(Device, "Intel(R) Arc A770", "pci-arc-0") });

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-arc-0");

        Assert.Equal(
            WidgetPayloadPresenter.NotAvailable,
            payload.GetText(WidgetPayloadPresenter.GpuVramKey));
        Assert.Equal(
            GpuMemoryPresentation.UnspecifiedLabel,
            payload.GetText(WidgetPayloadPresenter.GpuVramLabelKey));
    }

    [Fact]
    public void AnUnavailableFigure_OnADeviceWhoseOwnFigureEstablishesTheCategory_KeepsIt()
    {
        // The used figure could not be read, but the card's own capacity
        // figure names dedicated video memory. That is a fact about the
        // card, so the heading may name it — and no figure is substituted.
        var snapshot = MetricsSnapshot.Create(
            ReadAt,
            gpus: new[] { Gpu(Device, "NVIDIA GeForce RTX 3080", "pci-nv-0", GpuIntegration.Discrete) },
            readings: new[]
            {
                new DeviceReading
                {
                    DeviceId = Device,
                    Metric = "VramTotalBytes",
                    Measurement = Figure(8_000_000_000.0, DedicatedTotal),
                },
            });

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        Assert.Equal(
            WidgetPayloadPresenter.NotAvailable,
            payload.GetText(WidgetPayloadPresenter.GpuVramKey));
        Assert.Equal(
            GpuMemoryPresentation.DedicatedLabel,
            payload.GetText(WidgetPayloadPresenter.GpuVramLabelKey));
    }

    // =====================================================================
    // G. Switching devices updates the label and the values together
    // =====================================================================

    [Fact]
    public void SwitchingDevices_UpdatesTheLabelAndTheValuesTogether()
    {
        // Two cards with different memory semantics: one with dedicated
        // video memory, one whose only memory figure is shared system
        // memory.
        var snapshot = MetricsSnapshot.Create(
            ReadAt,
            gpus: new[]
            {
                Gpu(Device, "NVIDIA GeForce RTX 3080", "pci-nv-0", GpuIntegration.Discrete),
                Gpu(OtherDevice, "Intel(R) UHD Graphics 630", "pci-intel-0", GpuIntegration.Integrated),
            },
            readings: new[]
            {
                MemoryReading(Device, "VramUsedBytes", 4_000_000_000.0, DedicatedUsed),
                MemoryReading(Device, "VramTotalBytes", 8_000_000_000.0, DedicatedTotal),
                MemoryReading(OtherDevice, "VramUsedBytes", 1_000_000_000.0, SharedUsed),
            });

        var dedicated = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");
        var shared = WidgetPayloadPresenter.Present(snapshot, "pci-intel-0");

        Assert.Equal(
            GpuMemoryPresentation.DedicatedLabel,
            dedicated.GetText(WidgetPayloadPresenter.GpuVramLabelKey));
        Assert.Equal(
            GpuMemoryPresentation.SharedLabel,
            shared.GetText(WidgetPayloadPresenter.GpuVramLabelKey));

        // The values changed with the label; no label was retained from the
        // previously selected device.
        Assert.NotEqual(
            dedicated.GetText(WidgetPayloadPresenter.GpuVramKey),
            shared.GetText(WidgetPayloadPresenter.GpuVramKey));
    }

    [Fact]
    public void SwitchingFromADedicatedCardToAGenericOne_LeavesNoDedicatedHeadingBehind()
    {
        // The card that was showing dedicated VRAM is gone; the card now
        // shown reports a generic memory pair. The heading must follow the
        // data, not the previous selection.
        var snapshot = MetricsSnapshot.Create(
            ReadAt,
            gpus: new[]
            {
                Gpu(OtherDevice, "Generic Reporting Card", "pci-generic-0", GpuIntegration.Discrete),
            },
            readings: new[]
            {
                MemoryReading(OtherDevice, "VramUsedBytes", 4_000_000_000.0, GenericUsed),
                MemoryReading(OtherDevice, "VramTotalBytes", 8_000_000_000.0, GenericTotal),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-generic-0");

        Assert.Equal(
            GpuMemoryPresentation.UnspecifiedLabel,
            payload.GetText(WidgetPayloadPresenter.GpuVramLabelKey));
        AssertNoHeadingClaimsDedicatedVram(payload.GetText(WidgetPayloadPresenter.GpuVramLabelKey));
    }

    // =====================================================================
    // H. Every production size binds the label, and none contradicts it
    // =====================================================================

    [Theory]
    [InlineData(WidgetLayoutSize.Medium, "gpuVramLabel")]
    [InlineData(WidgetLayoutSize.Large, "gpuVramLabel")]
    public void EveryProductionSize_BindsAMemoryLabel(WidgetLayoutSize size, string expectedKey)
    {
        // The roomier layouts bind the heading from the payload, so the
        // heading on screen is the one the payload resolved for the device
        // being presented.
        //
        // The small layout binds no heading: it carries the GPU figures
        // without sub-headings, because the 148-pixel card has no room for
        // them. It shows the used-memory figure alone, which claims
        // nothing about the memory pool, so it needs no heading to keep
        // the claim honest.
        var template = WidgetTemplates.BuildTemplate(size);

        var referenced = TemplateBindings.ExtractPropertyNames(template);

        Assert.Contains(expectedKey, referenced);
    }

    [Fact]
    public void TheSmallLayoutBindsNoMemoryHeading()
    {
        // The small card shows the GPU figures without sub-headings, so it
        // binds no memory label at all — neither the full form nor the
        // compact one.
        var template = WidgetTemplates.BuildTemplate(WidgetLayoutSize.Small);

        var referenced = TemplateBindings.ExtractPropertyNames(template);

        Assert.DoesNotContain(WidgetPayloadPresenter.GpuVramLabelKey, referenced);
        Assert.DoesNotContain(WidgetPayloadPresenter.GpuVramLabelCompactKey, referenced);
    }

    [Theory]
    [InlineData(WidgetLayoutSize.Small)]
    [InlineData(WidgetLayoutSize.Medium)]
    [InlineData(WidgetLayoutSize.Large)]
    public void EveryProductionSize_CarriesNoStaticVramHeading(WidgetLayoutSize size)
    {
        // No production template may still say "VRAM" or "Dedicated VRAM"
        // in a static heading: those are claims about a memory pool that the
        // template cannot know. The heading is the payload's to make.
        var template = WidgetTemplates.BuildTemplate(size);

        var staticTexts = StaticTemplateTexts(template);

        Assert.DoesNotContain(staticTexts, t => t.Contains("VRAM", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(WidgetLayoutSize.Medium, "gpuVramLabel", "Dedicated GPU memory")]
    [InlineData(WidgetLayoutSize.Large, "gpuVramLabel", "Dedicated GPU memory")]
    public void EveryProductionSize_RendersTheLabelFromThePayload(
        WidgetLayoutSize size,
        string labelKey,
        string expectedHeading)
    {
        // The heading that appears on screen is the one the payload
        // resolved for the device being presented.
        var snapshot = MetricsSnapshot.Create(
            ReadAt,
            gpus: new[] { Gpu(Device, "NVIDIA GeForce RTX 3080", "pci-nv-0", GpuIntegration.Discrete) },
            readings: new[]
            {
                MemoryReading(Device, "VramUsedBytes", 4_000_000_000.0, DedicatedUsed),
                MemoryReading(Device, "VramTotalBytes", 8_000_000_000.0, DedicatedTotal),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot, "pci-nv-0");

        // The size's own template binds that key, so the heading on screen
        // is the one the payload resolved.
        var template = WidgetTemplates.BuildTemplate(size);

        Assert.Contains(labelKey, TemplateBindings.ExtractPropertyNames(template));
        Assert.Equal(expectedHeading, payload.GetText(labelKey));
    }

    // =====================================================================
    // The label text itself
    // =====================================================================

    [Fact]
    public void TheCompactLabelNeverClaimsADifferentPoolThanTheFullOne()
    {
        Assert.Equal(
            GpuMemoryPresentation.UnspecifiedLabel,
            GpuMemoryPresentation.CompactLabelText(GpuMemoryLabel.Unspecified));

        // The compact form drops the word "GPU" — the figure is already in
        // the GPU section — but never the claim about the pool.
        Assert.Contains("Dedicated", GpuMemoryPresentation.CompactLabelText(GpuMemoryLabel.Dedicated));
        Assert.Contains("Shared", GpuMemoryPresentation.CompactLabelText(GpuMemoryLabel.Shared));

        AssertNoHeadingClaimsDedicatedVram(
            GpuMemoryPresentation.CompactLabelText(GpuMemoryLabel.Unspecified));
    }

    [Fact]
    public void TheSharedLabelDoesNotSayVram()
    {
        // Shared system memory is not VRAM, so the word may not appear in
        // its heading.
        Assert.DoesNotContain(
            "VRAM",
            GpuMemoryPresentation.SharedLabel,
            StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain(
            "VRAM",
            GpuMemoryPresentation.SharedLabelCompact,
            StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    /// <summary>
    /// The static texts a template contains — the ones that are not a
    /// payload binding.
    /// </summary>
    private static IReadOnlyList<string> StaticTemplateTexts(string template)
    {
        var texts = new List<string>();

        foreach (var line in template.Split('\n'))
        {
            var trimmed = line.Trim();

            var marker = "\"text\":";
            var start = trimmed.IndexOf(marker, StringComparison.Ordinal);
            if (start < 0)
                continue;

            var value = trimmed.Substring(start + marker.Length).TrimStart();

            if (!value.StartsWith("\"", StringComparison.Ordinal))
                continue;

            var end = value.IndexOf('"', 1);
            if (end < 0)
                continue;

            var text = value.Substring(1, end - 1);

            // A binding is not a static text: the payload supplies it.
            if (text.Contains("${", StringComparison.Ordinal))
                continue;

            texts.Add(text);
        }

        return texts;
    }

    private static void AssertNoHeadingClaimsDedicatedVram(string heading) =>
        Assert.DoesNotContain("VRAM", heading, StringComparison.OrdinalIgnoreCase);
}

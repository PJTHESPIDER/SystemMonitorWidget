using System;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for hardware name normalization and compact payload fields.
/// </summary>
public sealed class WidgetPayloadPresenterNormalizationTests
{
    private static readonly DateTimeOffset SampleTime =
        new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static HardwareDevice Cpu(string deviceId, string name, string? vendor = null) =>
        new()
        {
            DeviceId = deviceId,
            DisplayName = name,
            Vendor = vendor,
            DeviceClass = DeviceClass.Cpu,
        };

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

    private static DeviceReading Read(string deviceId, string metric, double? value, string? unit = "B", string? source = null) =>
        new()
        {
            DeviceId = deviceId,
            Metric = metric,
            Measurement = value.HasValue
                ? Measurement.AvailableValue(value.Value, unit, source ?? "test")
                : Measurement.Unsupported("test"),
        };

    private static MetricsSnapshot Snapshot(
        IEnumerable<HardwareDevice>? cpus = null,
        IEnumerable<HardwareDevice>? gpus = null,
        IEnumerable<DeviceReading>? readings = null,
        DateTimeOffset? timestamp = null,
        Measurement? ramUsedBytes = null,
        Measurement? ramTotalBytes = null,
        Measurement? diskUsedBytes = null,
        Measurement? diskTotalBytes = null) =>
        MetricsSnapshot.Create(
            timestamp ?? SampleTime,
            cpus: cpus,
            gpus: gpus,
            readings: readings,
            ramUsedBytes: ramUsedBytes,
            ramTotalBytes: ramTotalBytes,
            diskUsedBytes: diskUsedBytes,
            diskTotalBytes: diskTotalBytes);

    // =====================================================================
    // Hardware name normalization
    // =====================================================================

    [Theory]
    [InlineData("AMD Ryzen 7 7800X3D 8-Core Processor", "Ryzen 7 7800X3D 8-Core")]
    [InlineData("AMD Ryzen 9 5950X", "Ryzen 9 5950X")]
    [InlineData("Intel Core i7-10875H", "Core i7-10875H")]
    [InlineData("Intel Core i9-13900K", "Core i9-13900K")]
    [InlineData("Intel Core i5-12400F", "Core i5-12400F")]
    [InlineData("NVIDIA GeForce RTX 3080", "GeForce RTX 3080")]
    [InlineData("NVIDIA GeForce RTX 4090", "GeForce RTX 4090")]
    [InlineData("Nvidia GeForce RTX 3080", "GeForce RTX 3080")]
    [InlineData("AMD Radeon RX 6700 XT", "Radeon RX 6700 XT")]
    [InlineData("Intel(R) UHD Graphics 630", "UHD Graphics 630")]
    [InlineData("Intel(R) Iris Xe Graphics", "Iris Xe")]
    [InlineData("Intel(R) Core(TM) i7-10875H CPU @ 2.30GHz", "Core i7-10875H @ 2.30GHz")]
    [InlineData("NVIDIA GeForce RTX 4070 Laptop GPU", "GeForce RTX 4070 Laptop")]
    public void NormalizeHardwareName_RemovesManufacturerPrefixAndTrailingSuffix(string input, string expected)
    {
        var result = WidgetPayloadPresenter.NormalizeHardwareName(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("AMD Ryzen 7 7800X3D Processor", "Ryzen 7 7800X3D")]
    [InlineData("Intel Core i7 CPU", "Core i7")]
    [InlineData("NVIDIA GPU", "")]  // Falls back to "NVIDIA GPU"
    [InlineData("AMD APU", "")]    // Falls back to "AMD APU"
    [InlineData("Intel Graphics", "")]  // Falls back to "Intel Graphics"
    [InlineData("Intel Core i7-10875H", "Core i7-10875H")]
    [InlineData("Intel(R) Core(TM) i7-10875H CPU @ 2.30GHz", "Core i7-10875H @ 2.30GHz")]
    [InlineData("NVIDIA GeForce RTX 4070 Laptop GPU", "GeForce RTX 4070 Laptop")]
    [InlineData("Very Long Unfamiliar Device Name With Many Words That Exceeds Normal Length", "Very Long Unfamiliar Device Name With Many Words That Exceeds Normal Length")]
    public void NormalizeHardwareName_RemovesTrailingSuffixes(string input, string expected)
    {
        var result = WidgetPayloadPresenter.NormalizeHardwareName(input);
        // When normalization produces empty, it falls back to original trimmed
        if (string.IsNullOrEmpty(expected))
        {
            Assert.Equal(input.Trim(), result);
        }
        else
        {
            Assert.Equal(expected, result);
        }
    }

    [Theory]
    [InlineData(null, WidgetPayloadPresenter.NotAvailable)]
    [InlineData("", WidgetPayloadPresenter.NotAvailable)]
    [InlineData("   ", WidgetPayloadPresenter.NotAvailable)]
    public void NormalizeHardwareName_NullOrEmpty_ReturnsNotAvailable(string? input, string expected)
    {
        Assert.Equal(expected, WidgetPayloadPresenter.NormalizeHardwareName(input));
    }

    [Fact]
    public void NormalizeHardwareName_UnknownNames_ConservativeCleanup()
    {
        // Unknown manufacturer should get conservative cleanup (trademarks, trailing suffixes only)
        var result = WidgetPayloadPresenter.NormalizeHardwareName("UnknownBrand SuperChip 9000 Processor");
        Assert.Equal("UnknownBrand SuperChip 9000", result);

        // Empty manufacturer with just model
        result = WidgetPayloadPresenter.NormalizeHardwareName("SuperChip 9000 CPU");
        Assert.Equal("SuperChip 9000", result);
    }

    [Fact]
    public void NormalizeHardwareName_NormalizesWhitespace()
    {
        var input = "  AMD    Ryzen    7    7800X3D  ";
        var result = WidgetPayloadPresenter.NormalizeHardwareName(input);
        Assert.Equal("Ryzen 7 7800X3D", result);
    }

    [Fact]
    public void NormalizeHardwareName_FallbackWhenEmpty()
    {
        // "GPU" alone normalizes to empty, so falls back to original
        var result = WidgetPayloadPresenter.NormalizeHardwareName("GPU");
        Assert.Equal("GPU", result);

        // "CPU" alone normalizes to empty, so falls back to original
        result = WidgetPayloadPresenter.NormalizeHardwareName("CPU");
        Assert.Equal("CPU", result);
    }

    // =====================================================================
    // Compact capacity fields in payload
    // =====================================================================

    [Fact]
    public void Present_IncludesRamCapacityCompact()
    {
        var snapshot = Snapshot(
            cpus: new[] { Cpu("cpu0", "Intel Core i7-10875H", "Intel") },
            ramUsedBytes: Measurement.AvailableValue(8_589_934_592.0, "B", "test"),
            ramTotalBytes: Measurement.AvailableValue(17_179_869_184.0, "B", "test"));

        var payload = WidgetPayloadPresenter.Present(snapshot);

        // total = 16 GB (>= 10, < 100): 1 decimal, trimmed
        Assert.Equal("8 / 16 GB", payload.GetText(WidgetPayloadPresenter.RamCapacityCompactKey));
    }

    [Fact]
    public void Present_IncludesDiskCapacityCompact()
    {
        var snapshot = Snapshot(
            diskUsedBytes: Measurement.AvailableValue(100_000_000_000.0, "B", "test"),
            diskTotalBytes: Measurement.AvailableValue(500_000_000_000.0, "B", "test"));

        var payload = WidgetPayloadPresenter.Present(snapshot);

        // 100B / 500B = 93.13 GB / 465.66 GB
        // total = 465.66 GB (>= 100): whole units
        // used = 93.13 -> 93
        // total = 465.66 -> 466
        Assert.Equal("93 / 466 GB", payload.GetText(WidgetPayloadPresenter.DiskCapacityCompactKey));
    }

    [Fact]
    public void Present_IncludesGpuMemoryCapacityCompact()
    {
        var gpu = Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0");
        var snapshot = Snapshot(
            gpus: new[] { gpu },
            readings: new[]
            {
                Read("gpu0", "VramUsedBytes", 4_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Used"),
                Read("gpu0", "VramTotalBytes", 10_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Total"),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        // 4GB / 10GB = 3.725 GB / 9.313 GB
        // total = 9.313 GB (< 10): 2 decimals -> "9.31"
        // used = 3.725 -> "3.73"
        Assert.Equal("3.73 / 9.31 GB", payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
    }

    [Fact]
    public void Present_RamCapacityCompact_NotAvailableWhenMissing()
    {
        var snapshot = Snapshot(
            ramUsedBytes: Measurement.Failed("test"),
            ramTotalBytes: Measurement.Failed("test"));

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.RamCapacityCompactKey));
    }

    [Fact]
    public void Present_DiskCapacityCompact_NotAvailableWhenMissing()
    {
        var snapshot = Snapshot(
            diskUsedBytes: Measurement.Unsupported("test"),
            diskTotalBytes: Measurement.Unsupported("test"));

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.DiskCapacityCompactKey));
    }

    [Fact]
    public void Present_GpuMemoryCapacityCompact_NotAvailableWhenMissing()
    {
        var gpu = Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0");
        var snapshot = Snapshot(
            gpus: new[] { gpu },
            readings: new[]
            {
                Read("gpu0", "LoadPercent", 10.0),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
    }

    [Fact]
    public void Present_CpuNameCompact_NormalizesName()
    {
        var cpu = Cpu("cpu0", "AMD Ryzen 7 7800X3D 8-Core Processor", "AMD");
        var snapshot = Snapshot(cpus: new[] { cpu });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        // "AMD " removed -> "Ryzen 7 7800X3D 8-Core Processor" -> " Processor" removed -> "Ryzen 7 7800X3D 8-Core"
        Assert.Equal("Ryzen 7 7800X3D 8-Core", payload.GetText(WidgetPayloadPresenter.CpuNameCompactKey));
        // Full name still available
        Assert.Equal("AMD Ryzen 7 7800X3D 8-Core Processor", payload.GetText(WidgetPayloadPresenter.CpuNameKey));
    }

    [Fact]
    public void Present_GpuNameCompact_NormalizesName()
    {
        var gpu = Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0");
        var snapshot = Snapshot(gpus: new[] { gpu });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal("GeForce RTX 3080", payload.GetText(WidgetPayloadPresenter.GpuNameCompactKey));
        // Full name still available
        Assert.Equal("NVIDIA GeForce RTX 3080", payload.GetText(WidgetPayloadPresenter.GpuNameKey));
    }

    [Fact]
    public void Present_EmptyPayload_IncludesCompactKeysAsNotAvailable()
    {
        var payload = WidgetPayloadPresenter.Present(null);

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.RamCapacityCompactKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.DiskCapacityCompactKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.CpuNameCompactKey));
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuNameCompactKey));
    }

    // =====================================================================
    // Partial availability for GPU memory compact display
    // =====================================================================

    [Fact]
    public void Present_GpuMemoryCapacityCompact_UsedAvailableTotalUnavailable_ReturnsUsedOnlyCompact()
    {
        // When total is unavailable but used is available, the compact display
        // shows the used figure alone (no slash, no total)
        var gpu = Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0");
        var snapshot = Snapshot(
            gpus: new[] { gpu },
            readings: new[]
            {
                Read("gpu0", "VramUsedBytes", 4_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Used"),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        // Used alone: 4GB = 3.725 GB -> "3.73 GB" (total < 10 so 2 decimals)
        // But since there's no total, the compact display should just show the used amount
        Assert.NotEqual(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
        Assert.DoesNotContain("/", payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
        Assert.Contains("GB", payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
    }

    [Fact]
    public void Present_GpuMemoryCapacityCompact_TotalAvailableUsedUnavailable_ReturnsNotAvailable()
    {
        // When used is unavailable but total is available, we cannot show
        // a meaningful compact display because we don't know what the used
        // figure is. The compact display requires at least the used figure.
        var gpu = Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0");
        var snapshot = Snapshot(
            gpus: new[] { gpu },
            readings: new[]
            {
                Read("gpu0", "VramTotalBytes", 10_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Total"),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        // No used figure means we cannot show a compact capacity
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
    }

    [Fact]
    public void Present_GpuMemoryCapacityCompact_BothUnavailable_ReturnsNotAvailable()
    {
        var gpu = Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0");
        var snapshot = Snapshot(
            gpus: new[] { gpu },
            readings: new[]
            {
                Read("gpu0", "LoadPercent", 10.0),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
    }

    // =====================================================================
    // GPU compatibility governing gpuMemoryCapacityCompact
    // =====================================================================

    [Fact]
    public void Present_GpuMemoryCapacityCompact_DifferentDevices_ReturnsNotAvailable()
    {
        // Used from one device, total from another: not compatible
        var gpu = Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0");
        var snapshot = Snapshot(
            gpus: new[] { gpu },
            readings: new[]
            {
                Read("gpu0", "VramUsedBytes", 4_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Used"),
                // Different device ID for total
                new DeviceReading
                {
                    DeviceId = "gpu1",
                    Metric = "VramTotalBytes",
                    Measurement = Measurement.AvailableValue(10_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Total", SampleTime),
                },
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        // Different devices -> not compatible -> used only (no slash)
        Assert.NotEqual(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
        Assert.DoesNotContain("/", payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
    }

    [Fact]
    public void Present_GpuMemoryCapacityCompact_DedicatedUsedSharedTotal_ReturnsUsedOnly()
    {
        // Dedicated used + shared total -> conflicting pools -> used only
        var gpu = Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0");
        var snapshot = Snapshot(
            gpus: new[] { gpu },
            readings: new[]
            {
                Read("gpu0", "VramUsedBytes", 4_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Used"),
                Read("gpu0", "VramTotalBytes", 10_000_000_000.0, "B", "LibreHardwareMonitor:D3D Shared Memory Total"),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        // Conflicting families -> used only
        Assert.NotEqual(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
        Assert.DoesNotContain("/", payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
    }

    [Fact]
    public void Present_GpuMemoryCapacityCompact_SharedUsedDedicatedTotal_ReturnsUsedOnly()
    {
        // Shared used + dedicated total -> conflicting pools -> used only
        var gpu = Gpu("gpu0", "Intel(R) UHD Graphics 630", "Intel", GpuIntegration.Integrated, "pci-0");
        var snapshot = Snapshot(
            gpus: new[] { gpu },
            readings: new[]
            {
                Read("gpu0", "VramUsedBytes", 1_000_000_000.0, "B", "LibreHardwareMonitor:D3D Shared Memory Used"),
                Read("gpu0", "VramTotalBytes", 8_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Total"),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        // Conflicting families -> used only
        Assert.NotEqual(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
        Assert.DoesNotContain("/", payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
    }

    [Fact]
    public void Present_GpuMemoryCapacityCompact_DedicatedUsedGenericTotal_ReturnsUsedOnly()
    {
        // Dedicated used + generic total -> different families -> used only
        var gpu = Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0");
        var snapshot = Snapshot(
            gpus: new[] { gpu },
            readings: new[]
            {
                Read("gpu0", "VramUsedBytes", 4_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Used"),
                Read("gpu0", "VramTotalBytes", 10_000_000_000.0, "B", "LibreHardwareMonitor:Memory Total"),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        // Different families -> used only
        Assert.NotEqual(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
        Assert.DoesNotContain("/", payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
    }

    [Fact]
    public void Present_GpuMemoryCapacityCompact_GenericUsedDedicatedTotal_ReturnsUsedOnly()
    {
        // Generic used + dedicated total -> different families -> used only
        var gpu = Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0");
        var snapshot = Snapshot(
            gpus: new[] { gpu },
            readings: new[]
            {
                Read("gpu0", "VramUsedBytes", 4_000_000_000.0, "B", "LibreHardwareMonitor:Memory Used"),
                Read("gpu0", "VramTotalBytes", 10_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Total"),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        // Different families -> used only
        Assert.NotEqual(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
        Assert.DoesNotContain("/", payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
    }

    [Fact]
    public void Present_GpuMemoryCapacityCompact_AttributedUsedUnattributedTotal_ReturnsUsedOnly()
    {
        // Used has device ID, total does not -> unattributed total can't be combined
        var gpu = Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0");
        var snapshot = Snapshot(
            gpus: new[] { gpu },
            readings: new[]
            {
                Read("gpu0", "VramUsedBytes", 4_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Used"),
                // Total without device ID (empty string)
                new DeviceReading
                {
                    DeviceId = string.Empty,
                    Metric = "VramTotalBytes",
                    Measurement = Measurement.AvailableValue(10_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Total", SampleTime),
                },
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        // Unattributed total -> used only
        Assert.NotEqual(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
        Assert.DoesNotContain("/", payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
    }

    [Fact]
    public void Present_GpuMemoryCapacityCompact_UnattributedUsedAttributedTotal_ReturnsNotAvailable()
    {
        // Used has no device ID, total does -> can't show meaningful compact
        var gpu = Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0");
        var snapshot = Snapshot(
            gpus: new[] { gpu },
            readings: new[]
            {
                // Used without device ID (empty string)
                new DeviceReading
                {
                    DeviceId = string.Empty,
                    Metric = "VramUsedBytes",
                    Measurement = Measurement.AvailableValue(4_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Used", SampleTime),
                },
                Read("gpu0", "VramTotalBytes", 10_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Total"),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        // Unattributed used -> N/A (can't show used without knowing it's for the presented device)
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
    }

    [Fact]
    public void Present_GpuMemoryCapacityCompact_ValidGenericFamilyPair_ReturnsCompactCombined()
    {
        // Both from same generic family -> compatible -> combined compact
        var gpu = Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0");
        var snapshot = Snapshot(
            gpus: new[] { gpu },
            readings: new[]
            {
                Read("gpu0", "VramUsedBytes", 4_000_000_000.0, "B", "LibreHardwareMonitor:Memory Used"),
                Read("gpu0", "VramTotalBytes", 10_000_000_000.0, "B", "LibreHardwareMonitor:Memory Total"),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        // Same generic family -> combined
        Assert.Equal("3.73 / 9.31 GB", payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
    }

    [Fact]
    public void Present_GpuMemoryCapacityCompact_ValidDedicatedPair_ReturnsCompactCombined()
    {
        // Both from dedicated family -> compatible -> combined compact
        var gpu = Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0");
        var snapshot = Snapshot(
            gpus: new[] { gpu },
            readings: new[]
            {
                Read("gpu0", "VramUsedBytes", 4_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Used"),
                Read("gpu0", "VramTotalBytes", 10_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Total"),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        // Both dedicated -> combined
        Assert.Equal("3.73 / 9.31 GB", payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
    }

    [Fact]
    public void Present_GpuMemoryCapacityCompact_MissingUsed_ReturnsNotAvailable()
    {
        var gpu = Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0");
        var snapshot = Snapshot(
            gpus: new[] { gpu },
            readings: new[]
            {
                Read("gpu0", "LoadPercent", 10.0),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
    }

    [Fact]
    public void Present_GpuMemoryCapacityCompact_MissingTotal_ReturnsUsedOnlyCompact()
    {
        var gpu = Gpu("gpu0", "NVIDIA GeForce RTX 3080", "NVIDIA", GpuIntegration.Discrete, "pci-0");
        var snapshot = Snapshot(
            gpus: new[] { gpu },
            readings: new[]
            {
                Read("gpu0", "VramUsedBytes", 4_000_000_000.0, "B", "LibreHardwareMonitor:GPU Memory Used"),
            });

        var payload = WidgetPayloadPresenter.Present(snapshot);

        // Used only -> no slash
        Assert.NotEqual(WidgetPayloadPresenter.NotAvailable, payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
        Assert.DoesNotContain("/", payload.GetText(WidgetPayloadPresenter.GpuMemoryCapacityCompactKey));
    }
}
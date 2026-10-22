using System;
using System.Collections.Generic;
using System.Linq;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the documented default GPU selection policy.
///
/// <para>
/// The policy has to be deterministic: the same machine, seen twice, must
/// present the same card. These tests pin both the preference order and
/// the tie-breaks that make it total.
/// </para>
/// </summary>
public class GpuSelectionPolicyTests
{
    private static HardwareDevice Gpu(
        string deviceId,
        string? instanceId,
        GpuIntegration integration,
        string name = "GPU") =>
        new()
        {
            DeviceId = deviceId,
            DisplayName = name,
            Vendor = "Vendor",
            DeviceClass = DeviceClass.Gpu,
            GpuIntegration = integration,
            InstanceId = instanceId,
        };

    [Fact]
    public void NoSelection_OutcomeIsDefaultPolicy()
    {
        var selection = GpuSelectionPolicy.Resolve(
            new[] { Gpu("gpu0", "pci-0", GpuIntegration.Discrete) },
            null);

        Assert.Equal(GpuSelectionOutcome.DefaultPolicy, selection.Outcome);
        Assert.False(selection.IsUserSelected);
        Assert.NotNull(selection.Device);
    }

    [Fact]
    public void NoGpus_NothingIsPresented()
    {
        var selection = GpuSelectionPolicy.Resolve(Array.Empty<HardwareDevice>(), null);

        Assert.Null(selection.Device);
        Assert.Equal(GpuSelectionOutcome.NoGpuAvailable, selection.Outcome);
    }

    [Fact]
    public void DiscreteWinsOverUnknownWhichWinsOverIntegrated()
    {
        var devices = new[]
        {
            Gpu("gpu-integrated", "pci-i", GpuIntegration.Integrated),
            Gpu("gpu-unknown", "pci-u", GpuIntegration.Unknown),
            Gpu("gpu-discrete", "pci-d", GpuIntegration.Discrete),
        };

        var selection = GpuSelectionPolicy.Resolve(devices, null);

        Assert.Equal("gpu-discrete", selection.Device!.DeviceId);
    }

    [Fact]
    public void UnknownBeatsIntegratedWhenThereIsNoDiscreteCard()
    {
        var devices = new[]
        {
            Gpu("gpu-integrated", "pci-i", GpuIntegration.Integrated),
            Gpu("gpu-unknown", "pci-u", GpuIntegration.Unknown),
        };

        var selection = GpuSelectionPolicy.Resolve(devices, null);

        Assert.Equal("gpu-unknown", selection.Device!.DeviceId);
    }

    [Fact]
    public void WithinAClass_TheLowestInstanceIdWins()
    {
        var devices = new[]
        {
            Gpu("gpu-b", "pci-b", GpuIntegration.Discrete),
            Gpu("gpu-a", "pci-a", GpuIntegration.Discrete),
        };

        var selection = GpuSelectionPolicy.Resolve(devices, null);

        Assert.Equal("pci-a", selection.Device!.InstanceId);
    }

    [Fact]
    public void TheDefaultIsTheSameRegardlessOfEnumerationOrder()
    {
        var devices = new[]
        {
            Gpu("gpu-integrated", "pci-i", GpuIntegration.Integrated),
            Gpu("gpu-unknown", "pci-u", GpuIntegration.Unknown),
            Gpu("gpu-discrete", "pci-d", GpuIntegration.Discrete),
        };

        var forwards = GpuSelectionPolicy.Resolve(devices, null);
        var backwards = GpuSelectionPolicy.Resolve(devices.Reverse().ToList(), null);

        Assert.Equal(forwards.Device!.DeviceId, backwards.Device!.DeviceId);
    }

    [Fact]
    public void TheDefaultIsTheSameRegardlessOfEnumerationOrder_WithinAClass()
    {
        var devices = new[]
        {
            Gpu("gpu-b", "pci-b", GpuIntegration.Discrete),
            Gpu("gpu-a", "pci-a", GpuIntegration.Discrete),
        };

        var forwards = GpuSelectionPolicy.Resolve(devices, null);
        var backwards = GpuSelectionPolicy.Resolve(devices.Reverse().ToList(), null);

        Assert.Equal(forwards.Device!.DeviceId, backwards.Device!.DeviceId);
    }

    [Fact]
    public void DevicesWithTheSameInstanceIdAreSeparatedByDeviceId()
    {
        // Should not happen, but the order has to stay total when it does.
        var devices = new[]
        {
            Gpu("gpu-b", "pci-same", GpuIntegration.Discrete),
            Gpu("gpu-a", "pci-same", GpuIntegration.Discrete),
        };

        var selection = GpuSelectionPolicy.Resolve(devices, null);

        Assert.Equal("gpu-a", selection.Device!.DeviceId);
    }

    [Fact]
    public void AUserSelectionOverridesTheDefaultPolicy()
    {
        var devices = new[]
        {
            Gpu("gpu-integrated", "pci-i", GpuIntegration.Integrated),
            Gpu("gpu-discrete", "pci-d", GpuIntegration.Discrete),
        };

        var selection = GpuSelectionPolicy.Resolve(devices, "pci-i");

        Assert.Equal(GpuSelectionOutcome.Selected, selection.Outcome);
        Assert.True(selection.IsUserSelected);
        Assert.Equal("pci-i", selection.Device!.InstanceId);
    }

    [Fact]
    public void ASelectionThatIsGoneFallsBackToTheDefaultAndSaysSo()
    {
        var devices = new[]
        {
            Gpu("gpu-integrated", "pci-i", GpuIntegration.Integrated),
            Gpu("gpu-discrete", "pci-d", GpuIntegration.Discrete),
        };

        var selection = GpuSelectionPolicy.Resolve(devices, "pci-removed");

        Assert.Equal(GpuSelectionOutcome.SelectionMissing, selection.Outcome);
        Assert.False(selection.IsUserSelected);

        // The default policy still applies, so the widget keeps working.
        Assert.Equal("pci-d", selection.Device!.InstanceId);

        // The requested id is retained, so the caller can tell "the user
        // chose this and it is gone" from "the user never chose anything".
        Assert.Equal("pci-removed", selection.RequestedDeviceId);
    }

    [Fact]
    public void ASelectionIsNeverResolvedByDisplayName()
    {
        var devices = new[]
        {
            Gpu("gpu-a", "pci-a", GpuIntegration.Discrete, "NVIDIA GeForce RTX 3080"),
            Gpu("gpu-b", "pci-b", GpuIntegration.Discrete, "NVIDIA GeForce RTX 3080"),
        };

        // "pci-b" is an instance id, not a name; it must resolve to the
        // second card, not to the first one that happens to share its name.
        var selection = GpuSelectionPolicy.Resolve(devices, "pci-b");

        Assert.Equal("gpu-b", selection.Device!.DeviceId);
    }

    [Fact]
    public void ABlankSelectionIsTreatedAsNoSelection()
    {
        var devices = new[] { Gpu("gpu0", "pci-0", GpuIntegration.Discrete) };

        var selection = GpuSelectionPolicy.Resolve(devices, "   ");

        Assert.Equal(GpuSelectionOutcome.DefaultPolicy, selection.Outcome);
    }

    [Fact]
    public void PresentationOrderMatchesTheDefaultPolicy()
    {
        var devices = new[]
        {
            Gpu("gpu-integrated", "pci-i", GpuIntegration.Integrated),
            Gpu("gpu-discrete", "pci-d", GpuIntegration.Discrete),
        };

        var ordered = GpuSelectionPolicy.OrderForPresentation(devices);

        Assert.Equal("gpu-discrete", ordered[0].DeviceId);
        Assert.Equal("gpu-integrated", ordered[1].DeviceId);
    }

    [Fact]
    public void ASelectionIsCaseInsensitiveOnTheInstanceId()
    {
        // Windows PNP ids are case-insensitive; the same card must resolve
        // however it was written.
        var devices = new[] { Gpu("gpu0", @"PCI\VEN_10DE&DEV_1E91\4&1&0", GpuIntegration.Discrete) };

        var selection = GpuSelectionPolicy.Resolve(devices, @"pci\ven_10de&dev_1e91\4&1&0");

        Assert.Equal(GpuSelectionOutcome.Selected, selection.Outcome);
    }
}

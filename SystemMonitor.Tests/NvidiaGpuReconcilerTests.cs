using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the identity normalization and the reconciliation rule that
/// keeps a fallback reading from being attributed to the wrong device.
/// </summary>
public class NvidiaGpuReconcilerTests
{
    private static readonly BackendGpuIdentity NvidiaDevice =
        new("/gpu-nvidia/0", "VEN_10DE&DEV_1E91");

    private static NvidiaGpuSample Sample(
        string uuid = "GPU-a5e273b9",
        string pciDeviceId = "0x1E9110DE",
        double? utilization = 40,
        double? temperature = 55,
        ulong? vramUsed = 100,
        ulong? vramTotal = 200)
    {
        return new NvidiaGpuSample
        {
            Uuid = uuid,
            Name = "NVIDIA GeForce RTX 2070 Super",
            PciBusId = "00000000:01:00.0",
            PciDeviceId = pciDeviceId,
            PciDeviceKey = NvidiaGpuIdentity.NormalizePciDeviceKey(pciDeviceId),
            UtilizationPercent = utilization,
            TemperatureC = temperature,
            VramUsedBytes = vramUsed,
            VramTotalBytes = vramTotal,
        };
    }

    // ---- Identity -------------------------------------------------------

    [Theory]
    [InlineData("0x1E9110DE", "VEN_10DE&DEV_1E91")]
    [InlineData("0X1e9110de", "VEN_10DE&DEV_1E91")]
    [InlineData("1E9110DE", "VEN_10DE&DEV_1E91")]
    public void NormalizesThePackedPciDeviceId(string input, string expected)
    {
        Assert.Equal(expected, NvidiaGpuIdentity.NormalizePciDeviceKey(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0x1234")]
    [InlineData("not-a-number")]
    public void ReturnsNoKeyForAnUnusablePciDeviceId(string? input)
    {
        Assert.Equal(string.Empty, NvidiaGpuIdentity.NormalizePciDeviceKey(input));
    }

    [Fact]
    public void ExtractsTheKeyFromAWindowsPnpDeviceId()
    {
        const string deviceId = @"PCI\VEN_10DE&DEV_1E91&SUBSYS_20091A58&REV_A1\4&7AADB68&0&0008";

        Assert.Equal("VEN_10DE&DEV_1E91", NvidiaGpuIdentity.ExtractPciDeviceKey(deviceId));
    }

    [Fact]
    public void ExtractsNoKeyFromADeviceIdMissingEitherPart()
    {
        Assert.Equal(string.Empty, NvidiaGpuIdentity.ExtractPciDeviceKey(@"PCI\VEN_8086&SUBSYS_1\3&11583659&0&10"));
        Assert.Equal(string.Empty, NvidiaGpuIdentity.ExtractPciDeviceKey("not a pnp id"));
        Assert.Equal(string.Empty, NvidiaGpuIdentity.ExtractPciDeviceKey(null));
    }

    [Fact]
    public void TheTwoIdentitySourcesProduceTheSameKey()
    {
        // The Windows PCI device tree and nvidia-smi describe the same card
        // differently, and the normalized keys must agree.
        const string windowsDeviceId = @"PCI\VEN_10DE&DEV_1E91&SUBSYS_20091A58&REV_A1\4&7AADB68&0&0008";

        var fromWindows = NvidiaGpuIdentity.ExtractPciDeviceKey(windowsDeviceId);
        var fromSmi = NvidiaGpuIdentity.NormalizePciDeviceKey("0x1E9110DE");

        Assert.Equal(fromWindows, fromSmi);
    }

    // ---- Reconciliation -------------------------------------------------

    [Fact]
    public void MatchesASampleToTheDeviceWithTheSameIdentityKey()
    {
        var results = NvidiaGpuReconciler.Reconcile(
            new[] { Sample() },
            new[] { NvidiaDevice });

        var result = Assert.Single(results);
        Assert.Equal(NvidiaReconciliationOutcome.Matched, result.Outcome);
        Assert.Equal("/gpu-nvidia/0", result.MatchedDeviceId);
    }

    [Fact]
    public void DoesNotMatchASampleWhoseDeviceTheBackendDidNotReport()
    {
        var results = NvidiaGpuReconciler.Reconcile(
            new[] { Sample(pciDeviceId: "0x268410DE") },
            new[] { NvidiaDevice });

        var result = Assert.Single(results);
        Assert.Equal(NvidiaReconciliationOutcome.Unreconciled, result.Outcome);
        Assert.Null(result.MatchedDeviceId);
    }

    [Fact]
    public void DoesNotMatchASampleWithNoIdentityAtAll()
    {
        // A sample whose PCI device id could not be read has no key, so it
        // cannot be attributed to any device.
        var results = NvidiaGpuReconciler.Reconcile(
            new[] { Sample(pciDeviceId: "garbage") },
            new[] { NvidiaDevice });

        var result = Assert.Single(results);
        Assert.Equal(NvidiaReconciliationOutcome.Unreconciled, result.Outcome);
        Assert.Null(result.MatchedDeviceId);
    }

    [Fact]
    public void DoesNotMatchWhenTwoBackendDevicesShareTheKey()
    {
        // Two identical cards: the key says which model, not which card.
        var results = NvidiaGpuReconciler.Reconcile(
            new[] { Sample() },
            new[]
            {
                new BackendGpuIdentity("/gpu-nvidia/0", "VEN_10DE&DEV_1E91"),
                new BackendGpuIdentity("/gpu-nvidia/1", "VEN_10DE&DEV_1E91"),
            });

        var result = Assert.Single(results);
        Assert.Equal(NvidiaReconciliationOutcome.Ambiguous, result.Outcome);
        Assert.Null(result.MatchedDeviceId);
    }

    [Fact]
    public void DoesNotMatchWhenTwoSamplesShareTheKey()
    {
        // Two identical cards reported by nvidia-smi: the key says which
        // model, not which card was measured.
        var results = NvidiaGpuReconciler.Reconcile(
            new[]
            {
                Sample(uuid: "GPU-a", pciDeviceId: "0x1E9110DE", utilization: 10),
                Sample(uuid: "GPU-b", pciDeviceId: "0x1E9110DE", utilization: 90),
            },
            new[] { NvidiaDevice });

        Assert.Equal(2, results.Count);
        Assert.All(results, r =>
        {
            Assert.Equal(NvidiaReconciliationOutcome.Ambiguous, r.Outcome);
            Assert.Null(r.MatchedDeviceId);
        });
    }

    [Fact]
    public void MatchesEachSampleToItsOwnDeviceWhenTheModelsDiffer()
    {
        var results = NvidiaGpuReconciler.Reconcile(
            new[]
            {
                Sample(uuid: "GPU-a", pciDeviceId: "0x1E9110DE", utilization: 10),
                Sample(uuid: "GPU-b", pciDeviceId: "0x268410DE", utilization: 90),
            },
            new[]
            {
                new BackendGpuIdentity("/gpu-nvidia/0", "VEN_10DE&DEV_1E91"),
                new BackendGpuIdentity("/gpu-nvidia/1", "VEN_10DE&DEV_2684"),
            });

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(NvidiaReconciliationOutcome.Matched, r.Outcome));
        Assert.Equal("/gpu-nvidia/0", results[0].MatchedDeviceId);
        Assert.Equal("/gpu-nvidia/1", results[1].MatchedDeviceId);
    }

    [Fact]
    public void NeverMatchesByPosition()
    {
        // The sample names a card the backend did not report, and the
        // backend reported a different card first. The sample must not be
        // attributed to the backend's first device.
        var results = NvidiaGpuReconciler.Reconcile(
            new[] { Sample(pciDeviceId: "0x268410DE", utilization: 77) },
            new[]
            {
                new BackendGpuIdentity("/gpu-nvidia/0", "VEN_10DE&DEV_1E91"),
            });

        var result = Assert.Single(results);
        Assert.Equal(NvidiaReconciliationOutcome.Unreconciled, result.Outcome);
        Assert.Null(result.MatchedDeviceId);
    }

    [Fact]
    public void IgnoresBackendDevicesWithoutAnIdentityKey()
    {
        var results = NvidiaGpuReconciler.Reconcile(
            new[] { Sample() },
            new[] { new BackendGpuIdentity("/gpu-nvidia/0", string.Empty) });

        var result = Assert.Single(results);
        Assert.Equal(NvidiaReconciliationOutcome.Unreconciled, result.Outcome);
    }

    [Fact]
    public void HandlesEmptyInputs()
    {
        Assert.Empty(NvidiaGpuReconciler.Reconcile(Array.Empty<NvidiaGpuSample>(), Array.Empty<BackendGpuIdentity>()));
        Assert.Empty(NvidiaGpuReconciler.Reconcile(Array.Empty<NvidiaGpuSample>(), new[] { NvidiaDevice }));
    }
}

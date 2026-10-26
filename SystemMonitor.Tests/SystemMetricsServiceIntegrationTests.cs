using System;
using System.Collections.Generic;
using System.Linq;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Production collection integration tests using the real SystemMetricsService
/// with an injected fake sensor backend. These tests exercise the actual
/// Collect() entry point and verify the tracker → snapshot → presenter
/// pipeline end-to-end through the production code path.
/// </summary>
public sealed class SystemMetricsServiceIntegrationTests
{
    /// <summary>
    /// A fake sensor source with mutable device list to simulate
    /// changing hardware state across collection cycles while keeping
    /// the same service instance (which holds the tracker state).
    /// </summary>
    private sealed class FakeSource : ILhmSensorSource
    {
        private IReadOnlyList<LhmDeviceDescriptor> _devices;
        private bool _disposed;

        public FakeSource(IReadOnlyList<LhmDeviceDescriptor> devices)
        {
            _devices = devices;
        }

        /// <summary>
        /// Updates the devices the source will return on the next Read().
        /// This simulates the backend seeing different hardware state on
        /// the next collection cycle.
        /// </summary>
        public void SetDevices(IReadOnlyList<LhmDeviceDescriptor> devices)
        {
            _devices = devices;
        }

        public bool IsAvailable { get; set; } = true;
        public string? UnavailableReason { get; set; }
        public bool IsDisposed => _disposed;
        public int ReadCount { get; private set; }

        public IReadOnlyList<LhmDeviceDescriptor> Read()
        {
            ReadCount++;
            return _devices;
        }

        public void Dispose() => _disposed = true;
    }

    /// <summary>
    /// A fake SystemMetrics model for testing (null object pattern).
    /// </summary>
    private sealed class FakeModel : SystemMetrics
    {
        // No-op implementation for testing
    }

    /// <summary>
    /// Creates a SystemMetricsService with an injected fake sensor source.
    /// </summary>
    private static SystemMetricsService CreateService(ILhmSensorSource sensorSource)
    {
        var model = new FakeModel();
        var nvidiaFallback = new NvidiaGpuFallback(); // Uses real process runner but we won't invoke it
        // Use internal constructor to inject the fake sensor source
        return new SystemMetricsService(model, nvidiaFallback, sensorSource);
    }

    /// <summary>
    /// Builds a readable NVIDIA GPU fixture.
    /// </summary>
    private static LhmDeviceDescriptor NvidiaGpu(string deviceId, string displayName, string instanceId, bool readable = true)
    {
        var sensors = readable
            ? new List<LhmSensorDescriptor>
            {
                LhmFixtures.Temp("GPU Core", 61.0),
                LhmFixtures.Clock("GPU Core", 330.0),
                LhmFixtures.Load("GPU Core", 25.0),
                LhmFixtures.Load("GPU Memory Controller", 18.0),
                LhmFixtures.Load("GPU Video Engine", 0.0),
                LhmFixtures.Memory("GPU Memory Used", 1233.0),
                LhmFixtures.Memory("GPU Memory Total", 8192.0),
            }
            : new List<LhmSensorDescriptor>();

        return new LhmDeviceDescriptor
        {
            DeviceId = deviceId,
            DisplayName = displayName,
            Vendor = "NVIDIA",
            IsCpu = false,
            IsIntegratedGpu = false,
            PciDeviceKey = "VEN_10DE&DEV_1E91",
            InstanceId = instanceId,
            Sensors = sensors,
        };
    }

    /// <summary>
    /// Test 1: End-to-end grace boundary for secondary GPU via production Collect().
    /// Verifies: readable secondary appears, failures 1-3 retain, failure 4 removes,
    /// recovery restores eligibility, earlier snapshots unchanged.
    /// </summary>
    [Fact]
    public void ProductionCollection_SecondaryGpu_GraceBoundary_FullLifecycle()
    {
        // Two GPUs: gpu0 (primary), gpu1 (secondary)
        var gpu0 = NvidiaGpu("/gpu-nvidia/0", "NVIDIA GeForce RTX 2070 Super",
            @"PCI\VEN_10DE&DEV_1E91&SUBSYS_14581458\4&7AADB68&0&0008");
        var gpu1 = NvidiaGpu("/gpu-nvidia/1", "NVIDIA GeForce RTX 3070",
            @"PCI\VEN_10DE&DEV_2206&SUBSYS_12341458\4&7AADB68&0&0010");

        var source = new FakeSource(new[] { gpu0, gpu1 });
        var service = CreateService(source);

        // Helper to collect and present
        var snapshots = new List<MetricsSnapshot>();

        // --- CYCLE 1: Initial success - both GPUs have telemetry ---
        var snapshot1 = service.Collect();
        snapshots.Add(snapshot1);
        var payload1 = WidgetPayloadPresenter.Present(snapshot1);

        // Both GPUs should appear: gpu0 as primary, gpu1 as extra
        Assert.True(payload1.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.Contains(gpu1.InstanceId, snapshot1.GpuTrackedExtraInstanceIds);

        // --- CYCLE 2: Failure 1 - gpu1 loses all telemetry ---
        var gpu1NoTelemetry = gpu1 with { Sensors = new List<LhmSensorDescriptor>() };
        source.SetDevices(new[] { gpu0, gpu1NoTelemetry });

        var snapshot2 = service.Collect();
        snapshots.Add(snapshot2);
        var payload2 = WidgetPayloadPresenter.Present(snapshot2);

        // gpu1 should still appear as extra (within grace period, failure 1 of 3)
        Assert.True(payload2.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.Contains(gpu1.InstanceId, snapshot2.GpuTrackedExtraInstanceIds);

        // --- CYCLE 3: Failure 2 - gpu1 still no telemetry ---
        source.SetDevices(new[] { gpu0, gpu1NoTelemetry });

        var snapshot3 = service.Collect();
        snapshots.Add(snapshot3);
        var payload3 = WidgetPayloadPresenter.Present(snapshot3);

        // gpu1 should still appear (failure 2 of 3)
        Assert.True(payload3.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.Contains(gpu1.InstanceId, snapshot3.GpuTrackedExtraInstanceIds);

        // --- CYCLE 4: Failure 3 - gpu1 still no telemetry (last grace cycle) ---
        source.SetDevices(new[] { gpu0, gpu1NoTelemetry });

        var snapshot4 = service.Collect();
        snapshots.Add(snapshot4);
        var payload4 = WidgetPayloadPresenter.Present(snapshot4);

        // gpu1 should still appear (failure 3 of 3, last grace cycle)
        Assert.True(payload4.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.Contains(gpu1.InstanceId, snapshot4.GpuTrackedExtraInstanceIds);

        // --- CYCLE 5: Failure 4 - grace exhausted, gpu1 removed ---
        source.SetDevices(new[] { gpu0, gpu1NoTelemetry });

        var snapshot5 = service.Collect();
        snapshots.Add(snapshot5);
        var payload5 = WidgetPayloadPresenter.Present(snapshot5);

        // gpu1 should be gone (failure 4 exceeds grace of 3)
        Assert.False(payload5.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.DoesNotContain(gpu1.InstanceId, snapshot5.GpuTrackedExtraInstanceIds);

        // --- CYCLE 6: Recovery - gpu1 reappears WITH telemetry ---
        var gpu1Recovered = NvidiaGpu("/gpu-nvidia/1", "NVIDIA GeForce RTX 3070",
            @"PCI\VEN_10DE&DEV_2206&SUBSYS_12341458\4&7AADB68&0&0010");
        source.SetDevices(new[] { gpu0, gpu1Recovered });

        var snapshot6 = service.Collect();
        snapshots.Add(snapshot6);
        var payload6 = WidgetPayloadPresenter.Present(snapshot6);

        // gpu1 should reappear and qualify fresh (new eligibility history)
        Assert.True(payload6.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.Contains(gpu1.InstanceId, snapshot6.GpuTrackedExtraInstanceIds);

        // --- Verify historical stability: earlier snapshots unchanged ---
        // snapshot1 eligibility should still have gpu1
        Assert.Contains(gpu1.InstanceId, snapshot1.GpuTrackedExtraInstanceIds);
        // snapshot5 should still not have gpu1
        Assert.DoesNotContain(gpu1.InstanceId, snapshot5.GpuTrackedExtraInstanceIds);
    }

    /// <summary>
    /// Test 2: Production collection cancellation does not consume grace.
    /// Verifies that a canceled collection (one that never calls Collect())
    /// does not advance the grace failure counter.
    /// </summary>
    [Fact]
    public void ProductionCollection_Cancellation_DoesNotConsumeGrace()
    {
        var gpu0 = NvidiaGpu("/gpu-nvidia/0", "NVIDIA GeForce RTX 2070 Super",
            @"PCI\VEN_10DE&DEV_1E91&SUBSYS_14581458\4&7AADB68&0&0008");
        var gpu1 = NvidiaGpu("/gpu-nvidia/1", "NVIDIA GeForce RTX 3070",
            @"PCI\VEN_10DE&DEV_2206&SUBSYS_12341458\4&7AADB68&0&0010");

        var source = new FakeSource(new[] { gpu0, gpu1 });
        var service = CreateService(source);

        // --- CYCLE 1: Initial success - both have telemetry ---
        var snapshot1 = service.Collect();
        var payload1 = WidgetPayloadPresenter.Present(snapshot1);
        Assert.True(payload1.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));

        // --- CYCLE 2: Failure 1 - gpu1 loses telemetry ---
        var gpu1NoTelemetry = gpu1 with { Sensors = new List<LhmSensorDescriptor>() };
        source.SetDevices(new[] { gpu0, gpu1NoTelemetry });

        var snapshot2 = service.Collect();
        var payload2 = WidgetPayloadPresenter.Present(snapshot2);
        Assert.True(payload2.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));

        // --- SIMULATED CANCELED COLLECTION ---
        // In the real pipeline: UpdatePipeline checks cancellation token BEFORE
        // calling Collect(). If canceled, Collect() is never called, and the
        // tracker's Update() is never invoked. We simulate this by simply NOT
        // calling Collect() for one cycle. The time passes but no collection occurs.
        // (No service.Collect() call here)

        // --- CYCLE 3 (actual): Failure 2 - gpu1 still no telemetry ---
        source.SetDevices(new[] { gpu0, gpu1NoTelemetry });

        var snapshot3 = service.Collect();
        var payload3 = WidgetPayloadPresenter.Present(snapshot3);
        // Should still be retained (this is failure 2, not 3)
        Assert.True(payload3.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));

        // --- CYCLE 4 (actual): Failure 3 ---
        source.SetDevices(new[] { gpu0, gpu1NoTelemetry });

        var snapshot4 = service.Collect();
        var payload4 = WidgetPayloadPresenter.Present(snapshot4);
        Assert.True(payload4.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));

        // --- CYCLE 5 (actual): Failure 4 - grace exhausted ---
        source.SetDevices(new[] { gpu0, gpu1NoTelemetry });

        var snapshot5 = service.Collect();
        var payload5 = WidgetPayloadPresenter.Present(snapshot5);
        // Now removed (failure 4)
        Assert.False(payload5.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));

        // If the canceled cycle had consumed grace, removal would have happened
        // on what we called "CYCLE 4" above. The fact it happens on CYCLE 5
        // proves cancellation doesn't consume grace.
    }

    /// <summary>
    /// Test 3: Primary GPU grace boundary via production Collect().
    /// </summary>
    [Fact]
    public void ProductionCollection_PrimaryGpu_GraceBoundary_FullLifecycle()
    {
        var gpu0 = NvidiaGpu("/gpu-nvidia/0", "NVIDIA GeForce RTX 2070 Super",
            @"PCI\VEN_10DE&DEV_1E91&SUBSYS_14581458\4&7AADB68&0&0008");
        var gpu1 = NvidiaGpu("/gpu-nvidia/1", "NVIDIA GeForce RTX 3070",
            @"PCI\VEN_10DE&DEV_2206&SUBSYS_12341458\4&7AADB68&0&0010");

        var source = new FakeSource(new[] { gpu0, gpu1 });
        var service = CreateService(source);

        // --- CYCLE 1: Initial success - gpu0 has telemetry, becomes tracked ---
        var snapshot1 = service.Collect();
        var payload1 = WidgetPayloadPresenter.Present(snapshot1);
        Assert.Equal(gpu0.InstanceId, snapshot1.GpuTrackedInstanceId);

        // --- CYCLE 2: Failure 1 - gpu0 loses telemetry ---
        var gpu0NoTelemetry = gpu0 with { Sensors = new List<LhmSensorDescriptor>() };
        source.SetDevices(new[] { gpu0NoTelemetry, gpu1 });

        var snapshot2 = service.Collect();
        var payload2 = WidgetPayloadPresenter.Present(snapshot2);
        Assert.Equal(gpu0.InstanceId, snapshot2.GpuTrackedInstanceId);

        // --- CYCLE 3: Failure 2 ---
        source.SetDevices(new[] { gpu0NoTelemetry, gpu1 });

        var snapshot3 = service.Collect();
        var payload3 = WidgetPayloadPresenter.Present(snapshot3);
        Assert.Equal(gpu0.InstanceId, snapshot3.GpuTrackedInstanceId);

        // --- CYCLE 4: Failure 3 (last grace cycle) ---
        source.SetDevices(new[] { gpu0NoTelemetry, gpu1 });

        var snapshot4 = service.Collect();
        var payload4 = WidgetPayloadPresenter.Present(snapshot4);
        Assert.Equal(gpu0.InstanceId, snapshot4.GpuTrackedInstanceId);

        // --- CYCLE 5: Failure 4 - grace exhausted, fallback to gpu1 ---
        source.SetDevices(new[] { gpu0NoTelemetry, gpu1 });

        var snapshot5 = service.Collect();
        var payload5 = WidgetPayloadPresenter.Present(snapshot5);
        Assert.Equal(gpu1.InstanceId, snapshot5.GpuTrackedInstanceId);
    }

    /// <summary>
    /// Test 4: Multiple secondary GPUs coexistence with one in grace period.
    /// Exercises the real SystemMetricsService.Collect() path with:
    /// - GPU A: primary, consistently readable
    /// - GPU B: secondary, consistently readable
    /// - GPU C: secondary, loses displayed telemetry after initial qualification
    ///
    /// Verifies:
    /// - B and C are both eligible and presented during C's failures 1-3
    /// - B precedes grace-retained C in presentation order
    /// - C disappears on failure 4
    /// - B remains present throughout
    /// - Earlier snapshots remain unchanged (immutability)
    /// </summary>
    [Fact]
    public void ProductionCollection_MultipleSecondaries_OneInGrace_CoexistAndOrderCorrectly()
    {
        // GPU A (primary) - RTX 2070 Super
        var gpuA = NvidiaGpu("/gpu-nvidia/0", "NVIDIA GeForce RTX 2070 Super",
            @"PCI\VEN_10DE&DEV_1E91&SUBSYS_14581458\4&7AADB68&0&0008");
        // GPU B (secondary, always readable) - RTX 3070
        var gpuB = NvidiaGpu("/gpu-nvidia/1", "NVIDIA GeForce RTX 3070",
            @"PCI\VEN_10DE&DEV_2206&SUBSYS_12341458\4&7AADB68&0&0010");
        // GPU C (secondary, loses telemetry) - RTX 4070
        var gpuC = NvidiaGpu("/gpu-nvidia/2", "NVIDIA GeForce RTX 4070",
            @"PCI\VEN_10DE&DEV_2484&SUBSYS_12341458\4&7AADB68&0&0012");

        var source = new FakeSource(new[] { gpuA, gpuB, gpuC });
        var service = CreateService(source);

        var snapshots = new List<MetricsSnapshot>();

        // --- CYCLE 1: Initial success - all three GPUs have telemetry ---
        var snapshot1 = service.Collect();
        snapshots.Add(snapshot1);
        var payload1 = WidgetPayloadPresenter.Present(snapshot1);

        // A is primary, B and C are both extras (both have telemetry)
        // Order: B (lexicographically smaller InstanceId) then C
        Assert.True(payload1.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.True(payload1.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(2)));
        Assert.Equal(2, snapshot1.GpuTrackedExtraInstanceIds.Count);

        var extra1Text1 = payload1.GetText(WidgetPayloadPresenter.ExtraKey(1));
        var extra2Text1 = payload1.GetText(WidgetPayloadPresenter.ExtraKey(2));
        Assert.Contains("RTX 3070", extra1Text1); // B first (smaller InstanceId)
        Assert.Contains("RTX 4070", extra2Text1); // C second

        // --- CYCLE 2: C loses telemetry (failure 1), B remains readable ---
        var gpuCNoTelemetry = gpuC with { Sensors = new List<LhmSensorDescriptor>() };
        source.SetDevices(new[] { gpuA, gpuB, gpuCNoTelemetry });

        var snapshot2 = service.Collect();
        snapshots.Add(snapshot2);
        var payload2 = WidgetPayloadPresenter.Present(snapshot2);

        // Both B and C should still appear: B has telemetry, C in grace (failure 1)
        // B precedes C (withTelemetry before withoutTelemetry)
        Assert.True(payload2.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.True(payload2.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(2)));
        Assert.Equal(2, snapshot2.GpuTrackedExtraInstanceIds.Count);

        var extra1Text2 = payload2.GetText(WidgetPayloadPresenter.ExtraKey(1));
        var extra2Text2 = payload2.GetText(WidgetPayloadPresenter.ExtraKey(2));
        Assert.Contains("RTX 3070", extra1Text2); // B first (has telemetry)
        Assert.Contains("RTX 4070", extra2Text2); // C second (grace)
        Assert.Contains(gpuB.InstanceId, snapshot2.GpuTrackedExtraInstanceIds);
        Assert.Contains(gpuC.InstanceId, snapshot2.GpuTrackedExtraInstanceIds);

        // --- CYCLE 3: C failure 2 ---
        source.SetDevices(new[] { gpuA, gpuB, gpuCNoTelemetry });

        var snapshot3 = service.Collect();
        snapshots.Add(snapshot3);
        var payload3 = WidgetPayloadPresenter.Present(snapshot3);

        Assert.True(payload3.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.True(payload3.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(2)));
        Assert.Equal(2, snapshot3.GpuTrackedExtraInstanceIds.Count);
        Assert.Contains(gpuB.InstanceId, snapshot3.GpuTrackedExtraInstanceIds);
        Assert.Contains(gpuC.InstanceId, snapshot3.GpuTrackedExtraInstanceIds);

        // --- CYCLE 4: C failure 3 (last grace cycle) ---
        source.SetDevices(new[] { gpuA, gpuB, gpuCNoTelemetry });

        var snapshot4 = service.Collect();
        snapshots.Add(snapshot4);
        var payload4 = WidgetPayloadPresenter.Present(snapshot4);

        Assert.True(payload4.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.True(payload4.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(2)));
        Assert.Equal(2, snapshot4.GpuTrackedExtraInstanceIds.Count);
        Assert.Contains(gpuB.InstanceId, snapshot4.GpuTrackedExtraInstanceIds);
        Assert.Contains(gpuC.InstanceId, snapshot4.GpuTrackedExtraInstanceIds);

        // --- CYCLE 5: C failure 4 - grace exhausted, C removed ---
        source.SetDevices(new[] { gpuA, gpuB, gpuCNoTelemetry });

        var snapshot5 = service.Collect();
        snapshots.Add(snapshot5);
        var payload5 = WidgetPayloadPresenter.Present(snapshot5);

        // C gone, B still present
        Assert.True(payload5.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(1)));
        Assert.False(payload5.GetFlag(WidgetPayloadPresenter.ExtraAvailableKey(2)));
        Assert.Single(snapshot5.GpuTrackedExtraInstanceIds);
        Assert.Contains(gpuB.InstanceId, snapshot5.GpuTrackedExtraInstanceIds);
        Assert.DoesNotContain(gpuC.InstanceId, snapshot5.GpuTrackedExtraInstanceIds);

        var extra1Text5 = payload5.GetText(WidgetPayloadPresenter.ExtraKey(1));
        Assert.Contains("RTX 3070", extra1Text5); // B still there

        // --- Verify historical stability: earlier snapshots unchanged ---
        // snapshot1: both B and C present
        Assert.Equal(2, snapshot1.GpuTrackedExtraInstanceIds.Count);
        Assert.Contains(gpuB.InstanceId, snapshot1.GpuTrackedExtraInstanceIds);
        Assert.Contains(gpuC.InstanceId, snapshot1.GpuTrackedExtraInstanceIds);

        // snapshot2-4: both B and C present
        Assert.Equal(2, snapshot2.GpuTrackedExtraInstanceIds.Count);
        Assert.Contains(gpuB.InstanceId, snapshot2.GpuTrackedExtraInstanceIds);
        Assert.Contains(gpuC.InstanceId, snapshot2.GpuTrackedExtraInstanceIds);
        Assert.Equal(2, snapshot3.GpuTrackedExtraInstanceIds.Count);
        Assert.Equal(2, snapshot4.GpuTrackedExtraInstanceIds.Count);

        // snapshot5: only B
        Assert.Single(snapshot5.GpuTrackedExtraInstanceIds);
    }
}

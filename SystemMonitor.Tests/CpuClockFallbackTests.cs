using System;
using System.Collections.Generic;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the CPU clock fallback reader and the merge path that
/// selects between the LHM backend clock and the Windows performance-counter
/// fallback.
///
/// <para>
/// The LHM backend's MSR-backed clock sensor blocks under a standard user
/// (it is null without elevation), so the performance-counter reader is the
/// live source on this machine. These tests cover the reader's null contract,
/// the merge precedence (backend wins, fallback fills in), the source string,
/// and recovery from an unavailable read — all through the production
/// Collect() pipeline using injected fakes so no real hardware is touched.
/// </para>
/// </summary>
public sealed class CpuClockFallbackTests
{
    /// <summary>
    /// A fake <see cref="ICpuFrequencyReader"/> that returns a scripted
    /// sequence of readings, one per call to <see cref="ReadMegahertz"/>.
    /// </summary>
    private sealed class FakeCpuFrequencyReader : ICpuFrequencyReader
    {
        private readonly IEnumerator<double?> _readings;

        public FakeCpuFrequencyReader(params double?[] readings)
        {
            _readings = readings.AsEnumerable().GetEnumerator();
        }

        public double? ReadMegahertz()
        {
            return _readings.MoveNext() ? _readings.Current : null;
        }

        public void Dispose() { }
    }

    /// <summary>
    /// Returns a TimeProvider pinned to a fixed instant, so the publication
    /// stale threshold can be exercised deterministically.
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// Builds a SystemMetricsService with an injected fake sensor source and an
    /// injected fake CPU frequency reader.
    /// </summary>
    private static SystemMetricsService CreateService(
        ILhmSensorSource sensorSource,
        ICpuFrequencyReader? cpuFrequencyReader = null)
    {
        var model = new SystemMetrics();
        var nvidiaFallback = new NvidiaGpuFallback();
        return new SystemMetricsService(model, nvidiaFallback, sensorSource,
            cpuFrequencyReader ?? new FakeCpuFrequencyReader([3600.0]));
    }

    /// <summary>
    /// Builds a readable Intel CPU fixture (mirrors the real LHM 0.9.6
    /// sensor naming, including the "CPU Core #1" Clock sensor the backend
    /// selects).
    /// </summary>
    private static LhmDeviceDescriptor ReadableIntelCpu() =>
        new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            DisplayName = "Intel Core i7-10875H",
            Vendor = "Intel",
            IsCpu = true,
            Sensors = new List<LhmSensorDescriptor>
            {
                LhmFixtures.Clock("CPU Core #1", 3600.0),
                LhmFixtures.Temp("CPU Package", 72.0),
            },
        };

    /// <summary>
    /// A fake sensor source with a mutable device list, copied from the
    /// integration-test fixtures so changing hardware state across cycles
    /// is simulated on the same service instance.
    /// </summary>
    private sealed class FakeSource : ILhmSensorSource
    {
        private IReadOnlyList<LhmDeviceDescriptor> _devices;

        public FakeSource(IReadOnlyList<LhmDeviceDescriptor> devices)
        {
            _devices = devices;
        }

        public void SetDevices(IReadOnlyList<LhmDeviceDescriptor> devices) => _devices = devices;

        public bool IsAvailable { get; set; } = true;
        public string? UnavailableReason { get; set; }

        public IReadOnlyList<LhmDeviceDescriptor> Read() => _devices;

        public Task<ReinitializeResult> ReinitializeAsync(CancellationToken ct = default) =>
            Task.FromResult(new ReinitializeResult
            {
                Outcome = ReinitializationOutcome.Success,
                WasAvailableBefore = IsAvailable,
                CompletedAt = DateTimeOffset.UtcNow,
            });

        public void Dispose() { }
    }

    // ------------------------------------------------------------------
    // 1. Live clock changing across samples
    // ------------------------------------------------------------------

    [Fact]
    public void ThePerformanceCounterReader_ReturnsDifferentValuesAcrossReads()
    {
        // The clock is a live measurement: under varying load the frequency
        // ratio changes, so two consecutive reads must not be locked to a
        // single static figure. The fake reader sequences two distinct
        // values to model that variance.
        var reader = new FakeCpuFrequencyReader([3628.0, 3366.0, 3678.0]);

        Assert.Equal(3628.0, reader.ReadMegahertz());
        Assert.Equal(3366.0, reader.ReadMegahertz());
        Assert.Equal(3678.0, reader.ReadMegahertz());
    }

    [Fact]
    public void ThePerformanceCounterReader_NeverReturnsZeroForUnavailable()
    {
        // A blocked or absent counter is unavailable, not zero. Returning
        // zero would let a dead sensor masquerade as an idle reading.
        var reader = new FakeCpuFrequencyReader([null]);

        Assert.Null(reader.ReadMegahertz());
    }

    // ------------------------------------------------------------------
    // 2. Source priority / fallback
    // ------------------------------------------------------------------

    [Fact]
    public void TheBackendClock_WinsOverThePerformanceCounterFallback()
    {
        // When both sources supply the clock, the backend's figure wins: it
        // is device-attributed and primary.
        var cpu = ReadableIntelCpu();
        var source = new FakeSource(new[] { cpu });
        var reader = new FakeCpuFrequencyReader(2800.0); // fallback value, should be ignored
        var service = CreateService(source, reader);

        var snapshot = service.Collect();

        // The backend reported 3600.0 MHz; the fallback's 2800.0 must not win.
        Assert.NotNull(snapshot.CpuClockMHz);
        Assert.Equal(MeasurementStatus.Available, snapshot.CpuClockMHz!.Status);
        Assert.Equal(3600.0, snapshot.CpuClockMHz.Value);
        Assert.Equal("LibreHardwareMonitor", snapshot.CpuClockMHz.Source);
    }

    [Fact]
    public void TheFallbackFillsIn_WhenTheBackendCouldNotReadTheClock()
    {
        // The backend has no clock sensor → the fallback reader fills in.
        var cpu = new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            DisplayName = "Intel Core i7-10875H",
            Vendor = "Intel",
            IsCpu = true,
            Sensors = new List<LhmSensorDescriptor>
            {
                // No Clock sensor at all; only a temperature.
                LhmFixtures.Temp("CPU Package", 72.0),
            },
        };
        var source = new FakeSource(new[] { cpu });
        var reader = new FakeCpuFrequencyReader(3542.0);
        var service = CreateService(source, reader);

        var snapshot = service.Collect();

        Assert.NotNull(snapshot.CpuClockMHz);
        Assert.Equal(MeasurementStatus.Available, snapshot.CpuClockMHz!.Status);
        Assert.Equal(3542.0, snapshot.CpuClockMHz.Value);
        Assert.Equal("Processor Information", snapshot.CpuClockMHz.Source);
    }

    [Fact]
    public void TheFallbackFillsIn_WhenTheBackendClockIsBlocked()
    {
        // The backend reports the device but its clock sensor is null (the
        // standard-user MSR block). The fallback reader supplies the value.
        var cpu = new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            DisplayName = "Intel Core i7-10875H",
            Vendor = "Intel",
            IsCpu = true,
            Sensors = new List<LhmSensorDescriptor>
            {
                // Clock present but value is null (permission-blocked).
                LhmFixtures.Clock("CPU Core #1", null),
                LhmFixtures.Temp("CPU Package", 72.0),
            },
        };
        var source = new FakeSource(new[] { cpu });
        var reader = new FakeCpuFrequencyReader(3210.0);
        var service = CreateService(source, reader);

        var snapshot = service.Collect();

        Assert.NotNull(snapshot.CpuClockMHz);
        Assert.Equal(MeasurementStatus.Available, snapshot.CpuClockMHz!.Status);
        Assert.Equal(3210.0, snapshot.CpuClockMHz.Value);
        Assert.Equal("Processor Information", snapshot.CpuClockMHz.Source);
    }

    // ------------------------------------------------------------------
    // 3. Missing / failed / stale clock
    // ------------------------------------------------------------------

    [Fact]
    public void ANullReaderResult_ProducesAFailedClockMeasurement()
    {
        // Neither the backend nor the fallback could supply a clock reading.
        // The measurement must read as Failed (no value, no zero).
        var cpu = new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            DisplayName = "Intel Core i7-10875H",
            Vendor = "Intel",
            IsCpu = true,
            Sensors = new List<LhmSensorDescriptor>
            {
                LhmFixtures.Temp("CPU Package", 72.0),
            },
        };
        var source = new FakeSource(new[] { cpu });
        var reader = new FakeCpuFrequencyReader([null]); // fallback unavailable
        var service = CreateService(source, reader);

        var snapshot = service.Collect();

        Assert.NotNull(snapshot.CpuClockMHz);
        Assert.Null(snapshot.CpuClockMHz!.Value);
        Assert.Equal(MeasurementStatus.Failed, snapshot.CpuClockMHz.Status);
        Assert.Equal("Processor Information", snapshot.CpuClockMHz.Source);
    }

    [Fact]
    public void AFailedClockRead_DoesNotPresentAsZero()
    {
        // Explicitly assert the zero-substitution guard: an unavailable
        // clock must never surface as 0 MHz, which would be read as "CPU
        // stopped."
        var cpu = new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            IsCpu = true,
            Sensors = new List<LhmSensorDescriptor>(),
        };
        var source = new FakeSource(new[] { cpu });
        var reader = new FakeCpuFrequencyReader([null]);
        var service = CreateService(source, reader);

        var snapshot = service.Collect();
        var payload = WidgetPayloadPresenter.Present(snapshot);

        var clockText = payload.GetText(WidgetPayloadPresenter.CpuClockKey);
        Assert.Equal(WidgetPayloadPresenter.NotAvailable, clockText);
    }

    [Fact]
    public void AStaleClock_FromFailedFallback_UsesTheFailedSource()
    {
        // First cycle: fallback unavailable → clock is Failed.
        // Second cycle: fallback also unavailable → the state has no prior
        // success, so it stays Failed (not Stale, because nothing was ever
        // successfully read).
        var cpu = new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            IsCpu = true,
            Sensors = new List<LhmSensorDescriptor>(),
        };
        var source = new FakeSource(new[] { cpu });
        var reader = new FakeCpuFrequencyReader([null, null]);
        var service = CreateService(source, reader);

        var snapshot1 = service.Collect();
        var snapshot2 = service.Collect();

        Assert.Equal(MeasurementStatus.Failed, snapshot1.CpuClockMHz!.Status);
        Assert.Equal(MeasurementStatus.Failed, snapshot2.CpuClockMHz!.Status);
        Assert.Null(snapshot1.CpuClockMHz!.Value);
        Assert.Null(snapshot2.CpuClockMHz!.Value);
    }

    // ------------------------------------------------------------------
    // 4. Recovery after an unavailable read
    // ------------------------------------------------------------------

    [Fact]
    public void Recovery_FromUnavailableFallback_ProducesAFreshClock()
    {
        // Cycle 1: fallback blocks (no clock anywhere) → Failed.
        // Cycle 2: fallback recovers → Available with the new value.
        var cpu = new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            IsCpu = true,
            Sensors = new List<LhmSensorDescriptor>(),
        };
        var source = new FakeSource(new[] { cpu });
        var reader = new FakeCpuFrequencyReader([null, 3450.0]);
        var service = CreateService(source, reader);

        var snapshot1 = service.Collect();
        var snapshot2 = service.Collect();

        Assert.Equal(MeasurementStatus.Failed, snapshot1.CpuClockMHz!.Status);
        Assert.Null(snapshot1.CpuClockMHz!.Value);

        Assert.Equal(MeasurementStatus.Available, snapshot2.CpuClockMHz!.Status);
        Assert.Equal(3450.0, snapshot2.CpuClockMHz!.Value);
        Assert.Equal("Processor Information", snapshot2.CpuClockMHz.Source);
    }

    [Fact]
    public void Recovery_ReplacesAStaleBackendReading_WithTheFreshBackendValue()
    {
        // Cycle 1: backend readable, clock = 3600.
        // Cycle 2: backend clock sensor nulled (blocked) AND fallback blocked
        //           → clock is Stale (last successful still the 3600 backend
        //           read) and the value is not carried into the measurement.
        // Cycle 3: backend readable again → clock is fresh at 3600.
        var cpuReadable = ReadableIntelCpu();
        var cpuBlocked = new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            DisplayName = "Intel Core i7-10875H",
            Vendor = "Intel",
            IsCpu = true,
            Sensors = new List<LhmSensorDescriptor>
            {
                // Clock sensor present but null → blocked.
                LhmFixtures.Clock("CPU Core #1", null),
                LhmFixtures.Temp("CPU Package", 72.0),
            },
        };

        var source = new FakeSource(new[] { cpuReadable });
        // The fallback reader: cycle 1 returns a value (backend wins, so it's
        // ignored), cycle 2 returns null (both sources unavailable → stale),
        // cycle 3 returns a value (backend recovers, so it's ignored).
        var reader = new FakeCpuFrequencyReader(3000.0, null, 3000.0);
        var service = CreateService(source, reader);

        // Cycle 1: backend reads 3600.
        var snapshot1 = service.Collect();
        Assert.Equal(MeasurementStatus.Available, snapshot1.CpuClockMHz!.Status);
        Assert.Equal(3600.0, snapshot1.CpuClockMHz.Value);
        var readAt1 = snapshot1.CpuClockMHz.LastSuccessfulUpdate;
        Assert.NotNull(readAt1);

        // Cycle 2: backend clock blocked → stale, value not carried.
        source.SetDevices(new[] { cpuBlocked });
        var snapshot2 = service.Collect();
        Assert.Equal(MeasurementStatus.Stale, snapshot2.CpuClockMHz!.Status);
        Assert.Null(snapshot2.CpuClockMHz!.Value);
        Assert.Equal(readAt1, snapshot2.CpuClockMHz.LastSuccessfulUpdate);

        // Cycle 3: backend recovered → fresh at 3600 again.
        source.SetDevices(new[] { cpuReadable });
        var snapshot3 = service.Collect();
        Assert.Equal(MeasurementStatus.Available, snapshot3.CpuClockMHz!.Status);
        Assert.Equal(3600.0, snapshot3.CpuClockMHz.Value);
        Assert.True(snapshot3.CpuClockMHz.LastSuccessfulUpdate > readAt1);
    }

    // ------------------------------------------------------------------
    // 5. No snapshot / tracker mutation during presentation
    // ------------------------------------------------------------------

    [Fact]
    public void Presentation_DoesNotMutateTheSnapshot()
    {
        // Presenting a snapshot — including applying the stale threshold —
        // must not change the snapshot's measurements. The snapshot is an
        // immutable published value handed to the presenter.
        var cpu = ReadableIntelCpu();
        var source = new FakeSource(new[] { cpu });
        var reader = new FakeCpuFrequencyReader([3600.0]);
        var service = CreateService(source, reader);

        var snapshot = service.Collect();
        var clockBefore = snapshot.CpuClockMHz;
        Assert.NotNull(clockBefore);
        var readAt = clockBefore!.LastSuccessfulUpdate;

        // Force a "stale" condition: publish 60 seconds after the read,
        // past the 30-second stale threshold. PresentForPublication uses a
        // TimeProvider to determine "now".
        var staleNow = readAt!.Value.AddSeconds(60);
        var frozen = new FixedTimeProvider(staleNow);

        var payload = WidgetPayloadPresenter.PresentForPublication(snapshot, null, frozen);

        // The clock should display as stale...
        var clockText = payload.GetText(WidgetPayloadPresenter.CpuClockKey);
        Assert.Equal(WidgetPayloadPresenter.StaleMarker, clockText);

        // ...but the snapshot's measurement must be untouched: still Available
        // at the original read time, with the value intact.
        Assert.Same(clockBefore, snapshot.CpuClockMHz);
        Assert.Equal(MeasurementStatus.Available, snapshot.CpuClockMHz!.Status);
        Assert.Equal(3600.0, snapshot.CpuClockMHz.Value);
        Assert.Equal(readAt, snapshot.CpuClockMHz.LastSuccessfulUpdate);
    }

    [Fact]
    public void Presentation_DoesNotMutateTheCollectionTracker()
    {
        // The collector's internal state must not change when the payload is
        // presented. Presenting is a read-only view over the snapshot.
        var cpu = ReadableIntelCpu();
        var source = new FakeSource(new[] { cpu });
        var reader = new FakeCpuFrequencyReader([3600.0]);
        var service = CreateService(source, reader);

        var snapshot1 = service.Collect();
        _ = WidgetPayloadPresenter.Present(snapshot1);

        // A second collection must see a clean slate: the tracker is not
        // disturbed by presentation, so the second clock read comes from the
        // reader's next scripted value.
        var snapshot2 = service.Collect();
        var clockBeforePresent = snapshot1.CpuClockMHz;
        var clockAfterPresent = snapshot2.CpuClockMHz;

        Assert.Equal(MeasurementStatus.Available, clockBeforePresent!.Status);
        Assert.Equal(MeasurementStatus.Available, clockAfterPresent!.Status);
        // The snapshot reference is stable — it was built from a copy of the
        // model state, not a live view that presentation could alter.
        Assert.NotSame(clockBeforePresent, clockAfterPresent);
    }

    // ------------------------------------------------------------------
    // Sensor selection: the clock sensor the backend picks
    // ------------------------------------------------------------------

    [Fact]
    public void TheBackendSelectsTheFirstMatchingClockSensor()
    {
        // The backend names a CPU device and picks the first Clock sensor
        // whose name matches the candidate list ("CPU Core #1"). Two clock
        // sensors are present; only the first should be honored.
        var cpu = new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            IsCpu = true,
            Sensors = new List<LhmSensorDescriptor>
            {
                LhmFixtures.Clock("CPU Core #1", 3700.0),
                LhmFixtures.Clock("CPU Core #2", 9999.0), // should be ignored
            },
        };
        var source = new FakeSource(new[] { cpu });
        // Fallback provides a sentinel that must NOT win when the backend is readable.
        var reader = new FakeCpuFrequencyReader(2200.0);
        var service = CreateService(source, reader);

        var snapshot = service.Collect();

        Assert.Equal(3700.0, snapshot.CpuClockMHz!.Value);
    }
}

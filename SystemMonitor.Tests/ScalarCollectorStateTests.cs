using System;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the rule that distinguishes a measured value from a value
/// that could not be measured.
///
/// <para>
/// The system-level collectors write into a mutable model that keeps the
/// last value it managed to read. Without a record of whether the read
/// actually succeeded, a collector that never succeeded would report the
/// model's initial value — zero — as a measurement. This is the state that
/// prevents that, and it is also what keeps a carried-over value stamped
/// with the time it was read rather than the time it was published.
/// </para>
/// </summary>
public class ScalarCollectorStateTests
{
    private const string Metric = "ram-used";
    private const string Unit = "bytes";
    private const string Source = "GlobalMemoryStatusEx";

    private static readonly DateTimeOffset ReadAt = new(2026, 10, 21, 6, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ACollectorThatNeverRan_ProducesNoValue()
    {
        // Nothing was ever measured. The number zero is not a substitute
        // for a value that was never read.
        var state = new ScalarCollectorState();

        var measurement = state.ToMeasurement(Metric);

        Assert.Null(measurement.Value);
        Assert.Equal(MeasurementStatus.Failed, measurement.Status);
    }

    [Fact]
    public void AFailedFirstRead_ProducesNoValue()
    {
        // The read was attempted and failed. There is no value.
        var state = new ScalarCollectorState();
        state.ReportNoValue(Metric, Unit, Source);

        var measurement = state.ToMeasurement(Metric);

        Assert.Null(measurement.Value);
        Assert.Equal(MeasurementStatus.Failed, measurement.Status);
        Assert.Equal(Source, measurement.Source);
    }

    [Fact]
    public void ASuccessfulRead_ProducesTheValue()
    {
        var state = new ScalarCollectorState();
        state.ReportSuccess(Metric, 1024, Unit, Source, ReadAt);

        var measurement = state.ToMeasurement(Metric);

        Assert.Equal(1024, measurement.Value);
        Assert.Equal(MeasurementStatus.Available, measurement.Status);
        Assert.Equal(Unit, measurement.Unit);
        Assert.Equal(Source, measurement.Source);
    }

    [Fact]
    public void AMeasuredZero_IsPresentedAsAvailable()
    {
        // Zero is a legitimate measurement — a disk that is genuinely
        // empty, a network that is genuinely idle. It must be presented as
        // a value, not as an absence.
        var state = new ScalarCollectorState();
        state.ReportSuccess(Metric, 0, Unit, Source, ReadAt);

        var measurement = state.ToMeasurement(Metric);

        Assert.Equal(0, measurement.Value);
        Assert.Equal(MeasurementStatus.Available, measurement.Status);
    }

    [Fact]
    public void AFailedReadAfterASuccess_ProducesAStaleValue()
    {
        // The value was read earlier and has not been refreshed. It is
        // kept — a transient failure should not blank a working reading —
        // but it is marked stale so it is not presented as current.
        var state = new ScalarCollectorState();
        state.ReportSuccess(Metric, 1024, Unit, Source, ReadAt);
        state.ReportNoValue(Metric, Unit, Source);

        var measurement = state.ToMeasurement(Metric);

        Assert.Equal(MeasurementStatus.Stale, measurement.Status);
    }

    [Fact]
    public void AStaleValue_KeepsTheTimeItWasRead()
    {
        // The timestamp must be the time of the read, not the time the
        // payload was built. A stale value stamped with the publish time
        // would claim to be current.
        var state = new ScalarCollectorState();
        state.ReportSuccess(Metric, 1024, Unit, Source, ReadAt);
        state.ReportNoValue(Metric, Unit, Source);

        var measurement = state.ToMeasurement(Metric);

        Assert.Equal(ReadAt, measurement.LastSuccessfulUpdate);
    }

    [Fact]
    public void AStaleValue_DoesNotExposeItsValue()
    {
        // A stale reading must not be shown as a number: the widget shows
        // "stale" in its place. The value is deliberately not carried into
        // the measurement the presenter formats.
        var state = new ScalarCollectorState();
        state.ReportSuccess(Metric, 1024, Unit, Source, ReadAt);
        state.ReportNoValue(Metric, Unit, Source);

        var measurement = state.ToMeasurement(Metric);

        Assert.Null(measurement.Value);
    }

    [Fact]
    public void ARecoveredRead_ProducesAFreshAvailableValue()
    {
        // A collector that failed and then recovered must not stay stale.
        var state = new ScalarCollectorState();
        state.ReportSuccess(Metric, 1024, Unit, Source, ReadAt);
        state.ReportNoValue(Metric, Unit, Source);
        state.ReportSuccess(Metric, 2048, Unit, Source, ReadAt.AddSeconds(5));

        var measurement = state.ToMeasurement(Metric);

        Assert.Equal(2048, measurement.Value);
        Assert.Equal(MeasurementStatus.Available, measurement.Status);
        Assert.Equal(ReadAt.AddSeconds(5), measurement.LastSuccessfulUpdate);
    }

    [Fact]
    public void EachMetric_IsTrackedIndependently()
    {
        // The memory collector publishes three metrics. One of them
        // failing must not affect the others.
        var state = new ScalarCollectorState();
        state.ReportSuccess("ram-used", 1024, Unit, Source, ReadAt);
        state.ReportSuccess("ram-total", 2048, Unit, Source, ReadAt);
        state.ReportNoValue("ram-percent", "%", Source);

        Assert.Equal(MeasurementStatus.Available, state.ToMeasurement("ram-used").Status);
        Assert.Equal(MeasurementStatus.Available, state.ToMeasurement("ram-total").Status);
        Assert.Equal(MeasurementStatus.Failed, state.ToMeasurement("ram-percent").Status);
    }

    [Fact]
    public void AMetricThatWasNeverReported_IsFailedRatherThanZero()
    {
        var state = new ScalarCollectorState();
        state.ReportSuccess("ram-used", 1024, Unit, Source, ReadAt);

        var measurement = state.ToMeasurement("ram-total");

        Assert.Null(measurement.Value);
        Assert.Equal(MeasurementStatus.Failed, measurement.Status);
    }

    [Fact]
    public void TheLastSuccessfulUpdate_IsTheTimeOfTheRead()
    {
        var state = new ScalarCollectorState();
        state.ReportSuccess(Metric, 1024, Unit, Source, ReadAt);

        Assert.Equal(ReadAt, state.LastSuccessfulUpdate);
    }

    [Fact]
    public void TheLastSuccessfulUpdate_IsNullBeforeAnySuccess()
    {
        var state = new ScalarCollectorState();
        state.ReportNoValue(Metric, Unit, Source);

        Assert.Null(state.LastSuccessfulUpdate);
    }
}

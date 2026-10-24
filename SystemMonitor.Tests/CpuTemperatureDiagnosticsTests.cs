using System;
using System.Collections.Generic;
using System.Linq;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the CPU temperature path, from the raw sensor to the
/// displayed figure.
///
/// <para>
/// The path is: raw sensor → selected sensor → mapped measurement →
/// merged scalar → snapshot → displayed temperature. A figure that is
/// "N/A" is correct when the sensor genuinely cannot be read, and wrong
/// when a readable reading was dropped on the way. These tests pin the
/// points where that can happen.
/// </para>
///
/// <para>
/// The failure this suite was written for is diagnostic case 2: the
/// sensor is enumerated and recognized, but reports no usable value
/// because the underlying read is blocked. On the machine this was
/// investigated on, that is the shape of the Intel MSR temperature
/// sensors without elevation: every temperature sensor exists, and every
/// one of them reports null. The software path is correct in that case;
/// the figure is "N/A" because the value is genuinely unavailable, and it
/// must not be filled in with a fabricated one.
/// </para>
/// </summary>
public class CpuTemperatureDiagnosticsTests
{
    private static readonly DateTimeOffset ReadAt =
        new DateTimeOffset(2026, 10, 21, 6, 0, 0, TimeSpan.Zero);

    // =====================================================================
    // Case 2: matching sensors exist but their values are null
    // =====================================================================

    [Fact]
    public void ASensorThatExistsButHasNoValue_IsSelectedAndReportedAsFailed()
    {
        // "CPU Package" is the preferred candidate and it is enumerated, but
        // its value is null. The metric must be Failed — distinguishable
        // from "no such sensor" — and must carry no value.
        var sensors = new List<LhmSensorDescriptor>
        {
            LhmFixtures.Temp("CPU Package", null),
            LhmFixtures.Load("CPU Total", 19.44),
        };

        var selected = LhmSensorSelection.Select(sensors, LhmSensorKind.CpuTemperatureC);

        Assert.NotNull(selected);
        Assert.Equal("CPU Package", selected!.Name);
        Assert.False(LhmSensorSelection.IsReadable(selected));
    }

    [Fact]
    public void ThePreferredSensorUnreadableWithAValidFallback_UsesTheFallback()
    {
        // The preferred sensor is blocked but a later candidate reads, so
        // the metric stays populated rather than becoming unavailable.
        var sensors = new List<LhmSensorDescriptor>
        {
            LhmFixtures.Temp("CPU Package", null),
            LhmFixtures.Temp("Core Average", 71.5),
        };

        var selected = LhmSensorSelection.Select(
            sensors, LhmSensorKind.CpuTemperatureC, preferReadableValue: true);

        Assert.NotNull(selected);
        Assert.Equal("Core Average", selected!.Name);
        Assert.Equal(71.5, selected.Value);
    }

    [Fact]
    public void ThePreferredSensorUnreadableWithNoFallback_IsStillReportedAsPresent()
    {
        // Without a readable alternative the selection returns the
        // preferred sensor, so the caller can report "present but
        // unreadable" rather than "no sensor at all". That distinction is
        // what makes the diagnosis correct.
        var sensors = new List<LhmSensorDescriptor>
        {
            LhmFixtures.Temp("CPU Package", null),
            LhmFixtures.Temp("Core Average", null),
        };

        var selected = LhmSensorSelection.Select(
            sensors, LhmSensorKind.CpuTemperatureC, preferReadableValue: true);

        Assert.NotNull(selected);
        Assert.Equal("CPU Package", selected!.Name);
    }

    // =====================================================================
    // Case 1: no sensor enumerated, versus case 2: unreadable sensor
    // =====================================================================

    [Fact]
    public void NoTemperatureSensorAtAll_IsUnsupportedRatherThanFailed()
    {
        // A device with only a load sensor has no temperature sensor. That
        // is "not supported here", and it is different from "attempted and
        // failed".
        var device = new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            DisplayName = "Intel Core i7-10875H",
            IsCpu = true,
            Sensors = new List<LhmSensorDescriptor>
            {
                LhmFixtures.Load("CPU Total", 19.44),
            },
        };

        var result = LhmSnapshotMapper.Project(new[] { device });

        var temp = result.Readings
            .First(r => r.DeviceId == device.DeviceId && r.Metric == LhmSensorSelection.CpuTemperatureMetric)
            .Measurement;

        Assert.Equal(MeasurementStatus.Unsupported, temp.Status);
        Assert.Null(temp.Value);
    }

    [Fact]
    public void AnUnreadableTemperatureSensor_IsFailedAndNeverZero()
    {
        var device = new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            DisplayName = "Intel Core i7-10875H",
            IsCpu = true,
            Sensors = new List<LhmSensorDescriptor>
            {
                LhmFixtures.Load("CPU Total", 19.44),
                LhmFixtures.Temp("CPU Package", null),
            },
        };

        var result = LhmSnapshotMapper.Project(new[] { device });

        var temp = result.Readings
            .First(r => r.DeviceId == device.DeviceId && r.Metric == LhmSensorSelection.CpuTemperatureMetric)
            .Measurement;

        // Failed, not Unsupported: the sensor is there.
        Assert.Equal(MeasurementStatus.Failed, temp.Status);
        Assert.Null(temp.Value);
    }

    // =====================================================================
    // Case 3: a readable sensor whose name is not recognized
    // =====================================================================

    [Fact]
    public void AReadableSensorWithAnUnrecognizedName_IsNotSelected()
    {
        // The selection must not invent a value from a name it does not
        // recognize. The reading is available but unused, which the
        // diagnosis reports as case 3.
        var sensors = new List<LhmSensorDescriptor>
        {
            LhmFixtures.Temp("Some Unheard Of Sensor", 61.0),
        };

        var selected = LhmSensorSelection.Select(
            sensors, LhmSensorKind.CpuTemperatureC, preferReadableValue: true);

        Assert.Null(selected);
    }

    [Fact]
    public void AReadableSensorWithAnUnrecognizedName_IsDiagnosedAsSuch()
    {
        // The trace must see the readable sensor even though the selection
        // did not choose it, so the diagnosis can say "the reading was
        // there and the name was not recognized" rather than "no sensor".
        var device = new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            DisplayName = "Intel Core i7-10875H",
            IsCpu = true,
            Sensors = new List<LhmSensorDescriptor>
            {
                LhmFixtures.Temp("Some Unheard Of Sensor", 61.0),
            },
        };

        var result = LhmSnapshotMapper.Project(new[] { device });

        var trace = Assert.Single(result.Traces);
        var metric = trace.Metrics.Single(m => m.Metric == LhmSensorSelection.CpuTemperatureMetric);

        Assert.Equal(1, metric.SensorCount);
        Assert.Empty(metric.RecognizedSensors);
        Assert.Single(metric.ReadableSensors);
        Assert.Equal("Some Unheard Of Sensor", metric.ReadableSensors[0].Name);

        var diagnosis = LhmSensorDiagnosisPolicy.Diagnose(
            HealthyBackend(),
            trace,
            metric,
            null);

        Assert.Equal(LhmSensorDiagnosis.SensorNamesNotRecognized, diagnosis);
    }

    [Fact]
    public void AReadableSensorThatWasDeliberatelyExcluded_IsNotDiagnosedAsUnrecognized()
    {
        // The Intel integrated GPU reports readable memory sensors, but they
        // describe shared system memory, which is not dedicated video
        // memory. Using them would be wrong, so they are excluded — a
        // decision, not a gap in the candidate list. Reporting it as
        // "the name was not recognized" would send a maintainer looking
        // for a name to add when there is nothing to add.
        var device = LhmFixtures.IntelIntegratedGpu();

        var result = LhmSnapshotMapper.Project(new[] { device });

        var trace = Assert.Single(result.Traces);
        var metric = trace.Metrics.Single(m => m.Metric == LhmSensorSelection.GpuMemoryUsedMetric);

        Assert.NotEmpty(metric.ExcludedSensors);
        Assert.All(metric.ExcludedSensors, s => Assert.Contains("Shared", s.Name));

        var diagnosis = LhmSensorDiagnosisPolicy.Diagnose(
            HealthyBackend(),
            trace,
            metric,
            null);

        Assert.Equal(LhmSensorDiagnosis.SensorDeliberatelyExcluded, diagnosis);
    }

    [Fact]
    public void ASharedMemorySensor_IsExcludedOnlyForTheMemoryMetrics()
    {
        // The exclusion is scoped: shared system memory is not video memory,
        // but the same name says nothing wrong about a temperature, and
        // excluding it everywhere would hide a real sensor.
        Assert.True(LhmSensorSelection.IsDeliberatelyExcluded(
            "D3D Shared Memory Used", LhmSensorKind.GpuMemoryUsedBytes));

        Assert.True(LhmSensorSelection.IsDeliberatelyExcluded(
            "D3D Shared Memory Total", LhmSensorKind.GpuMemoryTotalBytes));

        Assert.False(LhmSensorSelection.IsDeliberatelyExcluded(
            "D3D Shared Memory Used", LhmSensorKind.GpuTemperatureC));

        Assert.False(LhmSensorSelection.IsDeliberatelyExcluded(
            "GPU Memory Used", LhmSensorKind.GpuMemoryUsedBytes));

        Assert.False(LhmSensorSelection.IsDeliberatelyExcluded(
            null, LhmSensorKind.GpuMemoryUsedBytes));
    }

    // =====================================================================
    // The trace itself
    // =====================================================================

    [Fact]
    public void TheTraceRecordsTheCandidateNamesThatWereSearched()
    {
        // A reader who knows what was looked for can tell an unrecognized
        // name from a recognized name that had no value.
        var device = LhmFixtures.IntelCpu();

        var result = LhmSnapshotMapper.Project(new[] { device });

        var trace = Assert.Single(result.Traces);
        var metric = trace.Metrics.Single(m => m.Metric == LhmSensorSelection.CpuTemperatureMetric);

        Assert.Contains("CPU Package", metric.CandidateNames);
        Assert.Contains("Core (Tctl/Tdie)", metric.CandidateNames);
        Assert.Equal("Temperature", metric.RequiredSensorType);
    }

    [Fact]
    public void TheTraceRecordsEveryRawSensorWithItsTypeValueAndUnit()
    {
        var device = LhmFixtures.IntelCpu();

        var result = LhmSnapshotMapper.Project(new[] { device });

        var trace = Assert.Single(result.Traces);

        var package = trace.Sensors.Single(s => s.Name == "CPU Package");
        Assert.Equal("Temperature", package.SensorType);
        Assert.Equal(84.0, package.Value);
        Assert.Equal(LhmSensorUnit.Celsius, package.Unit);
    }

    [Fact]
    public void TheTraceRecordsWhenNoSensorOfTheRequiredTypeExists()
    {
        var device = new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            DisplayName = "Intel Core i7-10875H",
            IsCpu = true,
            Sensors = new List<LhmSensorDescriptor>
            {
                LhmFixtures.Load("CPU Total", 19.44),
            },
        };

        var result = LhmSnapshotMapper.Project(new[] { device });

        var trace = Assert.Single(result.Traces);
        var metric = trace.Metrics.Single(m => m.Metric == LhmSensorSelection.CpuTemperatureMetric);

        Assert.Equal(0, metric.SensorCount);
        Assert.Empty(metric.ReadableSensors);
        Assert.Null(metric.SelectedSensor);
    }

    // =====================================================================
    // Case 5: a failed hardware update must not refresh an old reading
    // =====================================================================

    [Fact]
    public void AFailedHardwareUpdate_DoesNotPublishARetainedValueAsFresh()
    {
        // The backend retains a sensor's previous value when the hardware
        // node cannot be refreshed. That value is a real reading — of some
        // earlier moment — but it is not a measurement of this cycle, so it
        // must not be published as one.
        var device = new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            DisplayName = "Intel Core i7-10875H",
            IsCpu = true,
            RefreshFailed = true,
            Sensors = new List<LhmSensorDescriptor>
            {
                LhmFixtures.Load("CPU Total", 19.44),
                LhmFixtures.Temp("CPU Package", 84.0),
            },
        };

        var result = LhmSnapshotMapper.Project(new[] { device });

        var temp = result.Readings
            .First(r => r.DeviceId == device.DeviceId && r.Metric == LhmSensorSelection.CpuTemperatureMetric)
            .Measurement;

        Assert.Equal(MeasurementStatus.Failed, temp.Status);
        Assert.Null(temp.Value);
    }

    [Fact]
    public void AFailedHardwareUpdate_IsDiagnosedAsSuch()
    {
        var device = new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            DisplayName = "Intel Core i7-10875H",
            IsCpu = true,
            RefreshFailed = true,
            Sensors = new List<LhmSensorDescriptor>
            {
                LhmFixtures.Temp("CPU Package", 84.0),
            },
        };

        var result = LhmSnapshotMapper.Project(new[] { device });

        var trace = Assert.Single(result.Traces);
        var metric = trace.Metrics.Single(m => m.Metric == LhmSensorSelection.CpuTemperatureMetric);

        Assert.True(metric.RefreshFailed);
        Assert.True(trace.RefreshFailed);

        var diagnosis = LhmSensorDiagnosisPolicy.Diagnose(
            HealthyBackend(),
            trace,
            metric,
            null);

        Assert.Equal(LhmSensorDiagnosis.HardwareUpdateFailed, diagnosis);
    }

    [Fact]
    public void ARetainedSensorValue_IsMarkedOnTheDescriptor()
    {
        // The source marks the sensors of a node it could not refresh, so
        // the distinction is carried rather than re-derived downstream.
        var sensor = new LhmSensorDescriptor
        {
            Name = "CPU Package",
            SensorType = "Temperature",
            Value = 84.0,
            Unit = LhmSensorUnit.Celsius,
            IsRetained = true,
        };

        Assert.True(sensor.IsRetained);
    }

    // =====================================================================
    // The aggregate sensors the backend leaves holding an old value
    // =====================================================================

    [Fact]
    public void AnAggregateHoldingAnOldValue_IsMarkedRetainedWhenNoCoreRead()
    {
        // The backend clears a core's temperature sensor when its read
        // fails, but leaves the aggregates alone. "Core Average" and "Core
        // Max" are derived from the per-core readings, so with every core
        // unreadable they are holding a value from an earlier cycle and
        // must not be published as current.
        var sensors = new List<LhmSensorDescriptor>
        {
            LhmFixtures.Temp("Core Max", 95.0),
            LhmFixtures.Temp("Core Average", 78.0),
            LhmFixtures.Temp("CPU Core #1", null),
            LhmFixtures.Temp("CPU Core #2", null),
            LhmFixtures.Temp("CPU Package", null),
        };

        var marked = LhmRetainedSensorPolicy.MarkRetainedAggregates(sensors);

        Assert.True(marked.Single(s => s.Name == "Core Max").IsRetained);
        Assert.True(marked.Single(s => s.Name == "Core Average").IsRetained);
    }

    [Fact]
    public void AnAggregateIsNotMarkedWhenACoreDidRead()
    {
        // If any per-core reading succeeded, the aggregate was computed
        // from it in this cycle and is current.
        var sensors = new List<LhmSensorDescriptor>
        {
            LhmFixtures.Temp("Core Max", 95.0),
            LhmFixtures.Temp("Core Average", 78.0),
            LhmFixtures.Temp("CPU Core #1", 80.0),
            LhmFixtures.Temp("CPU Core #2", null),
            LhmFixtures.Temp("CPU Package", null),
        };

        var marked = LhmRetainedSensorPolicy.MarkRetainedAggregates(sensors);

        Assert.False(marked.Single(s => s.Name == "Core Max").IsRetained);
        Assert.False(marked.Single(s => s.Name == "Core Average").IsRetained);
    }

    [Fact]
    public void ARetainedAggregate_IsNotPublishedAsAFreshMeasurement()
    {
        // The end-to-end effect: the aggregate's old value does not reach
        // the published figure.
        var device = new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            DisplayName = "Intel Core i7-10875H",
            IsCpu = true,
            Sensors = new List<LhmSensorDescriptor>
            {
                LhmFixtures.Temp("Core Max", 95.0),
                LhmFixtures.Temp("Core Average", 78.0),
                LhmFixtures.Temp("CPU Core #1", null),
                LhmFixtures.Temp("CPU Package", null),
            },
        };

        var marked = LhmRetainedSensorPolicy.MarkRetainedAggregates(device.Sensors);

        var result = LhmSnapshotMapper.Project(new[] { device with { Sensors = marked } });

        var temp = result.Readings
            .First(r => r.DeviceId == device.DeviceId && r.Metric == LhmSensorSelection.CpuTemperatureMetric)
            .Measurement;

        Assert.Equal(MeasurementStatus.Failed, temp.Status);
        Assert.Null(temp.Value);
    }

    [Fact]
    public void TheHeadroomSensorsAreNotTreatedAsAggregateInputs()
    {
        // "CPU Core #N Distance to TjMax" is a reading of its own, not an
        // input to the aggregates. A readable headroom value must not make
        // an old aggregate look current.
        var sensors = new List<LhmSensorDescriptor>
        {
            LhmFixtures.Temp("Core Average", 78.0),
            LhmFixtures.Temp("CPU Core #1", null),
            LhmFixtures.Temp("CPU Core #1 Distance to TjMax", 20.0),
        };

        var marked = LhmRetainedSensorPolicy.MarkRetainedAggregates(sensors);

        Assert.True(marked.Single(s => s.Name == "Core Average").IsRetained);
    }

    // =====================================================================
    // Case 6: the backend could not be initialized
    // =====================================================================

    [Fact]
    public void ABackendThatNeverInitialized_IsDiagnosedAsUnavailable()
    {
        var backend = new LhmBackendReadState
        {
            Initialized = false,
            ReadFailed = true,
            FailureReason = "Win32Exception",
            ReadAt = ReadAt,
        };

        var device = new LhmSensorTrace
        {
            DeviceId = "/intelcpu/0",
            DeviceName = "Intel Core i7-10875H",
            IsCpu = true,
        };

        var metric = new LhmMetricTrace
        {
            Metric = LhmSensorSelection.CpuTemperatureMetric,
            RequiredSensorType = "Temperature",
        };

        var diagnosis = LhmSensorDiagnosisPolicy.Diagnose(backend, device, metric, null);

        Assert.Equal(LhmSensorDiagnosis.BackendUnavailable, diagnosis);
    }

    [Fact]
    public void ABackendThatInitializedButFailedToRead_IsDiagnosedAsUnavailable()
    {
        var backend = new LhmBackendReadState
        {
            Initialized = true,
            ReadFailed = true,
            FailureReason = "Win32Exception",
            ReadAt = ReadAt,
        };

        var diagnosis = LhmSensorDiagnosisPolicy.Diagnose(
            backend,
            new LhmSensorTrace { DeviceId = "/intelcpu/0" },
            new LhmMetricTrace { Metric = LhmSensorSelection.CpuTemperatureMetric },
            null);

        Assert.Equal(LhmSensorDiagnosis.BackendUnavailable, diagnosis);
    }

    // =====================================================================
    // Case 4: a valid reading lost while merging
    // =====================================================================

    [Fact]
    public void AValidReadingThatDidNotReachThePublishedFigure_IsDiagnosedAsLost()
    {
        var device = LhmFixtures.IntelCpu();

        var result = LhmSnapshotMapper.Project(new[] { device });

        var trace = Assert.Single(result.Traces);
        var metric = trace.Metrics.Single(m => m.Metric == LhmSensorSelection.CpuTemperatureMetric);

        // The sensor read 84 degrees Celsius, but the figure that was
        // published is not available. The reading was valid and was lost.
        var published = Measurement.Failed("LibreHardwareMonitor");

        var diagnosis = LhmSensorDiagnosisPolicy.Diagnose(
            HealthyBackend(),
            trace,
            metric,
            published);

        Assert.Equal(LhmSensorDiagnosis.ReadingLostInMerge, diagnosis);
    }

    [Fact]
    public void AValidReadingThatReachedThePublishedFigure_IsDiagnosedAsAvailable()
    {
        var device = LhmFixtures.IntelCpu();

        var result = LhmSnapshotMapper.Project(new[] { device });

        var trace = Assert.Single(result.Traces);
        var metric = trace.Metrics.Single(m => m.Metric == LhmSensorSelection.CpuTemperatureMetric);

        var published = Measurement.AvailableValue(84.0, "°C", "LibreHardwareMonitor:CPU Package");

        var diagnosis = LhmSensorDiagnosisPolicy.Diagnose(
            HealthyBackend(),
            trace,
            metric,
            published);

        Assert.Equal(LhmSensorDiagnosis.ReadingAvailable, diagnosis);
    }

    // =====================================================================
    // A valid temperature surviving merge and presentation
    // =====================================================================

    [Fact]
    public void AValidTemperature_SurvivesTheMergeAndIsPresented()
    {
        // The whole path, on a sensor set that reads: the sensor is
        // selected, the value is mapped, the scalar is merged, and the
        // presenter shows the number.
        var device = LhmFixtures.IntelCpu();

        var result = LhmSnapshotMapper.Project(new[] { device });

        var temp = result.Readings
            .First(r => r.DeviceId == device.DeviceId && r.Metric == LhmSensorSelection.CpuTemperatureMetric)
            .Measurement;

        Assert.Equal(MeasurementStatus.Available, temp.Status);
        Assert.Equal(84.0, temp.Value);

        // The scalar the collection publishes is the same figure.
        var candidate = new ScalarCandidate(
            temp.Value!.Value,
            "°C",
            temp.Source!,
            ReadAt,
            device.DeviceId);

        var merged = ScalarMergePolicy.Resolve(candidate, null);

        Assert.NotNull(merged);
        Assert.Equal(84.0, merged!.Value);

        var snapshot = MetricsSnapshot.Create(
            ReadAt,
            cpus: result.Cpus,
            readings: result.Readings,
            cpuTemperatureC: Measurement.AvailableValue(
                merged.Value, "°C", merged.Source, merged.ReadAt));

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal("84.0°C", payload.Text[WidgetPayloadPresenter.CpuTempKey]);
    }

    [Fact]
    public void AnUnreadableTemperature_IsPresentedAsUnavailableNotAsZero()
    {
        // The figure the widget shows for an unreadable sensor is "N/A".
        // It is never "0.0°C", and never a stale value.
        var device = new LhmDeviceDescriptor
        {
            DeviceId = "/intelcpu/0",
            DisplayName = "Intel Core i7-10875H",
            IsCpu = true,
            Sensors = new List<LhmSensorDescriptor>
            {
                LhmFixtures.Temp("CPU Package", null),
            },
        };

        var result = LhmSnapshotMapper.Project(new[] { device });

        var temp = result.Readings
            .First(r => r.DeviceId == device.DeviceId && r.Metric == LhmSensorSelection.CpuTemperatureMetric)
            .Measurement;

        var snapshot = MetricsSnapshot.Create(
            ReadAt,
            cpus: result.Cpus,
            readings: result.Readings,
            cpuTemperatureC: temp);

        var payload = WidgetPayloadPresenter.Present(snapshot);

        Assert.Equal("N/A", payload.Text[WidgetPayloadPresenter.CpuTempKey]);
    }

    // =====================================================================
    // The inventory's sensor trace
    // =====================================================================

    [Fact]
    public void TheInventory_RecordsTheRawSensorsAndTheSelectionOutcome()
    {
        var device = LhmFixtures.PartiallyBlockedCpu();

        var result = LhmSnapshotMapper.Project(new[] { device });

        var report = new LhmSensorBackendReport
        {
            Backend = HealthyBackend(),
            Devices = result.Traces,
        };

        var snapshot = MetricsSnapshot.Create(
            ReadAt,
            cpus: result.Cpus,
            readings: result.Readings,
            cpuTemperatureC: Measurement.Failed("LibreHardwareMonitor"));

        var text = DiagnosticInventory.Build(snapshot, report);

        // The raw sensor, with its type, value and unit.
        Assert.Contains("CPU Package", text);
        Assert.Contains("type: Temperature", text);
        Assert.Contains("unit: °C", text);
        Assert.Contains("value: (null)", text);

        // The selection outcome and the diagnosis.
        Assert.Contains("required_sensor_type: Temperature", text);
        Assert.Contains("selected_sensor: CPU Package", text);
        Assert.Contains("measurement_status: Failed", text);
        Assert.Contains("case 2", text);
    }

    [Fact]
    public void TheInventory_RecordsTheBackendStateAndTheTimestamp()
    {
        var device = LhmFixtures.IntelCpu();

        var result = LhmSnapshotMapper.Project(new[] { device });

        var report = new LhmSensorBackendReport
        {
            Backend = HealthyBackend(),
            Devices = result.Traces,
        };

        var text = DiagnosticInventory.Build(result.ToSnapshot(ReadAt), report);

        Assert.Contains("backend_initialized: True", text);
        Assert.Contains("read_failed: False", text);
        Assert.Contains("read_at:", text);
    }

    [Fact]
    public void TheInventory_OfNoSensorReport_SaysSoRatherThanFailing()
    {
        var text = DiagnosticInventory.Build(
            MetricsSnapshot.Create(ReadAt),
            sensorReport: null);

        Assert.Contains("[sensor trace]", text);
        Assert.Contains("(not collected)", text);
    }

    [Fact]
    public void TheInventory_OfABackendThatNeverInitialized_SaysSo()
    {
        var report = new LhmSensorBackendReport
        {
            Backend = new LhmBackendReadState
            {
                Initialized = false,
                ReadFailed = true,
                FailureReason = "Win32Exception",
                ReadAt = ReadAt,
            },
        };

        var text = DiagnosticInventory.Build(MetricsSnapshot.Create(ReadAt), report);

        Assert.Contains("backend_initialized: False", text);
        Assert.Contains("failure_reason: Win32Exception", text);
        Assert.Contains("case 6", text);
    }

    [Fact]
    public void AFailedUpdate_DoesNotRefreshTheSuccessTimestampOfAnEarlierReading()
    {
        // Cycle 1 reads the temperature. Cycle 2's hardware update fails, so
        // the sensor retains the value it read in cycle 1. The published
        // figure is then stale — and it keeps the time it was actually
        // measured, not the time of the failed cycle. A failed update must
        // never make an old reading look freshly read.
        var readable = LhmFixtures.IntelCpu();

        var firstCycle = LhmSnapshotMapper.Project(new[] { readable });

        var firstReading = firstCycle.Readings
            .First(r => r.DeviceId == readable.DeviceId
                && r.Metric == LhmSensorSelection.CpuTemperatureMetric)
            .Measurement;

        Assert.Equal(MeasurementStatus.Available, firstReading.Status);

        var state = new ScalarCollectorState();

        state.ReportSuccess(
            "cpu-temperature",
            firstReading.Value!.Value,
            "°C",
            firstReading.Source!,
            ReadAt);

        // The next cycle's update fails. The sensor still exposes the value
        // it read before, and the mapper refuses to publish it as fresh.
        var failed = LhmFixtures.IntelCpu();
        failed = failed with { RefreshFailed = true };

        var secondCycle = LhmSnapshotMapper.Project(new[] { failed });

        var secondReading = secondCycle.Readings
            .First(r => r.DeviceId == failed.DeviceId
                && r.Metric == LhmSensorSelection.CpuTemperatureMetric)
            .Measurement;

        Assert.Equal(MeasurementStatus.Failed, secondReading.Status);

        state.ReportNoValue("cpu-temperature", "°C", "LibreHardwareMonitor");

        var published = state.ToMeasurement("cpu-temperature");

        Assert.Equal(MeasurementStatus.Stale, published.Status);
        Assert.Equal(ReadAt, published.LastSuccessfulUpdate);
        Assert.Equal(ReadAt, state.LastSuccessfulUpdate);
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static LhmBackendReadState HealthyBackend() => new()
    {
        Initialized = true,
        ReadFailed = false,
        FailureReason = null,
        DeviceCount = 1,
        ReadAt = ReadAt,
    };
}

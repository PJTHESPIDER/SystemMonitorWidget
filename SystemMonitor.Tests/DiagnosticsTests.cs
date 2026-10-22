using System;
using System.Collections.Generic;
using System.Linq;
using System_Monitor.Models;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

/// <summary>
/// Tests for the diagnostic log's rate limiting, the scrubbing of
/// machine identifiers, and the explanations for unavailable sensors.
/// </summary>
public class DiagnosticsTests
{
    // ------------------------------------------------------------------
    // Rate limiting
    // ------------------------------------------------------------------

    [Fact]
    public void RepeatedEvents_AreCollapsedAfterTheRepeatLimit()
    {
        var sink = new InMemoryDiagnosticSink();
        var log = new DiagnosticLog { RepeatLimit = 3 };
        log.AddSink(sink);

        for (var i = 0; i < 10; i++)
            log.Warning("LHM", "read", "failed", detail: "attempt");

        // The first three occurrences are reported in full. The fourth is
        // reported as a summary, and the rest are suppressed until the
        // summary interval elapses.
        var events = sink.Events;
        Assert.Equal(4, events.Count);

        Assert.Equal("failed", events[0].Status);
        Assert.Equal("attempt", events[0].Detail);
        Assert.Equal("attempt", events[1].Detail);
        Assert.Equal("attempt", events[2].Detail);

        // The fourth line says how many were suppressed rather than
        // repeating the event.
        Assert.Contains("suppressed", events[3].Detail);
    }

    [Fact]
    public void DifferentEvents_DoNotSuppressEachOther()
    {
        var sink = new InMemoryDiagnosticSink();
        var log = new DiagnosticLog { RepeatLimit = 1 };
        log.AddSink(sink);

        log.Warning("LHM", "read", "failed");
        log.Warning("LHM", "read", "failed");
        log.Error("nvidia-smi", "query", "failed");
        log.Error("nvidia-smi", "query", "failed");

        // The two backends are tracked independently.
        var backends = sink.Events.Select(e => e.Backend).Distinct().ToList();
        Assert.Equal(2, backends.Count);
    }

    [Fact]
    public void EventsBelowTheMinimumLevel_AreNotRecorded()
    {
        var sink = new InMemoryDiagnosticSink();
        var log = new DiagnosticLog { MinimumLevel = DiagnosticLevel.Warning };
        log.AddSink(sink);

        log.Info("LHM", "read", "ok");
        log.Debug("LHM", "read", "ok");
        log.Warning("LHM", "read", "failed");
        log.Error("LHM", "read", "failed");

        Assert.Equal(2, sink.Events.Count);
        Assert.All(sink.Events, e => Assert.True(e.Level >= DiagnosticLevel.Warning));
    }

    [Fact]
    public void ADisabledLog_RecordsNothing()
    {
        var sink = new InMemoryDiagnosticSink();
        var log = new DiagnosticLog { Enabled = false };
        log.AddSink(sink);

        log.Error("LHM", "read", "failed");

        Assert.Empty(sink.Events);
    }

    [Fact]
    public void ResetRateLimiting_MakesTheNextEventReportInFullAgain()
    {
        var sink = new InMemoryDiagnosticSink();
        var log = new DiagnosticLog { RepeatLimit = 1 };
        log.AddSink(sink);

        log.Warning("LHM", "read", "failed", detail: "the reason");
        log.Warning("LHM", "read", "failed", detail: "the reason");
        log.Warning("LHM", "read", "failed", detail: "the reason");

        log.ResetRateLimiting();

        log.Warning("LHM", "read", "failed", detail: "the reason");

        // The last event is reported in full, not as a suppression summary.
        Assert.Equal("the reason", sink.Events.Last().Detail);
    }

    [Fact]
    public void ARepeatedEvent_IsSummarizedAgainAfterTheSummaryInterval()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var sink = new InMemoryDiagnosticSink();
        var log = new DiagnosticLog(time) { RepeatLimit = 1, SummaryInterval = TimeSpan.FromMinutes(10) };
        log.AddSink(sink);

        log.Warning("LHM", "read", "failed");
        log.Warning("LHM", "read", "failed");
        log.Warning("LHM", "read", "failed");

        // Nothing new: the interval has not elapsed.
        Assert.Equal(2, sink.Events.Count);

        time.Advance(TimeSpan.FromMinutes(11));

        log.Warning("LHM", "read", "failed");

        Assert.Equal(3, sink.Events.Count);
        Assert.Contains("suppressed", sink.Events[2].Detail);
    }

    // ------------------------------------------------------------------
    // Event content
    // ------------------------------------------------------------------

    [Fact]
    public void AnEvent_RecordsTheBackendOperationStatusAndElapsedTime()
    {
        var sink = new InMemoryDiagnosticSink();
        var log = new DiagnosticLog();
        log.AddSink(sink);

        log.Info("LHM", "read", "ok", elapsed: TimeSpan.FromMilliseconds(12.5), detail: "devices=2");

        var e = Assert.Single(sink.Events);
        Assert.Equal("LHM", e.Backend);
        Assert.Equal("read", e.Operation);
        Assert.Equal("ok", e.Status);
        Assert.Equal(TimeSpan.FromMilliseconds(12.5), e.Elapsed);
        Assert.Equal("devices=2", e.Detail);
    }

    [Fact]
    public void TheFormattedLine_ContainsTheBackendOperationStatusAndElapsedTime()
    {
        var line = new DiagnosticEvent(
            DateTimeOffset.UtcNow,
            DiagnosticLevel.Warning,
            "LHM",
            "read",
            "failed",
            TimeSpan.FromMilliseconds(3),
            "detail").Format();

        Assert.Contains("LHM", line);
        Assert.Contains("read", line);
        Assert.Contains("failed", line);
        Assert.Contains("3.0ms", line);
        Assert.Contains("WARNING", line);
    }

    // ------------------------------------------------------------------
    // Scrubbing
    // ------------------------------------------------------------------

    [Fact]
    public void Scrub_RemovesWindowsPaths()
    {
        var scrubbed = DiagnosticDetail.Scrub("could not open C:\\Users\\someone\\AppData\\Local\\thing.dll");

        Assert.DoesNotContain("someone", scrubbed);
        Assert.Contains("<path>", scrubbed);
    }

    [Fact]
    public void Scrub_RemovesQuotedPaths()
    {
        var scrubbed = DiagnosticDetail.Scrub("failed to open \"D:\\Program Files\\Vendor\\tool.exe\"");

        Assert.DoesNotContain("Vendor", scrubbed);
        Assert.Contains("<path>", scrubbed);
    }

    [Fact]
    public void Scrub_TruncatesVeryLongText()
    {
        var scrubbed = DiagnosticDetail.Scrub(new string('x', 500));

        Assert.True(scrubbed.Length <= DiagnosticDetail.MaxLength);
    }

    [Fact]
    public void Describe_KeepsTheExceptionTypeButNotTheMessage()
    {
        var described = DiagnosticDetail.Describe(
            new InvalidOperationException("could not read C:\\secret\\path\\file.txt"));

        // The type is what makes the failure actionable.
        Assert.Contains("InvalidOperationException", described);
        // The path is a machine identifier and does not belong in a log.
        Assert.DoesNotContain("secret", described);
    }

    [Fact]
    public void Describe_OfNull_IsEmpty()
    {
        Assert.Equal(string.Empty, DiagnosticDetail.Describe(null));
    }

    // ------------------------------------------------------------------
    // Sensor availability
    // ------------------------------------------------------------------

    [Fact]
    public void AnUnavailableSensor_IsExplainedOnce()
    {
        var sink = new InMemoryDiagnosticSink();
        var log = new DiagnosticLog();
        log.AddSink(sink);

        var availability = new SensorAvailability(log);

        availability.ReportUnavailable("gpu-temperature", SensorAvailability.Reasons.NotPresent);
        availability.ReportUnavailable("gpu-temperature", SensorAvailability.Reasons.NotPresent);
        availability.ReportUnavailable("gpu-temperature", SensorAvailability.Reasons.NotPresent);

        var events = sink.Events.Where(e => e.Operation == "gpu-temperature").ToList();

        // The explanation is given once. The repeats are not reported as
        // new problems, so the user is not alarmed again and again.
        Assert.Single(events);
        Assert.Equal("unavailable", events[0].Status);
        Assert.Equal(SensorAvailability.Reasons.NotPresent, events[0].Detail);
    }

    [Fact]
    public void AnUnavailableSensor_HasAReasonTheWidgetCanShow()
    {
        var availability = new SensorAvailability();

        availability.ReportUnavailable("gpu-temperature", SensorAvailability.Reasons.NotPresent);

        Assert.Equal(
            SensorAvailability.Reasons.NotPresent,
            availability.ReasonFor("gpu-temperature"));
    }

    [Fact]
    public void ARecoveredSensor_ClearsItsReasonAndIsReportedOnce()
    {
        var sink = new InMemoryDiagnosticSink();
        var log = new DiagnosticLog();
        log.AddSink(sink);

        var availability = new SensorAvailability(log);

        availability.ReportUnavailable("gpu-temperature", SensorAvailability.Reasons.NotPresent);
        availability.ReportAvailable("gpu-temperature");

        Assert.Null(availability.ReasonFor("gpu-temperature"));
        Assert.Contains(sink.Events, e => e.Status == "recovered");
    }

    [Fact]
    public void ARecoveredSensor_ReportsItsNextFailureInFullAgain()
    {
        var sink = new InMemoryDiagnosticSink();
        var log = new DiagnosticLog();
        log.AddSink(sink);

        var availability = new SensorAvailability(log);

        availability.ReportUnavailable("gpu-temperature", SensorAvailability.Reasons.NotPresent);
        availability.ReportAvailable("gpu-temperature");
        availability.ReportUnavailable("gpu-temperature", SensorAvailability.Reasons.ReadFailed);

        var failures = sink.Events
            .Where(e => e.Operation == "gpu-temperature" && e.Status == "unavailable")
            .ToList();

        Assert.Equal(2, failures.Count);
        Assert.Equal(SensorAvailability.Reasons.ReadFailed, failures[1].Detail);
    }

    [Fact]
    public void AnAvailableSensorThatNeverFailed_HasNoReason()
    {
        var availability = new SensorAvailability();

        availability.ReportAvailable("gpu-temperature");

        Assert.Null(availability.ReasonFor("gpu-temperature"));
    }

    // ------------------------------------------------------------------
    // The opt-in inventory export
    // ------------------------------------------------------------------

    [Fact]
    public void TheInventory_DescribesTheDevicesAndTheMetrics()
    {
        var snapshot = MetricsSnapshot.Create(
            DateTimeOffset.UtcNow,
            cpus: new[]
            {
                new HardwareDevice
                {
                    DeviceId = "/intelcpu/0",
                    DisplayName = "Intel Core i7",
                    Vendor = "Intel",
                    DeviceClass = DeviceClass.Cpu,
                },
            },
            gpus: new[]
            {
                new HardwareDevice
                {
                    DeviceId = "/gpu-nvidia/0",
                    DisplayName = "Test GPU",
                    Vendor = "NVIDIA",
                    DeviceClass = DeviceClass.Gpu,
                    GpuIntegration = GpuIntegration.Discrete,
                    PciDeviceKey = "VEN_10DE&DEV_1E91",
                    InstanceId = "instance-1",
                },
            },
            readings: new[]
            {
                new DeviceReading
                {
                    DeviceId = "/gpu-nvidia/0",
                    Metric = "LoadPercent",
                    Measurement = Measurement.AvailableValue(42, "%", "LHM"),
                },
            },
            gpuLoadPercent: Measurement.AvailableValue(42, "%", "LHM"));

        var text = DiagnosticInventory.Build(snapshot);

        Assert.Contains("Test GPU", text);
        Assert.Contains("VEN_10DE&DEV_1E91", text);
        Assert.Contains("LoadPercent", text);
        Assert.Contains("gpu_load_percent", text);
    }

    [Fact]
    public void TheInventory_OfNoSnapshot_SaysSoRatherThanFailing()
    {
        var text = DiagnosticInventory.Build(null);

        Assert.Contains("No snapshot", text);
    }

    [Fact]
    public void TheInventory_IsWrittenOnlyWhenAsked()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sm-diag-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "inventory.txt");

        try
        {
            DiagnosticInventory.Write(null, path);

            Assert.True(File.Exists(path));
            Assert.Contains("SystemMonitor diagnostic inventory", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    // ------------------------------------------------------------------
    // The opt-in switch
    // ------------------------------------------------------------------

    [Fact]
    public void TheExportSwitch_IsFoundInBothForms()
    {
        Assert.True(DiagnosticHost.TryGetExportRequest(
            new[] { "--export-diagnostics", "out.txt" }, out var path));
        Assert.Equal("out.txt", path);

        Assert.True(DiagnosticHost.TryGetExportRequest(
            new[] { "--export-diagnostics=out2.txt" }, out var path2));
        Assert.Equal("out2.txt", path2);
    }

    [Fact]
    public void WithoutTheExportSwitch_NoExportIsRequested()
    {
        Assert.False(DiagnosticHost.TryGetExportRequest(
            new[] { "-RegisterProcessAsComServer" }, out var path));
        Assert.Null(path);
    }

    [Fact]
    public void TheLogStartLine_DoesNotLeakTheLogPath()
    {
        var sink = new InMemoryDiagnosticSink();
        var log = new DiagnosticLog();
        log.AddSink(sink);

        // The log file's own path contains the user's account name, so the
        // start line reports only the file's name.
        var path = Path.Combine("C:\\Users\\someone\\AppData\\Local", "systemmonitor.log");

        DiagnosticHost.WriteLogStartLine(log, path);

        var e = Assert.Single(sink.Events);
        Assert.DoesNotContain("someone", e.Detail);
        Assert.DoesNotContain("Users", e.Detail);
        Assert.Contains("systemmonitor.log", e.Detail);
    }

    [Fact]
    public void OptingIn_RecordsTheSuccessfulCollectionEventsToo()
    {
        // A user who opts in wants to know what the widget is doing, not
        // only what went wrong. The successful collection events are
        // recorded at Debug level, so opting in has to lower the threshold.
        var log = new DiagnosticLog { MinimumLevel = DiagnosticLevel.Info };
        var sink = new InMemoryDiagnosticSink();
        log.AddSink(sink);

        log.Debug("collector", "cpu-load", "ok");
        Assert.Empty(sink.Events);

        log.MinimumLevel = DiagnosticLevel.Debug;
        log.Debug("collector", "cpu-load", "ok");

        Assert.Single(sink.Events);
    }
}

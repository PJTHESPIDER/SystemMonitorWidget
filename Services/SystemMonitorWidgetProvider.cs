using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    [ComVisible(true)]
    [Guid("f72c45b3-9fa6-4155-82de-ab4ccf67cdb8")]
    public class SystemMonitorWidgetProvider : IWidgetProvider, IDisposable
    {
        public static readonly Guid Clsid = Guid.Parse("f72c45b3-9fa6-4155-82de-ab4ccf67cdb8");

        /// <summary>
        /// Thread-safe registry of widgets this provider is serving.
        ///
        /// Entries are immutable: the registry replaces an entry atomically
        /// rather than mutating properties in place. A ConcurrentDictionary
        /// alone would not make mutable entry properties safe, which is why
        /// <see cref="CompactWidgetInfo"/> was replaced with
        /// <see cref="WidgetEntry{WidgetSize}"/>.
        /// </summary>
        private static readonly WidgetRegistry<WidgetSize> RunningWidgets = new();

        private static readonly ManualResetEvent EmptyWidgetListEvent = new(false);

        private readonly SystemMetrics _metrics;
        private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(5);

        private static SystemMetricsService? _metricsService;
        private static UpdatePipeline<WidgetSize>? _pipeline;
        private static readonly object _pipelineLock = new();

        // Timestamp of the most recent completed collection. Used for
        // lastUpdated in immediate (non-pipeline) widget updates, and as the
        // sample timestamp for the payload.
        private DateTimeOffset _lastSnapshotTimestamp;

        public SystemMonitorWidgetProvider()
        {
            _metrics = new SystemMetrics();

            try
            {
                var existingWidgets = WidgetManager.GetDefault().GetWidgetInfos();
                if (existingWidgets != null && existingWidgets.Length > 0)
                {
                    foreach (var widgetInfo in existingWidgets)
                    {
                        var context = widgetInfo.WidgetContext;
                        RunningWidgets.Set(new WidgetEntry<WidgetSize>
                        {
                            WidgetId = context.Id,
                            WidgetName = context.DefinitionId,
                            Size = context.Size,
                            IsActive = context.IsActive
                        });
                    }

                    if (RunningWidgets.HasActiveWidgets)
                    {
                        EnsurePipelineStarted();
                    }
                }
                else
                {
                    EmptyWidgetListEvent.Set();
                }
            }
            catch
            {
                // In non-packaged or test environments, WidgetManager may not be available
            }
        }

        public static void WaitForCompletion()
        {
            if (RunningWidgets.IsEmpty)
            {
                // Wait briefly or until widgets are pinned
                EmptyWidgetListEvent.WaitOne(TimeSpan.FromSeconds(30));
            }
            else
            {
                EmptyWidgetListEvent.WaitOne();
            }
        }

        public static ManualResetEvent GetEmptyWidgetListEvent() => EmptyWidgetListEvent;

        public void CreateWidget(WidgetContext widgetContext)
        {
            var widgetId = widgetContext.Id;
            var entry = new WidgetEntry<WidgetSize>
            {
                WidgetId = widgetId,
                WidgetName = widgetContext.DefinitionId,
                Size = widgetContext.Size,
                IsActive = widgetContext.IsActive
            };

            RunningWidgets.Set(entry);
            EmptyWidgetListEvent.Reset();

            EnsurePipelineStarted();
            UpdateWidget(entry);
        }

        public void DeleteWidget(string widgetId, string customState)
        {
            RunningWidgets.Remove(widgetId);

            // Same non-blocking pause as Deactivate(): the inactive
            // transition happens synchronously, the coordinator unwinds in
            // the background.
            if (!RunningWidgets.HasActiveWidgets)
            {
                StopPipeline();
            }

            if (RunningWidgets.IsEmpty)
            {
                EmptyWidgetListEvent.Set();
            }
        }

        public void Activate(WidgetContext widgetContext)
        {
            var widgetId = widgetContext.Id;

            if (!RunningWidgets.TrySetActive(widgetId, true, widgetContext.Size))
            {
                RunningWidgets.Set(new WidgetEntry<WidgetSize>
                {
                    WidgetId = widgetId,
                    WidgetName = widgetContext.DefinitionId,
                    Size = widgetContext.Size,
                    IsActive = true
                });
            }

            EmptyWidgetListEvent.Reset();
            EnsurePipelineStarted();
            UpdateWidget(RunningWidgets.GetAll().FirstOrDefault(e => e.WidgetId == widgetId)
                ?? new WidgetEntry<WidgetSize>
                {
                    WidgetId = widgetId,
                    WidgetName = widgetContext.DefinitionId,
                    Size = widgetContext.Size,
                    IsActive = true
                });
        }

        public void Deactivate(string widgetId)
        {
            RunningWidgets.TrySetActive(widgetId, false);

            // If no widgets are currently active, stop polling to preserve CPU/battery.
            //
            // This is a synchronous COM callback, so it must not block on the
            // pipeline. Stop() transitions the pipeline to inactive through the
            // same coordination that admits a new cycle and requests
            // cancellation of the in-flight run; the coordinator then unwinds
            // in the background. The awaitable quiescence boundary
            // (DeactivateAsync) is used by the shutdown path, where awaiting
            // is safe.
            if (!RunningWidgets.HasActiveWidgets)
            {
                StopPipeline();
            }
        }

        public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
        {
            var verb = actionInvokedArgs.Verb;
            if (string.Equals(verb, "refresh", StringComparison.OrdinalIgnoreCase))
            {
                // Coalesce: multiple refreshes during a collection collapse
                // into a single re-collection after the current one finishes.
                _pipeline?.ScheduleRefresh();
            }
        }

        public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
        {
            var widgetContext = contextChangedArgs.WidgetContext;
            var widgetId = widgetContext.Id;
            RunningWidgets.TrySetSize(widgetId, widgetContext.Size);
            UpdateWidget(RunningWidgets.GetAll().FirstOrDefault(e => e.WidgetId == widgetId)
                ?? new WidgetEntry<WidgetSize>
                {
                    WidgetId = widgetId,
                    WidgetName = widgetContext.DefinitionId,
                    Size = widgetContext.Size,
                    IsActive = false
                });
        }

        private void EnsurePipelineStarted()
        {
            if (_metricsService == null)
            {
                _metricsService = new SystemMetricsService(_metrics);
            }

            lock (_pipelineLock)
            {
                if (_pipeline == null)
                {
                    _pipeline = new UpdatePipeline<WidgetSize>(
                        _metricsService,
                        RunningWidgets,
                        BuildPayloadFromSnapshot,
                        PublishToWidget,
                        UpdateInterval);
                }

                _pipeline.Start();
            }
        }

        private void StopPipeline()
        {
            lock (_pipelineLock)
            {
                _pipeline?.Stop();
            }
        }

        /// <summary>
        /// Stops the pipeline and disposes owned resources (including the
        /// sensor backend). Called by <see cref="Program.Main"/> after
        /// <see cref="WaitForCompletion"/> returns, so the backend is never
        /// disposed while the loop may still be using it.
        /// </summary>
        public static async Task ShutdownAsync()
        {
            UpdatePipeline<WidgetSize>? pipeline;
            SystemMetricsService? metricsService;
            lock (_pipelineLock)
            {
                pipeline = _pipeline;
                _pipeline = null;
                metricsService = _metricsService;
                _metricsService = null;
            }

            if (pipeline is not null)
            {
                // Request the pause and await quiescence: the deactivated
                // generation's collection and publishing work has finished
                // or unwound. Only then may the backend be disposed.
                await pipeline.DeactivateAsync();
                await pipeline.DisposeAsync();
            }

            if (metricsService is not null)
            {
                metricsService.Dispose();
            }
        }

        /// <summary>
        /// Pipeline callback: builds one payload from the completed snapshot
        /// and records the sample timestamp for <see cref="lastUpdated"/>.
        /// </summary>
        private string BuildPayloadFromSnapshot(MetricsSnapshot snapshot)
        {
            _lastSnapshotTimestamp = snapshot.Timestamp;
            return CreateDataPayload(snapshot);
        }

        /// <summary>
        /// Pipeline callback: publishes one payload to one widget. Widget
        /// manager calls are blocking COM interop and run on the pipeline
        /// loop thread; one widget's failure does not prevent the others.
        /// </summary>
        private Task PublishToWidget(string payload, WidgetEntry<WidgetSize> widget)
        {
            UpdateWidget(widget, payload);
            return Task.CompletedTask;
        }

        private void UpdateWidget(WidgetEntry<WidgetSize> widget, string? payload = null)
        {
            try
            {
                var template = WidgetTemplates.GetTemplateForSize(widget.Size);
                var data = payload ?? CreateDataPayloadFromCurrentMetrics();

                var updateOptions = new WidgetUpdateRequestOptions(widget.WidgetId)
                {
                    Template = template,
                    Data = data
                };

                WidgetManager.GetDefault().UpdateWidget(updateOptions);
            }
            catch
            {
                // Widget manager update error (e.g., host disconnected)
            }
        }

        /// <summary>
        /// Builds the complete payload from a completed immutable snapshot.
        /// <see cref="lastUpdated"/> uses the sample timestamp from the
        /// snapshot, not the serialization time.
        /// </summary>
        private string CreateDataPayload(MetricsSnapshot snapshot)
        {
            // Prefer the device-identified GPU load (a real sensor on a known
            // GPU) over the legacy Windows engine-counter average, which can
            // report a value while naming no device.
            double? primaryGpuLoad = snapshot.GpuLoadPercent?.Value ?? _metrics.GpuPercent;

            string gpu = primaryGpuLoad.HasValue
                ? $"{primaryGpuLoad.Value:F1}%"
                : "N/A";

            string cpuTempStr = FormatMeasurement(snapshot.CpuTemperatureC, "F1", "°C");
            string gpuTempStr = FormatMeasurement(snapshot.GpuTemperatureC, "F1", "°C");

            // ACPI thermal-zone sensors are not component-identified; expose
            // separately so they are never mistaken for confirmed CPU/GPU temps.
            string acpiThermalZoneTempStr = FormatMeasurement(snapshot.AcpiThermalZoneTemperatureC, "F1", "°C");

            string tempSummary;
            if (snapshot.CpuTemperatureC?.Status == MeasurementStatus.Available
                && snapshot.GpuTemperatureC?.Status == MeasurementStatus.Available)
                tempSummary = $"{cpuTempStr} | {gpuTempStr}";
            else if (snapshot.CpuTemperatureC?.Status == MeasurementStatus.Available)
                tempSummary = cpuTempStr;
            else if (snapshot.GpuTemperatureC?.Status == MeasurementStatus.Available)
                tempSummary = gpuTempStr;
            else
                tempSummary = "N/A";

            string gpuVramUsed = FormatBytesMeasurement(snapshot.GpuVramUsedBytes);
            string ramUsed = ByteFormatter.FormatBytes(MeasurementValue(snapshot.RamUsedBytes));
            string ramTotal = ByteFormatter.FormatBytes(MeasurementValue(snapshot.RamTotalBytes));
            string ramCombined = $"{ramUsed} / {ramTotal}";

            string diskUsed = ByteFormatter.FormatBytes(Math.Max(0, MeasurementValue(snapshot.DiskUsedBytes)));
            string diskTotal = ByteFormatter.FormatBytes(Math.Max(0, MeasurementValue(snapshot.DiskTotalBytes)));
            string diskCombined = $"{diskUsed} / {diskTotal}";

            string cpuClockStr = FormatClock(snapshot.CpuClockMHz);
            string cpuDetailSmall = snapshot.CpuTemperatureC?.Status == MeasurementStatus.Available
                ? $"{cpuClockStr} | {cpuTempStr}"
                : cpuClockStr;

            string gpuDetailSmall = snapshot.GpuTemperatureC?.Status == MeasurementStatus.Available
                ? $"{gpuTempStr} | {gpuVramUsed}"
                : gpuVramUsed;

            var payload = new
            {
                cpu = FormatPercent(snapshot.CpuLoadPercent),
                cpuClock = cpuClockStr,
                cpuDetailSmall,
                gpuDetailSmall,
                gpu,
                // igpu/dgpu are integration-class keys, not vendor keys: they
                // report the load of a GPU known to be integrated with the CPU
                // and of a GPU known to be discrete. When the integration class
                // is unknown, the value is unavailable rather than a guess.
                igpu = FormatPercentFromModel(_metrics.IntegratedGpuLoadPercent),
                dgpu = FormatPercentFromModel(_metrics.DiscreteGpuLoadPercent),
                ram = FormatRamUsage(snapshot),
                ramUsed,
                ramTotal,
                ramCombined,
                ramPercent = FormatPercent(snapshot.RamPercent),
                ramCompact = $"{ramUsed} ({_metrics.RamPercent:F0}%)",
                disk = FormatDiskUsage(snapshot),
                diskUsed,
                diskTotal,
                diskCombined,
                diskPercent = FormatPercent(snapshot.DiskPercent),
                diskCompact = $"{diskUsed} ({_metrics.DiskPercent:F0}%)",
                netDown = ByteFormatter.FormatNetworkSpeed(MeasurementValue(snapshot.NetworkDownloadBytesPerSec)),
                netUp = ByteFormatter.FormatNetworkSpeed(MeasurementValue(snapshot.NetworkUploadBytesPerSec)),
                cpuTemp = cpuTempStr,
                gpuTemp = gpuTempStr,
                tempSummary,
                gpuVram = FormatGpuVram(snapshot) ?? "N/A",
                gpuVramUsed,
                lastUpdated = snapshot.Timestamp.ToString("h:mm:ss tt"),
                acpiThermalZoneTemp = acpiThermalZoneTempStr
            };

            return JsonSerializer.Serialize(payload);
        }

        /// <summary>
        /// Builds a payload from the current mutable model values, used for
        /// immediate (non-pipeline) widget updates when no completed
        /// snapshot exists yet.
        /// </summary>
        private string CreateDataPayloadFromCurrentMetrics()
        {
            var timestamp = _lastSnapshotTimestamp != default
                ? _lastSnapshotTimestamp
                : DateTimeOffset.UtcNow;

            // Build a minimal snapshot from current model values so the same
            // payload builder is used for both pipeline and immediate paths.
            var snapshot = MetricsSnapshot.Create(
                timestamp,
                cpuLoadPercent: Measurement.AvailableValue(_metrics.CpuPercent, "%", "Windows CPU counters"),
                ramUsedBytes: Measurement.AvailableValue((double)_metrics.RamUsedBytes, "bytes", "GlobalMemoryStatusEx"),
                ramTotalBytes: Measurement.AvailableValue((double)_metrics.RamTotalBytes, "bytes", "GlobalMemoryStatusEx"),
                ramPercent: Measurement.AvailableValue(_metrics.RamPercent, "%", "GlobalMemoryStatusEx"),
                diskUsedBytes: Measurement.AvailableValue(Math.Max(0, (double)_metrics.DiskUsedBytes), "bytes", "DriveInfo"),
                diskTotalBytes: Measurement.AvailableValue(Math.Max(0, (double)_metrics.DiskTotalBytes), "bytes", "DriveInfo"),
                diskPercent: Measurement.AvailableValue(_metrics.DiskPercent, "%", "DriveInfo"),
                networkDownloadBytesPerSec: Measurement.AvailableValue(_metrics.NetworkDownloadBytesPerSec, "bytes/sec", "NetworkInterface"),
                networkUploadBytesPerSec: Measurement.AvailableValue(_metrics.NetworkUploadBytesPerSec, "bytes/sec", "NetworkInterface"),
                cpuClockMHz: _metrics.CpuClockMHz.HasValue
                    ? Measurement.AvailableValue(_metrics.CpuClockMHz.Value, "MHz", "Win32_Processor")
                    : null,
                cpuTemperatureC: _metrics.CpuTemperature.HasValue
                    ? Measurement.AvailableValue(_metrics.CpuTemperature.Value, "°C", "MSAcpi_ThermalZoneTemperature")
                    : null,
                gpuLoadPercent: _metrics.GpuLoadPercent.HasValue
                    ? Measurement.AvailableValue(_metrics.GpuLoadPercent.Value, "%", "Device-identified backend")
                    : null,
                gpuTemperatureC: _metrics.GpuTemperature.HasValue
                    ? Measurement.AvailableValue(_metrics.GpuTemperature.Value, "°C", "nvidia-smi")
                    : null,
                gpuVramUsedBytes: _metrics.GpuVramUsedBytes.HasValue
                    ? Measurement.AvailableValue((double)_metrics.GpuVramUsedBytes.Value, "bytes", "nvidia-smi/WMI")
                    : null,
                gpuVramTotalBytes: _metrics.GpuVramTotalBytes.HasValue
                    ? Measurement.AvailableValue((double)_metrics.GpuVramTotalBytes.Value, "bytes", "nvidia-smi/WMI")
                    : null,
                acpiThermalZoneTemperatureC: _metrics.AcpiThermalZoneTemperatureC.HasValue
                    ? Measurement.AvailableValue(_metrics.AcpiThermalZoneTemperatureC.Value, "°C", "MSAcpi_ThermalZoneTemperature")
                    : null);

            return CreateDataPayload(snapshot);
        }

        private static string FormatMeasurement(Measurement? m, string format, string suffix)
        {
            if (m?.Status == MeasurementStatus.Available && m.Value.HasValue)
                return m.Value.Value.ToString(format) + suffix;
            return "N/A";
        }

        private static double MeasurementValue(Measurement? m)
        {
            return m?.Status == MeasurementStatus.Available && m.Value.HasValue
                ? m.Value.Value
                : 0.0;
        }

        private static string FormatPercent(Measurement? m) =>
            m?.Status == MeasurementStatus.Available && m.Value.HasValue
                ? $"{m.Value.Value:F1}%"
                : "N/A";

        private static string FormatPercentFromModel(double? value) =>
            value.HasValue ? $"{value.Value:F1}%" : "N/A";

        private static string FormatBytesMeasurement(Measurement? m) =>
            m?.Status == MeasurementStatus.Available && m.Value.HasValue
                ? ByteFormatter.FormatBytes(m.Value.Value)
                : "N/A";

        private static string FormatClock(Measurement? m) =>
            m?.Status == MeasurementStatus.Available && m.Value.HasValue
            ? (m.Value.Value >= 1000 ? $"{m.Value.Value / 1000.0:F2} GHz" : $"{m.Value.Value:F0} MHz")
            : "N/A";

        private static string FormatRamUsage(MetricsSnapshot snapshot)
        {
            if (snapshot.RamPercent?.Status == MeasurementStatus.Available && snapshot.RamPercent.Value.HasValue)
            {
                var usedStr = FormatBytesMeasurement(snapshot.RamUsedBytes);
                var totalStr = FormatBytesMeasurement(snapshot.RamTotalBytes);
                return $"{usedStr} / {totalStr} ({snapshot.RamPercent.Value:F1}%)";
            }
            return FormatBytesMeasurement(snapshot.RamUsedBytes);
        }

        private static string FormatDiskUsage(MetricsSnapshot snapshot)
        {
            if (snapshot.DiskPercent?.Status == MeasurementStatus.Available && snapshot.DiskPercent.Value.HasValue)
            {
                var usedStr = FormatBytesMeasurement(snapshot.DiskUsedBytes);
                var totalStr = FormatBytesMeasurement(snapshot.DiskTotalBytes);
                return $"{usedStr} / {totalStr} ({snapshot.DiskPercent.Value:F1}%)";
            }
            return FormatBytesMeasurement(snapshot.DiskUsedBytes);
        }

        private static string? FormatGpuVram(MetricsSnapshot snapshot)
        {
            if (snapshot.GpuVramUsedBytes?.Status != MeasurementStatus.Available || !snapshot.GpuVramUsedBytes.Value.HasValue)
                return null;
            if (snapshot.GpuVramTotalBytes?.Status == MeasurementStatus.Available
                && snapshot.GpuVramTotalBytes.Value.HasValue
                && snapshot.GpuVramTotalBytes.Value.Value > 0)
            {
                var used = snapshot.GpuVramUsedBytes.Value.Value;
                var total = snapshot.GpuVramTotalBytes.Value.Value;
                var pct = used / total * 100.0;
                return ByteFormatter.FormatUsage(used, total, pct);
            }
            return ByteFormatter.FormatBytes(snapshot.GpuVramUsedBytes.Value.Value);
        }

        public void Dispose()
        {
            // Best-effort synchronous disposal; the preferred async shutdown
            // path is ShutdownAsync(), called from Program.Main.
            StopPipeline();
            try
            {
                _metricsService?.Dispose();
            }
            catch { }
        }
    }
}

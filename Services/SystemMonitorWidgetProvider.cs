using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    public class CompactWidgetInfo
    {
        public string WidgetId { get; set; } = string.Empty;
        public string WidgetName { get; set; } = string.Empty;
        public WidgetSize Size { get; set; } = WidgetSize.Medium;
        public bool IsActive { get; set; }
    }

    [ComVisible(true)]
    [Guid("f72c45b3-9fa6-4155-82de-ab4ccf67cdb8")]
    public class SystemMonitorWidgetProvider : IWidgetProvider, IDisposable
    {
        public static readonly Guid Clsid = Guid.Parse("f72c45b3-9fa6-4155-82de-ab4ccf67cdb8");

        private static readonly ConcurrentDictionary<string, CompactWidgetInfo> RunningWidgets = new();
        private static readonly ManualResetEvent EmptyWidgetListEvent = new(false);

        private readonly SystemMetrics _metrics;
        private readonly WidgetSettings _settings;
        private SystemMetricsService? _metricsService;
        private Timer? _updateTimer;
        private readonly object _lock = new();

        public SystemMonitorWidgetProvider()
        {
            _metrics = new SystemMetrics();
            _settings = SettingsStore.LoadAsync().GetAwaiter().GetResult();

            try
            {
                var existingWidgets = WidgetManager.GetDefault().GetWidgetInfos();
                if (existingWidgets != null && existingWidgets.Length > 0)
                {
                    foreach (var widgetInfo in existingWidgets)
                    {
                        var context = widgetInfo.WidgetContext;
                        var info = new CompactWidgetInfo
                        {
                            WidgetId = context.Id,
                            WidgetName = context.DefinitionId,
                            Size = context.Size,
                            IsActive = context.IsActive
                        };
                        RunningWidgets[context.Id] = info;
                    }

                    if (RunningWidgets.Values.Any(w => w.IsActive))
                    {
                        EnsureMetricsServiceStarted();
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
            var info = new CompactWidgetInfo
            {
                WidgetId = widgetId,
                WidgetName = widgetContext.DefinitionId,
                Size = widgetContext.Size,
                IsActive = widgetContext.IsActive
            };

            RunningWidgets[widgetId] = info;
            EmptyWidgetListEvent.Reset();

            EnsureMetricsServiceStarted();
            UpdateWidget(info);
        }

        public void DeleteWidget(string widgetId, string customState)
        {
            RunningWidgets.TryRemove(widgetId, out _);

            if (RunningWidgets.IsEmpty)
            {
                StopMetricsService();
                EmptyWidgetListEvent.Set();
            }
        }

        public void Activate(WidgetContext widgetContext)
        {
            var widgetId = widgetContext.Id;
            if (RunningWidgets.TryGetValue(widgetId, out var info))
            {
                info.IsActive = true;
                info.Size = widgetContext.Size;
            }
            else
            {
                info = new CompactWidgetInfo
                {
                    WidgetId = widgetId,
                    WidgetName = widgetContext.DefinitionId,
                    Size = widgetContext.Size,
                    IsActive = true
                };
                RunningWidgets[widgetId] = info;
            }

            EnsureMetricsServiceStarted();
            UpdateWidget(info);
        }

        public void Deactivate(string widgetId)
        {
            if (RunningWidgets.TryGetValue(widgetId, out var info))
            {
                info.IsActive = false;
            }

            // If no widgets are currently active, stop polling to preserve CPU/battery
            if (!RunningWidgets.Values.Any(w => w.IsActive))
            {
                StopMetricsService();
            }
        }

        public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
        {
            var verb = actionInvokedArgs.Verb;
            if (string.Equals(verb, "refresh", StringComparison.OrdinalIgnoreCase))
            {
                UpdateAllWidgets();
            }
        }

        public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
        {
            var widgetContext = contextChangedArgs.WidgetContext;
            var widgetId = widgetContext.Id;
            if (RunningWidgets.TryGetValue(widgetId, out var info))
            {
                info.Size = widgetContext.Size;
                UpdateWidget(info);
            }
        }

        private void EnsureMetricsServiceStarted()
        {
            lock (_lock)
            {
                if (_metricsService == null)
                {
                    _metricsService = new SystemMetricsService(_metrics, _settings, null);
                }

                if (_updateTimer == null)
                {
                    var interval = TimeSpan.FromSeconds(Math.Max(1, _settings.RefreshIntervalSeconds));
                    _updateTimer = new Timer(_ => UpdateAllWidgets(), null, TimeSpan.FromSeconds(1), interval);
                }
            }
        }

        private void StopMetricsService()
        {
            lock (_lock)
            {
                _updateTimer?.Dispose();
                _updateTimer = null;

                _metricsService?.Dispose();
                _metricsService = null;
            }
        }

        private void UpdateAllWidgets()
        {
            foreach (var widget in RunningWidgets.Values.Where(w => w.IsActive))
            {
                UpdateWidget(widget);
            }
        }

        private void UpdateWidget(CompactWidgetInfo info)
        {
            try
            {
                var template = WidgetTemplates.GetTemplateForSize(info.Size);
                var data = CreateDataPayload();

                var updateOptions = new WidgetUpdateRequestOptions(info.WidgetId)
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

        private string CreateDataPayload()
        {
            var payload = new
            {
                cpu = $"{_metrics.CpuPercent:F1}%",
                cpuClock = _metrics.CpuClockDisplay,
                igpu = _metrics.GpuPercent.HasValue ? $"{_metrics.GpuPercent.Value:F1}%" : "N/A",
                dgpu = _metrics.NvidiaGpuPercent.HasValue ? $"{_metrics.NvidiaGpuPercent.Value:F1}%" : "N/A",
                ram = _metrics.RamDisplay,
                ramUsed = ByteFormatter.FormatBytes(_metrics.RamUsedBytes),
                ramTotal = ByteFormatter.FormatBytes(_metrics.RamTotalBytes),
                ramPercent = $"{_metrics.RamPercent:F1}%",
                disk = _metrics.DiskDisplay,
                diskUsed = ByteFormatter.FormatBytes(Math.Max(0, _metrics.DiskUsedBytes)),
                diskTotal = ByteFormatter.FormatBytes(Math.Max(0, _metrics.DiskTotalBytes)),
                diskPercent = $"{_metrics.DiskPercent:F1}%",
                netDown = ByteFormatter.FormatNetworkSpeed(_metrics.NetworkDownloadBytesPerSec),
                netUp = ByteFormatter.FormatNetworkSpeed(_metrics.NetworkUploadBytesPerSec),
                cpuTemp = _metrics.CpuTemperature.HasValue ? $"{_metrics.CpuTemperature.Value:F1}°C" : "N/A",
                gpuTemp = _metrics.GpuTemperature.HasValue ? $"{_metrics.GpuTemperature.Value:F1}°C" : "N/A",
                gpuVram = _metrics.GpuVramDisplay ?? "N/A",
                lastUpdated = DateTime.Now.ToString("h:mm:ss tt")
            };

            return JsonSerializer.Serialize(payload);
        }

        public void Dispose()
        {
            StopMetricsService();
        }
    }
}


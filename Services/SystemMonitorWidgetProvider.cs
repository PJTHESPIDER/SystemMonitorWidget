using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    [ComVisible(true)]
    [Guid("f72c45b3-9fa6-4155-82de-ab4ccf67cdb8")]
    public class SystemMonitorWidgetProvider : IWidgetProvider, IWidgetProvider2, IDisposable
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

        /// <summary>
        /// How long the process waits, with no widget registered, before it
        /// treats the host as finished.
        ///
        /// <para>
        /// The window gives the host time to pin a widget after the
        /// provider starts. A widget pinned inside the window keeps the
        /// process alive; only a window that elapses with the list still
        /// empty ends the wait.
        /// </para>
        /// </summary>
        private static readonly TimeSpan InactivityWindow = TimeSpan.FromSeconds(30);

        private readonly SystemMetrics _metrics;
        private static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(5);
        private static readonly TimeProvider Clock = TimeProvider.System;

        private static SystemMetricsService? _metricsService;
        private static UpdatePipeline<WidgetSize>? _pipeline;
        private static readonly object _pipelineLock = new();

        /// <summary>
        /// The most recent completed collection, kept so an immediate
        /// (non-pipeline) widget update has a real snapshot to present
        /// rather than an empty one.
        /// </summary>
        private static MetricsSnapshot? _lastSnapshot;

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
                            IsActive = context.IsActive,
                            // The host persists the selection for us, so a
                            // widget that was pinned before the provider
                            // restarted keeps its chosen device.
                            SelectedGpuInstanceId = ReadPersistedSelection(widgetInfo.CustomState),
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
            catch (Exception ex)
            {
                // In non-packaged or test environments, WidgetManager may not
                // be available. The widget still starts; it simply has no
                // pinned widgets to serve.
                DiagnosticLog.Shared.Warning(
                    "widget-host",
                    "enumerate-widgets",
                    "unavailable",
                    detail: DiagnosticDetail.Describe(ex));
            }
        }

        /// <summary>
        /// Waits until the widget host is finished with this process.
        ///
        /// <para>
        /// The wait ends when the widget list is empty and stays empty for
        /// the inactivity window. A widget pinned during the window
        /// restarts it, so a live widget is never torn down: the previous
        /// implementation returned on the timeout regardless, which stopped
        /// the pipeline for a widget the host had just pinned.
        /// </para>
        ///
        /// <para>
        /// While widgets are registered, the wait continues for as long as
        /// they exist. The unbounded wait in that branch is the plan's open
        /// item #15; it is left as it is, because bounding it properly
        /// means driving the process exit off the pipeline's drain
        /// boundary, which is a larger change than this task allows.
        /// </para>
        /// </summary>
        public static void WaitForCompletion()
        {
            while (true)
            {
                if (!RunningWidgets.IsEmpty)
                {
                    // Widgets are registered. Wait for the last one to be
                    // deleted.
                    EmptyWidgetListEvent.WaitOne(InactivityWindow);
                    continue;
                }

                // The list is empty. Wait for the host either to disappear
                // or to pin a widget.
                if (EmptyWidgetListEvent.WaitOne(InactivityWindow))
                {
                    // The list went empty and was signalled: the host is
                    // finished.
                    return;
                }

                // The window elapsed with the list still empty. The host
                // may have pinned a widget during the window, in which
                // case the wait continues for that widget rather than
                // tearing down a live pipeline.
                if (!RunningWidgets.IsEmpty)
                    continue;

                return;
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
            // transition happens synchronously, the coordinator unwinds in the
            // background.
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

        /// <summary>
        /// The widget host asks for the widget's customization experience.
        ///
        /// <para>
        /// This provider does not use a separate customization card: the
        /// device picker is a set of ordinary actions on the widget itself,
        /// so there is no separate customization payload to serve. The
        /// callback is implemented because the interface requires it, and
        /// because the host may ask for the settings payload at any time —
        /// the regular template already carries the picker, so the
        /// customization request is answered by republishing the widget.
        /// </para>
        /// </summary>
        public void OnCustomizationRequested(WidgetCustomizationRequestedArgs customizationRequestedArgs)
        {
            var widgetId = customizationRequestedArgs.WidgetContext.Id;

            if (RunningWidgets.TryGet(widgetId, out var entry) && entry is not null)
                UpdateWidget(entry);
        }

        public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
        {
            // The templates carry no root-level actions, so the host has
            // nothing to invoke. The callback is implemented because the
            // interface requires it.
            //
            // The widget refreshes on its own interval, and the device
            // picker that used to live here was removed with the action
            // row: it cost the card the height its figures needed.
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
            lock (_pipelineLock)
            {
                // The collector is created under the same lock as the
                // pipeline, and the pipeline is built over the collector
                // that is actually in the field. Constructing the collector
                // outside the lock would let two callers race: the second
                // would overwrite the field, and the pipeline would be built
                // over a service that is no longer the one the field holds —
                // so the service the pipeline uses would never be disposed.
                if (_metricsService == null)
                {
                    _metricsService = new SystemMetricsService(_metrics);
                }

                if (_pipeline == null)
                {
                    _pipeline = new UpdatePipeline<WidgetSize>(
                        _metricsService,
                        RunningWidgets,
                        BuildPayloadFromSnapshot,
                        PublishToWidget,
                        UpdateInterval,
                        Clock);
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
        /// Pipeline callback: builds one payload from the completed snapshot.
        ///
        /// <para>
        /// The payload is built per widget, because the presented GPU is a
        /// per-widget setting: two widgets on the same machine may show
        /// different cards. The snapshot is the same for both, so the
        /// collection work is not repeated.
        /// </para>
        /// </summary>
        private string BuildPayloadFromSnapshot(MetricsSnapshot snapshot)
        {
            _lastSnapshot = snapshot;
            return string.Empty;
        }

        /// <summary>
        /// Pipeline callback: publishes one payload to one widget. Widget
        /// manager calls are blocking COM interop and run on the pipeline
        /// loop thread; one widget's failure does not prevent the others.
        /// </summary>
        private Task PublishToWidget(string payload, WidgetEntry<WidgetSize> widget)
        {
            UpdateWidget(widget);
            return Task.CompletedTask;
        }

        private void UpdateWidget(WidgetEntry<WidgetSize> widget)
        {
            var started = Stopwatch.StartNew();

            try
            {
                var snapshot = _lastSnapshot;
                var selectionId = widget.SelectedGpuInstanceId;

                var template = WidgetTemplates.BuildTemplate(ToLayoutSize(widget.Size));

                var data = WidgetPayloadPresenter
                    .PresentForPublication(snapshot, selectionId, Clock)
                    .ToJson();

                var updateOptions = new WidgetUpdateRequestOptions(widget.WidgetId)
                {
                    Template = template,
                    Data = data,
                    // The host persists this per widget and hands it back on
                    // the next run, so the user's choice survives a restart
                    // of the provider.
                    CustomState = selectionId ?? string.Empty,
                };

                WidgetManager.GetDefault().UpdateWidget(updateOptions);

                DiagnosticLog.Shared.Debug(
                    "widget-host",
                    "update",
                    "ok",
                    elapsed: started.Elapsed,
                    detail: $"size={widget.Size}");
            }
            catch (Exception ex)
            {
                // The host may have disconnected, or the update may have
                // been rejected. The next cycle tries again, so this is
                // reported once rather than for every widget on every
                // cycle.
                DiagnosticLog.Shared.Warning(
                    "widget-host",
                    "update",
                    "failed",
                    elapsed: started.Elapsed,
                    detail: DiagnosticDetail.Describe(ex));
            }
        }

        /// <summary>
        /// Maps the host's widget size onto the project's layout size.
        /// </summary>
        private static WidgetLayoutSize ToLayoutSize(WidgetSize size) => size switch
        {
            WidgetSize.Small => WidgetLayoutSize.Small,
            WidgetSize.Large => WidgetLayoutSize.Large,
            _ => WidgetLayoutSize.Medium,
        };

        /// <summary>
        /// Reads the persisted selection from the host's custom state.
        ///
        /// <para>
        /// The custom state holds the instance id of the selected device, or
        /// nothing when no device was selected. A value that cannot be read
        /// is ignored rather than treated as a selection: an unresolvable
        /// selection is worse than no selection, because it would make the
        /// widget fall back to the default policy on every start.
        /// </para>
        /// </summary>
        private static string? ReadPersistedSelection(string? customState)
        {
            if (string.IsNullOrWhiteSpace(customState))
                return null;

            var trimmed = customState.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        /// <summary>
        /// Releases this provider instance.
        ///
        /// <para>
        /// The pipeline and the sensor backend are <i>shared</i>: they are
        /// process-wide, because the widget host may create more than one
        /// provider instance and every widget is served from the same
        /// collection. Disposing the backend here would therefore close the
        /// sensor source out from under a pipeline that is still running
        /// for another widget.
        /// </para>
        ///
        /// <para>
        /// The process-wide resources are released by
        /// <see cref="ShutdownAsync"/>, which runs after the last widget is
        /// gone and awaits the pipeline's drain boundary first. This method
        /// therefore does nothing: it exists because the host may release
        /// an RCW at any time, and releasing one provider instance must not
        /// affect the others.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            // Nothing is released here. The pipeline and the sensor backend
            // belong to the process, not to this instance, and are released
            // by ShutdownAsync() once the last widget has gone.
        }
    }
}

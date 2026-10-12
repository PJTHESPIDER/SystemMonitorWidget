using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using System;
using System.Linq;
using System.Threading.Tasks;
using System_Monitor.Models;
using System_Monitor.Services;
using Windows.UI;

namespace System_Monitor.Views
{
    public sealed partial class WidgetView : UserControl
    {
        private readonly SystemMetrics _model;
        private SystemMetricsService? _service;
        private WidgetSettings? _settings;
        private readonly DispatcherQueue _dispatcher;

        public WidgetView()
        {
            this.InitializeComponent();
            _dispatcher = DispatcherQueue.GetForCurrentThread();

            _model = new SystemMetrics();
            this.DataContext = _model;

            // Hook UI
            SettingsButton.Click += SettingsButton_Click;
            this.Unloaded += WidgetView_Unloaded;

            // Fire-and-forget load settings and start service
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            _settings = await SettingsStore.LoadAsync();

            // Start collector with settings (interval applied inside service)
            _service = new SystemMetricsService(_model, _settings, _dispatcher);

            // Persist settings whenever changed
            _settings.PropertyChanged += async (s, e) => await SettingsStore.SaveAsync(_settings);

            // Redraw charts periodically
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            t.Tick += (s, e) =>
            {
                DrawChart(CpuChart, _model.CpuSeries, 100);
                DrawChart(RamChart, _model.RamSeries, 100);
                // combine download and upload for a simple network chart
                DrawChart(NetChart, _model.NetworkDownloadSeries.Concat(_model.NetworkUploadSeries).ToArray(), 0);
            };
            t.Start();
        }

        private async void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_settings == null) return;

            var settingsView = new SettingsView(_settings);

            var dialog = new ContentDialog
            {
                Title = "Widget Settings",
                Content = settingsView,
                CloseButtonText = "Close",
                PrimaryButtonText = "Save"
            };

            var result = await dialog.ShowAsync();
            // Save happens on property change; optionally force save here
            await SettingsStore.SaveAsync(_settings);
        }

        private void WidgetView_Unloaded(object? sender, RoutedEventArgs e)
        {
            _service?.Dispose();
            _service = null;
        }

        private void DrawChart(Canvas canvas, System.Collections.IEnumerable valuesEnum, double maxValue)
        {
            try
            {
                var values = valuesEnum.Cast<double>().ToArray();
                canvas.Children.Clear();
                if (values.Length == 0)
                    return;

                var w = canvas.ActualWidth;
                var h = canvas.ActualHeight;
                if (w <= 0) w = canvas.Width; // fallback
                if (h <= 0) h = canvas.Height;

                var poly = new Polyline
                {
                    Stroke = new SolidColorBrush(Color.FromArgb(255, 30, 144, 255)),
                    StrokeThickness = 2
                };

                double max = maxValue > 0 ? maxValue : Math.Max(1, values.Max());
                var step = w / Math.Max(1, values.Length - 1);

                for (int i = 0; i < values.Length; i++)
                {
                    double x = i * step;
                    double normalized = Math.Max(0, Math.Min(1, values[i] / max));
                    double y = h - (normalized * h);
                    poly.Points.Add(new Windows.Foundation.Point(x, y));
                }

                canvas.Children.Add(poly);
            }
            catch
            {
                // ignore rendering errors
            }
        }
    }
}
using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    public static class SettingsStore
    {
        private const string FileName = "widgetsettings.json";

        public static async Task<WidgetSettings> LoadAsync()
        {
            // Start from a fresh instance so declared field defaults (e.g. ShowCpuTemp = true)
            // are preserved for any keys absent in an older saved file.
            var defaults = new WidgetSettings();
            try
            {
                var local = ApplicationData.Current.LocalFolder;
                var file = await local.TryGetItemAsync(FileName).AsTask().ConfigureAwait(false);
                if (file is StorageFile sf)
                {
                    var json = await FileIO.ReadTextAsync(sf).AsTask().ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;

                    // Apply only the properties present in the saved JSON,
                    // leaving everything else at its declared default.
                    if (root.TryGetProperty("showCpu", out var v) || root.TryGetProperty("ShowCpu", out v))
                        defaults.ShowCpu = v.GetBoolean();
                    if (root.TryGetProperty("showGpu", out v) || root.TryGetProperty("ShowGpu", out v))
                        defaults.ShowGpu = v.GetBoolean();
                    if (root.TryGetProperty("showRam", out v) || root.TryGetProperty("ShowRam", out v))
                        defaults.ShowRam = v.GetBoolean();
                    if (root.TryGetProperty("showDisk", out v) || root.TryGetProperty("ShowDisk", out v))
                        defaults.ShowDisk = v.GetBoolean();
                    if (root.TryGetProperty("showNetwork", out v) || root.TryGetProperty("ShowNetwork", out v))
                        defaults.ShowNetwork = v.GetBoolean();
                    if (root.TryGetProperty("showCpuTemp", out v) || root.TryGetProperty("ShowCpuTemp", out v))
                        defaults.ShowCpuTemp = v.GetBoolean();
                    if (root.TryGetProperty("showGpuTemp", out v) || root.TryGetProperty("ShowGpuTemp", out v))
                        defaults.ShowGpuTemp = v.GetBoolean();
                    if (root.TryGetProperty("showGpuVram", out v) || root.TryGetProperty("ShowGpuVram", out v))
                        defaults.ShowGpuVram = v.GetBoolean();
                    if (root.TryGetProperty("showCpuClock", out v) || root.TryGetProperty("ShowCpuClock", out v))
                        defaults.ShowCpuClock = v.GetBoolean();
                    if (root.TryGetProperty("refreshIntervalSeconds", out v) || root.TryGetProperty("RefreshIntervalSeconds", out v))
                        defaults.RefreshIntervalSeconds = v.GetInt32();
                }
            }
            catch
            {
                // ignore; return defaults below
            }
            return defaults;
        }

        public static async Task SaveAsync(WidgetSettings settings)
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                var local = ApplicationData.Current.LocalFolder;
                var file = await local.CreateFileAsync(FileName, CreationCollisionOption.OpenIfExists).AsTask().ConfigureAwait(false);
                await FileIO.WriteTextAsync(file, json).AsTask().ConfigureAwait(false);
            }
            catch
            {
                // ignore persistence errors for now
            }
        }
    }
}
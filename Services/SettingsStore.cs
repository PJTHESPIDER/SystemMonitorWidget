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
            try
            {
                var local = ApplicationData.Current.LocalFolder;
                var file = await local.TryGetItemAsync(FileName).AsTask().ConfigureAwait(false);
                if (file is StorageFile sf)
                {
                    var json = await FileIO.ReadTextAsync(sf).AsTask().ConfigureAwait(false);
                    var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var settings = JsonSerializer.Deserialize<WidgetSettings>(json, opts);
                    if (settings != null) return settings;
                }
            }
            catch
            {
                // ignore; return default below
            }
            return new WidgetSettings();
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
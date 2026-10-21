using System;
using System.Collections.Generic;
using System.Linq;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// Compatibility presenter that adapts an immutable <see cref="MetricsSnapshot"/>
    /// into the legacy flat payload contract consumed by <see cref="WidgetTemplates"/>.
    ///
    /// This keeps existing Adaptive Card templates working during the migration
    /// to manufacturer-neutral hardware models. Legacy keys that are ambiguous
    /// (e.g. a single "gpu" key when multiple GPUs exist) are still populated
    /// but marked with comments so they can be removed in a later phase.
    /// </summary>
    public static class LegacyPayloadPresenter
    {
        public static Dictionary<string, string> Present(MetricsSnapshot snapshot)
        {
            if (snapshot is null)
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var payload = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // --- CPU ---
            var cpuDevice = LhmSnapshotMapper.SelectPrimaryCpu(snapshot.Cpus);
            payload["cpu"] = FormatPercent(MeasurementOf(Reading(snapshot, cpuDevice?.DeviceId, "LoadPercent")));
            payload["cpuClock"] = FormatClock(MeasurementOf(Reading(snapshot, cpuDevice?.DeviceId, "ClockMHz")));
            payload["cpuTemp"] = FormatTemperature(MeasurementOf(Reading(snapshot, cpuDevice?.DeviceId, "TemperatureC")));

            // cpuDetailSmall / gpuDetailSmall are composite legacy keys used only
            // by the small template. They remain populated but are ambiguous when
            // multiple CPUs/GPUs exist — mark for removal in the template-alignment phase.
            payload["cpuDetailSmall"] = $"{payload["cpuClock"]} | {payload["cpuTemp"]}";

            // --- GPU (legacy single-value keys) ---
            // These keys collapse all GPUs into one value. When multiple GPUs are
            // present (e.g. Intel iGPU + NVIDIA dGPU), the "best available" reading
            // is chosen. This is intentionally lossy — templates should move to
            // per-device keys (gpuLoad:{deviceId}) in the alignment phase.
            var gpuLoad = BestGpuReading(snapshot, "LoadPercent");
            var gpuTemp = BestGpuReading(snapshot, "TemperatureC");
            var gpuVram = BestGpuReading(snapshot, "VramUsedBytes");
            var gpuVramTotal = BestGpuReading(snapshot, "VramTotalBytes");

            payload["gpu"] = FormatPercent(gpuLoad);
            payload["gpuTemp"] = FormatTemperature(gpuTemp);
            payload["gpuVram"] = FormatVram(gpuVram, gpuVramTotal);
            payload["gpuVramUsed"] = FormatBytesValue(gpuVram);

            // Legacy igpu/dgpu keys: populated when a matching-classification GPU
            // is found, otherwise "N/A". These do not encode the vendor (no
            // IntelGpuPercent/NvidiaGpuPercent) — they encode integration class,
            // which is only set when reliably known.
            var integratedGpu = snapshot.Gpus.FirstOrDefault(g => g.GpuIntegration == GpuIntegration.Integrated);
            var discreteGpu = snapshot.Gpus.FirstOrDefault(g => g.GpuIntegration == GpuIntegration.Discrete);
            payload["igpu"] = FormatPercent(MeasurementOf(Reading(snapshot, integratedGpu?.DeviceId, "LoadPercent")));
            payload["dgpu"] = FormatPercent(MeasurementOf(Reading(snapshot, discreteGpu?.DeviceId, "LoadPercent")));

            // --- RAM ---
            payload["ram"] = FormatUsage(snapshot.RamUsedBytes, snapshot.RamTotalBytes, snapshot.RamPercent);
            payload["ramUsed"] = FormatBytesValue(snapshot.RamUsedBytes);
            payload["ramTotal"] = FormatBytesValue(snapshot.RamTotalBytes);
            payload["ramCombined"] = $"{payload["ramUsed"]} / {payload["ramTotal"]}";
            payload["ramPercent"] = FormatPercent(snapshot.RamPercent);
            payload["ramCompact"] = $"{payload["ramUsed"]} ({payload["ramPercent"]})";

            // --- Disk ---
            payload["disk"] = FormatUsage(snapshot.DiskUsedBytes, snapshot.DiskTotalBytes, snapshot.DiskPercent);
            payload["diskUsed"] = FormatBytesValue(snapshot.DiskUsedBytes);
            payload["diskTotal"] = FormatBytesValue(snapshot.DiskTotalBytes);
            payload["diskCombined"] = $"{payload["diskUsed"]} / {payload["diskTotal"]}";
            payload["diskPercent"] = FormatPercent(snapshot.DiskPercent);
            payload["diskCompact"] = $"{payload["diskUsed"]} ({payload["diskPercent"]})";

            // --- Network ---
            payload["netDown"] = FormatNetworkSpeed(snapshot.NetworkDownloadBytesPerSec);
            payload["netUp"] = FormatNetworkSpeed(snapshot.NetworkUploadBytesPerSec);

            // --- Timestamps ---
            payload["lastUpdated"] = snapshot.Timestamp.ToString("h:mm:ss tt");

            // --- ACPI thermal zone (unidentified, separate from CPU/GPU) ---
            var acpiTemp = snapshot.Readings
                .Where(r => r.Metric == "AcpiThermalZoneTemperatureC")
                .Select(r => r.Measurement)
                .FirstOrDefault(m => m?.Status == MeasurementStatus.Available);
            payload["acpiThermalZoneTemp"] = FormatTemperature(acpiTemp);

            return payload;
        }

        private static DeviceReading? Reading(MetricsSnapshot snapshot, string? deviceId, string metric)
        {
            if (deviceId is null)
                return null;
            return snapshot.Readings.FirstOrDefault(r => r.DeviceId == deviceId && r.Metric == metric);
        }

        private static Measurement? MeasurementOf(DeviceReading? reading) => reading?.Measurement;

        private static Measurement? BestGpuReading(MetricsSnapshot snapshot, string metric)
        {
            // Use the same primary-GPU choice the widget uses, so the legacy
            // single-value keys and the per-device keys never disagree about
            // which device they describe.
            var primary = LhmSnapshotMapper.SelectPrimaryGpu(snapshot.Gpus);
            if (primary is not null)
            {
                var primaryReading = Reading(snapshot, primary.DeviceId, metric);
                if (primaryReading?.Measurement.Status == MeasurementStatus.Available
                    && primaryReading.Measurement.Value.HasValue)
                {
                    return primaryReading.Measurement;
                }
            }

            // Fall back to the first GPU with an available reading. Do not
            // average across vendors or device classes — that would manufacture
            // a value that no single device reports.
            foreach (var gpu in snapshot.Gpus)
            {
                var r = Reading(snapshot, gpu.DeviceId, metric);
                if (r?.Measurement.Status == MeasurementStatus.Available && r.Measurement.Value.HasValue)
                    return r.Measurement;
            }
            return null;
        }

        private static string FormatPercent(Measurement? m) =>
            m?.Status == MeasurementStatus.Available && m.Value.HasValue
                ? $"{m.Value.Value:F1}%"
                : "N/A";

        private static string FormatTemperature(Measurement? m) =>
            m?.Status == MeasurementStatus.Available && m.Value.HasValue
                ? $"{m.Value.Value:F1}°C"
                : "N/A";

        private static string FormatClock(Measurement? m) =>
            m?.Status == MeasurementStatus.Available && m.Value.HasValue
                ? FormatClockMhz(m.Value.Value)
                : "N/A";

        private static string FormatClockMhz(double mhz) =>
            mhz >= 1000 ? $"{mhz / 1000.0:F2} GHz" : $"{mhz:F0} MHz";

        private static string FormatBytesValue(Measurement? m) =>
            m?.Status == MeasurementStatus.Available && m.Value.HasValue
                ? FormatBytes(m.Value.Value)
                : "N/A";

        private static string FormatBytes(double bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes >= 1024.0 * 1024 * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024 * 1024 * 1024):F1} TB";
            if (bytes >= 1024.0 * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
            if (bytes >= 1024.0 * 1024)
                return $"{bytes / (1024.0 * 1024):F1} MB";
            if (bytes >= 1024.0)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes:F0} B";
        }

        private static string FormatVram(Measurement? used, Measurement? total)
        {
            if (used?.Status != MeasurementStatus.Available || !used.Value.HasValue)
                return "N/A";
            if (total?.Status == MeasurementStatus.Available && total.Value is { } t && t > 0)
            {
                var pct = used.Value.Value / t * 100.0;
                return $"{FormatBytes(used.Value.Value)} / {FormatBytes(t)} ({pct:F1}%)";
            }
            return FormatBytes(used.Value.Value);
        }

        private static string FormatUsage(Measurement? used, Measurement? total, Measurement? percent)
        {
            if (percent?.Status == MeasurementStatus.Available && percent.Value.HasValue)
            {
                var usedStr = FormatBytesValue(used);
                var totalStr = FormatBytesValue(total);
                return $"{usedStr} / {totalStr} ({percent.Value:F1}%)";
            }
            return FormatBytesValue(used);
        }

        private static string FormatNetworkSpeed(Measurement? m) =>
            m?.Status == MeasurementStatus.Available && m.Value.HasValue
                ? FormatNetworkSpeed(m.Value.Value)
                : "N/A";

        private static string FormatNetworkSpeed(double bytesPerSec)
        {
            if (bytesPerSec < 0) bytesPerSec = 0;
            if (bytesPerSec >= 1024.0 * 1024 * 1024)
                return $"{bytesPerSec / (1024.0 * 1024.0 * 1024.0):F1} GB/s";
            if (bytesPerSec >= 1024.0 * 1024)
                return $"{bytesPerSec / (1024.0 * 1024.0):F1} MB/s";
            if (bytesPerSec >= 1024.0)
                return $"{bytesPerSec / 1024.0:F1} KB/s";
            return $"{bytesPerSec:F0} B/s";
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// Compatibility presenter that adapts an immutable <see cref="MetricsSnapshot"/>
    /// into the legacy flat payload contract.
    ///
    /// <para>
    /// The widget is no longer served by this presenter: it is served by
    /// <see cref="WidgetPayloadPresenter"/>, which is manufacturer-neutral
    /// and names the devices it presents. This presenter remains so the
    /// tests that pin the legacy contract keep testing something.
    /// </para>
    ///
    /// <para>
    /// The legacy <c>igpu</c>/<c>dgpu</c> keys have been removed: they
    /// bucketed GPUs by integration class, which collapsed two different
    /// cards into one value and could name neither of them.
    /// </para>
    /// </summary>
    public static class LegacyPayloadPresenter
    {
        public static Dictionary<string, string> Present(MetricsSnapshot snapshot, DateTimeOffset? now = null)
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
            var primaryGpu = LhmSnapshotMapper.SelectPrimaryGpu(
                snapshot.Gpus,
                snapshot,
                snapshot.GpuLoadPercent,
                snapshot.GpuLoadDeviceId,
                snapshot.GpuTemperatureC,
                snapshot.GpuTemperatureDeviceId,
                snapshot.GpuVramUsedBytes,
                snapshot.GpuVramUsedDeviceId,
                snapshot.GpuVramTotalBytes,
                snapshot.GpuVramTotalDeviceId);

            payload["gpu"] = FormatPercent(MeasurementOf(Reading(snapshot, primaryGpu?.DeviceId, "LoadPercent")));
            payload["gpuTemp"] = FormatTemperature(MeasurementOf(Reading(snapshot, primaryGpu?.DeviceId, "TemperatureC")));

            // The memory figure and the heading it sits under come from
            // the same resolver the production presenter uses, so the
            // legacy contract cannot drift from it. The legacy presenter
            // reads both figures from the presented device's own
            // readings, so the device check is satisfied by construction.
            var gpuVram = MeasurementOf(Reading(snapshot, primaryGpu?.DeviceId, "VramUsedBytes"));
            var gpuVramTotal = MeasurementOf(Reading(snapshot, primaryGpu?.DeviceId, "VramTotalBytes"));
            var gpuMemory = GpuMemoryPresentation.Resolve(
                gpuVram,
                primaryGpu?.DeviceId,
                gpuVramTotal,
                primaryGpu?.DeviceId,
                primaryGpu?.DeviceId,
                now ?? DateTimeOffset.UtcNow);

            payload["gpuVram"] = gpuMemory.Display;
            payload["gpuVramLabel"] = GpuMemoryPresentation.LabelText(gpuMemory.Label);
            payload["gpuVramUsed"] = FormatBytesValue(gpuVram);

            // The legacy igpu/dgpu keys are gone. They bucketed GPUs by
            // integration class, which collapsed two different cards into
            // one value and could not name either of them. The
            // manufacturer-neutral payload presents the selected device and,
            // where the layout has room, the other devices individually.

            // --- RAM ---
            payload["ram"] = FormatUsage(snapshot.RamUsedBytes, snapshot.RamTotalBytes, snapshot.RamPercent);
            payload["ramUsed"] = FormatBytesValue(snapshot.RamUsedBytes);
            payload["ramTotal"] = FormatBytesValue(snapshot.RamTotalBytes);
            payload["ramCombined"] = $"{payload["ramUsed"]} / {payload["ramTotal"]}";
            payload["ramPercent"] = FormatPercent(snapshot.RamPercent);
            payload["ramCompact"] = $"{payload["ramUsed"]} ({payload["ramPercent"]})";
            payload["ramCapacityCompact"] = ByteFormatter.FormatCapacityCompact(
                GetValueOrDefault(snapshot.RamUsedBytes),
                GetValueOrDefault(snapshot.RamTotalBytes));

            // --- Disk ---
            payload["disk"] = FormatUsage(snapshot.DiskUsedBytes, snapshot.DiskTotalBytes, snapshot.DiskPercent);
            payload["diskUsed"] = FormatBytesValue(snapshot.DiskUsedBytes);
            payload["diskTotal"] = FormatBytesValue(snapshot.DiskTotalBytes);
            payload["diskCombined"] = $"{payload["diskUsed"]} / {payload["diskTotal"]}";
            payload["diskPercent"] = FormatPercent(snapshot.DiskPercent);
            payload["diskCompact"] = $"{payload["diskUsed"]} ({payload["diskPercent"]})";
            payload["diskCapacityCompact"] = ByteFormatter.FormatCapacityCompact(
                GetValueOrDefault(snapshot.DiskUsedBytes),
                GetValueOrDefault(snapshot.DiskTotalBytes));

            // --- Network ---
            payload["netDown"] = FormatNetworkSpeed(snapshot.NetworkDownloadBytesPerSec);
            payload["netUp"] = FormatNetworkSpeed(snapshot.NetworkUploadBytesPerSec);

            // --- Timestamps ---
            payload["lastUpdated"] = snapshot.Timestamp.ToString("h:mm:ss tt");

            // --- ACPI thermal zone (unidentified, separate from CPU/GPU) ---
            // The reading is a scalar measurement, not a device reading:
            // the ACPI zone names no component. It is read from the
            // snapshot's field so the status travels with it.
            payload["acpiThermalZoneTemp"] = FormatTemperature(snapshot.AcpiThermalZoneTemperatureC);

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

            // The presented device has no reading for this metric. Another
            // device's reading is not used in its place: all four legacy GPU
            // keys describe one device, and borrowing a figure from a
            // different card would report it under this one's name — and
            // pairing one card's usage with another card's capacity would
            // produce a percentage no device reports.
            return null;
        }

        private static string FormatPercent(Measurement? m) =>
            m?.Status == MeasurementStatus.Available && m.Value.HasValue
                ? ByteFormatter.FormatPercent(m.Value.Value)
                : "N/A";

        private static string FormatTemperature(Measurement? m) =>
            m?.Status == MeasurementStatus.Available && m.Value.HasValue
                ? ByteFormatter.FormatTemperature(m.Value.Value)
                : "N/A";

        private static string FormatClock(Measurement? m) =>
            m?.Status == MeasurementStatus.Available && m.Value.HasValue
                ? ByteFormatter.FormatClock(m.Value.Value)
                : "N/A";

        private static string FormatBytesValue(Measurement? m) =>
            m?.Status == MeasurementStatus.Available && m.Value.HasValue
                ? ByteFormatter.FormatBytes(m.Value.Value)
                : "N/A";

        /// <summary>
        /// Formats a used/total/percent triple, falling back to the used
        /// amount when the percentage is not available.
        ///
        /// <para>
        /// The three figures are system-level, so there is no device to
        /// attribute them to. What still has to hold is that they describe
        /// one coherent measurement: a capacity of zero cannot carry a
        /// percentage, a usage above the capacity proves the figures come
        /// from different readings, and a figure that is unavailable must
        /// not be shown beside a percentage computed without it.
        /// </para>
        /// </summary>
        private static string FormatUsage(Measurement? used, Measurement? total, Measurement? percent)
        {
            if (used?.Status != MeasurementStatus.Available || !used.Value.HasValue)
                return FormatBytesValue(used);

            var usedValue = used.Value.Value;

            if (total?.Status != MeasurementStatus.Available || !total.Value.HasValue)
                return FormatBytesValue(used);

            var totalValue = total.Value.Value;

            if (totalValue <= 0 || usedValue > totalValue)
                return FormatBytesValue(used);

            var usedStr = ByteFormatter.FormatBytes(usedValue);
            var totalStr = ByteFormatter.FormatBytes(totalValue);

            if (percent?.Status != MeasurementStatus.Available || !percent.Value.HasValue)
                return $"{usedStr} / {totalStr}";

            return $"{usedStr} / {totalStr} ({percent.Value.Value:F1}%)";
        }

        private static string FormatNetworkSpeed(Measurement? m) =>
            m?.Status == MeasurementStatus.Available && m.Value.HasValue
                ? ByteFormatter.FormatNetworkSpeed(m.Value.Value)
                : "N/A";

        /// <summary>
        /// Gets the double value of a measurement or 0 if not available.
        /// </summary>
        private static double GetValueOrDefault(Measurement? measurement)
        {
            if (measurement is not null
                && measurement.Status == MeasurementStatus.Available
                && measurement.Value.HasValue)
            {
                return measurement.Value.Value;
            }
            return 0;
        }
    }
}

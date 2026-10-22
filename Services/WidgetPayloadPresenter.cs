using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// Formats an immutable <see cref="MetricsSnapshot"/> into the flat,
    /// manufacturer-neutral payload the widget templates bind against.
    ///
    /// <para><b>Why this exists</b></para>
    ///
    /// <para>
    /// Payload formatting used to live inside the widget provider, where it
    /// could not be tested without a running widget host. Everything that
    /// decides what a widget shows now lives here, so it can be exercised
    /// against fixtures for every GPU vendor, every device count, and every
    /// combination of missing sensors.
    /// </para>
    ///
    /// <para><b>Presentation rules</b></para>
    ///
    /// <list type="bullet">
    /// <item><description>
    /// <b>Vendor-neutral naming.</b> A GPU is presented by its own display
    /// name and vendor, never by a hard-coded "Intel"/"NVIDIA" label and
    /// never by a synthetic "iGPU"/"dGPU" bucket. The only classification
    /// shown is <i>integrated</i> or <i>discrete</i>, and it is shown only
    /// when the backend actually determined it. When the class is unknown,
    /// no classification appears at all rather than a guess.
    /// </description></item>
    /// <item><description>
    /// <b>No raw exception text.</b> A value that could not be read is
    /// presented as "N/A"; a value that is known but too old is presented as
    /// "stale". The reason a read failed is never surfaced, and no exception
    /// message ever reaches the payload.
    /// </description></item>
    /// <item><description>
    /// <b>Last successful sample time.</b> The timestamp shown is the time
    /// of the most recent <i>successful</i> reading, not the time the
    /// payload was published. A widget that is publishing a stale sample
    /// says so, rather than implying the reading is current.
    /// </description></item>
    /// <item><description>
    /// <b>Zero is never substituted for an unavailable value.</b>
    /// </description></item>
    /// </list>
    /// </summary>
    public static class WidgetPayloadPresenter
    {
        /// <summary>Shown in place of a value that could not be read.</summary>
        public const string NotAvailable = "N/A";

        /// <summary>Shown for a value that is known but older than the current sample.</summary>
        public const string StaleMarker = "stale";

        /// <summary>
        /// How much older than the current sample a successful reading may be
        /// before the widget is marked stale. One collection interval plus a
        /// margin, so a single missed sample does not mark the widget stale.
        /// </summary>
        public static readonly TimeSpan StaleThreshold = TimeSpan.FromSeconds(30);

        /// <summary>
        /// The most additional GPUs the payload ever describes. Each layout
        /// shows as many of them as fit; the payload carries them all so one
        /// payload serves every size.
        /// </summary>
        public const int MaxExtraDevices = 3;

        // --- Text keys ---------------------------------------------------

        public const string CpuKey = "cpu";
        public const string CpuClockKey = "cpuClock";
        public const string CpuTempKey = "cpuTemp";
        public const string CpuNameKey = "cpuName";
        public const string CpuVendorKey = "cpuVendor";

        public const string GpuKey = "gpu";
        public const string GpuTempKey = "gpuTemp";
        public const string GpuVramKey = "gpuVram";
        public const string GpuVramUsedKey = "gpuVramUsed";
        public const string GpuVramLabelKey = "gpuVramLabel";
        public const string GpuVramLabelCompactKey = "gpuVramLabelCompact";
        public const string GpuNameKey = "gpuName";
        public const string GpuVendorKey = "gpuVendor";
        public const string GpuIntegrationKey = "gpuIntegration";
        public const string GpuLabelKey = "gpuLabel";
        public const string GpuLabelCompactKey = "gpuLabelCompact";
        public const string GpuExtrasKey = "gpuExtras";
        public const string GpuSelectionNoticeKey = "gpuSelectionNotice";

        public const string RamKey = "ram";
        public const string RamUsedKey = "ramUsed";
        public const string RamTotalKey = "ramTotal";
        public const string RamCombinedKey = "ramCombined";
        public const string RamPercentKey = "ramPercent";
        public const string RamCompactKey = "ramCompact";

        public const string DiskKey = "disk";
        public const string DiskUsedKey = "diskUsed";
        public const string DiskTotalKey = "diskTotal";
        public const string DiskCombinedKey = "diskCombined";
        public const string DiskPercentKey = "diskPercent";
        public const string DiskCompactKey = "diskCompact";

        public const string NetDownKey = "netDown";
        public const string NetUpKey = "netUp";
        public const string TempSummaryKey = "tempSummary";
        public const string AcpiThermalZoneTempKey = "acpiThermalZoneTemp";

        public const string SampleTimeKey = "sampleTime";
        public const string LastUpdatedKey = "lastUpdated";

        /// <summary>
        /// Every text key the payload produces. The templates bind against
        /// exactly these names.
        /// </summary>
        public static readonly IReadOnlyList<string> TextKeys = new[]
        {
            CpuKey, CpuClockKey, CpuTempKey, CpuNameKey, CpuVendorKey,
            GpuKey, GpuTempKey, GpuVramKey, GpuVramUsedKey, GpuVramLabelKey,
            GpuVramLabelCompactKey,
            GpuNameKey, GpuVendorKey, GpuIntegrationKey, GpuLabelKey, GpuLabelCompactKey,
            GpuExtrasKey, GpuSelectionNoticeKey,
            RamKey, RamUsedKey, RamTotalKey, RamCombinedKey, RamPercentKey, RamCompactKey,
            DiskKey, DiskUsedKey, DiskTotalKey, DiskCombinedKey, DiskPercentKey, DiskCompactKey,
            NetDownKey, NetUpKey, TempSummaryKey, AcpiThermalZoneTempKey,
            SampleTimeKey, LastUpdatedKey,
        };

        /// <summary>
        /// The name of the key describing the <i>n</i>-th additional GPU.
        /// </summary>
        public static string ExtraKey(int index) => $"gpuExtra{index}";

        /// <summary>
        /// The name of the flag saying whether the <i>n</i>-th additional GPU
        /// exists.
        /// </summary>
        public static string ExtraAvailableKey(int index) => $"gpuExtra{index}Available";

        // --- Flag keys ---------------------------------------------------

        public const string GpuDevicePresentKey = "gpuDevicePresent";
        public const string GpuHasExtraKey = "gpuHasExtra";
        public const string SampleIsStaleKey = "sampleIsStale";
        public const string GpuSelectionMissingKey = "gpuSelectionMissing";

        /// <summary>
        /// Every boolean key the payload produces. These are consumed by
        /// <c>$when</c> conditions in the templates, so they are serialized
        /// as real JSON booleans rather than strings.
        /// </summary>
        public static readonly IReadOnlyList<string> FlagKeys = new[]
        {
            GpuDevicePresentKey,
            GpuHasExtraKey,
            SampleIsStaleKey,
            GpuSelectionMissingKey,
            ExtraAvailableKey(1),
            ExtraAvailableKey(2),
            ExtraAvailableKey(3),
        };

        /// <summary>
        /// Builds the payload for a snapshot.
        /// </summary>
        /// <param name="snapshot">The completed collection.</param>
        /// <param name="selectedGpuInstanceId">
        /// The instance id of the GPU the user selected, or null when no
        /// selection has been made. The instance id is the device's
        /// per-instance identity — never an enumeration index.
        /// </param>
        /// <param name="maxExtraDevices">
        /// How many additional GPUs may be described, beyond the presented
        /// one. This is a cap, not a quota: a device is only described when
        /// there is one to describe.
        /// </param>
        public static WidgetPayload Present(
            MetricsSnapshot? snapshot,
            string? selectedGpuInstanceId = null,
            int maxExtraDevices = MaxExtraDevices)
        {
            var text = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            if (snapshot is null)
                return FillEmpty(text, flags);

            // --- CPU -------------------------------------------------------
            // The CPU figures are system-level: the CPU is not a
            // user-selectable device, so the merged snapshot values are
            // always the right ones to show.
            var cpuDevice = LhmSnapshotMapper.SelectPrimaryCpu(snapshot.Cpus);

            text[CpuKey] = FormatPercent(snapshot.CpuLoadPercent);
            text[CpuClockKey] = FormatClock(snapshot.CpuClockMHz);
            text[CpuTempKey] = FormatTemperature(snapshot.CpuTemperatureC);
            text[CpuNameKey] = FormatDeviceName(cpuDevice?.DisplayName);
            text[CpuVendorKey] = FormatVendor(cpuDevice?.Vendor);

            // --- GPU: the presented device -----------------------------------
            var selection = GpuSelectionPolicy.Resolve(snapshot.Gpus, selectedGpuInstanceId);
            var gpu = selection.Device;

            // The snapshot's scalar GPU figures describe whichever device the
            // collection chose as primary. They are a valid fallback for the
            // default selection, but they must never be attributed to a
            // device the user explicitly chose: doing so would report one
            // card's figures as another's.
            //
            // Each figure carries the id of the device it describes, and the
            // check is per figure: the load may come from the backend's
            // primary GPU while the temperature comes from the reconciled
            // NVIDIA fallback for a different card. A figure is used only
            // when it names the device being presented.
            var gpuLoad = ResolveMetric(
                snapshot, gpu?.DeviceId, LhmSensorSelection.GpuLoadMetric,
                snapshot.GpuLoadPercent, snapshot.GpuLoadDeviceId).Measurement;
            var gpuTemp = ResolveMetric(
                snapshot, gpu?.DeviceId, LhmSensorSelection.GpuTemperatureMetric,
                snapshot.GpuTemperatureC, snapshot.GpuTemperatureDeviceId).Measurement;
            var gpuVramUsed = ResolveMetric(
                snapshot, gpu?.DeviceId, LhmSensorSelection.GpuMemoryUsedMetric,
                snapshot.GpuVramUsedBytes, snapshot.GpuVramUsedDeviceId);
            var gpuVramTotal = ResolveMetric(
                snapshot, gpu?.DeviceId, LhmSensorSelection.GpuMemoryTotalMetric,
                snapshot.GpuVramTotalBytes, snapshot.GpuVramTotalDeviceId);

            text[GpuNameKey] = FormatDeviceName(gpu?.DisplayName);
            text[GpuVendorKey] = FormatVendor(gpu?.Vendor);
            text[GpuIntegrationKey] = FormatIntegration(gpu?.GpuIntegration);
            text[GpuLabelKey] = ComposeDeviceLabel(gpu, maxLength: 40);
            text[GpuLabelCompactKey] = ComposeDeviceLabel(gpu, maxLength: 24, includeIntegration: false);

            text[GpuKey] = FormatPercent(gpuLoad);
            text[GpuTempKey] = FormatTemperature(gpuTemp);

            // The memory label and the memory figures are resolved
            // together, because the label is a claim about the same
            // readings the figures are drawn from. A label that outlived
            // the figure it described would be a claim about a card the
            // widget is no longer showing.
            var gpuMemory = GpuMemoryPresentation.Resolve(
                gpuVramUsed.Measurement,
                gpuVramUsed.DeviceId,
                gpuVramTotal.Measurement,
                gpuVramTotal.DeviceId,
                gpu?.DeviceId);

            text[GpuVramUsedKey] = FormatBytesValue(gpuVramUsed.Measurement);
            text[GpuVramKey] = gpuMemory.Display;
            text[GpuVramLabelKey] = GpuMemoryPresentation.LabelText(gpuMemory.Label);
            text[GpuVramLabelCompactKey] = GpuMemoryPresentation.CompactLabelText(gpuMemory.Label);

            flags[GpuDevicePresentKey] = gpu is not null;
            flags[GpuSelectionMissingKey] = selection.Outcome == GpuSelectionOutcome.SelectionMissing;

            // When the selected device is gone, the widget says so rather
            // than silently showing a different card as if the choice had
            // been honoured. The notice names no raw error text.
            text[GpuSelectionNoticeKey] = selection.Outcome switch
            {
                GpuSelectionOutcome.SelectionMissing =>
                    "Selected GPU is unavailable — showing the default device.",
                GpuSelectionOutcome.NoGpuAvailable =>
                    "No GPU detected.",
                _ => string.Empty,
            };

            // --- GPU: additional devices -----------------------------------
            var extras = BuildExtras(snapshot, gpu, maxExtraDevices);
            for (var i = 0; i < MaxExtraDevices; i++)
            {
                var present = i < extras.Count;
                text[ExtraKey(i + 1)] = present ? extras[i] : string.Empty;
                flags[ExtraAvailableKey(i + 1)] = present;
            }

            text[GpuExtrasKey] = string.Join("\n", extras);
            flags[GpuHasExtraKey] = extras.Count > 0;

            // --- RAM ---------------------------------------------------------
            text[RamKey] = FormatUsage(snapshot.RamUsedBytes, snapshot.RamTotalBytes, snapshot.RamPercent);
            text[RamUsedKey] = FormatBytesValue(snapshot.RamUsedBytes);
            text[RamTotalKey] = FormatBytesValue(snapshot.RamTotalBytes);
            text[RamCombinedKey] = $"{text[RamUsedKey]} / {text[RamTotalKey]}";
            text[RamPercentKey] = FormatPercent(snapshot.RamPercent);
            text[RamCompactKey] = $"{text[RamUsedKey]} ({text[RamPercentKey]})";

            // --- Disk --------------------------------------------------------
            text[DiskKey] = FormatUsage(snapshot.DiskUsedBytes, snapshot.DiskTotalBytes, snapshot.DiskPercent);
            text[DiskUsedKey] = FormatBytesValue(snapshot.DiskUsedBytes);
            text[DiskTotalKey] = FormatBytesValue(snapshot.DiskTotalBytes);
            text[DiskCombinedKey] = $"{text[DiskUsedKey]} / {text[DiskTotalKey]}";
            text[DiskPercentKey] = FormatPercent(snapshot.DiskPercent);
            text[DiskCompactKey] = $"{text[DiskUsedKey]} ({text[DiskPercentKey]})";

            // --- Network -----------------------------------------------------
            text[NetDownKey] = FormatNetworkSpeed(snapshot.NetworkDownloadBytesPerSec);
            text[NetUpKey] = FormatNetworkSpeed(snapshot.NetworkUploadBytesPerSec);

            // --- Temperatures ------------------------------------------------
            // The ACPI thermal-zone average comes from an unidentified
            // sensor, so it is published as its own scalar measurement
            // rather than as a device reading. It is read from the
            // snapshot's field, which carries the status the collector
            // recorded for it.
            text[AcpiThermalZoneTempKey] = FormatTemperature(snapshot.AcpiThermalZoneTemperatureC);

            text[TempSummaryKey] = FormatTemperatureSummary(
                FormatTemperature(snapshot.CpuTemperatureC),
                FormatTemperature(gpuTemp));

            // --- Provenance --------------------------------------------------
            // The sample time is the most recent successful reading, not the
            // publish time. A widget whose last successful read is older
            // than the current collection is marked stale rather than
            // implying the value is current.
            var sample = MostRecentSuccessfulSample(snapshot);
            var isStale = IsStale(sample, snapshot.Timestamp);
            text[SampleTimeKey] = sample?.ToString("h:mm:ss tt", CultureInfo.CurrentCulture) ?? NotAvailable;
            text[LastUpdatedKey] = text[SampleTimeKey];
            flags[SampleIsStaleKey] = isStale;

            return new WidgetPayload(text, flags);
        }

        /// <summary>
        /// Fills a payload with the "not available" placeholder for every
        /// key, so a template never binds to a missing property.
        /// </summary>
        private static WidgetPayload FillEmpty(
            Dictionary<string, string> text,
            Dictionary<string, bool> flags)
        {
            foreach (var key in TextKeys)
            {
                if (!text.ContainsKey(key))
                    text[key] = NotAvailable;
            }

            foreach (var key in FlagKeys)
            {
                if (!flags.ContainsKey(key))
                    flags[key] = false;
            }

            // The extra-device slots are empty rather than unavailable: an
            // empty line is not a missing value, it is the absence of a
            // device.
            for (var i = 0; i < MaxExtraDevices; i++)
            {
                text[ExtraKey(i + 1)] = string.Empty;
                flags[ExtraAvailableKey(i + 1)] = false;
            }

            text[GpuExtrasKey] = string.Empty;
            text[GpuIntegrationKey] = string.Empty;

            // The memory heading is a neutral label rather than the
            // unavailable placeholder: "N/A" is the right answer for a
            // figure that could not be read, but the heading is not a
            // figure. It names what the column is, and with no reading at
            // all it claims no memory pool.
            text[GpuVramLabelKey] = GpuMemoryPresentation.UnspecifiedLabel;
            text[GpuVramLabelCompactKey] = GpuMemoryPresentation.UnspecifiedLabel;

            return new WidgetPayload(text, flags);
        }

        // ------------------------------------------------------------------
        // Formatting helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Describes the GPUs other than the presented one, in the
        /// presentation order, up to <paramref name="max"/> of them.
        /// </summary>
        private static List<string> BuildExtras(
            MetricsSnapshot snapshot,
            HardwareDevice? presented,
            int max)
        {
            var extras = new List<string>();
            if (max <= 0)
                return extras;

            foreach (var device in GpuSelectionPolicy.OrderForPresentation(snapshot.Gpus))
            {
                if (extras.Count >= max)
                    break;

                // The presented device is not repeated among the extras.
                if (presented is not null
                    && string.Equals(device.DeviceId, presented.DeviceId, StringComparison.Ordinal))
                {
                    continue;
                }

                var load = DeviceMetric(snapshot, device.DeviceId, LhmSensorSelection.GpuLoadMetric);
                var temp = DeviceMetric(snapshot, device.DeviceId, LhmSensorSelection.GpuTemperatureMetric);

                extras.Add(FormatExtraDevice(device, load, temp));
            }

            return extras;
        }

        /// <summary>
        /// Formats one additional GPU as a single line: its name, its vendor
        /// when known, and the values it has.
        ///
        /// The name is truncated so a long device name cannot push the line
        /// out of the layout. The line gets a wider budget than the
        /// presented device's label, because it stands on its own and is
        /// the only place the other card is named.
        /// </summary>
        private static string FormatExtraDevice(
            HardwareDevice device,
            Measurement? load,
            Measurement? temp)
        {
            var label = ComposeDeviceLabel(device, maxLength: 40);
            return $"{label} — {FormatPercent(load)}, {FormatTemperature(temp)}";
        }

        /// <summary>
        /// Builds the label a device is shown under: its name, its vendor
        /// when the name does not already carry it, and its integration
        /// class when — and only when — that class is known.
        /// </summary>
        /// <param name="device">The device to describe, or null.</param>
        /// <param name="maxLength">
        /// The longest label that may be produced. The limit applies to the
        /// <i>finished</i> label, not just to the name: a name truncated to
        /// fit, with the classification appended afterwards, would still
        /// overflow the layout it was measured for.
        /// </param>
        /// <param name="includeIntegration">
        /// Whether the integration class may be shown. The compact layout
        /// leaves it out: there, the device's identity is the only thing
        /// that fits, and the class is a secondary detail.
        /// </param>
        public static string ComposeDeviceLabel(
            HardwareDevice? device,
            int maxLength,
            bool includeIntegration = true)
        {
            if (device is null)
                return NotAvailable;

            var name = string.IsNullOrWhiteSpace(device.DisplayName)
                ? NotAvailable
                : device.DisplayName.Trim();

            var vendor = string.IsNullOrWhiteSpace(device.Vendor) ? null : device.Vendor.Trim();

            // Many driver names already start with the brand ("NVIDIA
            // GeForce ..."), so the vendor is only prefixed when the name
            // does not already say it. Otherwise the label would read
            // "NVIDIA NVIDIA GeForce ...".
            var baseLabel = vendor is not null
                            && !name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)
                ? $"{vendor} {name}"
                : name;

            var integration = includeIntegration
                ? FormatIntegration(device.GpuIntegration)
                : string.Empty;

            if (string.IsNullOrEmpty(integration))
                return Truncate(baseLabel, maxLength);

            // The classification is kept whenever it is known: it is a fact
            // the backend established, and it is what tells two devices
            // apart when their names are alike. The name is what gives way,
            // so the finished label still fits the space it was given.
            var suffix = $" ({integration})";
            var budget = maxLength - suffix.Length;

            if (budget <= 0)
            {
                // No room for both. The name is the more useful of the two.
                return Truncate(baseLabel, maxLength);
            }

            return Truncate(baseLabel, budget) + suffix;
        }

        /// <summary>
        /// Truncates a device name to a maximum length, adding an ellipsis
        /// when it was shortened.
        /// </summary>
        public static string Truncate(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var trimmed = value.Trim();
            if (maxLength <= 0 || trimmed.Length <= maxLength)
                return trimmed;

            if (maxLength <= 1)
                return trimmed.Substring(0, maxLength);

            return trimmed.Substring(0, maxLength - 1) + "…";
        }

        /// <summary>
        /// Formats a device name for display. An absent name is reported as
        /// unavailable rather than as an empty string.
        /// </summary>
        private static string FormatDeviceName(string? displayName) =>
            string.IsNullOrWhiteSpace(displayName) ? NotAvailable : displayName.Trim();

        /// <summary>
        /// Formats a vendor name. An unknown vendor is reported as
        /// unavailable rather than guessed.
        /// </summary>
        private static string FormatVendor(string? vendor) =>
            string.IsNullOrWhiteSpace(vendor) ? NotAvailable : vendor.Trim();

        /// <summary>
        /// Formats the integration class. The class is shown only when it is
        /// actually known: "integrated", "discrete", or nothing at all.
        /// </summary>
        private static string FormatIntegration(GpuIntegration? integration) => integration switch
        {
            GpuIntegration.Integrated => "integrated",
            GpuIntegration.Discrete => "discrete",
            _ => string.Empty,
        };

        /// <summary>
        /// Combines the CPU and GPU temperatures into one summary, showing
        /// only the ones that are available.
        /// </summary>
        private static string FormatTemperatureSummary(string cpuTemp, string gpuTemp)
        {
            var parts = new[] { cpuTemp, gpuTemp }
                .Where(p => p != NotAvailable)
                .ToList();

            return parts.Count == 0 ? NotAvailable : string.Join(" | ", parts);
        }

        /// <summary>
        /// Finds the most recent successful reading time in the snapshot.
        ///
        /// A measurement that failed or was unsupported carries no successful
        /// update time, so it never contributes. This is the time the value
        /// on screen was actually read, which is what the widget should
        /// show — not the time the payload was serialized.
        /// </summary>
        public static DateTimeOffset? MostRecentSuccessfulSample(MetricsSnapshot snapshot)
        {
            if (snapshot is null)
                return null;

            DateTimeOffset? latest = null;

            void Consider(Measurement? measurement)
            {
                if (measurement?.LastSuccessfulUpdate is not { } stamp)
                    return;
                if (latest is null || stamp > latest.Value)
                    latest = stamp;
            }

            foreach (var reading in snapshot.Readings)
                Consider(reading.Measurement);

            Consider(snapshot.RamUsedBytes);
            Consider(snapshot.RamTotalBytes);
            Consider(snapshot.RamPercent);
            Consider(snapshot.DiskUsedBytes);
            Consider(snapshot.DiskTotalBytes);
            Consider(snapshot.DiskPercent);
            Consider(snapshot.NetworkDownloadBytesPerSec);
            Consider(snapshot.NetworkUploadBytesPerSec);
            Consider(snapshot.CpuLoadPercent);
            Consider(snapshot.CpuClockMHz);
            Consider(snapshot.CpuTemperatureC);
            Consider(snapshot.GpuLoadPercent);
            Consider(snapshot.GpuTemperatureC);
            Consider(snapshot.GpuVramUsedBytes);
            Consider(snapshot.GpuVramTotalBytes);
            Consider(snapshot.AcpiThermalZoneTemperatureC);

            return latest;
        }

        /// <summary>
        /// True when the newest successful reading is older than the current
        /// collection by more than the stale threshold.
        ///
        /// The comparison is against the snapshot's own timestamp, so a
        /// value that was read during this collection is never stale.
        /// </summary>
        private static bool IsStale(DateTimeOffset? sample, DateTimeOffset snapshotTimestamp)
        {
            if (sample is null)
                return false;

            return snapshotTimestamp - sample.Value > StaleThreshold;
        }

        // ------------------------------------------------------------------
        // Measurement formatting
        // ------------------------------------------------------------------

        /// <summary>
        /// The device-level measurement for a device, or null when the
        /// device does not report it.
        /// </summary>
        private static Measurement? DeviceMetric(
            MetricsSnapshot snapshot,
            string? deviceId,
            string metric)
        {
            if (deviceId is null)
                return null;

            return snapshot.Readings
                .FirstOrDefault(r => r.DeviceId == deviceId && r.Metric == metric)
                ?.Measurement;
        }

        /// <summary>
        /// Picks the measurement to present for one metric: the device's own
        /// reading when it has a usable one, otherwise the snapshot's scalar
        /// value — but only when the scalar is known to describe the same
        /// device.
        ///
        /// <para>
        /// The result carries the id of the device the chosen figure
        /// describes, so that a figure presented for this device is never
        /// paired with another figure that describes a different device.
        /// </para>
        /// </summary>
        /// <param name="snapshot">The completed collection.</param>
        /// <param name="deviceId">The device being presented, or null.</param>
        /// <param name="metric">The metric's name.</param>
        /// <param name="scalar">The snapshot's flattened figure for the metric.</param>
        /// <param name="scalarDeviceId">
        /// The device the scalar figure describes, or null when the
        /// collection could not attribute it.
        /// </param>
        private static ResolvedMetric ResolveMetric(
            MetricsSnapshot snapshot,
            string? deviceId,
            string metric,
            Measurement? scalar,
            string? scalarDeviceId)
        {
            var deviceValue = DeviceMetric(snapshot, deviceId, metric);

            if (IsUsable(deviceValue))
                return new ResolvedMetric(deviceValue, deviceId);

            // The scalar is a fallback for the device only when it names
            // that device. A figure attributed to a different card, or to
            // no card at all, is never presented under this device's name.
            var scalarMatches = deviceId is not null
                && scalarDeviceId is not null
                && string.Equals(scalarDeviceId, deviceId, StringComparison.Ordinal);

            if (!scalarMatches)
                return new ResolvedMetric(deviceValue, deviceId);

            // A device-level reading that exists but is unusable (no sensor,
            // or a failed read) is preferred over the scalar when the
            // scalar belongs to a different device, so the scalar is only
            // used when the device reported nothing at all.
            if (deviceValue is not null && !IsUsable(scalar))
                return new ResolvedMetric(deviceValue, deviceId);

            return IsUsable(scalar)
                ? new ResolvedMetric(scalar, scalarDeviceId)
                : new ResolvedMetric(deviceValue, deviceId);
        }

        /// <summary>
        /// One metric's chosen figure, and the device that figure
        /// describes.
        /// </summary>
        /// <param name="Measurement">The chosen figure, or null.</param>
        /// <param name="DeviceId">
        /// The device the figure describes, or null when it names no
        /// device.
        /// </param>
        private readonly record struct ResolvedMetric(Measurement? Measurement, string? DeviceId);

        /// <summary>
        /// Formats a measurement with a value formatter.
        ///
        /// A missing, failed or unsupported measurement is "N/A". A stale
        /// measurement is "stale", so the widget distinguishes "this sensor
        /// is not there" from "this value is out of date". A raw failure
        /// reason is never exposed.
        /// </summary>
        private static string Format(Measurement? measurement, Func<double, string> formatter)
        {
            if (measurement is null)
                return NotAvailable;

            if (measurement.Status == MeasurementStatus.Stale)
                return StaleMarker;

            if (measurement.Status != MeasurementStatus.Available || !measurement.Value.HasValue)
                return NotAvailable;

            return formatter(measurement.Value.Value);
        }

        /// <summary>
        /// Formats a percentage. A measurement that is not available, has
        /// failed, or is stale never becomes "0.0%".
        /// </summary>
        public static string FormatPercent(Measurement? measurement) =>
            Format(measurement, ByteFormatter.FormatPercent);

        /// <summary>
        /// Formats a temperature.
        /// </summary>
        public static string FormatTemperature(Measurement? measurement) =>
            Format(measurement, ByteFormatter.FormatTemperature);

        /// <summary>
        /// Formats a clock rate.
        /// </summary>
        public static string FormatClock(Measurement? measurement) =>
            Format(measurement, ByteFormatter.FormatClock);

        /// <summary>
        /// Formats a byte count.
        /// </summary>
        public static string FormatBytesValue(Measurement? measurement) =>
            Format(measurement, ByteFormatter.FormatBytes);

        /// <summary>
        /// Formats a used/total/percent triple, falling back to the used
        /// amount when the percentage is not available.
        ///
        /// <para>
        /// The three figures are system-level, so there is no device to
        /// attribute them to and no two memory pools to reconcile. What
        /// still has to hold is that the figures describe one coherent
        /// measurement: a capacity of zero cannot carry a percentage, a
        /// usage above the capacity proves the figures come from different
        /// readings, and a figure that is unavailable must not be shown
        /// beside a percentage that was computed without it.
        /// </para>
        /// </summary>
        private static string FormatUsage(Measurement? used, Measurement? total, Measurement? percent)
        {
            if (!IsUsable(used) || !used!.Value.HasValue)
                return FormatBytesValue(used);

            var usedValue = used.Value.Value;

            if (!IsUsable(total) || !total!.Value.HasValue)
                return FormatBytesValue(used);

            var totalValue = total.Value.Value;

            if (totalValue <= 0 || usedValue > totalValue)
                return FormatBytesValue(used);

            if (!IsUsable(percent) || !percent!.Value.HasValue)
                return $"{ByteFormatter.FormatBytes(usedValue)} / {ByteFormatter.FormatBytes(totalValue)}";

            return $"{ByteFormatter.FormatBytes(usedValue)} / {ByteFormatter.FormatBytes(totalValue)} ({percent.Value.Value:F1}%)";
        }

        /// <summary>
        /// Formats a network throughput.
        /// </summary>
        public static string FormatNetworkSpeed(Measurement? measurement) =>
            Format(measurement, ByteFormatter.FormatNetworkSpeed);

        /// <summary>
        /// True when a measurement carries a value that may be shown.
        ///
        /// A failed or unsupported measurement has no value, and a stale one
        /// is not presented as if it were current.
        /// </summary>
        private static bool IsUsable(Measurement? measurement) =>
            measurement is not null
            && measurement.Status == MeasurementStatus.Available
            && measurement.Value.HasValue;
    }
}

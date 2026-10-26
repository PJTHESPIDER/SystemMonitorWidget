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
        public const string CpuNameCompactKey = "cpuNameCompact";

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
        public const string GpuNameCompactKey = "gpuNameCompact";
        public const string GpuMemoryCapacityCompactKey = "gpuMemoryCapacityCompact";

        public const string RamKey = "ram";
        public const string RamUsedKey = "ramUsed";
        public const string RamTotalKey = "ramTotal";
        public const string RamCombinedKey = "ramCombined";
        public const string RamPercentKey = "ramPercent";
        public const string RamCompactKey = "ramCompact";
        public const string RamCapacityCompactKey = "ramCapacityCompact";

        public const string DiskKey = "disk";
        public const string DiskUsedKey = "diskUsed";
        public const string DiskTotalKey = "diskTotal";
        public const string DiskCombinedKey = "diskCombined";
        public const string DiskPercentKey = "diskPercent";
        public const string DiskCompactKey = "diskCompact";
        public const string DiskCapacityCompactKey = "diskCapacityCompact";

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
            CpuKey, CpuClockKey, CpuTempKey, CpuNameKey, CpuVendorKey, CpuNameCompactKey,
            GpuKey, GpuTempKey, GpuVramKey, GpuVramUsedKey, GpuVramLabelKey,
            GpuVramLabelCompactKey,
            GpuNameKey, GpuVendorKey, GpuIntegrationKey, GpuLabelKey, GpuLabelCompactKey,
            GpuExtrasKey, GpuSelectionNoticeKey, GpuNameCompactKey, GpuMemoryCapacityCompactKey,
            RamKey, RamUsedKey, RamTotalKey, RamCombinedKey, RamPercentKey, RamCompactKey, RamCapacityCompactKey,
            DiskKey, DiskUsedKey, DiskTotalKey, DiskCombinedKey, DiskPercentKey, DiskCompactKey, DiskCapacityCompactKey,
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
        /// there is one to describe with usable telemetry.
        /// </param>
        /// <param name="currentTime">
        /// The time at which the payload is being presented. Used for
        /// staleness checks. Defaults to <paramref name="snapshot"/>.Timestamp
        /// for backward compatibility, but callers that re-present the same
        /// snapshot after time has elapsed should pass the current time so
        /// that measurements older than the stale threshold are marked stale.
        /// </param>
        public static WidgetPayload Present(
            MetricsSnapshot? snapshot,
            string? selectedGpuInstanceId = null,
            int maxExtraDevices = MaxExtraDevices,
            DateTimeOffset? currentTime = null)
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

            var now = currentTime ?? snapshot.Timestamp;
            text[CpuKey] = FormatAtTime(snapshot.CpuLoadPercent, ByteFormatter.FormatPercent, now);
            text[CpuClockKey] = FormatAtTime(snapshot.CpuClockMHz, ByteFormatter.FormatClock, now);
            text[CpuTempKey] = FormatAtTime(snapshot.CpuTemperatureC, ByteFormatter.FormatTemperature, now);
            text[CpuNameKey] = FormatDeviceName(cpuDevice?.DisplayName);
            text[CpuVendorKey] = FormatVendor(cpuDevice?.Vendor);
            text[CpuNameCompactKey] = NormalizeHardwareName(cpuDevice?.DisplayName);

            // --- GPU: the presented device -----------------------------------
            var selection = GpuSelectionPolicy.Resolve(
                snapshot.Gpus,
                selectedGpuInstanceId,
                snapshot,
                snapshot.GpuLoadPercent,
                snapshot.GpuLoadDeviceId,
                snapshot.GpuTemperatureC,
                snapshot.GpuTemperatureDeviceId,
                snapshot.GpuVramUsedBytes,
                snapshot.GpuVramUsedDeviceId,
                snapshot.GpuVramTotalBytes,
                snapshot.GpuVramTotalDeviceId);
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
            //
            // IMPORTANT: When the collector's tracker is in grace mode (retaining
            // a device despite missing telemetry), the snapshot's scalar figures
            // describe that tracked device. We must present the tracked device
            // to match those figures, even if another device currently has telemetry.
            // The tracker's instance id is stored in GpuTrackedInstanceId.
            if (selectedGpuInstanceId is null && snapshot.GpuTrackedInstanceId is not null)
            {
                // Tracker is in grace mode: present the tracked device to match
                // the scalar figures. The selection policy would pick a device
                // with current telemetry, but that would attribute the tracked
                // device's scalar figures to the wrong card.
                var tracked = snapshot.Gpus.FirstOrDefault(g =>
                    string.Equals(g.InstanceId, snapshot.GpuTrackedInstanceId, StringComparison.OrdinalIgnoreCase));
                if (tracked is not null)
                {
                    gpu = tracked;
                }
            }

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
            text[GpuNameCompactKey] = NormalizeHardwareName(gpu?.DisplayName);

            text[GpuKey] = FormatAtTime(gpuLoad, ByteFormatter.FormatPercent, now);
            text[GpuTempKey] = FormatAtTime(gpuTemp, ByteFormatter.FormatTemperature, now);

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
                gpu?.DeviceId,
                now);

            text[GpuVramUsedKey] = FormatAtTime(gpuVramUsed.Measurement, ByteFormatter.FormatBytes, now);
            text[GpuVramKey] = gpuMemory.Display;
            text[GpuVramLabelKey] = GpuMemoryPresentation.LabelText(gpuMemory.Label);
            text[GpuVramLabelCompactKey] = GpuMemoryPresentation.CompactLabelText(gpuMemory.Label);

            // GPU memory capacity compact - use the compact display from the resolved presentation
            text[GpuMemoryCapacityCompactKey] = gpuMemory.CompactDisplay;

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
            var extras = BuildExtras(snapshot, gpu, maxExtraDevices, now);
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
            text[RamCapacityCompactKey] = FormatSystemCapacityCompact(
                snapshot.RamUsedBytes,
                snapshot.RamTotalBytes,
                now);

            // --- Disk --------------------------------------------------------
            text[DiskKey] = FormatUsage(snapshot.DiskUsedBytes, snapshot.DiskTotalBytes, snapshot.DiskPercent);
            text[DiskUsedKey] = FormatBytesValue(snapshot.DiskUsedBytes);
            text[DiskTotalKey] = FormatBytesValue(snapshot.DiskTotalBytes);
            text[DiskCombinedKey] = $"{text[DiskUsedKey]} / {text[DiskTotalKey]}";
            text[DiskPercentKey] = FormatPercent(snapshot.DiskPercent);
            text[DiskCompactKey] = $"{text[DiskUsedKey]} ({text[DiskPercentKey]})";
            text[DiskCapacityCompactKey] = FormatSystemCapacityCompact(
                snapshot.DiskUsedBytes,
                snapshot.DiskTotalBytes,
                now);

            // --- Network -----------------------------------------------------
            text[NetDownKey] = FormatNetworkSpeed(snapshot.NetworkDownloadBytesPerSec, now);
            text[NetUpKey] = FormatNetworkSpeed(snapshot.NetworkUploadBytesPerSec, now);

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
            var isStale = IsStale(sample, now);
            text[SampleTimeKey] = sample?.ToString("h:mm:ss tt", CultureInfo.CurrentCulture) ?? NotAvailable;
            text[LastUpdatedKey] = text[SampleTimeKey];
            flags[SampleIsStaleKey] = isStale;

            return new WidgetPayload(text, flags);
        }

        /// <summary>Production publication boundary: evaluates the immutable snapshot against the injected clock at publish time.</summary>
        public static WidgetPayload PresentForPublication(
            MetricsSnapshot? snapshot,
            string? selectedGpuInstanceId,
            TimeProvider timeProvider) =>
            Present(snapshot, selectedGpuInstanceId, currentTime: (timeProvider ?? throw new ArgumentNullException(nameof(timeProvider))).GetUtcNow());

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
            text[GpuMemoryCapacityCompactKey] = NotAvailable;
            text[RamCapacityCompactKey] = NotAvailable;
            text[DiskCapacityCompactKey] = NotAvailable;

            return new WidgetPayload(text, flags);
        }

        // ------------------------------------------------------------------
        // Formatting helpers
        // ------------------------------------------------------------------

        /// <summary>
        /// Formats RAM or disk capacity without discarding measurement status.
        /// Numeric formatting remains in <see cref="ByteFormatter"/>; this
        /// method decides which measurements are eligible to be combined.
        /// </summary>
        private static string FormatSystemCapacityCompact(Measurement? used, Measurement? total, DateTimeOffset? now = null)
        {
            // Apply publication-time aging
            if (now.HasValue)
            {
                used = MaybeStaleAt(used, now.Value);
                total = MaybeStaleAt(total, now.Value);
            }

            if (used?.Status == MeasurementStatus.Stale || total?.Status == MeasurementStatus.Stale)
                return StaleMarker;

            var usedIsAvailable = used?.Status == MeasurementStatus.Available;
            var totalIsAvailable = total?.Status == MeasurementStatus.Available;

            if (usedIsAvailable && !IsValidCapacityValue(used, allowZero: true))
                return NotAvailable;

            if (totalIsAvailable && !IsValidCapacityValue(total, allowZero: false))
                return NotAvailable;

            if (usedIsAvailable && totalIsAvailable)
            {
                var usedValue = used!.Value!.Value;
                var totalValue = total!.Value!.Value;
                if (usedValue > totalValue)
                    return NotAvailable;

                return ByteFormatter.FormatCapacityCompact(usedValue, totalValue);
            }

            if (usedIsAvailable)
                return $"{ByteFormatter.FormatBytes(used!.Value!.Value)} used";

            if (totalIsAvailable)
                return $"{ByteFormatter.FormatBytes(total!.Value!.Value)} total";

            return NotAvailable;
        }

        private static bool IsValidCapacityValue(Measurement? measurement, bool allowZero) =>
            measurement?.Value is double value
            && double.IsFinite(value)
            && value >= 0
            && (allowZero || value > 0);

        /// <summary>
        /// Describes the GPUs other than the presented one, in the
        /// presentation order, up to <paramref name="max"/> of them.
        ///
        /// Devices are included based on the collector's extras tracker,
        /// which retains eligible devices through a grace period for
        /// transient telemetry failures. Only devices the tracker has
        /// qualified (seen usable load or temperature) and has not yet
        /// removed are presented.
        /// </summary>
        private static List<string> BuildExtras(
            MetricsSnapshot snapshot,
            HardwareDevice? presented,
            int max,
            DateTimeOffset? now = null)
        {
            var extras = new List<string>();
            if (max <= 0)
                return extras;

            // Use the collector's tracked extra instance ids. These represent
            // devices that have qualified and are within their grace period.
            var trackedExtraInstanceIds = snapshot.GpuTrackedExtraInstanceIds ?? Array.Empty<string>();

            // Order them by the same presentation policy.
            var orderedExtras = GpuSelectionPolicy.OrderExtrasForPresentation(
                snapshot.Gpus,
                snapshot,
                snapshot.GpuLoadPercent,
                snapshot.GpuLoadDeviceId,
                snapshot.GpuTemperatureC,
                snapshot.GpuTemperatureDeviceId);

            foreach (var device in orderedExtras)
            {
                if (extras.Count >= max)
                    break;

                // The presented device is not repeated among the extras.
                if (presented is not null
                    && string.Equals(device.DeviceId, presented.DeviceId, StringComparison.Ordinal))
                {
                    continue;
                }

                // Only include devices that are in the tracker's eligible set.
                if (!trackedExtraInstanceIds.Contains(device.InstanceId, StringComparer.OrdinalIgnoreCase))
                    continue;

                var load = DeviceMetric(snapshot, device.DeviceId, LhmSensorSelection.GpuLoadMetric);
                var temp = DeviceMetric(snapshot, device.DeviceId, LhmSensorSelection.GpuTemperatureMetric);

                extras.Add(FormatExtraDevice(device, load, temp, now));
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
            Measurement? temp,
            DateTimeOffset? now = null)
        {
            var label = ComposeDeviceLabel(device, maxLength: 40);
            return $"{label} — {FormatAtTime(load, ByteFormatter.FormatPercent, now ?? DateTimeOffset.MinValue)}, {FormatAtTime(temp, ByteFormatter.FormatTemperature, now ?? DateTimeOffset.MinValue)}";
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
        /// Normalizes a hardware name for compact presentation.
        ///
        /// Contract:
        /// - Removes leading manufacturer prefix (AMD, Intel, NVIDIA, Nvidia) but preserves
        ///   "Core" in "Intel Core" and "Ryzen" in "AMD Ryzen"
        /// - Removes trailing "Processor", "CPU", "APU", "Graphics", "GPU" suffixes
        /// - Removes trademark markers (R), (TM), etc.
        /// - Preserves model numbers, meaningful suffixes (X3D, XT, Ti, Super, etc.),
        ///   and Laptop/Mobile distinctions
        /// - Trims excessive whitespace and normalizes to single spaces
        /// - Falls back to original trimmed name if normalization would produce empty
        /// - Does not modify the original full name (used for tooltips/full display)
        /// </summary>
        public static string NormalizeHardwareName(string? displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
                return NotAvailable;

            var name = displayName.Trim();

            // Remove trademark markers first (e.g., "Intel(R) Core(TM)" -> "Intel Core")
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\s*\([RTM]{1,2}\)\s*", " ");

            // Remove manufacturer prefixes but preserve model family identifiers (GeForce, Radeon, Ryzen, Core, etc.)
            // Order matters: LONGER specific patterns first, then generic manufacturer names
            // We remove "Intel " to leave "Core i7", but we remove only "NVIDIA " to leave "GeForce RTX"
            // "AMD " removes to leave "Ryzen 7", "Radeon RX" - preserving the model family
            var manufacturerPrefixes = new[]
            {
                "Intel(R) ",        // "Intel(R) i7" -> "i7", "Intel(R) Core i7" -> "Core i7"
                "Intel ",           // "Intel i7" -> "i7", "Intel Core i7" -> "Core i7"
                "AMD ",             // "AMD Ryzen 7" -> "Ryzen 7", "AMD Radeon RX" -> "Radeon RX", "AMD APU" -> "APU"
                "NVIDIA ",          // "NVIDIA GeForce RTX" -> "GeForce RTX", "NVIDIA Quadro" -> "Quadro", "NVIDIA GPU" -> "GPU"
                "Nvidia ",          // "Nvidia GeForce RTX" -> "GeForce RTX", "Nvidia Quadro" -> "Quadro", "Nvidia GPU" -> "GPU"
            };

            foreach (var prefix in manufacturerPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    name = name.Substring(prefix.Length);
                    break; // Only remove one prefix
                }
            }

            // Now handle the "Core " prefix that we want to keep for Intel Core models
            // If the name starts with "Core " and looks like an Intel CPU model (followed by i3/i5/i7/i9 or numbers),
            // KEEP "Core". Otherwise remove it (e.g., "Core 2 Duo" -> "2 Duo", "Core Solo" -> "Solo").
            if (name.StartsWith("Core ", StringComparison.OrdinalIgnoreCase))
            {
                var afterCore = name.Substring(5).TrimStart();
                // Check if it looks like an Intel Core model (i3/i5/i7/i9 or similar)
                bool isIntelCoreModel = System.Text.RegularExpressions.Regex.IsMatch(afterCore, @"^i[3579]\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    || System.Text.RegularExpressions.Regex.IsMatch(afterCore, @"^\d+[\s\-]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                if (!isIntelCoreModel)
                {
                    name = afterCore;
                }
            }

            // Also handle cases where suffix is followed by additional text like "@ 2.30GHz"
            // Remove " CPU" (but keep "@ 2.30GHz"), " Processor" etc. when followed by "@"
            var trailingSuffixesWithContent = new[]
            {
                " CPU", " cpu",
                " Processor", " processor",
                " APU", " apu",
            };

            foreach (var suffix in trailingSuffixesWithContent)
            {
                int idx = name.IndexOf(suffix + " @", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    // Remove the suffix but keep everything after " @"
                    var afterAt = name.Substring(idx + suffix.Length); // starts with " @..."
                    name = name.Substring(0, idx).Trim() + " " + afterAt.Trim();
                    break;
                }
            }

            // Remove trailing "Processor", "CPU", "APU", "Graphics", "GPU" (case-insensitive)
            // But preserve "Mobile", "Laptop" and meaningful model suffixes
            // These are removed only when they appear at the END of the name
            var trailingSuffixes = new[]
            {
                "Processor", " processor",
                "CPU", " cpu",
                "APU", " apu",
                "Graphics", " graphics",
                "GPU", " gpu",
            };

            foreach (var suffix in trailingSuffixes)
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    // Also handle leading space if present
                    int startIdx = name.Length - suffix.Length;
                    if (startIdx > 0 && name[startIdx - 1] == ' ')
                        startIdx--;
                    name = name.Substring(0, startIdx);
                    break; // Only remove one suffix
                }
            }

            // Normalize whitespace: collapse multiple spaces to single
            name = System.Text.RegularExpressions.Regex.Replace(name, @"\s+", " ").Trim();

            // Fallback to original if normalization produced empty
            if (string.IsNullOrWhiteSpace(name))
                return displayName.Trim();

            return name;
        }

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

        private static string FormatAtTime(Measurement? measurement, Func<double, string> formatter, DateTimeOffset now)
        {
            if (measurement?.Status == MeasurementStatus.Available
                && measurement.LastSuccessfulUpdate.HasValue
                && now - measurement.LastSuccessfulUpdate.Value > StaleThreshold)
                return StaleMarker;
            return Format(measurement, formatter);
        }

        /// <summary>
        /// Returns a stale copy of the measurement if it is Available but older
        /// than the stale threshold relative to <paramref name="now"/>.
        /// Otherwise returns the original measurement.
        /// </summary>
        private static Measurement? MaybeStaleAt(Measurement? measurement, DateTimeOffset now)
        {
            if (measurement?.Status == MeasurementStatus.Available
                && measurement.LastSuccessfulUpdate.HasValue
                && now - measurement.LastSuccessfulUpdate.Value > StaleThreshold)
            {
                // Create a stale copy preserving the value and source
                return Measurement.Stale(measurement.LastSuccessfulUpdate, measurement.Source);
            }
            return measurement;
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
        public static string FormatNetworkSpeed(Measurement? measurement, DateTimeOffset? now = null) =>
            FormatAtTime(measurement, ByteFormatter.FormatNetworkSpeed, now ?? DateTimeOffset.MinValue);

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

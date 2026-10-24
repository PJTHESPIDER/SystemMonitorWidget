using System;
using System.Collections.Generic;
using System.Linq;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// Why a metric could not be read.
    ///
    /// <para>
    /// The cases are the ones that need different answers, so they are
    /// named rather than lumped together:
    /// </para>
    ///
    /// <list type="number">
    /// <item><description>
    /// The device exposes no sensor of the required type at all. Nothing
    /// about permissions or naming is involved; the hardware has no such
    /// sensor.
    /// </description></item>
    /// <item><description>
    /// A sensor whose name is recognized exists, but its value is missing,
    /// NaN or infinite. This is the shape a permission-blocked sensor
    /// takes: the sensor is there and the reading is not.
    /// </description></item>
    /// <item><description>
    /// A readable sensor of the required type exists, but its name is not
    /// one the candidate list recognizes. The reading was available and was
    /// not used.
    /// </description></item>
    /// <item><description>
    /// A valid mapped reading was rejected or lost between the projection
    /// and the published scalar.
    /// </description></item>
    /// <item><description>
    /// The backend could not refresh the hardware node, so the sensor values
    /// it exposes are ones retained from an earlier read.
    /// </description></item>
    /// <item><description>
    /// The backend could not be initialized, or the hardware could not be
    /// reached at all.
    /// </description></item>
    /// </list>
    /// </summary>
    public enum LhmSensorDiagnosis
    {
        /// <summary>A reading was produced and published.</summary>
        ReadingAvailable,

        /// <summary>No sensor of the required type is enumerated.</summary>
        NoSensorEnumerated,

        /// <summary>Matching sensors exist but report no usable value.</summary>
        SensorValuesUnavailable,

        /// <summary>Readable sensors exist but their names are not recognized.</summary>
        SensorNamesNotRecognized,

        /// <summary>
        /// The only readable sensors of the required type were deliberately
        /// excluded — they describe something other than the metric.
        /// </summary>
        SensorDeliberatelyExcluded,

        /// <summary>A valid mapped reading was rejected or lost while merging.</summary>
        ReadingLostInMerge,

        /// <summary>The hardware update failed and old sensor values remain.</summary>
        HardwareUpdateFailed,

        /// <summary>The backend could not be initialized or the hardware reached.</summary>
        BackendUnavailable,
    }

    /// <summary>
    /// The state of the sensor backend for one read.
    ///
    /// <para>
    /// It answers the question the per-device traces cannot: was the
    /// backend itself able to run? An empty device list means one of two
    /// very different things — the backend never initialized, or it
    /// initialized and then failed to reach the hardware — and only this
    /// distinguishes them.
    /// </para>
    /// </summary>
    public sealed record LhmBackendReadState
    {
        /// <summary>True when the backend opened successfully.</summary>
        public bool Initialized { get; init; }

        /// <summary>True when the read itself failed.</summary>
        public bool ReadFailed { get; init; }

        /// <summary>
        /// Why the backend or the read failed, scrubbed of machine
        /// identifiers. Null when nothing failed.
        /// </summary>
        public string? FailureReason { get; init; }

        /// <summary>How many devices the backend reported.</summary>
        public int DeviceCount { get; init; }

        /// <summary>The time the sensors were read.</summary>
        public DateTimeOffset ReadAt { get; init; }
    }

    /// <summary>
    /// What the selection logic saw, and what it decided, for one metric on
    /// one device.
    ///
    /// <para>
    /// This is the evidence a reader needs to tell "there is no temperature
    /// sensor here" from "the sensor is there but blocked" from "the sensor
    /// is there, readable, and we did not recognize its name". Without it,
    /// all three look identical in the inventory: a metric with no value.
    /// </para>
    /// </summary>
    public sealed record LhmMetricTrace
    {
        /// <summary>The metric, e.g. "TemperatureC".</summary>
        public string Metric { get; init; } = string.Empty;

        /// <summary>The sensor type the metric requires, e.g. "Temperature".</summary>
        public string RequiredSensorType { get; init; } = string.Empty;

        /// <summary>
        /// How many sensors of the required type the device exposes. Zero
        /// means the device has no such sensor at all.
        /// </summary>
        public int SensorCount { get; init; }

        /// <summary>The candidate names that were considered, in order.</summary>
        public IReadOnlyList<string> CandidateNames { get; init; } = Array.Empty<string>();

        /// <summary>
        /// The sensors of the required type whose names the candidate list
        /// recognizes, whatever their values.
        /// </summary>
        public IReadOnlyList<LhmSensorDescriptor> RecognizedSensors { get; init; } =
            Array.Empty<LhmSensorDescriptor>();

        /// <summary>
        /// The sensors of the required type that carry a usable value,
        /// whether or not the candidate list recognizes their names.
        /// </summary>
        public IReadOnlyList<LhmSensorDescriptor> ReadableSensors { get; init; } =
            Array.Empty<LhmSensorDescriptor>();

        /// <summary>
        /// The sensors that were deliberately not used, because using them
        /// would have been wrong.
        ///
        /// <para>
        /// A shared-memory sensor offered for a dedicated-memory metric is
        /// the case that matters. It is a readable sensor whose name the
        /// candidate list does not recognize, but the exclusion is a
        /// decision rather than a gap, so it is reported separately.
        /// </para>
        /// </summary>
        public IReadOnlyList<LhmSensorDescriptor> ExcludedSensors { get; init; } =
            Array.Empty<LhmSensorDescriptor>();

        /// <summary>The sensor that was selected, or null when none matched.</summary>
        public LhmSensorDescriptor? SelectedSensor { get; init; }

        /// <summary>
        /// True when the backend could not refresh the hardware node that
        /// owns the selected sensor, so its value is one retained from an
        /// earlier read rather than one measured now.
        /// </summary>
        public bool RefreshFailed { get; init; }

        /// <summary>The status of the measurement the selection produced.</summary>
        public MeasurementStatus MeasurementStatus { get; init; }

        /// <summary>The provenance of that measurement.</summary>
        public string? Source { get; init; }
    }

    /// <summary>
    /// Everything the diagnostics know about one device's sensors: the raw
    /// sensors it exposed, and how each metric was selected from them.
    /// </summary>
    public sealed record LhmSensorTrace
    {
        /// <summary>The backend's own identifier for the device.</summary>
        public string DeviceId { get; init; } = string.Empty;

        /// <summary>The device's display name.</summary>
        public string DeviceName { get; init; } = string.Empty;

        /// <summary>True for a CPU, false for a GPU.</summary>
        public bool IsCpu { get; init; }

        /// <summary>
        /// True when any hardware node of this device failed to refresh
        /// during the last read.
        /// </summary>
        public bool RefreshFailed { get; init; }

        /// <summary>
        /// The time the device's sensors were last refreshed successfully,
        /// or null when they never were.
        /// </summary>
        public DateTimeOffset? LastSuccessfulRefreshAt { get; init; }

        /// <summary>Every sensor the device exposed, with its value.</summary>
        public IReadOnlyList<LhmSensorDescriptor> Sensors { get; init; } =
            Array.Empty<LhmSensorDescriptor>();

        /// <summary>One trace per metric the device was asked for.</summary>
        public IReadOnlyList<LhmMetricTrace> Metrics { get; init; } =
            Array.Empty<LhmMetricTrace>();
    }

    /// <summary>
    /// The complete diagnostic picture for one read of the sensor backend.
    /// </summary>
    public sealed record LhmSensorBackendReport
    {
        /// <summary>The backend-level state of the read.</summary>
        public LhmBackendReadState Backend { get; init; } = new();

        /// <summary>One trace per device the backend reported.</summary>
        public IReadOnlyList<LhmSensorTrace> Devices { get; init; } =
            Array.Empty<LhmSensorTrace>();

        /// <summary>
        /// The trace for one device, or null when the backend did not report
        /// it.
        /// </summary>
        public LhmSensorTrace? Device(string deviceId) =>
            Devices.FirstOrDefault(d => string.Equals(d.DeviceId, deviceId, StringComparison.Ordinal));
    }

    /// <summary>
    /// Works out why a metric is or is not available, from the evidence the
    /// selection logic gathered.
    ///
    /// <para>
    /// The rules are deliberately ordered from the most fundamental cause
    /// to the most specific, so the diagnosis names the earliest thing
    /// that went wrong rather than the last symptom. A backend that never
    /// initialized is reported as such, even though every metric on every
    /// device is consequently unavailable.
    /// </para>
    /// </summary>
    public static class LhmSensorDiagnosisPolicy
    {
        /// <summary>
        /// Diagnoses one metric of one device.
        /// </summary>
        /// <param name="backend">The backend's state for the read.</param>
        /// <param name="device">The device the metric was taken from.</param>
        /// <param name="metric">The selection outcome for the metric.</param>
        /// <param name="publishedMeasurement">
        /// The measurement that was actually published for the metric — the
        /// merged scalar — or null when the published figure comes from an
        /// unrelated source and therefore cannot be compared.
        /// </param>
        public static LhmSensorDiagnosis Diagnose(
            LhmBackendReadState backend,
            LhmSensorTrace device,
            LhmMetricTrace metric,
            Measurement? publishedMeasurement)
        {
            if (backend is null || !backend.Initialized || backend.ReadFailed)
                return LhmSensorDiagnosis.BackendUnavailable;

            // The device could not be refreshed. Any value it exposes was
            // retained from an earlier read, so the reading is not a
            // measurement of this cycle whatever else is true.
            if (metric.RefreshFailed)
                return LhmSensorDiagnosis.HardwareUpdateFailed;

            if (metric.SensorCount == 0)
                return LhmSensorDiagnosis.NoSensorEnumerated;

            // A readable sensor of the right type exists, but the candidate
            // list does not recognize its name. The reading was there and
            // was not used.
            //
            // A sensor that was deliberately excluded does not count: its
            // exclusion is a decision, not a gap in the candidate list.
            var usableButUnrecognized = metric.ReadableSensors
                .Where(s => !metric.ExcludedSensors.Any(e => e.Name == s.Name))
                .ToList();

            if (metric.RecognizedSensors.Count == 0 && usableButUnrecognized.Count > 0)
                return LhmSensorDiagnosis.SensorNamesNotRecognized;

            // Every readable sensor of the required type was deliberately
            // excluded — they describe a different quantity, so using one
            // would have been wrong. That is a decision, not a gap.
            if (metric.RecognizedSensors.Count == 0 && metric.ExcludedSensors.Count > 0)
                return LhmSensorDiagnosis.SensorDeliberatelyExcluded;

            var selected = metric.SelectedSensor;

            if (selected is null || !LhmSensorSelection.IsReadable(selected))
                return LhmSensorDiagnosis.SensorValuesUnavailable;

            // The reading was mapped successfully. If the figure that was
            // actually published is not that reading, it was lost on the
            // way out.
            if (publishedMeasurement is not null
                && publishedMeasurement.Status != MeasurementStatus.Available)
            {
                return LhmSensorDiagnosis.ReadingLostInMerge;
            }

            return LhmSensorDiagnosis.ReadingAvailable;
        }

        /// <summary>
        /// Describes a diagnosis in the words the inventory uses.
        /// </summary>
        public static string Describe(LhmSensorDiagnosis diagnosis) => diagnosis switch
        {
            LhmSensorDiagnosis.ReadingAvailable =>
                "reading available",
            LhmSensorDiagnosis.NoSensorEnumerated =>
                "case 1: no sensor of this type is enumerated on the device",
            LhmSensorDiagnosis.SensorValuesUnavailable =>
                "case 2: matching sensors are enumerated but report no usable value",
            LhmSensorDiagnosis.SensorNamesNotRecognized =>
                "case 3: readable sensors are enumerated but their names are not recognized",
            LhmSensorDiagnosis.SensorDeliberatelyExcluded =>
                "the only readable sensors of this type were deliberately excluded: they describe a different quantity",
            LhmSensorDiagnosis.ReadingLostInMerge =>
                "case 4: a valid mapped reading was rejected or lost while merging",
            LhmSensorDiagnosis.HardwareUpdateFailed =>
                "case 5: the hardware update failed and old sensor values remain accessible",
            LhmSensorDiagnosis.BackendUnavailable =>
                "case 6: the backend could not be initialized or the hardware reached",
            _ => "unknown",
        };
    }
}

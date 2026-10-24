using System;
using System.Collections.Generic;
using System.Linq;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// The result of projecting backend-neutral sensor devices onto the
    /// manufacturer-neutral domain model.
    /// </summary>
    public sealed record LhmProjectionResult
    {
        public IReadOnlyList<HardwareDevice> Cpus { get; init; } = Array.Empty<HardwareDevice>();
        public IReadOnlyList<HardwareDevice> Gpus { get; init; } = Array.Empty<HardwareDevice>();
        public IReadOnlyList<DeviceReading> Readings { get; init; } = Array.Empty<DeviceReading>();

        /// <summary>
        /// What the selection saw for each device and metric. It is the
        /// evidence behind the published values, and the diagnostics report
        /// it so an unavailable figure can be explained.
        /// </summary>
        public IReadOnlyList<LhmSensorTrace> Traces { get; init; } =
            Array.Empty<LhmSensorTrace>();

        /// <summary>True when the backend reported no devices at all.</summary>
        public bool IsEmpty => Cpus.Count == 0 && Gpus.Count == 0;

        /// <summary>
        /// Builds a snapshot from this projection, leaving the system-level
        /// RAM/disk/network measurements to the caller.
        /// </summary>
        public MetricsSnapshot ToSnapshot(
            DateTimeOffset timestamp,
            Measurement? ramUsedBytes = null,
            Measurement? ramTotalBytes = null,
            Measurement? ramPercent = null,
            Measurement? diskUsedBytes = null,
            Measurement? diskTotalBytes = null,
            Measurement? diskPercent = null,
            Measurement? networkDownloadBytesPerSec = null,
            Measurement? networkUploadBytesPerSec = null) =>
            MetricsSnapshot.Create(
                timestamp,
                Cpus,
                Gpus,
                Readings,
                ramUsedBytes,
                ramTotalBytes,
                ramPercent,
                diskUsedBytes,
                diskTotalBytes,
                diskPercent,
                networkDownloadBytesPerSec,
                networkUploadBytesPerSec);
    }

    /// <summary>
    /// Pure mapping from backend-neutral sensor devices onto the
    /// manufacturer-neutral device/reading model.
    ///
    /// This class deliberately knows nothing about LibreHardwareMonitor: it
    /// consumes <see cref="LhmDeviceDescriptor"/> values, so it can be
    /// exercised against fixtures for AMD, Intel, NVIDIA, missing sensors and
    /// partial permission failures without touching real hardware.
    /// </summary>
    public static class LhmSnapshotMapper
    {
        /// <summary>
        /// Source label recorded on every measurement produced here.
        /// </summary>
        public const string SourceName = "LibreHardwareMonitor";

        /// <summary>
        /// Projects a set of devices onto the domain model.
        ///
        /// Rules enforced here:
        ///  - Device identity is the backend's own device id. The device's
        ///    position in the list is never used as identity, and devices are
        ///    never joined to another backend by display name.
        ///  - A device that is detected but has no readable sensor is still
        ///    reported as a device; its measurements are Unsupported (no such
        ///    sensor) or Failed (sensor present but unreadable).
        ///  - Zero is never substituted for an unavailable value.
        ///  - Exactly one sensor supplies each metric. Sensors are never
        ///    averaged or merged.
        /// </summary>
        /// <param name="devices">Devices reported by the backend.</param>
        public static LhmProjectionResult Project(IEnumerable<LhmDeviceDescriptor> devices)
        {
            if (devices is null)
                return new LhmProjectionResult();

            var all = devices.Where(d => d is not null).ToList();

            var cpus = new List<HardwareDevice>();
            var gpus = new List<HardwareDevice>();
            var readings = new List<DeviceReading>();
            var traces = new List<LhmSensorTrace>();

            // Guard against a backend that reports the same device twice:
            // the first occurrence wins, so identity stays unambiguous.
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var device in all)
            {
                if (string.IsNullOrEmpty(device.DeviceId))
                    continue;
                if (!seen.Add(device.DeviceId))
                    continue;

                var hardwareDevice = ToHardwareDevice(device);
                if (device.IsCpu)
                    cpus.Add(hardwareDevice);
                else
                    gpus.Add(hardwareDevice);

                var projected = ProjectReadings(device, hardwareDevice);
                readings.AddRange(projected);

                traces.Add(BuildTrace(device, projected));
            }

            return new LhmProjectionResult
            {
                Cpus = cpus.ToArray(),
                Gpus = gpus.ToArray(),
                Readings = readings.ToArray(),
                Traces = traces.ToArray(),
            };
        }

        private static HardwareDevice ToHardwareDevice(LhmDeviceDescriptor device) => new()
        {
            DeviceId = device.DeviceId,
            DisplayName = device.DisplayName,
            Vendor = string.IsNullOrWhiteSpace(device.Vendor) ? null : device.Vendor,
            DeviceClass = device.IsCpu ? DeviceClass.Cpu : DeviceClass.Gpu,
            GpuIntegration = device.IsCpu
                ? GpuIntegration.Unknown
                : device.IsIntegratedGpu switch
                {
                    true => GpuIntegration.Integrated,
                    false => GpuIntegration.Discrete,
                    null => GpuIntegration.Unknown,
                },
            PciDeviceKey = string.IsNullOrWhiteSpace(device.PciDeviceKey) ? null : device.PciDeviceKey,
            InstanceId = string.IsNullOrWhiteSpace(device.InstanceId) ? null : device.InstanceId,
        };

        /// <summary>
        /// Selects the GPU whose readings back the single-GPU legacy payload
        /// keys.
        ///
        /// <para>
        /// The order is the one <see cref="GpuSelectionPolicy"/> documents
        /// and applies:
        /// </para>
        ///
        /// <list type="number">
        /// <item><description>
        /// <b>Telemetry availability first.</b> A device that has at least one
        /// usable current reading (load, temperature, or memory — including
        /// properly attributed fallback scalars) is preferred over a device with
        /// no usable telemetry. A valid zero-percent load is a real reading and
        /// counts as usable. Stale readings do not count.
        /// </description></item>
        /// <item><description>
        /// <b>Integration class.</b> Among devices with equal telemetry
        /// availability, a GPU known to be <i>discrete</i> wins, because a
        /// separate card is the device whose load and temperature are the
        /// conventional headline figures. A GPU whose class is <i>unknown</i>
        /// comes next — most vendors do not expose the class, and an unknown
        /// class is far more often discrete than integrated. A GPU known to be
        /// <i>integrated</i> comes last: it is the on-die part of the CPU, and
        /// is only the headline when nothing else is present.
        /// </description></item>
        /// <item><description>
        /// <b>Instance identity as the tie-break.</b> Within the same
        /// integration class and telemetry availability, the device with the
        /// lowest <see cref="HardwareDevice.InstanceId"/> wins, compared
        /// ordinally. The instance id is a per-device identifier assigned by
        /// the platform, so this choice does not depend on the order in which
        /// the devices were enumerated. It is therefore stable across
        /// collections, across reboots, and regardless of which card the
        /// backend happened to list first.
        /// </description></item>
        /// <item><description>
        /// <b>Device id as the final tie-break.</b> Two devices that share an
        /// instance id (which should not happen, but can when the backend
        /// reports a device twice) are separated by their device id, again
        /// ordinally. The order is total, so the default is always
        /// deterministic.
        /// </description></item>
        /// </list>
        ///
        /// <para>
        /// <b>This must be the same choice the widget presents.</b> The
        /// collection measures this device's sensors and publishes them as
        /// the snapshot's scalar GPU figures; the widget then falls back to
        /// those figures when the presented device has no reading of its
        /// own. If the two chose different devices, the widget would
        /// present one card's figures under another card's name. The
        /// ordering therefore lives in one place —
        /// <see cref="GpuSelectionPolicy"/> — and this method delegates to
        /// it.
        /// </para>
        /// </summary>
        public static HardwareDevice? SelectPrimaryGpu(
            IReadOnlyList<HardwareDevice> gpus,
            MetricsSnapshot? snapshot = null,
            Measurement? gpuLoadPercent = null,
            string? gpuLoadDeviceId = null,
            Measurement? gpuTemperatureC = null,
            string? gpuTemperatureDeviceId = null,
            Measurement? gpuVramUsedBytes = null,
            string? gpuVramUsedDeviceId = null,
            Measurement? gpuVramTotalBytes = null,
            string? gpuVramTotalDeviceId = null)
        {
            if (gpus is null || gpus.Count == 0)
                return null;

            var selection = GpuSelectionPolicy.Resolve(
                gpus,
                selectedInstanceId: null,
                snapshot,
                gpuLoadPercent,
                gpuLoadDeviceId,
                gpuTemperatureC,
                gpuTemperatureDeviceId,
                gpuVramUsedBytes,
                gpuVramUsedDeviceId,
                gpuVramTotalBytes,
                gpuVramTotalDeviceId);

            return selection.Device;
        }

        /// <summary>
        /// Selects the CPU whose readings back the single-CPU legacy payload
        /// keys. Systems with more than one CPU package are rare; the choice
        /// is by device id so it stays stable across collections.
        /// </summary>
        public static HardwareDevice? SelectPrimaryCpu(IReadOnlyList<HardwareDevice> cpus)
        {
            if (cpus is null || cpus.Count == 0)
                return null;

            return cpus.OrderBy(c => c.DeviceId, StringComparer.Ordinal).First();
        }

        /// <summary>
        /// The metrics a device is asked for, in the order they are
        /// published.
        /// </summary>
        private static IEnumerable<(string Metric, LhmSensorKind Kind)> MetricsFor(bool isCpu)
        {
            if (isCpu)
            {
                yield return (LhmSensorSelection.CpuLoadMetric, LhmSensorKind.CpuLoadPercent);
                yield return (LhmSensorSelection.CpuTemperatureMetric, LhmSensorKind.CpuTemperatureC);
                yield return (LhmSensorSelection.CpuClockMetric, LhmSensorKind.CpuClockMHz);
            }
            else
            {
                yield return (LhmSensorSelection.GpuLoadMetric, LhmSensorKind.GpuLoadPercent);
                yield return (LhmSensorSelection.GpuTemperatureMetric, LhmSensorKind.GpuTemperatureC);
                yield return (LhmSensorSelection.GpuClockMetric, LhmSensorKind.GpuClockMHz);
                yield return (LhmSensorSelection.GpuMemoryUsedMetric, LhmSensorKind.GpuMemoryUsedBytes);
                yield return (LhmSensorSelection.GpuMemoryTotalMetric, LhmSensorKind.GpuMemoryTotalBytes);
            }
        }

        private static List<DeviceReading> ProjectReadings(
            LhmDeviceDescriptor device,
            HardwareDevice hardwareDevice)
        {
            var readings = new List<DeviceReading>();

            foreach (var (metric, kind) in MetricsFor(device.IsCpu))
                readings.Add(Reading(hardwareDevice.DeviceId, metric, device.Sensors, kind, device.RefreshFailed));

            return readings;
        }

        /// <summary>
        /// Builds the trace of what the selection saw for one device.
        /// </summary>
        private static LhmSensorTrace BuildTrace(
            LhmDeviceDescriptor device,
            IReadOnlyList<DeviceReading> readings)
        {
            var metrics = new List<LhmMetricTrace>();

            foreach (var (metric, kind) in MetricsFor(device.IsCpu))
            {
                var reading = readings.FirstOrDefault(
                    r => r.DeviceId == device.DeviceId && r.Metric == metric);

                var selected = LhmSensorSelection.Select(
                    device.Sensors, kind, preferReadableValue: true);

                metrics.Add(LhmSensorSelection.Trace(
                    metric,
                    kind,
                    device.Sensors,
                    selected,
                    reading?.Measurement.Status ?? MeasurementStatus.Unsupported,
                    reading?.Measurement.Source,
                    device.RefreshFailed));
            }

            return new LhmSensorTrace
            {
                DeviceId = device.DeviceId,
                DeviceName = device.DisplayName,
                IsCpu = device.IsCpu,
                RefreshFailed = device.RefreshFailed,
                Sensors = device.Sensors,
                Metrics = metrics,
            };
        }

        private static DeviceReading Reading(
            string deviceId,
            string metric,
            IReadOnlyList<LhmSensorDescriptor> sensors,
            LhmSensorKind kind,
            bool refreshFailed = false)
        {
            var sensor = LhmSensorSelection.Select(sensors, kind, preferReadableValue: true);

            if (sensor is null)
            {
                // No sensor of this kind exists on this device. This is a
                // genuine "not supported here", not a failure.
                return new DeviceReading
                {
                    DeviceId = deviceId,
                    Metric = metric,
                    Measurement = Measurement.Unsupported(SourceName),
                };
            }

            var source = $"{SourceName}:{sensor.Name}";

            if (!LhmSensorSelection.TryConvert(kind, sensor.Value, sensor.Unit, out var converted))
            {
                // The sensor exists but the reading is missing, NaN, infinite,
                // or expressed in an unexpected unit. Report it as failed so
                // it is distinguishable from "no such sensor", and never as 0.
                return new DeviceReading
                {
                    DeviceId = deviceId,
                    Metric = metric,
                    Measurement = Measurement.Failed(source),
                };
            }

            // The value was read from a sensor whose hardware node could not
            // be refreshed, so it is one retained from an earlier read. It
            // is a real measurement — of some earlier moment — but it is not
            // a measurement of this cycle, and it must not be published as
            // one. It is reported as failed so it is never presented as a
            // current figure.
            if (sensor.IsRetained || refreshFailed)
            {
                return new DeviceReading
                {
                    DeviceId = deviceId,
                    Metric = metric,
                    Measurement = Measurement.Failed(source),
                };
            }

            return new DeviceReading
            {
                DeviceId = deviceId,
                Metric = metric,
                Measurement = Measurement.AvailableValue(
                    converted,
                    LhmSensorSelection.CanonicalUnit(kind),
                    source),
            };
        }
    }
}

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

                readings.AddRange(ProjectReadings(device, hardwareDevice));
            }

            return new LhmProjectionResult
            {
                Cpus = cpus.ToArray(),
                Gpus = gpus.ToArray(),
                Readings = readings.ToArray(),
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
        };

        /// <summary>
        /// Selects the GPU whose readings back the single-GPU legacy payload
        /// keys.
        ///
        /// Preference order:
        ///   1. A GPU known to be discrete — it is the device whose load and
        ///      temperature are the conventional headline figures.
        ///   2. A GPU whose integration class is unknown — it may well be
        ///      discrete, and a separate card is a more useful headline than
        ///      the integrated part of the CPU.
        ///   3. A GPU known to be integrated — the on-die part is the
        ///      secondary device and is only the headline when nothing else
        ///      is present.
        ///
        /// The tie-break is the device id, so the choice is stable across
        /// collections and does not depend on enumeration order.
        /// </summary>
        public static HardwareDevice? SelectPrimaryGpu(IReadOnlyList<HardwareDevice> gpus)
        {
            if (gpus is null || gpus.Count == 0)
                return null;

            return gpus
                .OrderBy(g => IntegrationRank(g.GpuIntegration))
                .ThenBy(g => g.DeviceId, StringComparer.Ordinal)
                .First();
        }

        private static int IntegrationRank(GpuIntegration integration) => integration switch
        {
            GpuIntegration.Discrete => 0,
            GpuIntegration.Unknown => 1,
            GpuIntegration.Integrated => 2,
            _ => 3,
        };

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

        private static IEnumerable<DeviceReading> ProjectReadings(LhmDeviceDescriptor device, HardwareDevice hardwareDevice)
        {
            if (device.IsCpu)
            {
                yield return Reading(hardwareDevice.DeviceId, LhmSensorSelection.CpuLoadMetric, device.Sensors, LhmSensorKind.CpuLoadPercent);
                yield return Reading(hardwareDevice.DeviceId, LhmSensorSelection.CpuTemperatureMetric, device.Sensors, LhmSensorKind.CpuTemperatureC);
                yield return Reading(hardwareDevice.DeviceId, LhmSensorSelection.CpuClockMetric, device.Sensors, LhmSensorKind.CpuClockMHz);
            }
            else
            {
                yield return Reading(hardwareDevice.DeviceId, LhmSensorSelection.GpuLoadMetric, device.Sensors, LhmSensorKind.GpuLoadPercent);
                yield return Reading(hardwareDevice.DeviceId, LhmSensorSelection.GpuTemperatureMetric, device.Sensors, LhmSensorKind.GpuTemperatureC);
                yield return Reading(hardwareDevice.DeviceId, LhmSensorSelection.GpuClockMetric, device.Sensors, LhmSensorKind.GpuClockMHz);
                yield return Reading(hardwareDevice.DeviceId, LhmSensorSelection.GpuMemoryUsedMetric, device.Sensors, LhmSensorKind.GpuMemoryUsedBytes);
                yield return Reading(hardwareDevice.DeviceId, LhmSensorSelection.GpuMemoryTotalMetric, device.Sensors, LhmSensorKind.GpuMemoryTotalBytes);
            }
        }

        private static DeviceReading Reading(
            string deviceId,
            string metric,
            IReadOnlyList<LhmSensorDescriptor> sensors,
            LhmSensorKind kind)
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

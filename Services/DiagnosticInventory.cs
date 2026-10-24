using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System_Monitor.Models;

namespace System_Monitor.Services
{
    /// <summary>
    /// An explicit, opt-in export of the full hardware inventory.
    ///
    /// <para><b>Why this is separate from the log</b></para>
    ///
    /// <para>
    /// The routine log deliberately omits serial numbers, machine
    /// identifiers and complete hardware inventories: a log that describes
    /// one specific machine in detail does not belong in a routine log, and
    /// the widget does not need it to work.
    /// </para>
    ///
    /// <para>
    /// But when something is actually wrong, knowing <i>which</i> adapter
    /// and <i>which</i> driver the machine has is the difference between a
    /// guess and a fix. This class produces that information on request
    /// only, and writes it to a file the user chooses. It is never written
    /// as a side effect of running the widget.
    /// </para>
    ///
    /// <para><b>What it contains</b></para>
    ///
    /// <para>
    /// Device identities (the backend's device id, the PCI device key and
    /// the per-instance id), the sensors each device reports and their
    /// availability, the detected device roles, and the effective
    /// configuration. It does not contain anything the platform did not
    /// already expose, and it does not read or store credentials.
    /// </para>
    ///
    /// <para><b>The opt-in</b></para>
    ///
    /// <para>
    /// The export runs only when the user asks for it: either through
    /// <see cref="DiagnosticHost.ExportInventory"/> (which the host wires to
    /// the <c>SYSTEMMONITOR_DIAGNOSTICS</c> opt-in and the
    /// <c>--export-diagnostics</c> command-line switch) or by calling
    /// <see cref="Write"/> directly. Nothing in the collection path calls
    /// it.
    /// </para>
    /// </summary>
    public static class DiagnosticInventory
    {
        /// <summary>
        /// The header written at the top of every export, so the file
        /// explains itself even when it is read months later.
        /// </summary>
        public const string Header =
            "# SystemMonitor diagnostic inventory" + "\r\n" +
            "# Generated on explicit request. This file describes one specific" + "\r\n" +
            "# machine in detail and may contain device identifiers." + "\r\n" +
            "# It is safe to share only after checking what it contains." + "\r\n";

        /// <summary>
        /// Writes the inventory for a snapshot to a file.
        /// </summary>
        /// <param name="snapshot">The snapshot to describe, or null when no snapshot exists yet.</param>
        /// <param name="path">The file to write.</param>
        /// <param name="log">The log the export itself is reported to, or null.</param>
        /// <param name="sensorReport">
        /// The sensor backend's state and per-device traces, or null when
        /// the collector did not run.
        /// </param>
        /// <returns>The path written to.</returns>
        public static string Write(
            MetricsSnapshot? snapshot,
            string path,
            DiagnosticLog? log = null,
            LhmSensorBackendReport? sensorReport = null)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("A destination path is required.", nameof(path));

            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, Build(snapshot, sensorReport), Encoding.UTF8);

            log?.Info(
                "diagnostics",
                "inventory-export",
                "ok",
                detail: "inventory written on request");

            return path;
        }

        /// <summary>
        /// Renders the inventory as text.
        /// </summary>
        /// <param name="snapshot">The snapshot to describe, or null.</param>
        /// <param name="sensorReport">
        /// The sensor backend's state and per-device traces, or null.
        /// </param>
        public static string Build(
            MetricsSnapshot? snapshot,
            LhmSensorBackendReport? sensorReport = null)
        {
            var builder = new StringBuilder(4096);

            builder.Append(Header);
            builder.Append("generated: ")
                   .Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture))
                   .Append("\r\n");
            builder.Append("os: ")
                   .Append(Environment.OSVersion.VersionString)
                   .Append("\r\n");
            builder.Append("runtime: ")
                   .Append(Environment.Version)
                   .Append("\r\n");
            builder.Append("process_arch: ")
                   .Append(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture)
                   .Append("\r\n");
            builder.Append("os_arch: ")
                   .Append(System.Runtime.InteropServices.RuntimeInformation.OSArchitecture)
                   .Append("\r\n");
            builder.Append("\r\n");

            if (snapshot is null)
            {
                builder.Append("No snapshot has been collected yet.\r\n");
                return builder.ToString();
            }

            builder.Append("sample_time: ")
                   .Append(snapshot.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                   .Append("\r\n");
            builder.Append("device_count: cpus=")
                   .Append(snapshot.Cpus.Count)
                   .Append(" gpus=")
                   .Append(snapshot.Gpus.Count)
                   .Append("\r\n\r\n");

            AppendDevices(builder, "cpu", snapshot.Cpus, snapshot);
            AppendDevices(builder, "gpu", snapshot.Gpus, snapshot);
            AppendSystemMetrics(builder, snapshot);
            AppendSensorTrace(builder, snapshot, sensorReport);

            return builder.ToString();
        }

        /// <summary>
        /// Writes one class of device and the sensors it reports.
        /// </summary>
        private static void AppendDevices(
            StringBuilder builder,
            string label,
            IReadOnlyList<HardwareDevice> devices,
            MetricsSnapshot snapshot)
        {
            builder.Append('[').Append(label).Append(" devices]\r\n");

            if (devices.Count == 0)
            {
                builder.Append("  (none detected)\r\n\r\n");
                return;
            }

            foreach (var device in devices)
            {
                builder.Append("  - device_id: ").Append(device.DeviceId).Append("\r\n");
                builder.Append("    name: ").Append(device.DisplayName).Append("\r\n");
                builder.Append("    vendor: ").Append(device.Vendor ?? "(unknown)").Append("\r\n");
                builder.Append("    class: ").Append(device.DeviceClass).Append("\r\n");
                builder.Append("    integration: ").Append(device.GpuIntegration).Append("\r\n");
                builder.Append("    pci_device_key: ").Append(device.PciDeviceKey ?? "(not exposed)").Append("\r\n");
                builder.Append("    instance_id: ").Append(device.InstanceId ?? "(not exposed)").Append("\r\n");

                var readings = snapshot.Readings
                    .Where(r => string.Equals(r.DeviceId, device.DeviceId, StringComparison.Ordinal))
                    .OrderBy(r => r.Metric, StringComparer.Ordinal)
                    .ToList();

                builder.Append("    sensors:\r\n");
                if (readings.Count == 0)
                {
                    builder.Append("      (none reported)\r\n");
                }
                else
                {
                    foreach (var reading in readings)
                    {
                        builder.Append("      - metric: ").Append(reading.Metric).Append("\r\n");
                        builder.Append("        status: ").Append(reading.Measurement.Status).Append("\r\n");
                        builder.Append("        value: ")
                               .Append(reading.Measurement.Value?.ToString("F2", CultureInfo.InvariantCulture) ?? "(none)")
                               .Append("\r\n");
                        builder.Append("        unit: ").Append(reading.Measurement.Unit ?? "(none)").Append("\r\n");
                        builder.Append("        source: ").Append(reading.Measurement.Source ?? "(none)").Append("\r\n");
                    }
                }

                builder.Append("\r\n");
            }
        }

        /// <summary>
        /// Writes the system-level measurements and their sources.
        /// </summary>
        private static void AppendSystemMetrics(StringBuilder builder, MetricsSnapshot snapshot)
        {
            builder.Append("[system metrics]\r\n");

            AppendSystemMetric(builder, "cpu_load_percent", snapshot.CpuLoadPercent);
            AppendSystemMetric(builder, "cpu_clock_mhz", snapshot.CpuClockMHz);
            AppendSystemMetric(builder, "cpu_temperature_c", snapshot.CpuTemperatureC);
            AppendSystemMetric(builder, "gpu_load_percent", snapshot.GpuLoadPercent);
            AppendSystemMetric(builder, "gpu_temperature_c", snapshot.GpuTemperatureC);
            AppendSystemMetric(builder, "gpu_vram_used_bytes", snapshot.GpuVramUsedBytes);
            AppendSystemMetric(builder, "gpu_vram_total_bytes", snapshot.GpuVramTotalBytes);
            AppendSystemMetric(builder, "ram_used_bytes", snapshot.RamUsedBytes);
            AppendSystemMetric(builder, "ram_total_bytes", snapshot.RamTotalBytes);
            AppendSystemMetric(builder, "ram_percent", snapshot.RamPercent);
            AppendSystemMetric(builder, "disk_used_bytes", snapshot.DiskUsedBytes);
            AppendSystemMetric(builder, "disk_total_bytes", snapshot.DiskTotalBytes);
            AppendSystemMetric(builder, "disk_percent", snapshot.DiskPercent);
            AppendSystemMetric(builder, "network_down_bytes_per_sec", snapshot.NetworkDownloadBytesPerSec);
            AppendSystemMetric(builder, "network_up_bytes_per_sec", snapshot.NetworkUploadBytesPerSec);
            AppendSystemMetric(builder, "acpi_thermal_zone_temperature_c", snapshot.AcpiThermalZoneTemperatureC);

            builder.Append("\r\n");
        }

        /// <summary>
        /// Writes one system-level measurement.
        /// </summary>
        private static void AppendSystemMetric(StringBuilder builder, string name, Measurement? measurement)
        {
            if (measurement is null)
            {
                builder.Append("  - ").Append(name).Append(": (not collected)\r\n");
                return;
            }

            builder.Append("  - ").Append(name).Append(":\r\n");
            builder.Append("      status: ").Append(measurement.Status).Append("\r\n");
            builder.Append("      value: ")
                   .Append(measurement.Value?.ToString("F2", CultureInfo.InvariantCulture) ?? "(none)")
                   .Append("\r\n");
            builder.Append("      unit: ").Append(measurement.Unit ?? "(none)").Append("\r\n");
            builder.Append("      source: ").Append(measurement.Source ?? "(none)").Append("\r\n");
        }

        /// <summary>
        /// Writes the sensor trace: what the backend enumerated, what the
        /// selection made of it, and why a figure is or is not available.
        ///
        /// <para>
        /// This section is what makes an unavailable figure explainable. It
        /// names every sensor the device exposed with its type, value and
        /// unit; says which candidate names were searched for; says which
        /// sensor was chosen; gives the status of the resulting
        /// measurement; and gives a diagnosis in the six cases that need
        /// different answers.
        /// </para>
        ///
        /// <para>
        /// It is opt-in like the rest of the inventory: it describes one
        /// machine in detail and belongs in a file the user asked for, not
        /// in the routine log.
        /// </para>
        /// </summary>
        private static void AppendSensorTrace(
            StringBuilder builder,
            MetricsSnapshot snapshot,
            LhmSensorBackendReport? report)
        {
            builder.Append("[sensor trace]\r\n");

            if (report is null)
            {
                builder.Append("  (not collected)\r\n\r\n");
                return;
            }

            var backend = report.Backend;

            builder.Append("  backend_initialized: ").Append(backend.Initialized).Append("\r\n");
            builder.Append("  read_failed: ").Append(backend.ReadFailed).Append("\r\n");
            builder.Append("  read_at: ")
                   .Append(backend.ReadAt.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                   .Append("\r\n");
            builder.Append("  device_count: ").Append(backend.DeviceCount).Append("\r\n");

            if (!string.IsNullOrWhiteSpace(backend.FailureReason))
                builder.Append("  failure_reason: ").Append(backend.FailureReason).Append("\r\n");

            builder.Append("\r\n");

            if (!backend.Initialized || backend.ReadFailed)
            {
                builder.Append("  diagnosis: ")
                       .Append(LhmSensorDiagnosisPolicy.Describe(LhmSensorDiagnosis.BackendUnavailable))
                       .Append("\r\n\r\n");
                return;
            }

            foreach (var device in report.Devices)
            {
                builder.Append("  - device_id: ").Append(device.DeviceId).Append("\r\n");
                builder.Append("    name: ").Append(device.DeviceName).Append("\r\n");
                builder.Append("    class: ").Append(device.IsCpu ? "Cpu" : "Gpu").Append("\r\n");
                builder.Append("    refresh_failed: ").Append(device.RefreshFailed).Append("\r\n");

                builder.Append("    raw_sensors:\r\n");
                if (device.Sensors.Count == 0)
                {
                    builder.Append("      (none)\r\n");
                }
                else
                {
                    foreach (var sensor in device.Sensors)
                    {
                        builder.Append("      - name: ").Append(sensor.Name).Append("\r\n");
                        builder.Append("        type: ").Append(sensor.SensorType).Append("\r\n");
                        builder.Append("        value: ")
                               .Append(sensor.Value?.ToString("F2", CultureInfo.InvariantCulture) ?? "(null)")
                               .Append("\r\n");
                        builder.Append("        unit: ").Append(DescribeUnit(sensor.Unit)).Append("\r\n");
                        builder.Append("        retained: ").Append(sensor.IsRetained).Append("\r\n");
                    }
                }

                builder.Append("    metrics:\r\n");
                foreach (var metric in device.Metrics)
                {
                    // The device's own reading, and the figure that was
                    // actually published for the metric. For the CPU
                    // temperature the published figure is the merged
                    // scalar, so it is the one the diagnosis compares
                    // against: it is what the widget shows.
                    var deviceReading = PublishedMeasurement(snapshot, device.DeviceId, metric.Metric);

                    var published = device.IsCpu && metric.Metric == LhmSensorSelection.CpuTemperatureMetric
                        ? snapshot.CpuTemperatureC ?? deviceReading
                        : deviceReading;

                    var diagnosis = LhmSensorDiagnosisPolicy.Diagnose(
                        backend,
                        device,
                        metric,
                        published);

                    builder.Append("      - metric: ").Append(metric.Metric).Append("\r\n");
                    builder.Append("        required_sensor_type: ")
                           .Append(metric.RequiredSensorType).Append("\r\n");
                    builder.Append("        sensor_count: ").Append(metric.SensorCount).Append("\r\n");
                    builder.Append("        candidate_names: ")
                           .Append(string.Join(", ", metric.CandidateNames)).Append("\r\n");
                    builder.Append("        recognized_sensors: ")
                           .Append(DescribeSensors(metric.RecognizedSensors)).Append("\r\n");
                    builder.Append("        readable_sensors: ")
                           .Append(DescribeSensors(metric.ReadableSensors)).Append("\r\n");
                    builder.Append("        excluded_sensors: ")
                           .Append(DescribeSensors(metric.ExcludedSensors)).Append("\r\n");
                    builder.Append("        selected_sensor: ")
                           .Append(metric.SelectedSensor?.Name ?? "(none)").Append("\r\n");
                    builder.Append("        measurement_status: ")
                           .Append(metric.MeasurementStatus).Append("\r\n");
                    builder.Append("        source: ")
                           .Append(metric.Source ?? "(none)").Append("\r\n");
                    builder.Append("        published_value: ")
                           .Append(published?.Value?.ToString("F2", CultureInfo.InvariantCulture) ?? "(none)")
                           .Append("\r\n");
                    builder.Append("        published_status: ")
                           .Append(published?.Status.ToString() ?? "(not published)").Append("\r\n");
                    builder.Append("        diagnosis: ")
                           .Append(LhmSensorDiagnosisPolicy.Describe(diagnosis)).Append("\r\n");
                }

                builder.Append("\r\n");
            }
        }

        /// <summary>
        /// The measurement published for a device's metric, or null when the
        /// published figure is a system-level scalar rather than the
        /// device's own reading.
        /// </summary>
        private static Measurement? PublishedMeasurement(
            MetricsSnapshot snapshot,
            string deviceId,
            string metric)
        {
            return snapshot.Readings
                .FirstOrDefault(r => r.DeviceId == deviceId && r.Metric == metric)?
                .Measurement;
        }

        private static string DescribeSensors(IReadOnlyList<LhmSensorDescriptor> sensors)
        {
            if (sensors.Count == 0)
                return "(none)";

            return string.Join(", ", sensors.Select(s => $"{s.Name}={s.Value?.ToString("F2", CultureInfo.InvariantCulture) ?? "(null)"}"));
        }

        private static string DescribeUnit(LhmSensorUnit unit) => unit switch
        {
            LhmSensorUnit.Percent => "%",
            LhmSensorUnit.Celsius => "°C",
            LhmSensorUnit.Megahertz => "MHz",
            LhmSensorUnit.Megabyte => "MB",
            LhmSensorUnit.Byte => "B",
            _ => "unknown",
        };
    }
}

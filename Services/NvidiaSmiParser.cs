using System;
using System.Collections.Generic;
using System.Globalization;

namespace System_Monitor.Services
{
    /// <summary>
    /// Parses the CSV output of a single nvidia-smi query.
    ///
    /// The parser is pure: it takes text and produces samples, so its
    /// behaviour can be tested exhaustively without an NVIDIA driver.
    ///
    /// The expected layout is one row per GPU, with the fields in the order
    /// the query asked for them:
    ///
    ///   uuid, name, pci.bus_id, pci.device_id, utilization.gpu,
    ///   temperature.gpu, memory.used, memory.total
    ///
    /// with <c>--format=csv,noheader,nounits</c>. A device name may itself
    /// contain a comma, so the row is split from the right: the first three
    /// fields are fixed-width identifiers, the name is the single field that
    /// may contain commas, and the trailing fields are numeric.
    ///
    /// Every value is parsed with the invariant culture, so a machine whose
    /// locale uses a comma as the decimal separator still parses correctly.
    /// </summary>
    public static class NvidiaSmiParser
    {
        /// <summary>
        /// The number of fields in a row: the uuid, the name, and the six
        /// fields that follow it.
        /// </summary>
        public const int FieldCount = 8;

        /// <summary>
        /// The number of fields that follow the name: the PCI bus id, the PCI
        /// device id, utilization, temperature, memory used and memory total.
        /// </summary>
        private const int TrailingFieldCount = 6;

        /// <summary>
        /// Parses the standard output of an nvidia-smi query.
        ///
        /// Rows that are malformed, that carry the wrong number of fields, or
        /// whose identity fields are unusable are skipped rather than
        /// throwing: a partially readable response is still useful.
        /// </summary>
        public static IReadOnlyList<NvidiaGpuSample> Parse(string? output)
        {
            var samples = new List<NvidiaGpuSample>();

            if (string.IsNullOrWhiteSpace(output))
                return samples;

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                var sample = ParseRow(line);
                if (sample is not null)
                    samples.Add(sample);
            }

            return samples;
        }

        /// <summary>
        /// Parses one row. Returns null when the row cannot yield a usable
        /// device.
        /// </summary>
        private static NvidiaGpuSample? ParseRow(string line)
        {
            var fields = line.Split(',');

            if (fields.Length < FieldCount)
                return null;

            // The name is the only field that may legitimately contain a
            // comma ("NVIDIA GeForce RTX 2070 Super, Max-Q"). Split from the
            // right so the trailing fields keep their positions: the last
            // TrailingFieldCount fields are fixed, and everything between
            // the uuid and them is the name.
            var uuid = fields[0].Trim();
            var trailingStart = fields.Length - TrailingFieldCount;
            var name = string.Join(',', fields, 1, trailingStart - 1).Trim();
            var pciBusId = fields[trailingStart].Trim();
            var pciDeviceId = fields[trailingStart + 1].Trim();
            var utilization = fields[trailingStart + 2].Trim();
            var temperature = fields[trailingStart + 3].Trim();
            var memoryUsed = fields[trailingStart + 4].Trim();
            var memoryTotal = fields[trailingStart + 5].Trim();

            // nvidia-smi writes "[N/A]" for a metric the device does not
            // support, and "[Not Supported]" for some fields.
            if (string.IsNullOrWhiteSpace(uuid) && string.IsNullOrWhiteSpace(pciDeviceId))
                return null;

            return new NvidiaGpuSample
            {
                Uuid = uuid,
                Name = name,
                PciBusId = pciBusId,
                PciDeviceId = pciDeviceId,
                PciDeviceKey = NvidiaGpuIdentity.NormalizePciDeviceKey(pciDeviceId),
                UtilizationPercent = ParsePercent(utilization),
                TemperatureC = ParseTemperature(temperature),
                VramUsedBytes = ParseMebibytes(memoryUsed),
                VramTotalBytes = ParseMebibytes(memoryTotal),
            };
        }

        /// <summary>
        /// Parses a utilization percentage. Values outside 0..100 are
        /// rejected rather than clamped: a sensor reporting 120 % is
        /// reporting something this parser does not understand.
        /// </summary>
        private static double? ParsePercent(string value)
        {
            if (!TryParseDouble(value, out var parsed))
                return null;

            if (parsed < 0 || parsed > 100)
                return null;

            return parsed;
        }

        /// <summary>
        /// Parses a temperature in degrees Celsius. A temperature outside a
        /// physically plausible range is rejected.
        /// </summary>
        private static double? ParseTemperature(string value)
        {
            if (!TryParseDouble(value, out var parsed))
                return null;

            if (parsed < -50 || parsed > 200)
                return null;

            return parsed;
        }

        /// <summary>
        /// Parses a memory figure in mebibytes and converts it to bytes.
        /// </summary>
        private static ulong? ParseMebibytes(string value)
        {
            if (!TryParseDouble(value, out var parsed))
                return null;

            if (parsed < 0)
                return null;

            var bytes = parsed * 1024.0 * 1024.0;

            if (bytes > ulong.MaxValue)
                return null;

            return (ulong)Math.Round(bytes);
        }

        /// <summary>
        /// Parses a number using the invariant culture, rejecting the values
        /// nvidia-smi uses to mean "not available".
        /// </summary>
        private static bool TryParseDouble(string value, out double result)
        {
            result = 0;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            // "[N/A]", "[Not Supported]", "<Not Supported>" and similar.
            if (value.StartsWith("[", StringComparison.Ordinal)
                || value.StartsWith("<", StringComparison.Ordinal)
                || value.IndexOf("N/A", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("Not Supported", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            return double.TryParse(
                value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out result);
        }
    }
}

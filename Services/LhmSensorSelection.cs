using System;
using System.Collections.Generic;
using System.Linq;

namespace System_Monitor.Services
{
    /// <summary>
    /// Sensor kinds this backend knows how to select. Deliberately generic:
    /// nothing here is vendor-specific, and no per-vendor metric fields exist.
    /// </summary>
    public enum LhmSensorKind
    {
        CpuLoadPercent,
        CpuTemperatureC,
        CpuClockMHz,
        GpuLoadPercent,
        GpuTemperatureC,
        GpuClockMHz,
        GpuMemoryUsedBytes,
        GpuMemoryTotalBytes,
    }

    /// <summary>
    /// The unit a sensor value is expressed in, as reported by the source.
    /// Unit verification happens before any conversion is applied.
    /// </summary>
    public enum LhmSensorUnit
    {
        Unknown,
        Percent,
        Celsius,
        Megahertz,
        Megabyte,
        Byte,
    }

    /// <summary>
    /// A minimal, hardware-agnostic description of a sensor as seen by the
    /// selection logic. The LibreHardwareMonitor adapter projects real sensors
    /// onto this shape so selection can be tested without hardware access.
    /// </summary>
    public sealed record LhmSensorDescriptor
    {
        /// <summary>Sensor display name, e.g. "CPU Package", "GPU Core".</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>Sensor type name, e.g. "Temperature", "Load", "Clock", "SmallData".</summary>
        public string SensorType { get; init; } = string.Empty;

        /// <summary>Current value, if the source has one. Null means unavailable.</summary>
        public double? Value { get; init; }

        /// <summary>Unit the raw value is expressed in.</summary>
        public LhmSensorUnit Unit { get; init; } = LhmSensorUnit.Unknown;
    }

    /// <summary>
    /// Pure sensor-selection and unit-conversion logic.
    ///
    /// Selection preference and fallback semantics
    /// ==========================================
    /// Sensors are matched on the (name, sensor-type) pair, never on name alone,
    /// because several vendors reuse the same name for different quantities —
    /// for example "CPU Core #1" exists as both a Temperature and a Clock
    /// sensor, "CPU Core Max" is a Load sensor, and "GPU Memory" exists as both
    /// a Load and a Clock sensor.
    ///
    /// Preference is an ordered list of candidate name patterns per metric.
    /// The first candidate that matches a sensor of the required type wins.
    /// Later candidates are fallbacks used only when earlier ones are absent or
    /// unreadable.
    ///
    /// Candidates are never merged, averaged, or summed: exactly one sensor
    /// supplies each metric, so unrelated package/core/hotspot/memory sensors
    /// are never combined into a synthetic value.
    ///
    /// CPU temperature preference:
    ///   1. "CPU Package"      — Intel's package-level sensor, the closest
    ///                           thing to a whole-CPU temperature.
    ///   2. "Core (Tctl/Tdie)" — AMD's package control/die temperature.
    ///   3. "Core (Tdie)"      — AMD's die temperature when Tctl is absent.
    ///   4. "Core Average"     — Intel's mean of the package's cores.
    ///   5. "Core Max"         — Intel's hottest core.
    ///   6. "CCDs Average (Tdie)" — AMD multi-CCD average, last resort.
    ///
    /// CPU load preference:
    ///   1. "CPU Total"        — whole-package load (Intel and AMD).
    ///   2. "Cores (Average)"  — AMD's aggregate core load.
    ///   3. "Total"            — generic fallback.
    ///
    /// CPU clock preference:
    ///   1. "CPU Core #1"      — the first core's clock, deterministic.
    ///   2. "CPU Core #"       — any "CPU Core #N" clock, lowest index wins.
    ///   3. "Core Clock"       — generic fallback.
    ///
    /// GPU temperature preference:
    ///   1. "GPU Core"        — the core/junction temperature.
    ///   2. "GPU Hot Spot"    — the hotspot sensor, a fallback.
    ///   3. "Core" / "Hot Spot" — used only when the "GPU "-prefixed names are
    ///                             absent.
    ///
    /// GPU utilization preference:
    ///   1. "GPU Core"        — the core load sensor.
    ///   2. "GPU Total"       — Intel's aggregate engine load.
    ///   3. "D3D 3D"          — the 3D engine load of an integrated D3D GPU.
    ///   4. "D3D "            — the aggregate D3D load of an integrated GPU.
    ///
    /// GPU clock preference:
    ///   1. "GPU Core"        — the core clock.
    ///   2. "GPU Memory"      — the memory clock.
    ///   3. "GPU SoC"         — the SoC clock.
    ///
    /// GPU memory preference (used/total, converted to bytes):
    ///   1. "GPU Memory Used" / "GPU Memory Total"  — NVIDIA/AMD dedicated VRAM.
    ///   2. "D3D Dedicated Memory Used" / "D3D Dedicated Memory Total" —
    ///                                             D3D-style dedicated memory.
    ///   3. "VRAM Used" / "VRAM Total"              — generic fallback.
    ///   4. "Memory Used" / "Memory Total"          — generic fallback.
    ///
    /// "D3D Shared Memory Used/Total" is deliberately NOT a candidate: shared
    /// system memory is not VRAM and reporting it as such would be wrong.
    ///
    /// Only sensor types actually observed for the requested metric are
    /// considered; a name match of the wrong type is rejected.
    /// </summary>
    public static class LhmSensorSelection
    {
        /// <summary>Metric names as used in the published snapshot.</summary>
        public const string CpuLoadMetric = "LoadPercent";
        public const string CpuTemperatureMetric = "TemperatureC";
        public const string CpuClockMetric = "ClockMHz";
        public const string GpuLoadMetric = "LoadPercent";
        public const string GpuTemperatureMetric = "TemperatureC";
        public const string GpuClockMetric = "ClockMHz";
        public const string GpuMemoryUsedMetric = "VramUsedBytes";
        public const string GpuMemoryTotalMetric = "VramTotalBytes";

        // --- Candidate preference tables (ordered; first match wins) ---

        private static readonly string[] CpuTemperatureCandidates =
        {
            "CPU Package",
            "Core (Tctl/Tdie)",
            "Core (Tdie)",
            "Core Average",
            "Core Max",
            "CCDs Average (Tdie)",
        };

        private static readonly string[] CpuLoadCandidates =
        {
            "CPU Total",
            "Cores (Average)",
            "Total",
        };

        private static readonly string[] CpuClockCandidates =
        {
            "CPU Core #1",
            "CPU Core #",
            "Core Clock",
        };

        private static readonly string[] GpuTemperatureCandidates =
        {
            "GPU Core",
            "GPU Hot Spot",
            "Core",
            "Hot Spot",
        };

        private static readonly string[] GpuLoadCandidates =
        {
            "GPU Core",
            "GPU Total",
            "D3D 3D",
            "D3D ",
        };

        private static readonly string[] GpuClockCandidates =
        {
            "GPU Core",
            "GPU Memory",
            "GPU SoC",
        };

        private static readonly string[] GpuMemoryUsedCandidates =
        {
            "GPU Memory Used",
            "D3D Dedicated Memory Used",
            "VRAM Used",
            "Memory Used",
        };

        private static readonly string[] GpuMemoryTotalCandidates =
        {
            "GPU Memory Total",
            "D3D Dedicated Memory Total",
            "VRAM Total",
            "Memory Total",
        };

        /// <summary>
        /// The memory-sensor families that say the sensor describes the
        /// card's own dedicated video memory.
        ///
        /// <para>
        /// "GPU Memory" and "D3D Dedicated Memory" say so explicitly.
        /// "VRAM" says so by definition. The bare "Memory" family does
        /// not: a name that does not say which pool it describes
        /// establishes nothing, and is deliberately left out.
        /// </para>
        /// </summary>
        private static readonly string[] DedicatedMemoryFamilies =
        {
            "GPU Memory",
            "D3D Dedicated Memory",
            "VRAM",
        };

        /// <summary>
        /// The one memory family whose name says nothing about which pool
        /// it describes.
        /// </summary>
        private const string GenericMemoryFamily = "Memory";

        /// <summary>
        /// The qualifiers that turn a memory-sensor family into a used or
        /// a total figure.
        /// </summary>
        private static readonly string[] MemorySensorQualifiers =
        {
            " Used",
            " Total",
        };

        /// <summary>
        /// True when the sensor name marks the sensor as describing shared
        /// system memory rather than the card's own dedicated video memory.
        ///
        /// <para>
        /// Shared system memory is not VRAM. The candidate lists exclude
        /// it, so it is never <i>selected</i> as a memory metric — but a
        /// figure that describes it may still arrive from elsewhere, and a
        /// presenter has to be able to recognise one.
        /// </para>
        /// </summary>
        /// <param name="sensorName">The sensor's name.</param>
        public static bool IsSharedMemorySensor(string? sensorName) =>
            !string.IsNullOrWhiteSpace(sensorName)
            && sensorName.Contains("Shared", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The family a memory sensor name belongs to: the name with its
        /// "Used" / "Total" qualifier removed.
        ///
        /// <para>
        /// "GPU Memory Used" and "GPU Memory Total" are two figures of one
        /// family; so are "Memory Used" and "Memory Total". The family is
        /// what makes two figures comparable: a used figure and a total
        /// figure from the same family describe the same pool of memory
        /// whatever that pool is.
        /// </para>
        /// </summary>
        /// <param name="sensorName">The sensor's name.</param>
        /// <returns>The family name, or null when the name is empty.</returns>
        public static string? MemoryFamilyOf(string? sensorName)
        {
            if (string.IsNullOrWhiteSpace(sensorName))
                return null;

            var name = sensorName.Trim();

            foreach (var qualifier in MemorySensorQualifiers)
            {
                if (name.EndsWith(qualifier, StringComparison.OrdinalIgnoreCase))
                {
                    return name.Substring(0, name.Length - qualifier.Length).Trim();
                }
            }

            return name;
        }

        /// <summary>
        /// True when the sensor name is one of the dedicated-memory
        /// candidates, and so describes the card's own video memory.
        ///
        /// <para>
        /// The generic "Memory Used" / "Memory Total" names are <b>not</b>
        /// in this list. A family that does not say which pool it
        /// describes establishes nothing by itself: it may be a driver's
        /// name for dedicated video memory on one card and for shared
        /// system memory on another.
        /// </para>
        /// </summary>
        /// <param name="sensorName">The sensor's name.</param>
        public static bool IsDedicatedMemorySensor(string? sensorName)
        {
            if (string.IsNullOrWhiteSpace(sensorName))
                return false;

            if (IsSharedMemorySensor(sensorName))
                return false;

            var family = MemoryFamilyOf(sensorName);

            return family is not null
                && DedicatedMemoryFamilies.Any(
                    f => string.Equals(f, family, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// True when the sensor name belongs to the generic memory family —
        /// the plain "Memory Used" / "Memory Total" names.
        ///
        /// <para>
        /// These names say nothing about which pool they describe, so they
        /// are not dedicated memory and they are not shared memory either.
        /// What they do establish is a family: two figures named from this
        /// family describe one pool, whatever that pool is.
        /// </para>
        /// </summary>
        /// <param name="sensorName">The sensor's name.</param>
        public static bool IsGenericMemorySensor(string? sensorName)
        {
            if (string.IsNullOrWhiteSpace(sensorName))
                return false;

            if (IsSharedMemorySensor(sensorName))
                return false;

            var family = MemoryFamilyOf(sensorName);

            return family is not null
                && string.Equals(family, GenericMemoryFamily, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Selects the sensor supplying <paramref name="kind"/>.
        /// </summary>
        /// <param name="sensors">All sensors exposed by the device.</param>
        /// <param name="kind">The metric to select.</param>
        /// <param name="preferReadableValue">
        /// When true, a later candidate with a usable value wins over an
        /// earlier candidate whose value is missing. This keeps a metric
        /// populated when the preferred sensor is blocked by permissions while
        /// still preferring the preferred sensor whenever it reads.
        /// </param>
        /// <returns>The chosen sensor, or null when none matches.</returns>
        public static LhmSensorDescriptor? Select(
            IEnumerable<LhmSensorDescriptor> sensors,
            LhmSensorKind kind,
            bool preferReadableValue = false)
        {
            if (sensors is null)
                return null;

            var all = sensors.ToList();
            var requiredType = RequiredSensorType(kind);
            var candidates = CandidatesFor(kind);

            // Only sensors of the required type are eligible. A name is never
            // matched across sensor types.
            var typed = all
                .Where(s => string.Equals(s.SensorType, requiredType, StringComparison.OrdinalIgnoreCase))
                .ToList();

            LhmSensorDescriptor? firstMatch = null;

            foreach (var candidate in candidates)
            {
                var match = typed.FirstOrDefault(s => string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    // "CPU Core #" style: any numbered variant, lowest index wins.
                    match = MatchIndexed(typed, candidate);
                }

                if (match is null)
                    continue;

                firstMatch ??= match;

                if (!preferReadableValue || IsReadable(match))
                    return match;
            }

            // No candidate had a usable value: return the preferred match so the
            // caller can report the sensor as present-but-unreadable rather
            // than as a missing sensor.
            return firstMatch;
        }

        /// <summary>
        /// True when the sensor carries a finite, usable value.
        /// </summary>
        public static bool IsReadable(LhmSensorDescriptor? sensor)
        {
            if (sensor?.Value is not { } value)
                return false;
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        /// <summary>
        /// Matches a "prefix #N" candidate against sensors named "prefix #M".
        /// When the candidate carries an explicit number, only that index
        /// matches. When it does not (e.g. "CPU Core #"), the lowest index is
        /// returned so selection is deterministic and does not depend on
        /// enumeration order.
        /// </summary>
        private static LhmSensorDescriptor? MatchIndexed(IEnumerable<LhmSensorDescriptor> sensors, string candidate)
        {
            var hashIndex = candidate.IndexOf('#');
            if (hashIndex < 0)
                return null;

            var prefix = candidate.Substring(0, hashIndex).TrimEnd();
            var wanted = candidate.Substring(hashIndex + 1).Trim();

            var matches = new List<(int Index, LhmSensorDescriptor Sensor)>();

            foreach (var s in sensors)
            {
                var sensorHash = s.Name.IndexOf('#');
                if (sensorHash < 0)
                    continue;

                var sensorPrefix = s.Name.Substring(0, sensorHash).TrimEnd();
                if (!string.Equals(sensorPrefix, prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var sensorSuffix = s.Name.Substring(sensorHash + 1).Trim();
                if (!int.TryParse(sensorSuffix, out var index))
                    continue;

                // For an exact candidate such as "CPU Core #1", only that index matches.
                if (wanted.Length > 0 && int.TryParse(wanted, out var wantedIndex) && wantedIndex != index)
                    continue;

                matches.Add((index, s));
            }

            return matches.Count == 0
                ? null
                : matches.OrderBy(m => m.Index).Select(m => m.Sensor).First();
        }

        /// <summary>
        /// The sensor type required for a given metric. Selection never crosses
        /// types, so a "Load" sensor can never be reported as a temperature.
        /// </summary>
        public static string RequiredSensorType(LhmSensorKind kind) => kind switch
        {
            LhmSensorKind.CpuLoadPercent => "Load",
            LhmSensorKind.CpuTemperatureC => "Temperature",
            LhmSensorKind.CpuClockMHz => "Clock",
            LhmSensorKind.GpuLoadPercent => "Load",
            LhmSensorKind.GpuTemperatureC => "Temperature",
            LhmSensorKind.GpuClockMHz => "Clock",
            LhmSensorKind.GpuMemoryUsedBytes => "SmallData",
            LhmSensorKind.GpuMemoryTotalBytes => "SmallData",
            _ => string.Empty,
        };

        /// <summary>
        /// The unit the raw value of a sensor for a given metric is expected to
        /// be in, before conversion.
        /// </summary>
        public static LhmSensorUnit ExpectedUnit(LhmSensorKind kind) => kind switch
        {
            LhmSensorKind.CpuLoadPercent => LhmSensorUnit.Percent,
            LhmSensorKind.CpuTemperatureC => LhmSensorUnit.Celsius,
            LhmSensorKind.CpuClockMHz => LhmSensorUnit.Megahertz,
            LhmSensorKind.GpuLoadPercent => LhmSensorUnit.Percent,
            LhmSensorKind.GpuTemperatureC => LhmSensorUnit.Celsius,
            LhmSensorKind.GpuClockMHz => LhmSensorUnit.Megahertz,
            LhmSensorKind.GpuMemoryUsedBytes => LhmSensorUnit.Megabyte,
            LhmSensorKind.GpuMemoryTotalBytes => LhmSensorUnit.Megabyte,
            _ => LhmSensorUnit.Unknown,
        };

        /// <summary>
        /// The canonical unit label published with a measurement.
        /// </summary>
        public static string CanonicalUnit(LhmSensorKind kind) => kind switch
        {
            LhmSensorKind.CpuLoadPercent => "%",
            LhmSensorKind.CpuTemperatureC => "°C",
            LhmSensorKind.CpuClockMHz => "MHz",
            LhmSensorKind.GpuLoadPercent => "%",
            LhmSensorKind.GpuTemperatureC => "°C",
            LhmSensorKind.GpuClockMHz => "MHz",
            LhmSensorKind.GpuMemoryUsedBytes => "B",
            LhmSensorKind.GpuMemoryTotalBytes => "B",
            _ => string.Empty,
        };

        private static string[] CandidatesFor(LhmSensorKind kind) => kind switch
        {
            LhmSensorKind.CpuTemperatureC => CpuTemperatureCandidates,
            LhmSensorKind.CpuLoadPercent => CpuLoadCandidates,
            LhmSensorKind.CpuClockMHz => CpuClockCandidates,
            LhmSensorKind.GpuTemperatureC => GpuTemperatureCandidates,
            LhmSensorKind.GpuLoadPercent => GpuLoadCandidates,
            LhmSensorKind.GpuClockMHz => GpuClockCandidates,
            LhmSensorKind.GpuMemoryUsedBytes => GpuMemoryUsedCandidates,
            LhmSensorKind.GpuMemoryTotalBytes => GpuMemoryTotalCandidates,
            _ => Array.Empty<string>(),
        };

        /// <summary>
        /// Converts a raw sensor value to the canonical unit for a metric.
        ///
        /// The source unit is verified first: a value whose unit does not match
        /// what the metric expects is rejected rather than silently reinterpreted.
        /// A missing, NaN or infinite value is unavailable, never zero.
        /// </summary>
        /// <param name="kind">The metric being converted.</param>
        /// <param name="rawValue">Raw value from the sensor.</param>
        /// <param name="rawUnit">Unit the raw value is expressed in.</param>
        /// <param name="converted">The converted value, when conversion succeeded.</param>
        /// <returns>True when the value was usable and converted.</returns>
        public static bool TryConvert(LhmSensorKind kind, double? rawValue, LhmSensorUnit rawUnit, out double converted)
        {
            converted = 0.0;

            if (!rawValue.HasValue)
                return false;

            var value = rawValue.Value;
            if (double.IsNaN(value) || double.IsInfinity(value))
                return false;

            var expected = ExpectedUnit(kind);
            if (expected == LhmSensorUnit.Unknown || rawUnit != expected)
                return false;

            switch (kind)
            {
                case LhmSensorKind.CpuLoadPercent:
                case LhmSensorKind.GpuLoadPercent:
                    converted = value;
                    return true;

                case LhmSensorKind.CpuTemperatureC:
                case LhmSensorKind.GpuTemperatureC:
                    converted = value;
                    return true;

                case LhmSensorKind.CpuClockMHz:
                case LhmSensorKind.GpuClockMHz:
                    converted = value;
                    return true;

                case LhmSensorKind.GpuMemoryUsedBytes:
                case LhmSensorKind.GpuMemoryTotalBytes:
                    // LibreHardwareMonitor memory sensors report megabytes.
                    // Convert to bytes.
                    converted = value * 1024.0 * 1024.0;
                    return true;

                default:
                    return false;
            }
        }
    }
}

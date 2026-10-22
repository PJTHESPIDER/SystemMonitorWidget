using System;

namespace System_Monitor.Services
{
    /// <summary>
    /// Pure numeric helpers: temperature conversion and percentage
    /// arithmetic. No hardware dependencies; trivially testable without
    /// real sensors.
    ///
    /// <para>
    /// The percentage helpers keep apart two things that look alike: a
    /// percentage that <i>was measured</i> and one that <i>could not be
    /// computed</i>. The second is returned as null, never as zero. Zero
    /// is a measurement — a genuinely idle CPU, a genuinely empty disk —
    /// and substituting it for an absent figure is exactly the
    /// substitution the collector state exists to prevent.
    /// </para>
    /// </summary>
    public static class ThermalMath
    {
        /// <summary>
        /// Converts an ACPI thermal-zone value reported in tenths of Kelvin
        /// to degrees Celsius.
        /// </summary>
        /// <param name="tenthsKelvin">Raw ACPI value (tenths of Kelvin).</param>
        /// <returns>Temperature in degrees Celsius.</returns>
        public static double TenthsKelvinToCelsius(double tenthsKelvin)
        {
            return tenthsKelvin / 10.0 - 273.15;
        }

        /// <summary>
        /// Clamps a measured percentage to the [0, 100] range, matching the
        /// clamping applied to displayed values.
        /// </summary>
        /// <param name="value">The measured percentage.</param>
        /// <returns>
        /// The clamped percentage, or null when <paramref name="value"/> is
        /// not a finite number.
        ///
        /// <para>
        /// A NaN or an infinite value is not a measurement, so it is
        /// reported as "no value" rather than as a measured zero. The
        /// caller decides what an absent figure means for its metric: an
        /// unavailable reading, or a stale one if an earlier cycle
        /// succeeded.
        /// </para>
        /// </returns>
        public static double? ClampPercent(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return null;
            return Math.Max(0.0, Math.Min(100.0, value));
        }

        /// <summary>
        /// Computes a percentage of a total.
        /// </summary>
        /// <param name="used">The measured amount used.</param>
        /// <param name="total">The measured total.</param>
        /// <returns>
        /// The clamped percentage, or null when the ratio cannot be
        /// computed.
        ///
        /// <para>
        /// A total of zero carries no percentage: the ratio is undefined,
        /// and any number produced from it would be a figure no device
        /// reports. A non-finite operand is not a measurement either, so
        /// it is reported as "no value" rather than as a measured zero.
        /// </para>
        /// </returns>
        public static double? PercentOf(double used, double total)
        {
            if (double.IsNaN(used) || double.IsInfinity(used))
                return null;

            if (double.IsNaN(total) || double.IsInfinity(total))
                return null;

            if (total <= 0)
                return null;

            return ClampPercent(used / total * 100.0);
        }
    }
}

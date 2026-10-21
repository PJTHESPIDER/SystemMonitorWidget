using System;

namespace System_Monitor.Services
{
    /// <summary>
    /// Pure temperature-conversion helpers. No hardware dependencies;
    /// trivially testable without real sensors.
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
        /// Clamps a percentage value to the [0, 100] range, matching the
        /// clamping applied to displayed values.
        /// </summary>
        public static double ClampPercent(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return 0.0;
            return Math.Max(0.0, Math.Min(100.0, value));
        }
    }
}

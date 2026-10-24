using System;
using System.Globalization;

namespace System_Monitor.Services
{
    /// <summary>
    /// The one place that turns a measurement into display text.
    ///
    /// <para><b>Why this exists</b></para>
    ///
    /// <para>
    /// Formatting was duplicated three times: once here, once in
    /// <see cref="WidgetPayloadPresenter"/> and once in
    /// <see cref="LegacyPayloadPresenter"/>. Three copies of the same
    /// thresholds drift apart, and a widget that showed "1.0 GB" in one
    /// place and "1024 MB" in another would be a bug report nobody could
    /// reproduce. There is now one implementation, and the presenters call
    /// it.
    /// </para>
    ///
    /// <para>
    /// This type is deliberately separate from the measurement types. A
    /// measurement is a number with a unit and a status; a formatted value
    /// is a string chosen for a human. Keeping them apart means the
    /// model never carries display text, and the display rules can change
    /// without touching a single sensor.
    /// </para>
    /// </summary>
    public static class ByteFormatter
    {
        /// <summary>
        /// Formats a byte count with the largest unit that keeps the number
        /// readable.
        /// </summary>
        public static string FormatBytes(double bytes)
        {
            if (bytes < 0) bytes = 0;

            if (bytes >= 1024.0 * 1024 * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024 * 1024 * 1024):F1} TB";
            if (bytes >= 1024.0 * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
            // The megabyte tier is not optional. Without it a figure of
            // about one gigabyte is shown in kilobytes — "1002496.0 KB" —
            // which is both hard to read and long enough for the host to
            // clip it out of a narrow column.
            if (bytes >= 1024.0 * 1024)
                return $"{bytes / (1024.0 * 1024):F1} MB";
            if (bytes >= 1024.0)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes:F0} B";
        }

        /// <summary>
        /// Formats a used/total/percent triple as "used / total (percent)".
        ///
        /// When the total is unknown, only the percentage is shown: a
        /// "used" figure with no total is not meaningful on its own.
        /// </summary>
        public static string FormatUsage(double usedBytes, double totalBytes, double percent)
        {
            if (totalBytes <= 0)
            {
                return $"{percent:F1}%";
            }
            return $"{FormatBytes(usedBytes)} / {FormatBytes(totalBytes)} ({percent:F1}%)";
        }

        /// <summary>
        /// Formats a throughput in bytes per second.
        /// </summary>
        public static string FormatNetworkSpeed(double bytesPerSec)
        {
            if (bytesPerSec < 0) bytesPerSec = 0;

            if (bytesPerSec >= 1024.0 * 1024 * 1024)
                return $"{bytesPerSec / (1024.0 * 1024.0 * 1024.0):F1} GB/s";
            if (bytesPerSec >= 1024.0 * 1024)
                return $"{bytesPerSec / (1024.0 * 1024.0):F1} MB/s";
            if (bytesPerSec >= 1024.0)
                return $"{bytesPerSec / 1024.0:F1} KB/s";
            return $"{bytesPerSec:F0} B/s";
        }

        /// <summary>
        /// Formats a used/total pair as a compact capacity string "used / total unit"
        /// with a shared unit and precision based on magnitude.
        ///
        /// Contract:
        /// - Both used and total are formatted in the SAME unit (the largest unit
        ///   that keeps the total >= 1, or KB if total < 1024). The unit is
        ///   selected from the raw total before any rounding.
        /// - Precision policy (applied after rounding to the target precision):
        ///   - Below 10 selected units: at most two decimal places.
        ///   - From 10 to below 100: at most one decimal place.
        ///   - At least 100: whole selected units.
        /// - Unnecessary trailing zeros are trimmed.
        /// - Rounding uses MidpointRounding.AwayFromZero (banker's rounding
        ///   would produce unexpected even-number bias in user-visible text).
        /// - Returns "N/A" if total is missing (<= 0) or used is missing (< 0).
        /// - Returns "N/A" if the computed total is zero (capacity not reported).
        /// - Returns "N/A" if either value is NaN or infinite.
        /// </summary>
        public static string FormatCapacityCompact(double usedBytes, double totalBytes)
        {
            // Missing or invalid inputs
            if (totalBytes <= 0 || usedBytes < 0 ||
                double.IsNaN(usedBytes) || double.IsInfinity(usedBytes) ||
                double.IsNaN(totalBytes) || double.IsInfinity(totalBytes))
            {
                return NotAvailable;
            }

            // Zero capacity is not a valid figure
            if (totalBytes == 0)
                return NotAvailable;

            // Reject used > total (incoherent pair)
            if (usedBytes > totalBytes)
                return NotAvailable;

            // Determine the shared unit based on total capacity (raw total, before rounding)
            string unit;
            double divisor;

            if (totalBytes >= 1024.0 * 1024 * 1024 * 1024)
            {
                unit = "TB";
                divisor = 1024.0 * 1024 * 1024 * 1024;
            }
            else if (totalBytes >= 1024.0 * 1024 * 1024)
            {
                unit = "GB";
                divisor = 1024.0 * 1024 * 1024;
            }
            else if (totalBytes >= 1024.0 * 1024)
            {
                unit = "MB";
                divisor = 1024.0 * 1024;
            }
            else if (totalBytes >= 1024.0)
            {
                unit = "KB";
                divisor = 1024.0;
            }
            else
            {
                unit = "B";
                divisor = 1.0;
            }

            double usedInUnit = usedBytes / divisor;
            double totalInUnit = totalBytes / divisor;

            // Apply precision policy based on total magnitude in the selected unit
            // Total determines the precision tier for both used and total
            int totalRounded;
            if (totalInUnit >= 100)
                totalRounded = (int)Math.Round(totalInUnit, MidpointRounding.AwayFromZero);
            else if (totalInUnit >= 10)
                totalRounded = (int)Math.Round(totalInUnit * 10) / 10; // 1 decimal
            else
                totalRounded = (int)Math.Round(totalInUnit * 100) / 100; // 2 decimals

            // Actually, we need to use the tier based on the unrounded totalInUnit
            // but apply the same tier to both used and total
            var tier = totalInUnit >= 100 ? 2 : (totalInUnit >= 10 ? 1 : 0);

            string FormatValue(double value, int tier)
            {
                double rounded;
                string format;
                if (tier == 2) // >= 100: whole units
                {
                    rounded = Math.Round(value, MidpointRounding.AwayFromZero);
                    format = "F0";
                }
                else if (tier == 1) // >= 10: one decimal
                {
                    rounded = Math.Round(value * 10) / 10.0;
                    format = "F1";
                }
                else // < 10: two decimals
                {
                    rounded = Math.Round(value * 100) / 100.0;
                    format = "F2";
                }

                var str = rounded.ToString(format, CultureInfo.InvariantCulture);
                // Trim trailing zeros
                if (str.Contains('.'))
                {
                    str = str.TrimEnd('0').TrimEnd('.');
                }
                return str;
            }

            string usedStr = FormatValue(usedInUnit, tier);
            string totalStr = FormatValue(totalInUnit, tier);

            // Special case: used rounds to 0 but is actually > 0
            // Show as "<0.01" (or appropriate minimum) to distinguish from true zero
            if (usedBytes > 0 && usedStr == "0")
            {
                if (tier == 0)
                    usedStr = "<0.01";
                else if (tier == 1)
                    usedStr = "<0.1";
                else
                    usedStr = "<1";
            }

            return $"{usedStr} / {totalStr} {unit}";
        }

        /// <summary>
        /// Formats a percentage.
        /// </summary>
        public static string FormatPercent(double value) =>
            $"{value.ToString("F1", CultureInfo.CurrentCulture)}%";

        /// <summary>
        /// Formats a temperature in degrees Celsius.
        /// </summary>
        public static string FormatTemperature(double value) =>
            $"{value.ToString("F1", CultureInfo.CurrentCulture)}°C";

        /// <summary>
        /// Formats a clock rate, switching to gigahertz above 1000 MHz.
        /// </summary>
        public static string FormatClock(double megahertz)
        {
            if (megahertz >= 1000)
                return $"{(megahertz / 1000.0).ToString("F2", CultureInfo.CurrentCulture)} GHz";
            return $"{megahertz.ToString("F0", CultureInfo.CurrentCulture)} MHz";
        }

        /// <summary>
        /// The unavailable marker used by the payload presenters.
        /// </summary>
        public const string NotAvailable = "N/A";
    }
}

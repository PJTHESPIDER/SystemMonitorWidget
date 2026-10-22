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
    }
}

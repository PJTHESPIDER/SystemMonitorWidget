using System;

namespace System_Monitor.Services
{
    public static class ByteFormatter
    {
        public static string FormatBytes(double bytes)
        {
            if (bytes < 0) bytes = 0;

            if (bytes >= 1024.0 * 1024 * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024 * 1024 * 1024):F1} TB";
            if (bytes >= 1024.0 * 1024 * 1024)
                return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
            if (bytes >= 1024.0 * 1024)
                return $"{bytes / (1024.0 * 1024):F1} MB";
            if (bytes >= 1024.0)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes:F0} B";
        }

        public static string FormatUsage(double usedBytes, double totalBytes, double percent)
        {
            if (totalBytes <= 0)
            {
                return $"{percent:F1}%";
            }
            return $"{FormatBytes(usedBytes)} / {FormatBytes(totalBytes)} ({percent:F1}%)";
        }

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
    }
}


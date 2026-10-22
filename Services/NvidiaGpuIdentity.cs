using System;
using System.Globalization;

namespace System_Monitor.Services
{
    /// <summary>
    /// Normalizes the identity keys reported by nvidia-smi and by the Windows
    /// PCI device tree, so the two can be compared.
    ///
    /// The keys are the PCI vendor id and device id, e.g. "VEN_10DE&amp;DEV_1E91".
    /// Both sides expose them:
    ///
    ///   - nvidia-smi reports <c>pci.device_id</c> as "0x1E9110DE", i.e. the
    ///     device id in the high half and the vendor id in the low half.
    ///   - The Windows PCI device tree (WMI <c>Win32_VideoController</c>'s
    ///     <c>PNPDeviceID</c>, and LibreHardwareMonitor's
    ///     <c>GenericGpu.DeviceId</c>) reports the pair as
    ///     "PCI\VEN_10DE&amp;DEV_1E91&amp;SUBSYS_...".
    ///
    /// A card that is the only card of its model is therefore identifiable
    /// across both sources. A machine with two identical cards is not: the
    /// keys would collide. That case is handled by the reconciler, which
    /// refuses to guess.
    /// </summary>
    public static class NvidiaGpuIdentity
    {
        /// <summary>
        /// Normalizes an nvidia-smi <c>pci.device_id</c> value into a
        /// "VEN_xxxx&amp;DEV_yyyy" key. Returns an empty string when the value
        /// cannot be interpreted.
        /// </summary>
        public static string NormalizePciDeviceKey(string? pciDeviceId)
        {
            if (string.IsNullOrWhiteSpace(pciDeviceId))
                return string.Empty;

            var value = pciDeviceId.Trim();

            // nvidia-smi writes "0x1E9110DE": device id high, vendor id low.
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(2);

            if (value.Length != 8)
                return string.Empty;

            if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed))
                return string.Empty;

            var deviceId = (packed >> 16) & 0xFFFF;
            var vendorId = packed & 0xFFFF;

            return FormatKey(vendorId, deviceId);
        }

        /// <summary>
        /// Extracts a "VEN_xxxx&amp;DEV_yyyy" key from a Windows PCI device
        /// id, e.g. "PCI\VEN_10DE&amp;DEV_1E91&amp;SUBSYS_20091A58&amp;REV_A1\4&amp;7AADB68&amp;0&amp;0008".
        /// Returns an empty string when the id does not carry both parts.
        /// </summary>
        public static string ExtractPciDeviceKey(string? windowsDeviceId)
        {
            if (string.IsNullOrWhiteSpace(windowsDeviceId))
                return string.Empty;

            var value = windowsDeviceId.Trim();

            var vendor = FindToken(value, "VEN_");
            var device = FindToken(value, "DEV_");

            if (vendor is null || device is null)
                return string.Empty;

            return $"VEN_{vendor}&DEV_{device}";
        }

        /// <summary>
        /// Finds a "PREFIX<hex>" token in a device id and returns the hex
        /// digits that follow it.
        /// </summary>
        private static string? FindToken(string value, string prefix)
        {
            var index = value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                return null;

            var start = index + prefix.Length;
            var length = 0;

            while (start + length < value.Length && length < 4)
            {
                var c = value[start + length];
                if (!Uri.IsHexDigit(c))
                    break;
                length++;
            }

            if (length == 0)
                return null;

            return value.Substring(start, length).ToUpperInvariant();
        }

        /// <summary>
        /// Formats a vendor/device id pair into the canonical key form.
        /// </summary>
        private static string FormatKey(uint vendorId, uint deviceId) =>
            $"VEN_{vendorId:X4}&DEV_{deviceId:X4}";
    }
}

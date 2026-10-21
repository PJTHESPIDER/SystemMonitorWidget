using System.Net.NetworkInformation;

namespace System_Monitor.Services
{
    /// <summary>
    /// Policy for which network interfaces contribute to throughput metrics.
    ///
    /// Policy:
    ///   - Exclude loopback interfaces (127.0.0.1 / ::1). They carry no
    ///     real external throughput and would distort the numbers.
    ///   - Exclude interfaces that are not operationally up.
    ///   - Include all other interfaces: Ethernet, Wi-Fi, and VPN/tunnel
    ///     adapters. VPN traffic is legitimate and must not be silently
    ///     excluded merely because an adapter is virtual.
    /// </summary>
    public static class NetworkInterfacePolicy
    {
        public static bool ShouldInclude(NetworkInterfaceType type, OperationalStatus status)
        {
            if (type == NetworkInterfaceType.Loopback)
                return false;
            if (status != OperationalStatus.Up)
                return false;
            return true;
        }

        public static bool ShouldInclude(NetworkInterface ni) =>
            ShouldInclude(ni.NetworkInterfaceType, ni.OperationalStatus);
    }
}

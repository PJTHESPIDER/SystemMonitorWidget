using System.Net.NetworkInformation;
using System_Monitor.Services;
using Xunit;

namespace SystemMonitor.Tests;

public class NetworkInterfacePolicyTests
{
    [Fact]
    public void LoopbackInterface_IsExcluded()
    {
        Assert.False(NetworkInterfacePolicy.ShouldInclude(
            NetworkInterfaceType.Loopback, OperationalStatus.Up));
    }

    [Fact]
    public void EthernetInterface_Up_IsIncluded()
    {
        Assert.True(NetworkInterfacePolicy.ShouldInclude(
            NetworkInterfaceType.Ethernet, OperationalStatus.Up));
    }

    [Fact]
    public void EthernetInterface_Down_IsExcluded()
    {
        Assert.False(NetworkInterfacePolicy.ShouldInclude(
            NetworkInterfaceType.Ethernet, OperationalStatus.Down));
    }

    [Fact]
    public void VpnTunnelInterface_Up_IsIncluded()
    {
        Assert.True(NetworkInterfacePolicy.ShouldInclude(
            NetworkInterfaceType.Tunnel, OperationalStatus.Up));
    }

    [Fact]
    public void Wireless80211Interface_Up_IsIncluded()
    {
        Assert.True(NetworkInterfacePolicy.ShouldInclude(
            NetworkInterfaceType.Wireless80211, OperationalStatus.Up));
    }
}

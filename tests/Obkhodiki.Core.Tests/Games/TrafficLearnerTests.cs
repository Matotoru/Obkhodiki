using System.Net;
using Obkhodiki.Core.Games;

namespace Obkhodiki.Core.Tests.Games;

public class TrafficLearnerTests
{
    private static FlowObservation Flow(string ip, int port, FlowProtocol proto = FlowProtocol.Udp) =>
        new(IPAddress.Parse(ip), port, proto);

    [Fact]
    public void Add_PublicFlows_CollectsAddressesAndPortsPerProtocol()
    {
        var learner = new TrafficLearner();

        learner.Add(Flow("3.120.10.5", 7777));
        learner.Add(Flow("3.120.10.5", 7778));
        learner.Add(Flow("18.192.1.1", 443, FlowProtocol.Tcp));

        Assert.Equal(new[] { "18.192.1.1", "3.120.10.5" }, learner.Addresses.Select(a => a.ToString()).Order());
        Assert.Equal("7777-7778", learner.UdpPorts.ToString());
        Assert.Equal("443", learner.TcpPorts.ToString());
    }

    [Fact]
    public void Endpoints_RememberWhetherAddressWasEverSeenOverUdp()
    {
        var learner = new TrafficLearner();

        learner.Add(Flow("3.120.10.5", 7777));
        learner.Add(Flow("3.120.10.5", 443, FlowProtocol.Tcp)); // TCP after UDP must not clear the flag
        learner.Add(Flow("18.192.1.1", 443, FlowProtocol.Tcp));

        var byIp = learner.Endpoints.ToDictionary(e => e.Address.ToString(), e => e.SeenUdp);
        Assert.True(byIp["3.120.10.5"]);
        Assert.False(byIp["18.192.1.1"]);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("172.16.0.0")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("100.64.0.0")]
    [InlineData("100.127.255.255")]
    [InlineData("169.254.1.1")]
    [InlineData("224.0.0.251")]
    [InlineData("255.255.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::1")]
    [InlineData("fe80::1")]
    [InlineData("fd00::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::fb")]
    public void Add_NonPublicAddress_Ignored(string ip)
    {
        var learner = new TrafficLearner();

        learner.Add(Flow(ip, 7777));

        Assert.Empty(learner.Addresses);
        Assert.True(learner.UdpPorts.IsEmpty);
    }

    [Theory]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    [InlineData("11.0.0.1")]
    [InlineData("223.255.255.254")]
    [InlineData("2a05:d014::1")]
    [InlineData("2001:4860::8888")]
    public void Add_PublicAddressNextToPrivateRanges_Kept(string ip)
    {
        var learner = new TrafficLearner();

        learner.Add(Flow(ip, 7777));

        Assert.Equal(ip, learner.Addresses.Single().ToString());
    }

    [Theory]
    [InlineData(53)]
    [InlineData(0)]
    public void Add_DnsOrZeroPort_Ignored(int port)
    {
        var learner = new TrafficLearner();

        learner.Add(Flow("8.8.8.8", port));

        Assert.Empty(learner.Addresses);
    }

    [Fact]
    public void Add_IPv4MappedIPv6_StoredAsIPv4()
    {
        var learner = new TrafficLearner();

        learner.Add(Flow("::ffff:3.120.10.5", 7777));

        Assert.Equal("3.120.10.5", learner.Addresses.Single().ToString());
    }

    [Fact]
    public void Add_IsThreadSafe()
    {
        var learner = new TrafficLearner();

        Parallel.For(0, 1000, i => learner.Add(Flow($"3.120.{i / 250}.{i % 250 + 1}", 7000 + i % 10)));

        Assert.Equal(1000, learner.Addresses.Count);
        Assert.Equal("7000-7009", learner.UdpPorts.ToString());
    }
}

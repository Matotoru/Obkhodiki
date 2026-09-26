using System.Runtime.InteropServices;
using ZapretHub.Core.Games;

namespace ZapretHub.Core.Tests.Games;

// The driver itself needs admin rights and is verified manually; these pin the interop contract and event filtering.
public unsafe class WinDivertFlowMonitorTests
{
    private const uint LoopbackBit = 1u << 18;
    private const uint Ipv6Bit = 1u << 20;

    [Fact]
    public void AddressStruct_MatchesWinDivertLayout()
    {
        Assert.Equal(80, sizeof(WinDivertFlowMonitor.WinDivertAddress));
        Assert.Equal(64, sizeof(WinDivertFlowMonitor.WinDivertDataFlow));
        Assert.Equal(16, Offset(nameof(WinDivertFlowMonitor.WinDivertDataFlow.ProcessId)));
        Assert.Equal(36, Offset(nameof(WinDivertFlowMonitor.WinDivertDataFlow.RemoteAddr)));
        Assert.Equal(52, Offset(nameof(WinDivertFlowMonitor.WinDivertDataFlow.LocalPort)));
        Assert.Equal(54, Offset(nameof(WinDivertFlowMonitor.WinDivertDataFlow.RemotePort)));
        Assert.Equal(56, Offset(nameof(WinDivertFlowMonitor.WinDivertDataFlow.Protocol)));
    }

    private static int Offset(string field) => (int)Marshal.OffsetOf<WinDivertFlowMonitor.WinDivertDataFlow>(field);

    [Theory]
    [InlineData(0u, false, false)]
    [InlineData(LoopbackBit, true, false)]
    [InlineData(Ipv6Bit, false, true)]
    public void AddressBits_LoopbackAndIpv6AreIndependentBits(uint bits, bool loopback, bool ipv6)
    {
        var addr = new WinDivertFlowMonitor.WinDivertAddress { Bits = bits };

        Assert.Equal(loopback, addr.Loopback);
        Assert.Equal(ipv6, addr.IPv6);
    }

    [Fact]
    public void AddressBits_LayerAndEvent()
    {
        var addr = new WinDivertFlowMonitor.WinDivertAddress { Bits = 3u | (4u << 8) };

        Assert.Equal(3, addr.Layer);
        Assert.Equal(4, addr.Event);
    }

    [Fact]
    public void ToAddress_IPv4_FromLowWordHostOrder()
    {
        var words = stackalloc uint[4] { 0x03780A05, 0x0000FFFF, 0, 0 };

        Assert.Equal("3.120.10.5", WinDivertFlowMonitor.ToAddress(words, ipv6: false).ToString());
    }

    [Fact]
    public void ToAddress_IPv6_WordsReversed()
    {
        // 2a05:d014:0001:0002:0000:0000:0000:0001 stored most-significant word last.
        var words = stackalloc uint[4] { 0x00000001, 0x00000000, 0x00010002, 0x2A05D014 };

        Assert.Equal("2a05:d014:1:2::1", WinDivertFlowMonitor.ToAddress(words, ipv6: true).ToString());
    }

    private static WinDivertFlowMonitor.WinDivertAddress Event(
        int evt = WinDivertFlowMonitor.EventFlowEstablished, byte proto = WinDivertFlowMonitor.ProtoUdp,
        uint pid = 42, uint extraBits = 0, ushort port = 7777)
    {
        var addr = new WinDivertFlowMonitor.WinDivertAddress { Bits = 2u | ((uint)evt << 8) | extraBits };
        addr.Data.ProcessId = pid;
        addr.Data.Protocol = proto;
        addr.Data.RemotePort = port;
        addr.Data.RemoteAddr[0] = 0x03780A05;
        return addr;
    }

    private static readonly Func<int, bool> Only42 = pid => pid == 42;

    [Fact]
    public void TryMap_WatchedUdpFlow_Mapped()
    {
        var flow = WinDivertFlowMonitor.TryMap(Event(), WinDivertFlowMonitor.EventFlowEstablished, Only42);

        Assert.NotNull(flow);
        Assert.Equal("3.120.10.5", flow!.Remote.ToString());
        Assert.Equal(7777, flow.RemotePort);
        Assert.Equal(FlowProtocol.Udp, flow.Protocol);
    }

    [Fact]
    public void TryMap_Tcp_MappedAsTcp()
    {
        var flow = WinDivertFlowMonitor.TryMap(Event(proto: WinDivertFlowMonitor.ProtoTcp, port: 443), WinDivertFlowMonitor.EventFlowEstablished, Only42);

        Assert.Equal(FlowProtocol.Tcp, flow?.Protocol);
    }

    [Fact]
    public void TryMap_OtherEvent_Ignored()
    {
        Assert.Null(WinDivertFlowMonitor.TryMap(Event(evt: 2), WinDivertFlowMonitor.EventFlowEstablished, Only42));
    }

    [Fact]
    public void TryMap_Loopback_Ignored()
    {
        Assert.Null(WinDivertFlowMonitor.TryMap(Event(extraBits: LoopbackBit), WinDivertFlowMonitor.EventFlowEstablished, Only42));
    }

    [Fact]
    public void TryMap_OtherProtocol_Ignored()
    {
        Assert.Null(WinDivertFlowMonitor.TryMap(Event(proto: 1), WinDivertFlowMonitor.EventFlowEstablished, Only42));
    }

    [Fact]
    public void TryMap_UnwatchedProcess_Ignored()
    {
        Assert.Null(WinDivertFlowMonitor.TryMap(Event(pid: 7), WinDivertFlowMonitor.EventFlowEstablished, Only42));
    }
}

using System.Net;
using System.Net.Sockets;

namespace ZapretHub.Core.Games;

public enum FlowProtocol { Tcp, Udp }

public sealed record FlowObservation(IPAddress Remote, int RemotePort, FlowProtocol Protocol);

/// <param name="SeenUdp">Game servers talk UDP; addresses seen only over TCP are usually APIs/CDNs.</param>
public sealed record LearnedAddress(IPAddress Address, bool SeenUdp);

/// <summary>Collects the public endpoints a game talks to while the user plays.</summary>
public sealed class TrafficLearner
{
    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, bool> _addresses = new();
    private readonly HashSet<int> _tcpPorts = new();
    private readonly HashSet<int> _udpPorts = new();
    private readonly List<IPEndPoint> _tcpEndpoints = new();
    private const int MaxTcpEndpoints = 32;

    public IReadOnlyCollection<IPAddress> Addresses
    {
        get { lock (_gate) return _addresses.Keys.ToList(); }
    }

    public IReadOnlyCollection<LearnedAddress> Endpoints
    {
        get { lock (_gate) return _addresses.Select(kv => new LearnedAddress(kv.Key, kv.Value)).ToList(); }
    }

    /// <summary>Distinct TCP endpoints in the order first seen (for path quality measurements).</summary>
    public IReadOnlyList<IPEndPoint> TcpEndpoints
    {
        get { lock (_gate) return _tcpEndpoints.ToList(); }
    }

    public PortSet TcpPorts
    {
        get { lock (_gate) return PortSet.FromPorts(_tcpPorts); }
    }

    public PortSet UdpPorts
    {
        get { lock (_gate) return PortSet.FromPorts(_udpPorts); }
    }

    public void Add(FlowObservation flow)
    {
        var ip = flow.Remote.IsIPv4MappedToIPv6 ? flow.Remote.MapToIPv4() : flow.Remote;
        // DNS is never game traffic and resolvers are not what DPI blocks.
        if (flow.RemotePort is <= 0 or > 65535 or 53 || !IsPublic(ip)) return;

        lock (_gate)
        {
            var udp = flow.Protocol == FlowProtocol.Udp;
            _addresses[ip] = udp || (_addresses.TryGetValue(ip, out var seen) && seen);
            (flow.Protocol == FlowProtocol.Tcp ? _tcpPorts : _udpPorts).Add(flow.RemotePort);
            if (!udp && _tcpEndpoints.Count < MaxTcpEndpoints)
            {
                var endpoint = new IPEndPoint(ip, flow.RemotePort);
                if (!_tcpEndpoints.Contains(endpoint)) _tcpEndpoints.Add(endpoint);
            }
        }
    }

    public static bool IsPublic(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 0
                     || b[0] == 10
                     || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                     || (b[0] == 169 && b[1] == 254)
                     || b[0] >= 224);
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return !(ip.IsIPv6LinkLocal
                     || ip.IsIPv6Multicast
                     || ip.IsIPv6SiteLocal
                     || (b[0] & 0xFE) == 0xFC // unique local fc00::/7
                     || ip.Equals(IPAddress.IPv6None)
                     || ip.IsIPv4MappedToIPv6);
        }
        return false;
    }
}

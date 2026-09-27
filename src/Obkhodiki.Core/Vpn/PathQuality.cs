using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Obkhodiki.Core.Vpn;

/// <param name="MedianMs">Median TCP handshake time of successful samples; null when none succeeded.</param>
/// <param name="JitterMs">Mean difference between consecutive successful samples to the same endpoint.</param>
/// <param name="LossPercent">Share of attempts that failed or timed out.</param>
public sealed record PathStats(double? MedianMs, double JitterMs, double LossPercent, int Samples)
{
    public static PathStats From(IReadOnlyList<TimeSpan?> samples) => From(new[] { samples });

    /// <summary>Samples grouped per endpoint: different servers differ in latency, and that is not jitter.</summary>
    public static PathStats From(IReadOnlyList<IReadOnlyList<TimeSpan?>> perEndpoint)
    {
        var all = perEndpoint.SelectMany(g => g).ToList();
        if (all.Count == 0) return new PathStats(null, 0, 100, 0);
        var ok = all.Where(s => s is not null).Select(s => s!.Value.TotalMilliseconds).ToList();
        var loss = 100.0 * (all.Count - ok.Count) / all.Count;
        if (ok.Count == 0) return new PathStats(null, 0, loss, all.Count);

        var sorted = ok.Order().ToList();
        var median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        var steps = perEndpoint
            .Select(g => g.Where(x => x is not null).Select(x => x!.Value.TotalMilliseconds).ToList())
            .SelectMany(g => g.Zip(g.Skip(1), (x, y) => Math.Abs(y - x)))
            .ToList();
        return new PathStats(median, steps.Count == 0 ? 0 : steps.Average(), loss, all.Count);
    }
}

public enum PathChoice { Direct, Vpn }

public sealed record PathDecision(PathChoice Choice, string Reason);

/// <summary>
/// Picks the tunnel only when it is clearly better: a detour through the VPS usually adds latency, so ties and
/// small differences stay direct.
/// </summary>
public static class PathChooser
{
    public const double LossMarginPercent = 2;
    public const double LatencyMarginMs = 15;

    public static PathDecision Choose(PathStats direct, PathStats tunnel)
    {
        if (tunnel.MedianMs is null) return new(PathChoice.Direct, "VPS не отвечает");
        if (direct.MedianMs is null) return new(PathChoice.Vpn, "напрямую сервер недоступен");

        if (tunnel.LossPercent + LossMarginPercent < direct.LossPercent)
        {
            return new(PathChoice.Vpn, $"меньше потерь: {tunnel.LossPercent:F0}% против {direct.LossPercent:F0}%");
        }
        if (direct.LossPercent + LossMarginPercent < tunnel.LossPercent)
        {
            return new(PathChoice.Direct, $"через VPS больше потерь: {tunnel.LossPercent:F0}% против {direct.LossPercent:F0}%");
        }
        if (tunnel.MedianMs + LatencyMarginMs < direct.MedianMs)
        {
            return new(PathChoice.Vpn, $"ниже пинг: {tunnel.MedianMs:F0} мс против {direct.MedianMs:F0} мс");
        }
        return new(PathChoice.Direct, $"напрямую не хуже: {direct.MedianMs:F0} мс против {tunnel.MedianMs:F0} мс через VPS");
    }
}

public interface IConnectProbe
{
    /// <returns>One application-level round trip to the target, or null on failure/timeout.</returns>
    Task<TimeSpan?> ConnectAsync(IPEndPoint target, TimeSpan timeout, CancellationToken ct);
}

/// <summary>
/// The round trip both paths are measured with, taken on an already established connection so both paths
/// measure the same thing: the first ClientHello offers no key share, a TLS 1.3 server answers with a
/// HelloRetryRequest, and the time from the second ClientHello to the server's answer is one clean round trip
/// (this machine → [VPS →] server → back). The first exchange is not timed: through the VPS it also contains
/// the VPS connecting to the server, and a local TUN stack only dials out after the first data.
/// Servers that do not ask for a retry (no TLS 1.3) cannot be measured this way and count as failures on both paths.
/// </summary>
public static class TlsPing
{
    public static bool IsTlsPort(int port) => port is 443 or 8443;

    // Published ranges of anycast CDNs (Cloudflare, Fastly). Such an address is answered by the node nearest to
    // whoever asks, so it says nothing about where a game's servers are: measured only when nothing else answers.
    private static readonly (uint Net, int Bits)[] AnycastCdn = new[]
    {
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18", "108.162.192.0/18",
        "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22", "1.1.1.0/24", "1.0.0.0/24",
        "151.101.0.0/16", "199.232.0.0/16", "146.75.0.0/17", "23.235.32.0/20", "43.249.72.0/22", "103.244.50.0/24",
        "103.245.222.0/23", "103.245.224.0/24", "104.156.80.0/20", "140.248.64.0/18", "140.248.128.0/17",
        "157.52.64.0/18", "167.82.0.0/17", "172.111.64.0/18", "185.31.16.0/22",
    }.Select(c =>
    {
        var parts = c.Split('/');
        return (BinaryPrimitives.ReadUInt32BigEndian(IPAddress.Parse(parts[0]).GetAddressBytes()), int.Parse(parts[1]));
    }).ToArray();

    public static bool IsAnycastCdn(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var ip = BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());
        return AnycastCdn.Any(r => (ip ^ r.Net) >> (32 - r.Bits) == 0);
    }

    /// <summary>
    /// Order in which a game's TCP endpoints are worth measuring: next to its UDP (game) servers first, the usual
    /// TLS ports before others, anycast CDNs last. Stable otherwise.
    /// </summary>
    public static IReadOnlyList<IPEndPoint> RankForProbe(IEnumerable<IPEndPoint> endpoints, IEnumerable<IPAddress>? gameServers = null)
    {
        var servers = (gameServers ?? Enumerable.Empty<IPAddress>())
            .Select(a => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.GetAddressBytes())
            .ToList();
        int Near(IPEndPoint e)
        {
            if (servers.Count == 0 || e.Address.AddressFamily != AddressFamily.InterNetwork) return 3;
            var b = e.Address.GetAddressBytes();
            if (servers.Any(s => s.AsSpan().SequenceEqual(b))) return 0;
            if (servers.Any(s => s[0] == b[0] && s[1] == b[1] && s[2] == b[2])) return 1;
            if (servers.Any(s => s[0] == b[0] && s[1] == b[1])) return 2;
            return 3;
        }
        return endpoints
            .OrderBy(e => IsAnycastCdn(e.Address) ? 1 : 0)
            .ThenBy(Near)
            .ThenBy(e => IsTlsPort(e.Port) ? 0 : 1)
            .ToList();
    }

    private static readonly byte[] HrrRandom = Convert.FromHexString("CF21AD74E59A6111BE1D8C021E65B891C2A211167ABB8C5E079E09E2C8A8339C");

    /// <summary>
    /// One round trip to a TLS server on an already open connection, timed on a second exchange so that tunnel
    /// setup and the server's first answer are not counted.
    /// TLS 1.3 servers answer a ClientHello without key shares with a HelloRetryRequest; the retried hello is timed.
    /// TLS 1.2-only servers (some game backends) finish their first flight with ServerHelloDone; a message out of
    /// order is then timed — the server rejects it with an alert or closes at once.
    /// </summary>
    public static async Task<TimeSpan?> RoundTripAsync(Socket socket, CancellationToken ct)
    {
        var random = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        await socket.SendAsync(BuildClientHello(random, keyShare: null, cookie: null), ct).ConfigureAwait(false);

        var first = await ReadRecordAsync(socket, ct).ConfigureAwait(false);
        if (first is null) return null;
        if (IsHelloRetryRequest(first, out var group, out var cookie))
        {
            var keyShare = KeyShareFor(group);
            if (keyShare is null) return null;
            return await TimeAnswerAsync(socket, BuildClientHello(random, keyShare, cookie), acceptClose: false, ct).ConfigureAwait(false);
        }
        if (!await ReachServerHelloDoneAsync(socket, first, ct).ConfigureAwait(false)) return null;
        return await TimeAnswerAsync(socket, UnexpectedFinished, acceptClose: true, ct).ConfigureAwait(false);
    }

    // A plaintext Finished before any key exchange: every TLS 1.2 server answers it with an alert (or a close).
    private static readonly byte[] UnexpectedFinished = { 0x16, 0x03, 0x03, 0x00, 0x04, 0x14, 0x00, 0x00, 0x00 };

    private static async Task<TimeSpan?> TimeAnswerAsync(Socket socket, byte[] message, bool acceptClose, CancellationToken ct)
    {
        // Servers often send a change_cipher_spec right after the retry request. Anything still arriving from
        // the first exchange must be consumed now, or it would be taken for the answer to the second one.
        await DrainAsync(socket, ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        await socket.SendAsync(message, ct).ConfigureAwait(false);
        var reply = new byte[1];
        var n = await socket.ReceiveAsync(reply, ct).ConfigureAwait(false);
        var elapsed = sw.Elapsed;
        // Handshake (0x16) or an alert (0x15, e.g. about the random key): either way the server answered.
        if (n == 0) return acceptClose ? elapsed : null;
        return reply[0] is 0x16 or 0x15 ? elapsed : null;
    }

    /// <summary>
    /// Reads a TLS 1.2 server's first flight (ServerHello … ServerHelloDone), which may span several records.
    /// </summary>
    internal static async Task<bool> ReachServerHelloDoneAsync(Socket socket, byte[] firstRecord, CancellationToken ct)
    {
        var handshake = new List<byte>();
        var record = firstRecord;
        var first = true;
        for (var count = 0; count < 32 && handshake.Count < 64 * 1024; count++)
        {
            if (record is null || record[0] != 0x16) return false;
            handshake.AddRange(record.Skip(5));
            var p = 0;
            while (p + 4 <= handshake.Count)
            {
                var type = handshake[p];
                if (first && type != 0x02) return false; // must start with ServerHello
                first = false;
                if (type == 0x0e) return true;
                p += 4 + ((handshake[p + 1] << 16) | (handshake[p + 2] << 8) | handshake[p + 3]);
            }
            record = await ReadRecordAsync(socket, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static async Task DrainAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[4096];
        for (var round = 0; round < 3; round++)
        {
            await Task.Delay(30, ct).ConfigureAwait(false);
            while (socket.Available > 0)
            {
                await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Reads one TLS record (header + body); null when the peer closed or sent something else.</summary>
    private static async Task<byte[]?> ReadRecordAsync(Socket socket, CancellationToken ct)
    {
        var header = new byte[5];
        if (!await ReadExactlyAsync(socket, header, ct).ConfigureAwait(false)) return null;
        if (header[0] is not (0x16 or 0x15) || header[1] != 0x03) return null;
        var length = (header[3] << 8) | header[4];
        if (length is 0 or > 16384 + 256) return null;
        var body = new byte[length];
        if (!await ReadExactlyAsync(socket, body, ct).ConfigureAwait(false)) return null;
        return header.Concat(body).ToArray();
    }

    private static async Task<bool> ReadExactlyAsync(Socket socket, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }

    /// <summary>Recognises a HelloRetryRequest and extracts the requested group and an optional cookie.</summary>
    internal static bool IsHelloRetryRequest(byte[] record, out int group, out byte[]? cookie)
    {
        group = 0;
        cookie = null;
        try
        {
            // record(5) | handshake type(1)=2 | len(3) | version(2) | random(32) | sid len(1) | sid | suite(2) | comp(1) | ext len(2) | ext
            if (record.Length < 5 + 4 + 2 + 32 + 1 || record[0] != 0x16 || record[5] != 0x02) return false;
            var p = 5 + 4 + 2;
            if (!record.AsSpan(p, 32).SequenceEqual(HrrRandom)) return false;
            p += 32;
            p += 1 + record[p];
            p += 2 + 1;
            var extEnd = p + 2 + ((record[p] << 8) | record[p + 1]);
            p += 2;
            while (p + 4 <= extEnd && extEnd <= record.Length)
            {
                var type = (record[p] << 8) | record[p + 1];
                var len = (record[p + 2] << 8) | record[p + 3];
                var data = record.AsSpan(p + 4, len);
                if (type == 0x0033 && len == 2) group = (data[0] << 8) | data[1];
                if (type == 0x002c) cookie = data.ToArray(); // cookie extension body, echoed as-is
                p += 4 + len;
            }
            return group != 0;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    // x25519 accepts any 32 bytes; for P-256 a random blob is not a valid point, but the server still answers
    // (with an alert), which is all the timing needs.
    private static byte[]? KeyShareFor(int group) => group switch
    {
        0x001d => System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
        0x0017 => new byte[] { 0x04 }.Concat(System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)).ToArray(),
        _ => null,
    };

    /// <summary>A minimal TLS 1.3/1.2 ClientHello (no SNI). Without <paramref name="keyShare"/> it asks for a retry;
    /// a 32-byte share is sent as x25519, a 65-byte one as secp256r1.</summary>
    public static byte[] BuildClientHello(byte[] random, byte[]? keyShare, byte[]? cookie) =>
        Build(random, keyShare is null ? null : (keyShare.Length == 32 ? 0x001d : 0x0017, keyShare), cookie);

    private static byte[] Build(byte[] random, (int Group, byte[] Key)? keyShare, byte[]? cookie)
    {
        var extensions = new List<byte>();
        void Ext(int type, byte[] data)
        {
            extensions.AddRange(new[] { (byte)(type >> 8), (byte)type, (byte)(data.Length >> 8), (byte)data.Length });
            extensions.AddRange(data);
        }
        Ext(0x002b, new byte[] { 4, 0x03, 0x04, 0x03, 0x03 });                    // supported_versions: TLS 1.3, 1.2
        Ext(0x000a, new byte[] { 0, 4, 0x00, 0x1d, 0x00, 0x17 });                 // supported_groups: x25519, secp256r1
        Ext(0x000b, new byte[] { 1, 0 });                                         // ec_point_formats: uncompressed (TLS 1.2)
        Ext(0x000d, new byte[] { 0, 8, 0x04, 0x03, 0x08, 0x04, 0x04, 0x01, 0x05, 0x01 }); // signature_algorithms
        if (keyShare is { } ks)
        {
            var entry = new List<byte> { (byte)(ks.Group >> 8), (byte)ks.Group, (byte)(ks.Key.Length >> 8), (byte)ks.Key.Length };
            entry.AddRange(ks.Key);
            Ext(0x0033, new[] { (byte)(entry.Count >> 8), (byte)entry.Count }.Concat(entry).ToArray());
        }
        else
        {
            Ext(0x0033, new byte[] { 0, 0 });                                     // empty key_share: "please retry"
        }
        if (cookie is not null) Ext(0x002c, cookie);

        var body = new List<byte> { 0x03, 0x03 };
        body.AddRange(random);
        body.Add(0); // no session id
        // TLS 1.3 suites, then ECDHE suites for TLS 1.2-only servers.
        byte[] suites = { 0x13, 0x01, 0x13, 0x02, 0x13, 0x03, 0xc0, 0x2b, 0xc0, 0x2f, 0xc0, 0x2c, 0xc0, 0x30, 0xcc, 0xa9, 0xcc, 0xa8 };
        body.AddRange(new[] { (byte)0, (byte)suites.Length });
        body.AddRange(suites);
        body.AddRange(new byte[] { 1, 0 });
        body.AddRange(new[] { (byte)(extensions.Count >> 8), (byte)extensions.Count });
        body.AddRange(extensions);

        var handshake = new List<byte> { 0x01, 0, (byte)(body.Count >> 8), (byte)body.Count };
        handshake.AddRange(body);
        var record = new List<byte> { 0x16, 0x03, 0x01, (byte)(handshake.Count >> 8), (byte)handshake.Count };
        record.AddRange(handshake);
        return record.ToArray();
    }
}

/// <summary>The direct path (through zapret, like the game's own traffic).</summary>
public sealed class DirectTcpProbe : IConnectProbe
{
    public async Task<TimeSpan?> ConnectAsync(IPEndPoint target, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(target, cts.Token).ConfigureAwait(false);
            return await TlsPing.RoundTripAsync(socket, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
    }
}

/// <summary>
/// The VPS path: SOCKS5 CONNECT through the local sing-box probe port, then the same TLS round trip as the
/// direct path. (The CONNECT reply itself is not timed: sing-box's hysteria2 client answers it immediately.)
/// </summary>
public sealed class Socks5TcpProbe : IConnectProbe
{
    private readonly IPEndPoint _proxy;
    private readonly ProbeCredentials _auth;

    public Socks5TcpProbe(IPEndPoint proxy, ProbeCredentials auth)
    {
        _proxy = proxy;
        _auth = auth;
    }

    public async Task<TimeSpan?> ConnectAsync(IPEndPoint target, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var socket = new Socket(_proxy.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(_proxy, cts.Token).ConfigureAwait(false);
            // Greeting: version 5, one method, "username/password" (RFC 1929).
            await socket.SendAsync(new byte[] { 5, 1, 2 }, cts.Token).ConfigureAwait(false);
            var reply = new byte[2];
            await ReceiveExactly(socket, reply, cts.Token).ConfigureAwait(false);
            if (reply[0] != 5 || reply[1] != 2) return null;

            await socket.SendAsync(BuildAuth(_auth), cts.Token).ConfigureAwait(false);
            await ReceiveExactly(socket, reply, cts.Token).ConfigureAwait(false);
            if (reply[1] != 0) return null;

            await socket.SendAsync(BuildConnect(target), cts.Token).ConfigureAwait(false);
            var head = new byte[4];
            await ReceiveExactly(socket, head, cts.Token).ConfigureAwait(false);
            if (head[0] != 5 || head[1] != 0) return null;
            // Rest of the reply: bound address (IPv4 or IPv6) and port.
            var rest = new byte[(head[3] == 4 ? 16 : 4) + 2];
            await ReceiveExactly(socket, rest, cts.Token).ConfigureAwait(false);
            return await TlsPing.RoundTripAsync(socket, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (SocketException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal static byte[] BuildAuth(ProbeCredentials auth)
    {
        var user = System.Text.Encoding.ASCII.GetBytes(auth.User);
        var pass = System.Text.Encoding.ASCII.GetBytes(auth.Password);
        if (user.Length is 0 or > 255 || pass.Length is 0 or > 255) throw new ArgumentException("Invalid SOCKS credentials.");
        var msg = new byte[3 + user.Length + pass.Length];
        msg[0] = 1;
        msg[1] = (byte)user.Length;
        user.CopyTo(msg, 2);
        msg[2 + user.Length] = (byte)pass.Length;
        pass.CopyTo(msg, 3 + user.Length);
        return msg;
    }

    internal static byte[] BuildConnect(IPEndPoint target)
    {
        var address = target.Address.GetAddressBytes();
        var request = new byte[4 + address.Length + 2];
        request[0] = 5; // version
        request[1] = 1; // CONNECT
        request[2] = 0;
        request[3] = (byte)(address.Length == 4 ? 1 : 4); // IPv4 / IPv6
        address.CopyTo(request, 4);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4 + address.Length), (ushort)target.Port);
        return request;
    }

    private static async Task ReceiveExactly(Socket socket, byte[] buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await socket.ReceiveAsync(buffer.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("Proxy closed the connection.");
            read += n;
        }
    }
}

public static class PathMeasurer
{
    /// <summary>
    /// Handshake samples to each endpoint in turn. The first attempt per endpoint is a warm-up and not counted:
    /// it includes one-time costs (tunnel setup, route caches) that the game would not see mid-session.
    /// </summary>
    public static async Task<PathStats> MeasureAsync(
        IConnectProbe probe, IReadOnlyList<IPEndPoint> endpoints, int samplesPerEndpoint, TimeSpan timeout, TimeSpan pause, CancellationToken ct) =>
        PathStats.From(await MeasureEachAsync(probe, endpoints, samplesPerEndpoint, timeout, pause, ct).ConfigureAwait(false));

    /// <summary>Samples per endpoint, in the order of <paramref name="endpoints"/>.</summary>
    public static async Task<IReadOnlyList<IReadOnlyList<TimeSpan?>>> MeasureEachAsync(
        IConnectProbe probe, IReadOnlyList<IPEndPoint> endpoints, int samplesPerEndpoint, TimeSpan timeout, TimeSpan pause, CancellationToken ct)
    {
        var result = new List<IReadOnlyList<TimeSpan?>>();
        foreach (var endpoint in endpoints)
        {
            await probe.ConnectAsync(endpoint, timeout, ct).ConfigureAwait(false);
            var samples = new List<TimeSpan?>();
            for (var i = 0; i < samplesPerEndpoint; i++)
            {
                ct.ThrowIfCancellationRequested();
                samples.Add(await probe.ConnectAsync(endpoint, timeout, ct).ConfigureAwait(false));
                if (pause > TimeSpan.Zero) await Task.Delay(pause, ct).ConfigureAwait(false);
            }
            result.Add(samples);
        }
        return result;
    }

    /// <summary>
    /// Which of a game's endpoints can be measured at all: one quick probe per endpoint and path, in parallel.
    /// Many endpoints do not speak TLS or drop unknown clients; they would only add the same fake loss to both
    /// paths. Keeps the given order (callers rank the endpoints first).
    /// </summary>
    public static async Task<IReadOnlyList<IPEndPoint>> SelectResponsiveAsync(
        IConnectProbe direct, IConnectProbe tunnel, IReadOnlyList<IPEndPoint> endpoints, TimeSpan timeout, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(4);
        async Task<bool> Answers(IPEndPoint endpoint)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (await direct.ConnectAsync(endpoint, timeout, ct).ConfigureAwait(false) is not null) return true;
                return await tunnel.ConnectAsync(endpoint, timeout, ct).ConfigureAwait(false) is not null;
            }
            finally
            {
                gate.Release();
            }
        }
        var answers = await Task.WhenAll(endpoints.Select(Answers)).ConfigureAwait(false);
        return endpoints.Where((_, i) => answers[i]).ToList();
    }

    /// <summary>
    /// What to measure among the answering endpoints: anycast CDNs only when nothing else answers, since they sit
    /// near the user rather than near the game's servers and would pull the result toward them.
    /// </summary>
    public static IReadOnlyList<IPEndPoint> PickForMeasurement(IReadOnlyList<IPEndPoint> responsive, int max)
    {
        var own = responsive.Where(e => !TlsPing.IsAnycastCdn(e.Address)).ToList();
        return (own.Count > 0 ? own : responsive).Take(max).ToList();
    }

    public sealed record Comparison(PathStats Direct, PathStats Tunnel, IReadOnlyList<int> Silent);

    /// <summary>
    /// An endpoint that answered on neither path during the measurement says nothing about the paths; it is
    /// left out unless nothing answered at all.
    /// </summary>
    public static Comparison Compare(IReadOnlyList<IReadOnlyList<TimeSpan?>> direct, IReadOnlyList<IReadOnlyList<TimeSpan?>> tunnel)
    {
        var silent = Enumerable.Range(0, direct.Count)
            .Where(i => direct[i].All(x => x is null) && tunnel[i].All(x => x is null))
            .ToList();
        if (silent.Count == direct.Count) return new(PathStats.From(direct), PathStats.From(tunnel), Array.Empty<int>());
        List<IReadOnlyList<TimeSpan?>> Keep(IReadOnlyList<IReadOnlyList<TimeSpan?>> groups) =>
            groups.Where((_, i) => !silent.Contains(i)).ToList();
        return new(PathStats.From(Keep(direct)), PathStats.From(Keep(tunnel)), silent);
    }
}

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ZapretHub.Core.Vpn;

/// <param name="MedianMs">Median TCP handshake time of successful samples; null when none succeeded.</param>
/// <param name="JitterMs">Mean difference between consecutive successful samples.</param>
/// <param name="LossPercent">Share of attempts that failed or timed out.</param>
public sealed record PathStats(double? MedianMs, double JitterMs, double LossPercent, int Samples)
{
    public static PathStats From(IReadOnlyList<TimeSpan?> samples)
    {
        if (samples.Count == 0) return new PathStats(null, 0, 100, 0);
        var ok = samples.Where(s => s is not null).Select(s => s!.Value.TotalMilliseconds).ToList();
        var loss = 100.0 * (samples.Count - ok.Count) / samples.Count;
        if (ok.Count == 0) return new PathStats(null, 0, loss, samples.Count);

        var sorted = ok.Order().ToList();
        var median = sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
        var jitter = ok.Count < 2 ? 0 : ok.Zip(ok.Skip(1), (a, b) => Math.Abs(b - a)).Average();
        return new PathStats(median, jitter, loss, samples.Count);
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

    private static readonly byte[] HrrRandom = Convert.FromHexString("CF21AD74E59A6111BE1D8C021E65B891C2A211167ABB8C5E079E09E2C8A8339C");

    public static async Task<TimeSpan?> RoundTripAsync(Socket socket, CancellationToken ct)
    {
        var random = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        await socket.SendAsync(BuildClientHello(random, keyShare: null, cookie: null), ct).ConfigureAwait(false);

        var first = await ReadRecordAsync(socket, ct).ConfigureAwait(false);
        if (first is null || !IsHelloRetryRequest(first, out var group, out var cookie)) return null;

        var keyShare = KeyShareFor(group);
        if (keyShare is null) return null;
        // Servers often send a change_cipher_spec right after the retry request. Anything still arriving from
        // the first exchange must be consumed now, or it would be taken for the answer to the second one.
        await DrainAsync(socket, ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();
        await socket.SendAsync(BuildClientHello(random, keyShare, cookie), ct).ConfigureAwait(false);
        var reply = new byte[1];
        var n = await socket.ReceiveAsync(reply, ct).ConfigureAwait(false);
        var elapsed = sw.Elapsed;
        // ServerHello (0x16), or an alert (0x15, e.g. about the random key): either way the server answered.
        return n == 1 && reply[0] is 0x16 or 0x15 ? elapsed : null;
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

    /// <summary>A minimal TLS 1.3 ClientHello (no SNI). Without <paramref name="keyShare"/> it asks for a retry;
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
        Ext(0x002b, new byte[] { 2, 0x03, 0x04 });                                // supported_versions: TLS 1.3 only
        Ext(0x000a, new byte[] { 0, 4, 0x00, 0x1d, 0x00, 0x17 });                 // supported_groups: x25519, secp256r1
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
        byte[] suites = { 0x13, 0x01, 0x13, 0x02, 0x13, 0x03 };
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
        IConnectProbe probe, IReadOnlyList<IPEndPoint> endpoints, int samplesPerEndpoint, TimeSpan timeout, TimeSpan pause, CancellationToken ct)
    {
        var samples = new List<TimeSpan?>();
        foreach (var endpoint in endpoints)
        {
            await probe.ConnectAsync(endpoint, timeout, ct).ConfigureAwait(false);
            for (var i = 0; i < samplesPerEndpoint; i++)
            {
                ct.ThrowIfCancellationRequested();
                samples.Add(await probe.ConnectAsync(endpoint, timeout, ct).ConfigureAwait(false));
                if (pause > TimeSpan.Zero) await Task.Delay(pause, ct).ConfigureAwait(false);
            }
        }
        return PathStats.From(samples);
    }
}

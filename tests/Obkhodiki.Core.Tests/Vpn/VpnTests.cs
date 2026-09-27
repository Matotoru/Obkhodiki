using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Obkhodiki.Core.Vpn;

namespace Obkhodiki.Core.Tests.Vpn;

public class Hysteria2LinkTests
{
    [Fact]
    public void Parse_FullLink()
    {
        var link = Hysteria2Link.Parse("hysteria2://p%40ss%3Aword@fr2.example.com:29615?obfs-password=ob%20f&security=tls&sni=fr2.example.com&alpn=h3&fp=chrome#Hysteria%20FR");

        Assert.Equal("fr2.example.com", link.Host);
        Assert.Equal(29615, link.Port);
        Assert.Equal("p@ss:word", link.Password);
        Assert.Equal("ob f", link.ObfsPassword);
        Assert.Equal("fr2.example.com", link.Sni);
        Assert.Equal(new[] { "h3" }, link.Alpn);
        Assert.False(link.Insecure);
        Assert.Equal("Hysteria FR", link.Name);
    }

    [Theory]
    [InlineData("hy2://pw@1.2.3.4:443", "1.2.3.4", 443)]
    [InlineData("hysteria2://pw@[2001:db8::1]:8443/?insecure=1", "2001:db8::1", 8443)]
    public void Parse_ShortSchemeIpv4AndIpv6(string text, string host, int port)
    {
        var link = Hysteria2Link.Parse(text);

        Assert.Equal(host, link.Host);
        Assert.Equal(port, link.Port);
    }

    [Fact]
    public void Parse_InsecureFlag()
    {
        Assert.True(Hysteria2Link.Parse("hy2://pw@a.example:1?insecure=1").Insecure);
    }

    [Theory]
    [InlineData("vless://pw@a.example:443")]
    [InlineData("hy2://a.example:443")]
    [InlineData("hy2://pw@a.example")]
    [InlineData("hy2://pw@a.example:0")]
    [InlineData("hy2://pw@a.example:70000")]
    [InlineData("hy2://pw@a.example:443,5000-6000")]
    [InlineData("hy2://pw@bad_host!:443")]
    [InlineData("hy2://pw@a.example:443?obfs=other&obfs-password=x")]
    [InlineData("hy2://pw@a.example:443?sni=evil%22%2C%22x")]
    [InlineData("hy2://pw@a.example:443?alpn=h3%22")]
    [InlineData("hy2://p%0Aw@a.example:443")]
    [InlineData("hy2://pw@[fe80::1%25eth0]:443")]
    [InlineData("hy2://pw@a.example:443?pinSHA256=AA")]
    public void Parse_InvalidOrUnsupported_Throws(string text)
    {
        Assert.Throws<FormatException>(() => Hysteria2Link.Parse(text));
    }

    [Fact]
    public void Parse_NameStrippedOfLineBreaksAndInvisibleCharsAndCapped()
    {
        var link = Hysteria2Link.Parse("hy2://pw@a.example:443#Na%0Ame%E2%80%AE" + new string('x', 100));

        Assert.DoesNotContain('\n', link.Name!);
        Assert.DoesNotContain('‮', link.Name!);
        Assert.Equal(64, link.Name!.Length);
        Assert.StartsWith("Name", link.Name);
    }

    [Fact]
    public void ToString_NeverShowsPasswords()
    {
        var link = Hysteria2Link.Parse("hy2://topsecret@a.example:443?obfs-password=obfsecret#Name");

        Assert.DoesNotContain("topsecret", link.ToString());
        Assert.DoesNotContain("obfsecret", link.ToString());
        Assert.Contains("a.example:443", link.ToString());
    }
}

public class SingBoxConfigTests
{
    private static readonly Hysteria2Link Server = Hysteria2Link.Parse("hy2://pw@vps.example:29615?obfs-password=ob&sni=vps.example&alpn=h3");
    private static readonly ProbeCredentials Auth = new("user1", "pass1");

    private static JsonElement Build(bool tun, string[]? processes = null, string[]? domains = null) =>
        JsonDocument.Parse(SingBoxConfig.Build(Server, new SingBoxOptions(tun, 20808, processes ?? Array.Empty<string>(),
            domains ?? Array.Empty<string>(), @"C:\logs\sb.log", Auth))).RootElement;

    [Fact]
    public void Build_OutboundCarriesServerSettings()
    {
        var proxy = Build(true).GetProperty("outbounds")[0];

        Assert.Equal("hysteria2", proxy.GetProperty("type").GetString());
        Assert.Equal("vps.example", proxy.GetProperty("server").GetString());
        Assert.Equal(29615, proxy.GetProperty("server_port").GetInt32());
        Assert.Equal("pw", proxy.GetProperty("password").GetString());
        Assert.Equal("salamander", proxy.GetProperty("obfs").GetProperty("type").GetString());
        Assert.Equal("vps.example", proxy.GetProperty("tls").GetProperty("server_name").GetString());
    }

    [Fact]
    public void Build_EverythingDirectByDefault()
    {
        var route = Build(true).GetProperty("route");

        Assert.Equal("direct", route.GetProperty("final").GetString());
        Assert.True(route.GetProperty("auto_detect_interface").GetBoolean());
    }

    [Fact]
    public void Build_WithoutTun_OnlyProbeInbound()
    {
        var inbounds = Build(false).GetProperty("inbounds");

        Assert.Equal(1, inbounds.GetArrayLength());
        Assert.Equal("mixed", inbounds[0].GetProperty("type").GetString());
    }

    [Fact]
    public void Build_ProbeInboundIsLocalOnlyWithCredentialsAndGoesThroughVps()
    {
        var root = Build(true);
        var probe = root.GetProperty("inbounds").EnumerateArray().Single(i => i.GetProperty("tag").GetString() == "probe-in");

        Assert.Equal("127.0.0.1", probe.GetProperty("listen").GetString());
        Assert.Equal("user1", probe.GetProperty("users")[0].GetProperty("username").GetString());
        // Probe rules come before everything else: the main user goes through the selected server.
        var probeRules = root.GetProperty("route").GetProperty("rules").EnumerateArray()
            .TakeWhile(r => r.TryGetProperty("inbound", out var i) && i[0].GetString() == "probe-in").ToList();
        Assert.Contains(probeRules, r => r.GetProperty("auth_user")[0].GetString() == "user1" && r.GetProperty("outbound").GetString() == "proxy");
    }

    // IPv4-only TUN would let selected programs leak straight out over IPv6.
    [Fact]
    public void Build_TunCoversIpv4AndIpv6()
    {
        var address = Build(true).GetProperty("inbounds")[0].GetProperty("address").EnumerateArray().Select(a => a.GetString()!).ToList();

        Assert.Contains(address, a => a.StartsWith("172."));
        Assert.Contains(address, a => a.Contains(':'));
    }

    [Fact]
    public void Build_SelectedSitesResolvedThroughVps()
    {
        var root = Build(true, domains: new[] { "chatgpt.com" });

        var dns = root.GetProperty("dns");
        var remote = dns.GetProperty("servers").EnumerateArray().Single(s => s.GetProperty("tag").GetString() == "remote");
        Assert.Equal("proxy", remote.GetProperty("detour").GetString());
        Assert.Equal("remote", dns.GetProperty("rules")[0].GetProperty("server").GetString());
        Assert.Equal("chatgpt.com", dns.GetProperty("rules")[0].GetProperty("domain_suffix")[0].GetString());
        Assert.Contains(root.GetProperty("route").GetProperty("rules").EnumerateArray(),
            r => r.TryGetProperty("action", out var a) && a.GetString() == "hijack-dns");
        // Windows' parallel DNS to the ISP must be blocked, or a poisoned answer could still win.
        Assert.True(root.GetProperty("inbounds")[0].GetProperty("strict_route").GetBoolean());
    }

    [Fact]
    public void Build_NoSites_DnsLeftAlone()
    {
        var root = Build(true, processes: new[] { "WarDogs.exe" });

        Assert.False(root.GetProperty("dns").TryGetProperty("rules", out _));
        Assert.False(root.GetProperty("inbounds")[0].GetProperty("strict_route").GetBoolean());
        Assert.DoesNotContain(root.GetProperty("route").GetProperty("rules").EnumerateArray(),
            r => r.TryGetProperty("action", out var a) && a.GetString() == "hijack-dns");
    }

    [Fact]
    public void Build_ProcessesAndDomainsRoutedToProxy()
    {
        var rules = Build(true, new[] { "WarDogs.exe", "wardogs.exe" }, new[] { "*.ChatGPT.com", "openai.com." }).GetProperty("route").GetProperty("rules");

        var byProcess = rules.EnumerateArray().Single(r => r.TryGetProperty("process_name", out _));
        Assert.Equal(1, byProcess.GetProperty("process_name").GetArrayLength());
        var byDomain = rules.EnumerateArray().Single(r => r.TryGetProperty("domain_suffix", out _));
        Assert.Equal(new[] { "chatgpt.com", "openai.com" }, byDomain.GetProperty("domain_suffix").EnumerateArray().Select(e => e.GetString()));
    }

    [Theory]
    [InlineData("Танки.exe")]
    [InlineData("War Dogs (x64).exe")]
    [InlineData("Tom Clancy's Rainbow Six & Co! [EU], v2.exe")]
    public void ProcessNames_UnicodeAndSpacesAccepted(string name)
    {
        Assert.True(SingBoxConfig.IsValidProcessName(name));
    }

    [Theory]
    [InlineData(@"..\evil.exe")]
    [InlineData("game")]
    [InlineData("a\".exe")]
    public void Build_InvalidProcess_Throws(string process)
    {
        Assert.Throws<ArgumentException>(() => Build(true, new[] { process }));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("a b.com")]
    [InlineData("evil\",\"x.com")]
    public void Build_InvalidDomain_Throws(string domain)
    {
        Assert.Throws<ArgumentException>(() => Build(true, domains: new[] { domain }));
    }
}

public class PathQualityTests
{
    private static TimeSpan? Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Stats_MedianJitterLoss()
    {
        var stats = PathStats.From(new[] { Ms(40), Ms(50), null, Ms(60), Ms(40) });

        Assert.Equal(45, stats.MedianMs);
        Assert.Equal((10 + 10 + 20) / 3.0, stats.JitterMs, 6);
        Assert.Equal(20, stats.LossPercent);
    }

    [Fact]
    public void Stats_AllFailed_NoMedianFullLoss()
    {
        var stats = PathStats.From(new TimeSpan?[] { null, null });

        Assert.Null(stats.MedianMs);
        Assert.Equal(100, stats.LossPercent);
    }

    private static PathStats S(double? median, double loss) => new(median, 0, loss, 10);

    [Theory]
    [InlineData(50, 0, 60, 0, PathChoice.Direct)]    // VPS detour slower: stay direct
    [InlineData(50, 0, 40, 0, PathChoice.Direct)]    // 10 ms better is within the margin
    [InlineData(80, 0, 60, 0, PathChoice.Vpn)]       // clearly lower ping
    [InlineData(50, 10, 70, 0, PathChoice.Vpn)]      // losses matter more than ping
    [InlineData(50, 1, 80, 0, PathChoice.Direct)]    // small loss difference ignored
    [InlineData(80, 0, 50, 10, PathChoice.Direct)]   // VPS lossy: direct
    public void Chooser_Rules(double dMed, double dLoss, double tMed, double tLoss, PathChoice expected)
    {
        Assert.Equal(expected, PathChooser.Choose(S(dMed, dLoss), S(tMed, tLoss)).Choice);
    }

    [Fact]
    public void Chooser_DirectUnreachableTunnelWorks_Vpn()
    {
        Assert.Equal(PathChoice.Vpn, PathChooser.Choose(S(null, 100), S(70, 0)).Choice);
    }

    [Fact]
    public void Chooser_TunnelDown_Direct()
    {
        Assert.Equal(PathChoice.Direct, PathChooser.Choose(S(null, 100), S(null, 100)).Choice);
    }

    private sealed class ScriptedProbe : IConnectProbe
    {
        private readonly Queue<TimeSpan?> _answers;
        public int Calls { get; private set; }
        public ScriptedProbe(params TimeSpan?[] answers) => _answers = new Queue<TimeSpan?>(answers);

        public Task<TimeSpan?> ConnectAsync(IPEndPoint target, TimeSpan timeout, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(_answers.Dequeue());
        }
    }

    [Fact]
    public async Task Measurer_DiscardsWarmUpPerEndpoint()
    {
        var probe = new ScriptedProbe(Ms(500), Ms(40), Ms(40), Ms(900), Ms(60), Ms(60));
        var endpoints = new[] { new IPEndPoint(IPAddress.Loopback, 1), new IPEndPoint(IPAddress.Loopback, 2) };

        var stats = await PathMeasurer.MeasureAsync(probe, endpoints, 2, TimeSpan.FromSeconds(1), TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(6, probe.Calls);
        Assert.Equal(4, stats.Samples);
        Assert.Equal(50, stats.MedianMs);
    }

    [Fact]
    public void Socks5_ConnectRequestFormat()
    {
        var request = Socks5TcpProbe.BuildConnect(new IPEndPoint(IPAddress.Parse("3.120.10.5"), 443));

        Assert.Equal(new byte[] { 5, 1, 0, 1, 3, 120, 10, 5, 1, 187 }, request);
    }

    // A HelloRetryRequest as a TLS 1.3 server sends it (asking for x25519), optionally with a cookie.
    internal static byte[] Hrr(byte[]? cookie = null)
    {
        var ext = new List<byte> { 0x00, 0x2b, 0x00, 0x02, 0x03, 0x04, 0x00, 0x33, 0x00, 0x02, 0x00, 0x1d };
        if (cookie is not null)
        {
            ext.AddRange(new[] { (byte)0x00, (byte)0x2c, (byte)(cookie.Length >> 8), (byte)cookie.Length });
            ext.AddRange(cookie);
        }
        var body = new List<byte> { 0x03, 0x03 };
        body.AddRange(Convert.FromHexString("CF21AD74E59A6111BE1D8C021E65B891C2A211167ABB8C5E079E09E2C8A8339C"));
        body.AddRange(new byte[] { 0x00, 0x13, 0x01, 0x00, (byte)(ext.Count >> 8), (byte)ext.Count });
        body.AddRange(ext);
        var hs = new List<byte> { 0x02, 0, (byte)(body.Count >> 8), (byte)body.Count };
        hs.AddRange(body);
        var rec = new List<byte> { 0x16, 0x03, 0x03, (byte)(hs.Count >> 8), (byte)hs.Count };
        rec.AddRange(hs);
        return rec.ToArray();
    }

    private static readonly byte[] ChangeCipherSpec = { 0x14, 0x03, 0x03, 0x00, 0x01, 0x01 };
    private static readonly byte[] Alert = { 0x15, 0x03, 0x03, 0x00, 0x02, 0x02, 0x28 };

    private static async Task<byte[]> ReadRecord(Stream s)
    {
        var head = new byte[5];
        await s.ReadExactlyAsync(head);
        var body = new byte[(head[3] << 8) | head[4]];
        await s.ReadExactlyAsync(body);
        return head.Concat(body).ToArray();
    }

    // Plays a TLS 1.3 server: answers the first ClientHello with HRR + change_cipher_spec at once (like real
    // servers), then after a delay answers the second ClientHello with the given bytes (or closes).
    internal static async Task PlayTlsServer(Stream s, int secondDelayMs, byte[] secondAnswer, byte[]? firstAnswer = null)
    {
        await ReadRecord(s);
        await s.WriteAsync(firstAnswer ?? Hrr().Concat(ChangeCipherSpec).ToArray());
        if (firstAnswer is not null) return;
        await ReadRecord(s);
        await Task.Delay(secondDelayMs);
        if (secondAnswer.Length > 0) await s.WriteAsync(secondAnswer);
    }

    // A tiny SOCKS5 server: checks the RFC 1929 credentials, answers CONNECT with the given reply code at once
    // (like sing-box's hysteria2 client), then plays the remote TLS server.
    private static async Task<(TcpListener Listener, Task Server)> FakeSocks(
        string user, string pass, byte replyCode, byte[]? serverAnswer = null, int serverDelayMs = 40, byte[]? firstAnswer = null)
    {
        serverAnswer ??= Alert;
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var s = client.GetStream();
            var buf = new byte[512];
            await s.ReadExactlyAsync(buf, 0, 3);
            await s.WriteAsync(new byte[] { 5, 2 });
            await s.ReadExactlyAsync(buf, 0, 2);
            var u = new byte[buf[1]];
            await s.ReadExactlyAsync(u);
            await s.ReadExactlyAsync(buf, 0, 1);
            var p = new byte[buf[0]];
            await s.ReadExactlyAsync(p);
            var ok = System.Text.Encoding.ASCII.GetString(u) == user && System.Text.Encoding.ASCII.GetString(p) == pass;
            await s.WriteAsync(new byte[] { 1, (byte)(ok ? 0 : 1) });
            if (!ok) return;
            await s.ReadExactlyAsync(buf, 0, 10);
            await s.WriteAsync(new byte[] { 5, replyCode, 0, 1, 0, 0, 0, 0, 0, 0 });
            if (replyCode != 0) return;
            try
            {
                await PlayTlsServer(s, serverDelayMs, serverAnswer, firstAnswer);
            }
            catch (IOException)
            {
                // The probe may give up and close first.
            }
        });
        return (listener, server);
    }

    // Only the second exchange is timed; the change_cipher_spec sent right after the HRR must not count as the answer.
    [Fact]
    public async Task Socks5_TimesSecondExchangeNotBufferedLeftovers()
    {
        var (listener, server) = await FakeSocks("u", "p", 0, serverDelayMs: 80);
        using var _ = listener;
        var probe = new Socks5TcpProbe((IPEndPoint)listener.LocalEndpoint, new ProbeCredentials("u", "p"));

        var rtt = await probe.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 443), TimeSpan.FromSeconds(5), CancellationToken.None);
        await server;

        Assert.NotNull(rtt);
        Assert.True(rtt!.Value >= TimeSpan.FromMilliseconds(70), rtt.ToString());
    }

    // What sing-box's hysteria2 client does for an unreachable target: "connected", then the stream just closes.
    [Fact]
    public async Task Socks5_ConnectAcceptedButServerNeverAnswers_Null()
    {
        var (listener, server) = await FakeSocks("u", "p", 0, firstAnswer: Array.Empty<byte>());
        using var _ = listener;
        var probe = new Socks5TcpProbe((IPEndPoint)listener.LocalEndpoint, new ProbeCredentials("u", "p"));

        Assert.Null(await probe.ConnectAsync(new IPEndPoint(IPAddress.Parse("192.0.2.1"), 443), TimeSpan.FromSeconds(5), CancellationToken.None));
        await server;
    }

    [Fact]
    public async Task Socks5_ServerWithoutRetry_Null()
    {
        // A plain ServerHello (TLS 1.2-style) instead of a retry request cannot give a clean second round trip.
        var serverHello = Hrr();
        serverHello[5 + 4 + 2] ^= 0xFF; // different random: not an HRR
        var (listener, server) = await FakeSocks("u", "p", 0, firstAnswer: serverHello);
        using var _ = listener;
        var probe = new Socks5TcpProbe((IPEndPoint)listener.LocalEndpoint, new ProbeCredentials("u", "p"));

        Assert.Null(await probe.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 443), TimeSpan.FromSeconds(5), CancellationToken.None));
        await server;
    }

    [Fact]
    public async Task Socks5_NonTlsSecondAnswer_Null()
    {
        var (listener, server) = await FakeSocks("u", "p", 0, serverAnswer: "HTTP/1.1 400"u8.ToArray());
        using var _ = listener;
        var probe = new Socks5TcpProbe((IPEndPoint)listener.LocalEndpoint, new ProbeCredentials("u", "p"));

        Assert.Null(await probe.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 443), TimeSpan.FromSeconds(5), CancellationToken.None));
        await server;
    }

    [Fact]
    public void ClientHello_FirstAsksForRetrySecondCarriesKeyAndCookie()
    {
        var random = new byte[32];
        var first = TlsPing.BuildClientHello(random, keyShare: null, cookie: null);
        var second = TlsPing.BuildClientHello(random, new byte[32], new byte[] { 0, 3, 1, 2, 3 });

        foreach (var hello in new[] { first, second })
        {
            Assert.Equal(0x16, hello[0]);
            Assert.Equal(hello.Length - 5, (hello[3] << 8) | hello[4]);
            Assert.Equal(0x01, hello[5]);
            Assert.Equal(hello.Length - 9, (hello[7] << 8) | hello[8]);
        }
        Assert.Contains("003300020000", Convert.ToHexString(first));        // key_share with an empty list
        Assert.Contains("003300260024001D0020", Convert.ToHexString(second)); // one x25519 share of 32 bytes
        Assert.Contains("002C00050003010203", Convert.ToHexString(second));  // cookie echoed
    }

    [Fact]
    public void HelloRetryRequest_ParsedWithGroupAndCookie()
    {
        Assert.True(TlsPing.IsHelloRetryRequest(Hrr(new byte[] { 0, 2, 9, 9 }), out var group, out var cookie));
        Assert.Equal(0x001d, group);
        Assert.Equal(new byte[] { 0, 2, 9, 9 }, cookie);
    }

    [Fact]
    public void HelloRetryRequest_TruncatedOrOrdinaryServerHello_Rejected()
    {
        var hrr = Hrr();
        Assert.False(TlsPing.IsHelloRetryRequest(hrr[..20], out _, out _));
        hrr[5 + 4 + 2] ^= 0xFF;
        Assert.False(TlsPing.IsHelloRetryRequest(hrr, out _, out _));
    }

    [Theory]
    [InlineData(443, true)]
    [InlineData(8443, true)]
    [InlineData(7777, false)]
    public void TlsPorts(int port, bool expected)
    {
        Assert.Equal(expected, TlsPing.IsTlsPort(port));
    }

    [Fact]
    public async Task Direct_TimesSecondExchange()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            await PlayTlsServer(c.GetStream(), 60, Alert);
        });

        var rtt = await new DirectTcpProbe().ConnectAsync((IPEndPoint)listener.LocalEndpoint, TimeSpan.FromSeconds(5), CancellationToken.None);
        await server;
        listener.Stop();

        Assert.True(rtt >= TimeSpan.FromMilliseconds(50), rtt?.ToString());
    }

    // A TLS 1.2-only server (seen on game backends): ServerHello, Certificate and ServerHelloDone split over
    // records, then an alert (or a close) after a delay in answer to the out-of-order message.
    private static async Task PlayTls12Server(Stream s, int secondDelayMs, bool closeInstead)
    {
        await ReadRecord(s);
        var serverHello = Hrr();
        serverHello[5 + 4 + 2] ^= 0xFF; // ordinary random: not a retry request
        var certificate = new byte[] { 0x16, 0x03, 0x03, 0x00, 0x08, 0x0b, 0x00, 0x00, 0x04, 1, 2, 3, 4 };
        var done = new byte[] { 0x16, 0x03, 0x03, 0x00, 0x04, 0x0e, 0x00, 0x00, 0x00 };
        await s.WriteAsync(serverHello.Concat(certificate).ToArray());
        await Task.Delay(20);
        await s.WriteAsync(done);
        await ReadRecord(s);
        await Task.Delay(secondDelayMs);
        if (!closeInstead) await s.WriteAsync(Alert);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_Tls12Server_TimesSecondExchange(bool closeInstead)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var server = Task.Run(async () =>
        {
            using var c = await listener.AcceptTcpClientAsync();
            await PlayTls12Server(c.GetStream(), 80, closeInstead);
        });

        var rtt = await new DirectTcpProbe().ConnectAsync((IPEndPoint)listener.LocalEndpoint, TimeSpan.FromSeconds(5), CancellationToken.None);
        await server;
        listener.Stop();

        Assert.NotNull(rtt);
        Assert.True(rtt >= TimeSpan.FromMilliseconds(70), rtt?.ToString());
    }

    [Fact]
    public void ClientHello_OffersTls13AndTls12()
    {
        var hex = Convert.ToHexString(TlsPing.BuildClientHello(new byte[32], null, null));
        Assert.Contains("002B00050403040303", hex); // supported_versions: 1.3, 1.2
        Assert.Contains("C02F", hex);               // an ECDHE suite for TLS 1.2
    }

    [Fact]
    public void Stats_JitterOnlyWithinEachEndpoint()
    {
        // Two steady endpoints at different distances: no jitter, although the samples differ.
        var stats = PathStats.From(new IReadOnlyList<TimeSpan?>[] { new[] { Ms(40), Ms(40), Ms(40) }, new[] { Ms(55), Ms(55) } });

        Assert.Equal(0, stats.JitterMs);
        Assert.Equal(0, stats.LossPercent);
    }

    [Fact]
    public void Compare_EndpointSilentOnBothPaths_LeftOut()
    {
        var direct = new IReadOnlyList<TimeSpan?>[] { new[] { Ms(40), Ms(42) }, new TimeSpan?[] { null, null } };
        var tunnel = new IReadOnlyList<TimeSpan?>[] { new[] { Ms(50), null }, new TimeSpan?[] { null, null } };

        var result = PathMeasurer.Compare(direct, tunnel);

        Assert.Equal(new[] { 1 }, result.Silent);
        Assert.Equal(0, result.Direct.LossPercent);
        Assert.Equal(50, result.Tunnel.LossPercent);
    }

    [Fact]
    public void Compare_SilentOnlyOnOnePath_IsRealLoss()
    {
        var direct = new IReadOnlyList<TimeSpan?>[] { new TimeSpan?[] { null, null } };
        var tunnel = new IReadOnlyList<TimeSpan?>[] { new[] { Ms(50), Ms(51) } };

        var result = PathMeasurer.Compare(direct, tunnel);

        Assert.Empty(result.Silent);
        Assert.Equal(100, result.Direct.LossPercent);
    }

    [Fact]
    public async Task SelectResponsive_KeepsOrderDropsSilent()
    {
        var a = new IPEndPoint(IPAddress.Parse("3.1.1.1"), 443);
        var b = new IPEndPoint(IPAddress.Parse("3.1.1.2"), 443);
        var c = new IPEndPoint(IPAddress.Parse("3.1.1.3"), 443);
        var direct = new MapProbe(new() { [a] = Ms(40) });
        var tunnel = new MapProbe(new() { [a] = Ms(50), [c] = Ms(60) });

        var result = await PathMeasurer.SelectResponsiveAsync(direct, tunnel, new[] { a, b, c }, TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(new[] { a, c }, result);
    }

    private sealed class MapProbe : IConnectProbe
    {
        private readonly Dictionary<IPEndPoint, TimeSpan?> _answers;
        public MapProbe(Dictionary<IPEndPoint, TimeSpan?> answers) => _answers = answers;
        public Task<TimeSpan?> ConnectAsync(IPEndPoint target, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(_answers.TryGetValue(target, out var v) ? v : null);
    }

    [Theory]
    [InlineData("104.18.124.108", true)]   // Cloudflare
    [InlineData("199.232.173.242", true)]  // Fastly
    [InlineData("1.1.1.1", true)]
    [InlineData("52.215.132.236", false)]  // AWS
    [InlineData("8.8.8.8", false)]
    public void AnycastCdn_Detected(string ip, bool expected)
    {
        Assert.Equal(expected, TlsPing.IsAnycastCdn(IPAddress.Parse(ip)));
    }

    [Fact]
    public void RankForProbe_NearGameServersFirstCdnLast()
    {
        var cdn = IPEndPoint.Parse("104.18.124.108:443");
        var far = IPEndPoint.Parse("20.1.1.1:443");
        var sameNet = IPEndPoint.Parse("52.215.132.236:8080");
        var sameHost = IPEndPoint.Parse("52.215.9.9:443");
        var udpServers = new[] { IPAddress.Parse("52.215.132.10"), IPAddress.Parse("52.215.9.9") };

        var ranked = TlsPing.RankForProbe(new[] { cdn, far, sameNet, sameHost }, udpServers);

        Assert.Equal(new[] { sameHost, sameNet, far, cdn }, ranked);
    }

    [Fact]
    public void RankForProbe_WithoutGameServers_TlsPortsFirstStable()
    {
        var a = IPEndPoint.Parse("20.1.1.1:7000");
        var b = IPEndPoint.Parse("20.1.1.2:443");
        var c = IPEndPoint.Parse("20.1.1.3:8443");

        Assert.Equal(new[] { b, c, a }, TlsPing.RankForProbe(new[] { a, b, c }));
    }

    [Fact]
    public async Task Socks5_TargetUnreachable_Null()
    {
        var (listener, server) = await FakeSocks("u", "p", 5);
        using var _ = listener;
        var probe = new Socks5TcpProbe((IPEndPoint)listener.LocalEndpoint, new ProbeCredentials("u", "p"));

        Assert.Null(await probe.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 443), TimeSpan.FromSeconds(5), CancellationToken.None));
        await server;
    }

    [Fact]
    public async Task Socks5_WrongCredentials_Null()
    {
        var (listener, server) = await FakeSocks("u", "p", 0);
        using var _ = listener;
        var probe = new Socks5TcpProbe((IPEndPoint)listener.LocalEndpoint, new ProbeCredentials("u", "wrong"));

        Assert.Null(await probe.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 443), TimeSpan.FromSeconds(5), CancellationToken.None));
        await server;
    }

    [Fact]
    public async Task Socks5_NothingListening_Null()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var probe = new Socks5TcpProbe(new IPEndPoint(IPAddress.Loopback, port), new ProbeCredentials("u", "p"));

        Assert.Null(await probe.ConnectAsync(new IPEndPoint(IPAddress.Parse("1.2.3.4"), 443), TimeSpan.FromSeconds(2), CancellationToken.None));
    }
}

public class SingBoxUnpackTests
{
    private static MemoryStream Zip(params (string Name, byte[] Content)[] entries)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(content);
            }
        }
        ms.Position = 0;
        return ms;
    }

    private static readonly byte[] Exe = new byte[] { (byte)'M', (byte)'Z' }.Concat(new byte[100]).ToArray();

    [Fact]
    public void Unpack_TakesOnlyKnownFilesFromReleaseFolder()
    {
        var files = SingBoxUpdater.UnpackZip(Zip(
            ("sing-box-1.14.2-windows-amd64/sing-box.exe", Exe),
            ("sing-box-1.14.2-windows-amd64/libcronet.dll", new byte[] { 1 }),
            ("sing-box-1.14.2-windows-amd64/LICENSE", new byte[] { 2 }),
            ("sing-box-1.14.2-windows-amd64/evil.dll", new byte[] { 3 })));

        Assert.Equal(new[] { "libcronet.dll", "sing-box.exe" }, files.Keys.Order());
    }

    [Fact]
    public void Unpack_NoExe_Rejected()
    {
        Assert.Throws<InvalidDataException>(() => SingBoxUpdater.UnpackZip(Zip(("x/libcronet.dll", new byte[] { 1 }))));
    }

    [Fact]
    public void Unpack_DuplicateExe_Rejected()
    {
        Assert.Throws<InvalidDataException>(() => SingBoxUpdater.UnpackZip(Zip(("a/sing-box.exe", Exe), ("b/sing-box.exe", Exe))));
    }
}

public class PathPickTests
{
    [Fact]
    public void Pick_SkipsCdnWhenOwnServersAnswer()
    {
        var cdn = IPEndPoint.Parse("104.18.124.108:443");
        var game = IPEndPoint.Parse("52.215.132.236:443");

        Assert.Equal(new[] { game }, PathMeasurer.PickForMeasurement(new[] { cdn, game }, 3));
        Assert.Equal(new[] { cdn }, PathMeasurer.PickForMeasurement(new[] { cdn }, 3));
    }
}

using System.Text;
using System.Text.Json.Nodes;
using Obkhodiki.Core.Vpn;

namespace Obkhodiki.Core.Tests.Vpn;

public class ProxyLinksTests
{
    private const string Uuid = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
    private const string Pbk = "Hh7cCHBW1W7zuEJqLUcmbJPbsVxBuwAQ3mvD6rPOxWM";

    [Fact]
    public void Vless_RealityVision_As3xUiExportsIt()
    {
        var link = (VlessLink)ProxyLinks.Parse(
            $"vless://{Uuid}@nl.example.com:443?type=tcp&security=reality&pbk={Pbk}&fp=chrome&sni=www.microsoft.com&sid=6ba85179e30d4fc2&spx=%2F&flow=xtls-rprx-vision#NL%20Reality");

        Assert.Equal("nl.example.com", link.Host);
        Assert.Equal(443, link.Port);
        Assert.Equal(Guid.Parse(Uuid), link.Uuid);
        Assert.Equal("xtls-rprx-vision", link.Flow);
        Assert.Equal(LinkSecurity.Reality, link.Tls.Security);
        Assert.Equal("NL Reality", link.Name);

        var o = link.ToOutbound("s-1");
        Assert.Equal("vless", (string?)o["type"]);
        Assert.Equal(Uuid, (string?)o["uuid"]);
        Assert.Equal("xtls-rprx-vision", (string?)o["flow"]);
        var tls = o["tls"]!;
        Assert.Equal("www.microsoft.com", (string?)tls["server_name"]);
        Assert.True((bool)tls["reality"]!["enabled"]!);
        Assert.Equal(Pbk, (string?)tls["reality"]!["public_key"]);
        Assert.Equal("6ba85179e30d4fc2", (string?)tls["reality"]!["short_id"]);
        Assert.Equal("chrome", (string?)tls["utls"]!["fingerprint"]);
        Assert.Null(o["transport"]);
    }

    [Fact]
    public void Vless_RealityPaddedKey_PaddingStripped()
    {
        var link = (VlessLink)ProxyLinks.Parse($"vless://{Uuid}@1.2.3.4:443?security=reality&pbk={Pbk}%3D&sid=ab");
        Assert.Equal(Pbk, link.Tls.RealityPublicKey);
    }

    [Fact]
    public void Vless_RealityAllowInsecure_NotReportedInsecure() =>
        Assert.False(ProxyLinks.Parse($"vless://{Uuid}@1.2.3.4:443?security=reality&pbk={Pbk}&allowInsecure=1").Insecure);

    [Fact]
    public void Vless_RealityWithoutFingerprint_DefaultsToChrome()
    {
        var link = (VlessLink)ProxyLinks.Parse($"vless://{Uuid}@1.2.3.4:443?security=reality&pbk={Pbk}&sni=a.example.com");
        Assert.Equal("chrome", link.Tls.Fingerprint);
        Assert.Equal("", link.Tls.RealityShortId);
    }

    [Fact]
    public void Vless_WebSocketTls_EarlyDataMovedOutOfPath()
    {
        var link = (VlessLink)ProxyLinks.Parse($"vless://{Uuid}@cdn.example.com:443?type=ws&security=tls&path=%2Fws%3Fed%3D2048&host=cdn.example.com&sni=cdn.example.com#ws");

        var t = link.ToOutbound("x")["transport"]!;
        Assert.Equal("ws", (string?)t["type"]);
        Assert.Equal("/ws", (string?)t["path"]);
        Assert.Equal("cdn.example.com", (string?)t["headers"]!["Host"]);
        Assert.Equal(2048, (int)t["max_early_data"]!);
        Assert.Equal("Sec-WebSocket-Protocol", (string?)t["early_data_header_name"]);
    }

    [Fact]
    public void Vless_WebSocketPathKeepsOtherQueryParameters()
    {
        var link = (VlessLink)ProxyLinks.Parse($"vless://{Uuid}@a.example.com:443?type=ws&security=tls&path=%2Fws%3Fa%3D1%26ed%3D512");
        Assert.Equal("/ws?a=1", link.Transport.Path);
        Assert.Equal(512, link.Transport.MaxEarlyData);
    }

    [Fact]
    public void Vless_GrpcAndHttpUpgrade()
    {
        var grpc = (VlessLink)ProxyLinks.Parse($"vless://{Uuid}@a.example.com:443?type=grpc&serviceName=gun&security=tls");
        Assert.Equal("gun", (string?)grpc.ToOutbound("x")["transport"]!["service_name"]);

        var hu = (VlessLink)ProxyLinks.Parse($"vless://{Uuid}@a.example.com:80?type=httpupgrade&path=%2Fup&host=h.example.com");
        var t = hu.ToOutbound("x")["transport"]!;
        Assert.Equal("httpupgrade", (string?)t["type"]);
        Assert.Equal("h.example.com", (string?)t["host"]);
        Assert.Null(hu.ToOutbound("x")["tls"]);
    }

    [Fact]
    public void Vless_TlsInsecureFlag()
    {
        var link = ProxyLinks.Parse($"vless://{Uuid}@a.example.com:443?security=tls&allowInsecure=1");
        Assert.True(link.Insecure);
        Assert.True((bool)link.ToOutbound("x")["tls"]!["insecure"]!);
    }

    [Theory]
    [InlineData("vless://not-a-uuid@a.example.com:443")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?type=xhttp&security=tls")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?type=kcp")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?security=reality&sni=a.example.com")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?security=reality&pbk=short")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?security=reality&pbk=" + Pbk + "&sid=zz")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?security=reality&pbk=" + Pbk + "&sid=abc")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?security=reality&pbk=" + Pbk + "&sid=0011223344556677aa")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?flow=xtls-rprx-vision")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?flow=xtls-rprx-direct&security=tls")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?encryption=aes")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?security=tls&fp=evil")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?type=ws&path=ws")]
    [InlineData("vless://" + Uuid + "@a.example.com:443?type=tcp&headerType=http")]
    [InlineData("vless://" + Uuid + "@bad_host:443")]
    [InlineData("hy2://pw@a.example.com:443?obfs=salamander")]
    [InlineData("vmess://eyJ2IjoiMiJ9")]
    [InlineData("socks5://a.example.com:1080")]
    public void Unsupported_Throws(string text) => Assert.Throws<FormatException>(() => ProxyLinks.Parse(text));

    [Fact]
    public void Trojan_DefaultsToTls()
    {
        var link = (TrojanLink)ProxyLinks.Parse("trojan://p%40ss@t.example.com:443?sni=t.example.com&type=ws&path=%2Ftr#Trojan");

        Assert.Equal("p@ss", link.Password);
        var o = link.ToOutbound("x");
        Assert.Equal("trojan", (string?)o["type"]);
        Assert.True((bool)o["tls"]!["enabled"]!);
        Assert.Equal("/tr", (string?)o["transport"]!["path"]);
    }

    [Fact]
    public void Trojan_SecurityNone_Rejected() =>
        Assert.Throws<FormatException>(() => ProxyLinks.Parse("trojan://pw@t.example.com:443?security=none"));

    [Theory]
    [InlineData("ss://YWVzLTI1Ni1nY206c2VjcmV0@s.example.com:8388#SS")] // SIP002, base64 userinfo
    [InlineData("ss://YWVzLTI1Ni1nY206c2VjcmV0QHMuZXhhbXBsZS5jb206ODM4OA==#SS")] // legacy, all base64
    [InlineData("ss://aes-256-gcm:secret@s.example.com:8388#SS")] // plain userinfo
    public void Shadowsocks_AllForms(string text)
    {
        var link = (ShadowsocksLink)ProxyLinks.Parse(text);
        Assert.Equal("s.example.com", link.Host);
        Assert.Equal(8388, link.Port);
        Assert.Equal("aes-256-gcm", link.Method);
        Assert.Equal("secret", link.Password);
        Assert.Equal("SS", link.Name);
    }

    [Fact]
    public void Shadowsocks_2022KeyWithPercentEncoding()
    {
        var link = (ShadowsocksLink)ProxyLinks.Parse("ss://2022-blake3-aes-256-gcm:YctPZ6U7xPPcU%2Bgp3u%2B0tx%2FtRizJN9K8y%2BuKlW2qjlI%3D@s.example.com:443");
        Assert.Equal("YctPZ6U7xPPcU+gp3u+0tx/tRizJN9K8y+uKlW2qjlI=", link.Password);
    }

    [Theory]
    [InlineData("ss://2022-blake3-aes-128-gcm:YctPZ6U7xPPcU%2Bgp3u%2B0tx%2FtRizJN9K8y%2BuKlW2qjlI%3D@s.example.com:443")] // 32-byte key for a 16-byte cipher
    [InlineData("ss://2022-blake3-aes-256-gcm:not-base64!@s.example.com:443")]
    [InlineData("ss://2022-blake3-chacha20-poly1305:YctPZ6U7xPPcU%2Bgp3u%2B0tx%2FtRizJN9K8y%2BuKlW2qjlI%3D%3AYctPZ6U7xPPcU%2Bgp3u%2B0tx%2FtRizJN9K8y%2BuKlW2qjlI%3D@s.example.com:443")]
    public void Shadowsocks_2022WrongKey_Rejected(string text) => Assert.Throws<FormatException>(() => ProxyLinks.Parse(text));

    [Fact]
    public void Shadowsocks_2022MultiUserKeys() =>
        Assert.NotNull(ProxyLinks.Parse("ss://2022-blake3-aes-128-gcm:MTIzNDU2Nzg5MDEyMzQ1Ng%3D%3D%3AYWJjZGVmZ2hpamtsbW5vcA%3D%3D@s.example.com:443"));

    [Theory]
    [InlineData("ss://cmM0LW1kNTpzZWNyZXQ@s.example.com:8388")] // rc4-md5
    [InlineData("ss://YWVzLTI1Ni1nY206c2VjcmV0@s.example.com:8388?plugin=obfs-local")]
    public void Shadowsocks_Unsupported(string text) => Assert.Throws<FormatException>(() => ProxyLinks.Parse(text));

    [Fact]
    public void Describe_NeverShowsSecrets()
    {
        foreach (var text in new[]
                 {
                     $"vless://{Uuid}@a.example.com:443?security=tls#Name",
                     "trojan://topsecret@a.example.com:443#Name",
                     "ss://aes-256-gcm:topsecret@a.example.com:8388#Name",
                     "hy2://topsecret@a.example.com:443#Name",
                 })
        {
            var s = ProxyLinks.Parse(text).ToString()!;
            Assert.DoesNotContain("topsecret", s);
            Assert.DoesNotContain(Uuid, s);
            Assert.Contains("a.example.com:", s);
        }
    }
}

public class SubscriptionTests
{
    private const string Uuid = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";

    private static string Links => string.Join("\n",
        $"vless://{Uuid}@a.example.com:443?security=tls#A",
        "hy2://pw@b.example.com:443#B",
        "vmess://eyJ2IjoiMiJ9",
        $"vless://{Uuid}@a.example.com:443?security=tls#A",
        "",
        "# comment");

    [Fact]
    public void Parse_Base64Body_SkipsUnsupportedAndDuplicates()
    {
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(Links));
        var content = SubscriptionContent.Parse(body);

        Assert.Equal(2, content.Servers.Count);
        Assert.Equal(new[] { "A", "B" }, content.Servers.Select(s => s.Server.Name));
        var skipped = Assert.Single(content.Skipped);
        Assert.StartsWith("vmess:", skipped);
        Assert.DoesNotContain("eyJ2", skipped);
    }

    [Fact]
    public void Parse_PlainAndUrlSafeUnpaddedBase64()
    {
        Assert.Equal(2, SubscriptionContent.Parse(Links).Servers.Count);
        var urlSafe = Convert.ToBase64String(Encoding.UTF8.GetBytes(Links)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(2, SubscriptionContent.Parse(urlSafe).Servers.Count);
        var wrapped = string.Join("\r\n", Convert.ToBase64String(Encoding.UTF8.GetBytes(Links)).Chunk(40).Select(c => new string(c)));
        Assert.Equal(2, SubscriptionContent.Parse(wrapped).Servers.Count);
    }

    [Fact]
    public void Tags_IgnoreTheName()
    {
        Assert.Equal(VpnServerEntry.TagFor("hy2://pw@b.example.com:443#B 10GB left"), VpnServerEntry.TagFor("hy2://pw@b.example.com:443#B 9GB left"));
    }

    [Fact]
    public void Tags_StablePerLinkAndDistinct()
    {
        var a = VpnServerEntry.TagFor("hy2://pw@b.example.com:443#B");
        Assert.Equal(a, VpnServerEntry.TagFor(" hy2://pw@b.example.com:443#B "));
        Assert.NotEqual(a, VpnServerEntry.TagFor("hy2://pw2@b.example.com:443#B"));
        Assert.Matches("^s-[0-9a-f]{10}$", a);
    }

    [Fact]
    public void Usage_ParsesHeader()
    {
        var u = SubscriptionUsage.Parse("upload=1000; download=2000; total=10737418240; expire=1767225600")!;
        Assert.Equal(3000, u.Used);
        Assert.Equal(10737418240, u.Total);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), u.Expire);

        Assert.Null(SubscriptionUsage.Parse(null)!);
        Assert.Null(SubscriptionUsage.Parse("upload=1; expire=0")!.Expire);
    }

    [Theory]
    [InlineData("ftp://a.example.com/sub")]
    [InlineData("https://user:pw@a.example.com/sub")]
    [InlineData("not a url")]
    public void Url_Rejected(string url) => Assert.Throws<FormatException>(() => SubscriptionContent.ValidateUrl(url));

    [Fact]
    public void TitleAndInterval()
    {
        Assert.Equal("Мой VPN", SubscriptionContent.ParseTitle("base64:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Мой VPN"))));
        Assert.Equal("Plain", SubscriptionContent.ParseTitle("Plain"));
        Assert.Equal(TimeSpan.FromHours(12), SubscriptionContent.ParseUpdateInterval(null));
        Assert.Equal(TimeSpan.FromHours(1), SubscriptionContent.ParseUpdateInterval("0"));
        Assert.Equal(TimeSpan.FromHours(24), SubscriptionContent.ParseUpdateInterval("24"));
    }
}

public class MultiServerConfigTests
{
    private static readonly ProbeCredentials Auth = new("u", "p");

    private static readonly VpnServerEntry[] Servers =
    {
        VpnServerEntry.FromLink("hy2://pw@a.example.com:443#A"),
        VpnServerEntry.FromLink("trojan://pw@b.example.com:443#B"),
    };

    private static JsonNode Build(SingBoxOptions o, string? active = null) =>
        JsonNode.Parse(SingBoxConfig.Build(Servers, active, o))!;

    private static SingBoxOptions Options(bool full = false) =>
        new(true, 20000, Array.Empty<string>(), Array.Empty<string>(), @"C:\x\sb.log", Auth) { FullTunnel = full };

    private static readonly LocalRuleSet RuSite = new("geosite-category-ru", @"C:\rs\geosite-category-ru.srs");
    private static readonly LocalRuleSet RuIp = new("geoip-ru", @"C:\rs\geoip-ru.srs");
    private static readonly LocalRuleSet Blocked = new("geosite-ru-blocked", @"C:\rs\geosite-ru-blocked.srs");

    [Fact]
    public void Selector_WrapsAllServers_DefaultIsActive()
    {
        var root = Build(Options(), Servers[1].Tag);
        var outbounds = root["outbounds"]!.AsArray();
        var selector = outbounds.Single(o => (string?)o!["type"] == "selector")!;

        Assert.Equal(SingBoxConfig.ProxyTag, (string?)selector["tag"]);
        Assert.Equal(Servers.Select(s => s.Tag), selector["outbounds"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(Servers[1].Tag, (string?)selector["default"]);
        Assert.Contains(outbounds, o => (string?)o!["tag"] == Servers[0].Tag && (string?)o["type"] == "hysteria2");
    }

    [Fact]
    public void Selector_UnknownActive_FallsBackToFirst() =>
        Assert.Equal(Servers[0].Tag, (string?)Build(Options(), "s-0000000000")["outbounds"]!.AsArray().Single(o => (string?)o!["type"] == "selector")!["default"]);

    [Fact]
    public void ClashApi_LoopbackWithSecret()
    {
        var api = Build(Options() with { ClashApi = new ClashApiOptions(20001, new string('a', 32)) })["experimental"]!["clash_api"]!;
        Assert.Equal("127.0.0.1:20001", (string?)api["external_controller"]);
        Assert.Equal(new string('a', 32), (string?)api["secret"]);
        Assert.Null(Build(Options())["experimental"]);
    }

    [Fact]
    public void FullTunnel_FinalProxy_RussianAndDirectGamesStayDirect()
    {
        var root = Build(Options(full: true) with
        {
            DirectProcesses = new[] { "Obkhodiki.exe", "cs2.exe" },
            Processes = new[] { "chrome.exe" },
            DirectRuleSets = new[] { RuSite, RuIp },
            ProxyRuleSets = new[] { Blocked },
        });
        var route = root["route"]!;
        Assert.Equal(SingBoxConfig.ProxyTag, (string?)route["final"]);
        var rules = route["rules"]!.AsArray();

        int IndexOf(Func<JsonNode, bool> f) => rules.Select((r, i) => (r, i)).First(x => f(x.r!)).i;
        var privateIdx = IndexOf(r => r["ip_is_private"] is not null);
        var directProc = IndexOf(r => r["process_name"] is JsonArray a && a.Any(n => (string?)n == "cs2.exe"));
        var proxyProc = IndexOf(r => r["process_name"] is JsonArray a && a.Any(n => (string?)n == "chrome.exe"));
        var ruSets = IndexOf(r => r["rule_set"] is not null);
        Assert.True(privateIdx < directProc && directProc < proxyProc && proxyProc < ruSets);
        Assert.Equal("direct", (string?)rules[ruSets]!["outbound"]);

        // Selective-mode categories are irrelevant here and not loaded.
        var sets = route["rule_set"]!.AsArray().Select(s => (string)s!["tag"]!).ToList();
        Assert.Equal(new[] { "geosite-category-ru", "geoip-ru" }, sets);

        // All lookups through the VPS, Russian domains locally; the geoip set is never used for DNS.
        var dns = root["dns"]!;
        Assert.Equal("remote", (string?)dns["final"]);
        var dnsRule = dns["rules"]!.AsArray().Single()!;
        Assert.Equal("local", (string?)dnsRule["server"]);
        Assert.Equal(new[] { "geosite-category-ru" }, dnsRule["rule_set"]!.AsArray().Select(n => (string)n!));
        Assert.True((bool)root["inbounds"]![0]!["strict_route"]!);
    }

    [Fact]
    public void Selective_CategoriesGoThroughVps_DnsForThemRemote()
    {
        var root = Build(Options() with { ProxyRuleSets = new[] { Blocked, new LocalRuleSet("geoip-ru-blocked", @"C:\rs\geoip-ru-blocked.srs") }, DirectRuleSets = new[] { RuIp } });
        var route = root["route"]!;
        Assert.Equal("direct", (string?)route["final"]);
        var setRule = route["rules"]!.AsArray().Single(r => r!["rule_set"] is not null)!;
        Assert.Equal(SingBoxConfig.ProxyTag, (string?)setRule["outbound"]);
        Assert.Equal(2, setRule["rule_set"]!.AsArray().Count);
        Assert.DoesNotContain(route["rule_set"]!.AsArray(), s => (string?)s!["tag"] == "geoip-ru");

        var dns = root["dns"]!;
        Assert.Equal("local", (string?)dns["final"]);
        var dnsRule = dns["rules"]!.AsArray().Single()!;
        Assert.Equal(new[] { "geosite-ru-blocked" }, dnsRule["rule_set"]!.AsArray().Select(n => (string)n!));
        Assert.Contains(route["rules"]!.AsArray(), r => (string?)r!["action"] == "hijack-dns");
    }

    [Fact]
    public void ProbeUsers_OnePerServer_RoutedToThatServer()
    {
        var root = Build(Options());
        var users = root["inbounds"]!.AsArray().Single(i => (string?)i!["tag"] == "probe-in")!["users"]!.AsArray();
        Assert.Equal(1 + Servers.Length, users.Count);
        Assert.All(users, u => Assert.Equal("p", (string?)u!["password"]));
        var rules = root["route"]!["rules"]!.AsArray();
        foreach (var s in Servers)
        {
            var rule = rules.Single(r => r!["auth_user"] is JsonArray a && (string?)a[0] == SingBoxConfig.ServerProbeUser(s.Tag))!;
            Assert.Equal(s.Tag, (string?)rule["outbound"]);
            Assert.Equal("probe-in", (string?)rule["inbound"]![0]);
        }
    }

    [Fact]
    public void NoHostIpv6_TunnelIpv4Only_DnsWithoutAaaa()
    {
        var root = Build(Options(full: true) with { HostIpv6 = false });
        Assert.Equal(new[] { "172.19.0.1/30" }, root["inbounds"]![0]!["address"]!.AsArray().Select(n => (string)n!));
        Assert.Equal("ipv4_only", (string?)root["dns"]!["strategy"]);

        var withV6 = Build(Options(full: true));
        Assert.Equal(2, withV6["inbounds"]![0]!["address"]!.AsArray().Count);
        Assert.Equal("prefer_ipv4", (string?)withV6["dns"]!["strategy"]);
    }

    [Fact]
    public void FullTunnel_AppDownloadHostsResolvedLocallyFirst()
    {
        var root = Build(Options(full: true) with { LocalDnsDomains = new[] { "raw.githubusercontent.com", "Panel.Example.com", "not a domain" } });
        var first = root["dns"]!["rules"]!.AsArray()[0]!;
        Assert.Equal("local", (string?)first["server"]);
        Assert.Equal(new[] { "raw.githubusercontent.com", "panel.example.com" }, first["domain_suffix"]!.AsArray().Select(n => (string)n!));
    }

    [Theory]
    [InlineData("bad tag", @"C:\rs\x.srs")]
    [InlineData("geosite-x", @"relative\x.srs")]
    public void InvalidRuleSet_Throws(string tag, string path) =>
        Assert.Throws<ArgumentException>(() => SingBoxConfig.Build(Servers, null, Options() with { ProxyRuleSets = new[] { new LocalRuleSet(tag, path) } }));

    [Fact]
    public void DuplicateOrReservedTags_Throw()
    {
        Assert.Throws<ArgumentException>(() => SingBoxConfig.Build(new[] { Servers[0], Servers[0] }, null, Options()));
        Assert.Throws<ArgumentException>(() => SingBoxConfig.Build(new[] { Servers[0] with { Tag = "proxy" } }, null, Options()));
    }
}

public class ServerRankerTests
{
    private static ServerPing P(string tag, int? ms, int ok = 3) => new(tag, ms, ok, 3);

    [Fact]
    public void ClearlyFaster_Switches() => Assert.Equal("b", ServerRanker.ChooseBetter("a", new[] { P("a", 120), P("b", 60) }));

    [Fact]
    public void SlightlyFaster_Stays() => Assert.Null(ServerRanker.ChooseBetter("a", new[] { P("a", 80), P("b", 70) }));

    [Fact]
    public void GainBelowRatio_Stays() => Assert.Null(ServerRanker.ChooseBetter("a", new[] { P("a", 300), P("b", 270) }));

    [Fact]
    public void CurrentDown_SwitchesToAnyReliable() =>
        Assert.Equal("b", ServerRanker.ChooseBetter("a", new[] { P("a", null, 0), P("b", 200) }));

    [Fact]
    public void CurrentUnknown_Switches() => Assert.Equal("b", ServerRanker.ChooseBetter(null, new[] { P("b", 90) }));

    [Fact]
    public void FlakyWinner_Ignored() => Assert.Null(ServerRanker.ChooseBetter("a", new[] { P("a", 150), P("b", 20, ok: 1) }));

    [Fact]
    public void NothingReliable_Stays() => Assert.Null(ServerRanker.ChooseBetter("a", new[] { P("a", null, 0), P("b", null, 0) }));

    [Fact]
    public void Ping_MedianOfSuccesses()
    {
        var p = ServerPing.From("a", new int?[] { 90, null, 50, 70 });
        Assert.Equal(70, p.MedianMs);
        Assert.Equal(3, p.Successes);
        Assert.True(p.Reliable);
        Assert.False(ServerPing.From("a", new int?[] { 50, null, null }).Reliable);
    }
}

public class BypassRulesTests
{
    private static readonly VpnServerEntry[] Servers = { VpnServerEntry.FromLink("hy2://pw@a.example.com:443#A") };

    private static JsonArray Rules(bool full, string[] processes, string[] entries, string[]? proxyDomains = null)
    {
        var o = new SingBoxOptions(true, 20000, new[] { "chrome.exe" }, proxyDomains ?? Array.Empty<string>(), @"C:\x\sb.log", new ProbeCredentials("u", "p"))
        {
            FullTunnel = full,
            BypassProcesses = processes,
            BypassEntries = entries,
        };
        return JsonNode.Parse(SingBoxConfig.Build(Servers, null, o))!["route"]!["rules"]!.AsArray();
    }

    private static int IndexOf(JsonArray rules, string key, string value) =>
        rules.Select((r, i) => (r, i)).First(x => x.r![key] is JsonArray a && a.Any(n => (string?)n == value)).i;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bypass_GoesDirectBeforeEveryProxyRule(bool full)
    {
        var rules = Rules(full, new[] { "steam.exe" }, new[] { "sberbank.ru", "10.20.0.0/16", "2001:db8::1" }, new[] { "sberbank.ru" });

        var byProcess = IndexOf(rules, "process_name", "steam.exe");
        var byDomain = IndexOf(rules, "domain_suffix", "sberbank.ru");
        var byCidr = IndexOf(rules, "ip_cidr", "10.20.0.0/16");
        Assert.Equal("direct", (string?)rules[byProcess]!["outbound"]);
        Assert.Equal("direct", (string?)rules[byDomain]!["outbound"]);
        Assert.Equal("direct", (string?)rules[byCidr]!["outbound"]);
        Assert.Contains(rules[byCidr]!["ip_cidr"]!.AsArray(), n => (string?)n == "2001:db8::1/128");
        // The user's VPS list for the same site comes later and so never applies.
        var proxyDomain = rules.Select((r, i) => (r, i)).Last(x => x.r!["domain_suffix"] is JsonArray a && a.Any(n => (string?)n == "sberbank.ru")).i;
        Assert.True(byDomain < proxyDomain);
        Assert.True(byProcess < IndexOf(rules, "process_name", "chrome.exe"));
    }

    [Theory]
    [InlineData("1.2.3.4", "1.2.3.4/32")]
    [InlineData(" 10.0.0.0/8 ", "10.0.0.0/8")]
    [InlineData("2001:db8::/32", "2001:db8::/32")]
    [InlineData("*.Example.COM", "example.com")]
    [InlineData("1.2.3", null)]
    [InlineData("1.2.3.4/33", null)]
    [InlineData("fe80::1%eth0", null)]
    [InlineData("not a site", null)]
    public void BypassEntry_Normalized(string input, string? expected)
    {
        Assert.Equal(expected, SingBoxConfig.NormalizeBypassEntry(input));
    }

    [Fact]
    public void Plan_BypassBeatsVpnListsAndGames()
    {
        var settings = new Obkhodiki.Core.Settings.AppSettings
        {
            VpnProcesses = { "Game.exe", "chrome.exe" },
            VpnBypassProcesses = { "game.exe" },
            VpnBypassEntries = { "Bank.ru", "8.8.8.8" },
        };

        var plan = VpnPlan.From(settings, Array.Empty<string>());

        Assert.Equal(new[] { "chrome.exe" }, plan.Processes);
        Assert.Equal(new[] { "game.exe" }, plan.BypassProcesses);
        Assert.Equal(new[] { "bank.ru", "8.8.8.8/32" }, plan.BypassEntries);
    }
}

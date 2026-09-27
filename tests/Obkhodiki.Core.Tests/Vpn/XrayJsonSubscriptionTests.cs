using Obkhodiki.Core.Vpn;

namespace Obkhodiki.Core.Tests.Vpn;

public class XrayJsonSubscriptionTests
{
    private const string Uuid = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0";
    private const string Pbk = "Hh7cCHBW1W7zuEJqLUcmbJPbsVxBuwAQ3mvD6rPOxWM";

    private static string Reality(string tag, string address) => $$"""
        { "protocol": "vless", "tag": "{{tag}}",
          "settings": { "vnext": [ { "address": "{{address}}", "port": 443, "users": [ { "id": "{{Uuid}}", "encryption": "none", "flow": "xtls-rprx-vision" } ] } ] },
          "streamSettings": { "network": "tcp", "security": "reality",
            "realitySettings": { "fingerprint": "qq", "publicKey": "{{Pbk}}", "serverName": "www.example.com", "shortId": "6ba85179e30d4fc2", "show": false, "spiderX": "/" } } }
        """;

    private const string Ws = $$"""
        { "protocol": "vless", "tag": "proxy-pl",
          "settings": { "vnext": [ { "address": "pl.example.com", "port": 443, "users": [ { "id": "{{Uuid}}", "encryption": "none" } ] } ] },
          "streamSettings": { "network": "ws", "security": "tls", "wsSettings": { "path": "/amws", "host": "cdn.example.com" },
            "tlsSettings": { "serverName": "pl.example.com", "fingerprint": "qq", "alpn": ["http/1.1"] } } }
        """;

    private const string Xhttp = $$"""
        { "protocol": "vless", "tag": "proxy-lt",
          "settings": { "vnext": [ { "address": "lt.example.com", "port": 443, "users": [ { "id": "{{Uuid}}", "encryption": "none" } ] } ] },
          "streamSettings": { "network": "xhttp", "security": "tls", "xhttpSettings": { "path": "/x" }, "tlsSettings": { "serverName": "lt.example.com" } } }
        """;

    private const string Direct = """{ "protocol": "freedom", "tag": "direct" }, { "protocol": "blackhole", "tag": "block" }""";

    // Remnawave: one "auto" config balancing several servers, then one config per server with a human name.
    private static string Subscription => $$"""
        [
          { "remarks": "Auto", "outbounds": [ {{Reality("p1-nl", "nl.example.com")}}, {{Reality("p1-de", "de.example.com")}}, {{Direct}} ] },
          { "remarks": "🇳🇱 Нидерланды", "outbounds": [ {{Reality("proxy-nl", "nl.example.com")}}, {{Direct}} ] },
          { "remarks": "🇵🇱 Польша", "outbounds": [ {{Ws}}, {{Direct}} ] },
          { "remarks": "🇱🇹 Литва", "outbounds": [ {{Xhttp}}, {{Direct}} ] }
        ]
        """;

    [Fact]
    public void RemnawaveJson_BecomesServers_NamedByRemarks_Deduplicated()
    {
        var content = SubscriptionContent.Parse(Subscription);

        var names = content.Servers.Select(s => s.Server.Name).ToList();
        // nl comes once (auto config and its own config are the same server) and keeps its human name.
        Assert.Equal(new[] { "🇳🇱 Нидерланды", "🇵🇱 Польша", "p1-de" }, names);
        var skipped = Assert.Single(content.Skipped);
        Assert.Contains("XHTTP", skipped);
        Assert.DoesNotContain(Uuid, skipped);
    }

    [Fact]
    public void RealityAndWebSocketSettingsCarriedOver()
    {
        var servers = SubscriptionContent.Parse(Subscription).Servers;

        var nl = (VlessLink)servers[0].Server;
        Assert.Equal("nl.example.com", nl.Host);
        Assert.Equal("xtls-rprx-vision", nl.Flow);
        Assert.Equal(LinkSecurity.Reality, nl.Tls.Security);
        Assert.Equal(Pbk, nl.Tls.RealityPublicKey);
        Assert.Equal("6ba85179e30d4fc2", nl.Tls.RealityShortId);
        Assert.Equal("www.example.com", nl.Tls.Sni);
        Assert.Equal("qq", nl.Tls.Fingerprint);

        var pl = (VlessLink)servers[1].Server;
        Assert.Equal(LinkTransportType.WebSocket, pl.Transport.Type);
        Assert.Equal("/amws", pl.Transport.Path);
        Assert.Equal("cdn.example.com", pl.Transport.HostHeader);
        Assert.Equal(new[] { "http/1.1" }, pl.Tls.Alpn);
    }

    [Fact]
    public void SingleConfigObject_TrojanAndShadowsocks()
    {
        const string json = """
            { "remarks": "Mixed", "outbounds": [
              { "protocol": "trojan", "tag": "t", "settings": { "servers": [ { "address": "t.example.com", "port": 443, "password": "p@ss:w" } ] },
                "streamSettings": { "network": "tcp", "security": "tls", "tlsSettings": { "serverName": "t.example.com" } } },
              { "protocol": "shadowsocks", "tag": "s", "settings": { "servers": [ { "address": "s.example.com", "port": 8388, "method": "aes-256-gcm", "password": "secret" } ] } }
            ] }
            """;

        var servers = SubscriptionContent.Parse(json).Servers;

        var trojan = Assert.IsType<TrojanLink>(servers[0].Server);
        Assert.Equal("p@ss:w", trojan.Password);
        Assert.Equal("t", trojan.Name);
        var ss = Assert.IsType<ShadowsocksLink>(servers[1].Server);
        Assert.Equal("secret", ss.Password);
    }

    [Fact]
    public void BrokenJson_ReportedNotThrown()
    {
        var content = SubscriptionContent.Parse("[{\"outbounds\": [");
        Assert.Empty(content.Servers);
        Assert.Single(content.Skipped);
    }
}

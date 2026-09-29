using System.Net;
using System.Text.Json.Nodes;
using Obkhodiki.Core.Vpn;

namespace Obkhodiki.Core.Tests.Vpn;

public class AmneziaWgTests
{
    private static readonly string Priv = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly string Pub = Convert.ToBase64String(Enumerable.Range(101, 32).Select(i => (byte)i).ToArray());
    private static readonly string Psk = Convert.ToBase64String(Enumerable.Range(201, 32).Select(i => (byte)i).ToArray());

    private static string Config(string endpoint = "203.0.113.7:51820", string extra = "") => $"""
        [Interface]
        Address = 10.8.1.2/32
        DNS = 1.1.1.1, 1.0.0.1
        PrivateKey = {Priv}
        Jc = 4
        Jmin = 10
        Jmax = 50
        S1 = 86
        S2 = 12
        H1 = 1234567
        H2 = 100-200
        H3 = 3
        H4 = 4
        I1 = <b 0xc6000000010843290a47ba8ba2ed000044d0e3efd9326adb60561baa3bc4b52471b2d459ddcc9a508dffddc97e4e40d4b8><r 16>
        {extra}

        [Peer]
        PublicKey = {Pub}
        PresharedKey = {Psk}
        AllowedIPs = 0.0.0.0/0, ::/0
        Endpoint = {endpoint}
        PersistentKeepalive = 25
        """;

    [Fact]
    public void ParseConfig_AmneziaWg_ToEndpoint()
    {
        var server = (AwgServer)ProxyLinks.Parse(Config());
        var o = server.ToOutbound("s-1");

        Assert.True(server.IsEndpoint);
        Assert.True(server.NeedsAmnezia);
        Assert.Equal("awg", server.Protocol);
        Assert.Equal("203.0.113.7", server.Host);
        Assert.Equal(51820, server.Port);
        Assert.Equal("awg", (string?)o["type"]);
        Assert.Equal("s-1", (string?)o["tag"]);
        Assert.Equal("10.8.1.2/32", (string?)o["address"]![0]);
        Assert.Equal(Priv, (string?)o["private_key"]);
        Assert.Equal(4, (int)o["jc"]!);
        Assert.Equal(50, (int)o["jmax"]!);
        Assert.Equal(86, (int)o["s1"]!);
        Assert.Equal("100-200", (string?)o["h2"]);
        Assert.StartsWith("<b 0xc6", (string?)o["i1"]);
        var peer = o["peers"]![0]!;
        Assert.Equal("203.0.113.7", (string?)peer["address"]);
        Assert.Equal(51820, (int)peer["port"]!);
        Assert.Equal(Pub, (string?)peer["public_key"]);
        Assert.Equal(Psk, (string?)peer["preshared_key"]);
        Assert.Equal(new[] { "0.0.0.0/0", "::/0" }, peer["allowed_ips"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(25, (int)peer["persistent_keepalive_interval"]!);
        Assert.DoesNotContain("dns", o.Select(p => p.Key));
    }

    [Fact]
    public void ParseConfig_PlainWireGuard_NoObfuscationFields()
    {
        var text = $"[Interface]\nPrivateKey = {Priv}\nAddress = 10.0.0.2\n\n[Peer]\nPublicKey = {Pub}\nEndpoint = 198.51.100.1:51820\n";
        var server = (AwgServer)ProxyLinks.Parse(text);
        var o = server.ToOutbound("t");

        Assert.Equal("wireguard", server.Protocol);
        Assert.Null(o["jc"]);
        Assert.Equal("10.0.0.2/32", (string?)o["address"]![0]);
        Assert.Equal(new[] { "0.0.0.0/0", "::/0" }, o["peers"]![0]!["allowed_ips"]!.AsArray().Select(n => (string?)n));
        Assert.Null(o["peers"]![0]!["preshared_key"]);
    }

    [Fact]
    public void ToOutbound_ResolvesNamesAndBracketsIpv6()
    {
        var saved = AwgServer.ResolveHost;
        try
        {
            AwgServer.ResolveHost = host => host == "vpn.example.com" ? IPAddress.Parse("2001:db8::5") : IPAddress.Parse(host);
            var o = ((AwgServer)ProxyLinks.Parse(Config("vpn.example.com:443"))).ToOutbound("t");
            Assert.Equal("[2001:db8::5]", (string?)o["peers"]![0]!["address"]);
            Assert.Equal(443, (int)o["peers"]![0]!["port"]!);
        }
        finally
        {
            AwgServer.ResolveHost = saved;
        }
    }

    [Theory]
    [InlineData("Jc = 999")]
    [InlineData("Jmin = 60")] // above Jmax = 50
    [InlineData("H1 = abc")]
    [InlineData("I2 = line\u0001break")]
    [InlineData("MTU = 20")]
    [InlineData("Address = not-an-ip")]
    public void ParseConfig_BadValues_Throw(string line)
    {
        // Later lines win: the bad value replaces the good one.
        Assert.Throws<FormatException>(() => ProxyLinks.Parse(Config(extra: line)));
    }

    [Fact]
    public void ParseConfig_BadKeyOrMissingParts_Throw()
    {
        Assert.Throws<FormatException>(() => ProxyLinks.Parse(Config().Replace(Pub, "short")));
        Assert.Throws<FormatException>(() => AwgServer.ParseConfig(Config().Replace("Endpoint = 203.0.113.7:51820", "")));
        Assert.Throws<FormatException>(() => AwgServer.ParseConfig(Config().Replace("Address = 10.8.1.2/32", "")));
    }

    [Fact]
    public void TagFor_ConfigKeepsCommentLines()
    {
        var a = "# Server A\n" + Config();
        var b = "# Server B\n" + Config("203.0.113.8:51820");
        Assert.NotEqual(VpnServerEntry.TagFor(a), VpnServerEntry.TagFor(b));
        Assert.Equal(VpnServerEntry.TagFor(Config()), VpnServerEntry.TagFor(Config().Replace("\n", "\r\n")));
    }

    // ---------- vpn:// keys ----------

    private static string AwgKey(string? defaultContainer = "amnezia-awg", bool withXray = false)
    {
        var lastConfig = new JsonObject
        {
            ["config"] = Config().Replace("Endpoint = 203.0.113.7:51820", "Endpoint = 203.0.113.7:36000"),
            ["hostName"] = "203.0.113.7",
            ["port"] = 36000,
            ["client_priv_key"] = Priv,
            ["server_pub_key"] = Pub,
            ["mtu"] = "1280",
        };
        var containers = new JsonArray
        {
            new JsonObject { ["container"] = "amnezia-openvpn", ["openvpn"] = new JsonObject { ["last_config"] = "{}" } },
            new JsonObject { ["container"] = "amnezia-awg", ["awg"] = new JsonObject { ["port"] = "36000", ["last_config"] = lastConfig.ToJsonString() } },
        };
        if (withXray) containers.Add(XrayContainer());
        var root = new JsonObject
        {
            ["containers"] = containers,
            ["defaultContainer"] = defaultContainer,
            ["description"] = "Мой сервер",
            ["dns1"] = "1.1.1.1",
            ["hostName"] = "203.0.113.7",
        };
        return AmneziaKey.Encode(root.ToJsonString());
    }

    private static JsonObject XrayContainer()
    {
        var xray = """
        {"outbounds":[{"protocol":"vless","settings":{"vnext":[{"address":"198.51.100.9","port":443,
          "users":[{"id":"0b5c5e1e-1111-2222-3333-444455556666","encryption":"none","flow":"xtls-rprx-vision"}]}]},
          "streamSettings":{"network":"tcp","security":"reality","realitySettings":{"serverName":"www.googletagmanager.com",
          "publicKey":"Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw","shortId":"0123abcd","fingerprint":"chrome"}}}]}
        """;
        return new JsonObject { ["container"] = "amnezia-xray", ["xray"] = new JsonObject { ["last_config"] = xray, ["port"] = "443" } };
    }

    [Fact]
    public void Key_AwgContainer_BecomesAwgServer()
    {
        var server = (AwgServer)ProxyLinks.Parse(AwgKey());

        Assert.Equal("Мой сервер", server.Name);
        Assert.Equal(36000, server.Port);
        Assert.Equal(1280, server.Mtu);
        Assert.Equal("4", server.Obfuscation["jc"]);
    }

    [Fact]
    public void Key_DefaultContainerWins_XrayBecomesVless()
    {
        var server = ProxyLinks.Parse(AwgKey(defaultContainer: "amnezia-xray", withXray: true));

        var vless = Assert.IsType<VlessLink>(server);
        Assert.Equal("198.51.100.9", vless.Host);
        Assert.Equal("Мой сервер", vless.Name);
        Assert.False(server.NeedsAmnezia);
    }

    [Fact]
    public void Key_OnlyUnsupportedProtocols_SaysWhich()
    {
        var root = new JsonObject
        {
            ["containers"] = new JsonArray(new JsonObject { ["container"] = "amnezia-openvpn-cloak", ["cloak"] = new JsonObject() }),
            ["hostName"] = "203.0.113.7",
        };
        var ex = Assert.Throws<FormatException>(() => ProxyLinks.Parse(AmneziaKey.Encode(root.ToJsonString())));
        Assert.Contains("Cloak", ex.Message);
    }

    [Fact]
    public void Key_PremiumAccount_Explained()
    {
        var key = AmneziaKey.Encode("""{"config_version":2,"api_key":"secret-token","name":"Amnezia Premium"}""");
        var ex = Assert.Throws<FormatException>(() => ProxyLinks.Parse(key));
        Assert.Contains("Premium", ex.Message);
        Assert.DoesNotContain("secret-token", ex.Message);
    }

    [Theory]
    [InlineData("vpn://")]
    [InlineData("vpn://!!!")]
    [InlineData("vpn://AAAAAQ")] // declares a size, no zlib stream
    public void Key_Garbage_Throws(string text)
    {
        Assert.Throws<FormatException>(() => ProxyLinks.Parse(text));
    }

    [Fact]
    public void Key_UncompressedJson_Accepted()
    {
        var json = AmneziaKey.Decode(AwgKey());
        var plain = "vpn://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.IsType<AwgServer>(ProxyLinks.Parse(plain));
    }

    // ---------- config ----------

    [Fact]
    public void SingBoxConfig_AwgServersGoToEndpoints()
    {
        var awg = new VpnServerEntry("s-awg", ProxyLinks.Parse(Config()));
        var hy = VpnServerEntry.FromLink("hysteria2://pass@hy.example.com:443?sni=hy.example.com#hy");
        var json = JsonNode.Parse(SingBoxConfig.Build(new[] { hy, awg }, "s-awg",
            new SingBoxOptions(false, 20000, Array.Empty<string>(), Array.Empty<string>(), "log.txt", new ProbeCredentials("u", "p"))))!;

        Assert.Equal("s-awg", (string?)json["endpoints"]![0]!["tag"]);
        Assert.DoesNotContain(json["outbounds"]!.AsArray(), o => (string?)o!["tag"] == "s-awg");
        var selector = json["outbounds"]!.AsArray().First(o => (string?)o!["type"] == "selector")!;
        Assert.Contains("s-awg", selector["outbounds"]!.AsArray().Select(n => (string?)n));
        Assert.Equal("s-awg", (string?)selector["default"]);
        var first = json["route"]!["rules"]![0]!;
        Assert.Equal("resolve", (string?)first["action"]);
        Assert.Equal("probe-in", (string?)first["inbound"]![0]);
        var hyOnly = JsonNode.Parse(SingBoxConfig.Build(new[] { hy }, null,
            new SingBoxOptions(false, 20000, Array.Empty<string>(), Array.Empty<string>(), "log.txt", new ProbeCredentials("u", "p"))))!;
        Assert.DoesNotContain(hyOnly["route"]!["rules"]!.AsArray(), r => (string?)r!["action"] == "resolve");
        Assert.Null(hyOnly["endpoints"]);
        Assert.True(SingBoxConfig.NeedsAmnezia(new[] { hy, awg }));
        Assert.False(SingBoxConfig.NeedsAmnezia(new[] { hy }));
        Assert.Equal(awg, SingBoxConfig.ServerAt(new[] { hy, awg }, endpoint: true, 0));
        Assert.Equal(hy, SingBoxConfig.ServerAt(new[] { hy, awg }, endpoint: false, 0));
        Assert.Null(SingBoxConfig.ServerAt(new[] { hy, awg }, endpoint: true, 1));
    }

    [Fact]
    public void Redactor_MasksKeysAndConfigs()
    {
        var text = Obkhodiki.Core.Diagnostics.Redactor.Redact("key " + AwgKey() + " and " + Config());
        Assert.DoesNotContain(Priv, text);
        Assert.DoesNotContain(Psk, text);
        Assert.DoesNotContain(AwgKey()["vpn://".Length..][..40], text);
    }
}

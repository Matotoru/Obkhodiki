using System.Text;
using System.Text.Json.Nodes;
using Obkhodiki.Core.Vpn;

namespace Obkhodiki.Core.Tests.Vpn;

public class VmessLinkTests
{
    private const string Id = "0b5c5e1e-1111-2222-3333-444455556666";

    private static string Link(JsonObject json, string? fragment = null) =>
        "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json.ToJsonString())) + (fragment is null ? "" : "#" + fragment);

    private static JsonObject Base(string net = "ws", string tls = "tls") => new()
    {
        ["v"] = "2", ["ps"] = "Нидерланды", ["add"] = "nl.example.com", ["port"] = "443", ["id"] = Id, ["aid"] = "0",
        ["scy"] = "auto", ["net"] = net, ["type"] = "none", ["host"] = "cdn.example.com", ["path"] = "/ray", ["tls"] = tls, ["sni"] = "nl.example.com",
    };

    [Fact]
    public void Parse_WebSocketTls_ToSingBoxOutbound()
    {
        var link = (VmessLink)ProxyLinks.Parse(Link(Base()));
        var o = link.ToOutbound("s-1");

        Assert.Equal("nl.example.com", link.Host);
        Assert.Equal(443, link.Port);
        Assert.Equal("Нидерланды", link.Name);
        Assert.Equal("vmess", (string?)o["type"]);
        Assert.Equal(Id, (string?)o["uuid"]);
        Assert.Equal("auto", (string?)o["security"]);
        Assert.Equal(0, (int)o["alter_id"]!);
        Assert.Equal("ws", (string?)o["transport"]!["type"]);
        Assert.Equal("/ray", (string?)o["transport"]!["path"]);
        Assert.Equal("cdn.example.com", (string?)o["transport"]!["headers"]!["Host"]);
        Assert.True((bool)o["tls"]!["enabled"]!);
        Assert.Equal("nl.example.com", (string?)o["tls"]!["server_name"]);
    }

    [Fact]
    public void Parse_NumbersAsJsonNumbers_GrpcServiceFromPath_UrlSafeBase64_FragmentName()
    {
        var json = Base("grpc");
        json["port"] = 8443;
        json["aid"] = 0;
        json["path"] = "svc";
        var link64 = "vmess://" + Link(json)["vmess://".Length..].Replace('+', '-').Replace('/', '_').TrimEnd('=');

        var link = (VmessLink)ProxyLinks.Parse(link64 + "#My%20Server");

        Assert.Equal(8443, link.Port);
        Assert.Equal("My Server", link.Name);
        Assert.Equal("svc", (string?)link.ToOutbound("t")["transport"]!["service_name"]);
    }

    [Fact]
    public void Parse_PlainTcp_NoTls()
    {
        var o = ((VmessLink)ProxyLinks.Parse(Link(Base("tcp", "")))).ToOutbound("t");

        Assert.Null(o["tls"]);
        Assert.Null(o["transport"]);
    }

    [Theory]
    [InlineData("add", null)]
    [InlineData("id", "not-a-uuid")]
    [InlineData("scy", "rc4")]
    [InlineData("net", "kcp")]
    [InlineData("tls", "reality")]
    [InlineData("port", "70000")]
    [InlineData("add", "bad host!")]
    [InlineData("sni", "evil\",\"x")]
    public void Parse_Invalid_Throws(string field, string? value)
    {
        var json = Base();
        if (value is null) json.Remove(field);
        else json[field] = value;

        Assert.Throws<FormatException>(() => ProxyLinks.Parse(Link(json)));
    }

    [Theory]
    [InlineData("vmess://")]
    [InlineData("vmess://!!!notbase64")]
    [InlineData("vmess://WzEsMiwzXQ==")] // a JSON array, not an object
    public void Parse_Garbage_Throws(string text)
    {
        Assert.Throws<FormatException>(() => ProxyLinks.Parse(text));
    }

    [Fact]
    public void XrayJsonSubscription_VmessOutbound_BecomesLink()
    {
        var config = """
        [{"remarks":"VM","outbounds":[{"protocol":"vmess","settings":{"vnext":[{"address":"de.example.com","port":443,
          "users":[{"id":"0b5c5e1e-1111-2222-3333-444455556666","alterId":0,"security":"auto"}]}]},
          "streamSettings":{"network":"ws","security":"tls","tlsSettings":{"serverName":"de.example.com"},"wsSettings":{"path":"/v"}}}]}]
        """;

        var content = SubscriptionContent.Parse(config);

        var server = Assert.Single(content.Servers);
        Assert.Equal("vmess", server.Server.Protocol);
        Assert.Equal("de.example.com", server.Server.Host);
    }

    [Fact]
    public void Redactor_MasksVmessLinks()
    {
        Assert.DoesNotContain("eyJ", Obkhodiki.Core.Diagnostics.Redactor.Redact("got " + Link(Base()) + " ok"));
    }
}

using System.Text.Json.Nodes;
using Obkhodiki.Core.Settings;
using Obkhodiki.Core.Vpn;

namespace Obkhodiki.Core.Tests.Vpn;

public class ProxyModeTests
{
    private static readonly VpnServerEntry Server = VpnServerEntry.FromLink("hysteria2://pass@hy.example.com:443?sni=hy.example.com#hy");

    private static JsonNode Build(bool tun, int? proxyPort, bool full = true) =>
        JsonNode.Parse(SingBoxConfig.Build(new[] { Server }, null,
            new SingBoxOptions(tun, 20000, Array.Empty<string>(), new[] { "chatgpt.com" }, "log.txt", new ProbeCredentials("u", "p"))
            {
                FullTunnel = full,
                SystemProxyPort = proxyPort,
            }))!;

    [Fact]
    public void ProxyPort_AddsOpenLoopbackMixedInbound_WithoutTun()
    {
        var json = Build(tun: false, proxyPort: 10801);
        var inbounds = json["inbounds"]!.AsArray();

        Assert.DoesNotContain(inbounds, i => (string?)i!["type"] == "tun");
        var proxy = Assert.Single(inbounds, i => (string?)i!["tag"] == "proxy-in")!;
        Assert.Equal("mixed", (string?)proxy["type"]);
        Assert.Equal("127.0.0.1", (string?)proxy["listen"]);
        Assert.Equal(10801, (int)proxy["listen_port"]!);
        // The Windows system proxy cannot send a password.
        Assert.Null(proxy["users"]);
        // Rules are not tied to the tunnel: the proxy's traffic follows the same ones, and "final" sends the rest.
        Assert.Equal("proxy", (string?)json["route"]!["final"]);
    }

    [Fact]
    public void NoProxyPort_NoProxyInbound()
    {
        Assert.DoesNotContain(Build(tun: true, proxyPort: null)["inbounds"]!.AsArray(), i => (string?)i!["tag"] == "proxy-in");
    }

    [Theory]
    [InlineData(80)]
    [InlineData(20000)] // the probe port
    [InlineData(70000)]
    public void BadProxyPort_Throws(int port)
    {
        Assert.Throws<ArgumentException>(() => Build(tun: false, proxyPort: port));
    }

    [Fact]
    public void Plan_TakesProxyModeFromSettings()
    {
        var settings = new AppSettings { VpnProxyMode = true, VpnFullTunnel = true };
        Assert.True(VpnPlan.From(settings, Array.Empty<string>()).ProxyMode);
        settings.VpnProxyMode = false;
        Assert.False(VpnPlan.From(settings, Array.Empty<string>()).ProxyMode);
    }

    [Fact]
    public void SettingsBundle_KeepsProxyMode()
    {
        var source = new AppSettings { VpnProxyMode = true };
        var bundle = SettingsBundle.Parse(SettingsBundle.Create(source, new Dictionary<string, string>(), new Dictionary<string, string>(), null, "0.9.0",
            DateTimeOffset.Now).Serialize());
        var target = new AppSettings();
        bundle.Settings.ApplyTo(target);
        Assert.True(target.VpnProxyMode);
    }
}

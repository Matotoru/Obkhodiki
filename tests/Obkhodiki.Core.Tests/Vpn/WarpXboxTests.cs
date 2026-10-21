using System.Text.Json.Nodes;
using Obkhodiki.Core.Settings;
using Obkhodiki.Core.Vpn;

namespace Obkhodiki.Core.Tests.Vpn;

public class WarpXboxTests
{
    private static readonly VpnServerEntry Server = VpnServerEntry.FromLink("hysteria2://pass@hy.example.com:443?sni=hy.example.com#hy");
    private static readonly string AiSet = Path.Combine(Path.GetTempPath(), "geosite-openai.srs");

    private static JsonNode Build(bool full, int? warpPort = 40000, bool xbox = false, int? proxyPort = null) =>
        JsonNode.Parse(SingBoxConfig.Build(new[] { Server }, null,
            new SingBoxOptions(proxyPort is null, 20000, Array.Empty<string>(), new[] { "chatgpt.com" }, "log.txt", new ProbeCredentials("u", "p"))
            {
                FullTunnel = full,
                WarpPort = warpPort,
                WarpDomains = new[] { "Gemini.Google.com" },
                WarpRuleSets = new[] { new LocalRuleSet("geosite-openai", AiSet) },
                XboxDns = xbox,
                SystemProxyPort = proxyPort,
            }))!;

    private static JsonArray Rules(JsonNode json) => json["route"]!["rules"]!.AsArray();

    [Fact]
    public void Warp_SocksOutbound_AndRulesBeforeVps()
    {
        var json = Build(full: true);

        var warp = Assert.Single(json["outbounds"]!.AsArray(), o => (string?)o!["tag"] == "warp")!;
        Assert.Equal("socks", (string?)warp["type"]);
        Assert.Equal("127.0.0.1", (string?)warp["server"]);
        Assert.Equal(40000, (int)warp["server_port"]!);

        var rules = Rules(json).Select(r => r!).ToList();
        // WARP's own tunnel to Cloudflare goes out directly.
        var self = rules.FindIndex(r => r["process_name"]?[0]?.GetValue<string>() == "warp-svc.exe");
        Assert.True(self >= 0);
        Assert.Equal("direct", (string?)rules[self]["outbound"]);

        var byDomain = rules.FindIndex(r => (string?)r["outbound"] == "warp" && r["domain_suffix"] is not null);
        var bySet = rules.FindIndex(r => (string?)r["outbound"] == "warp" && r["rule_set"] is not null);
        Assert.Equal("gemini.google.com", (string?)rules[byDomain]["domain_suffix"]![0]);
        Assert.Equal("geosite-openai", (string?)rules[bySet]["rule_set"]![0]);
        // Explicit WARP choices come before anything that sends traffic to the VPS.
        var firstVps = rules.FindIndex(r => (string?)r["outbound"] == "proxy" && r["inbound"] is null);
        Assert.True(byDomain < firstVps && bySet < firstVps);
        Assert.Contains(json["route"]!["rule_set"]!.AsArray(), r => (string?)r!["tag"] == "geosite-openai");
    }

    [Fact]
    public void NoWarpPort_NoWarpAnything()
    {
        var json = Build(full: true, warpPort: null);
        Assert.DoesNotContain(json["outbounds"]!.AsArray(), o => (string?)o!["tag"] == "warp");
        Assert.DoesNotContain(Rules(json), r => (string?)r!["outbound"] == "warp");
        Assert.DoesNotContain(Rules(json), r => r!["process_name"]?[0]?.GetValue<string>() == "warp-svc.exe");
    }

    [Fact]
    public void Xbox_Selective_IsDefaultResolver_AndHijacksDns()
    {
        var json = Build(full: false, xbox: true);
        var dns = json["dns"]!;

        var xbox = Assert.Single(dns["servers"]!.AsArray(), s => (string?)s!["tag"] == "xbox")!;
        Assert.Equal("https", (string?)xbox["type"]);
        Assert.Equal("xbox-dns.ru", (string?)xbox["server"]);
        Assert.Equal("local", (string?)xbox["domain_resolver"]);
        Assert.Null(xbox["detour"]);
        Assert.Equal("xbox", (string?)dns["final"]);
        // Its own name is looked up locally.
        Assert.Contains(dns["rules"]!.AsArray(), r => (string?)r!["server"] == "local" && r["domain_suffix"]!.AsArray().Any(d => (string?)d == "xbox-dns.ru"));
        // VPS sites keep the VPS resolver.
        Assert.Contains(dns["rules"]!.AsArray(), r => (string?)r!["server"] == "remote" && r["domain_suffix"]!.AsArray().Any(d => (string?)d == "chatgpt.com"));
        Assert.Contains(Rules(json), r => (string?)r!["action"] == "hijack-dns");
    }

    [Fact]
    public void Xbox_FullTunnel_OnlyForWarpTraffic()
    {
        var dns = Build(full: true, xbox: true)["dns"]!;
        Assert.Equal("remote", (string?)dns["final"]);
        var rules = dns["rules"]!.AsArray();
        Assert.Contains(rules, r => (string?)r!["server"] == "xbox" && r["domain_suffix"]?[0]?.GetValue<string>() == "gemini.google.com");
        Assert.Contains(rules, r => (string?)r!["server"] == "xbox" && r["rule_set"]?[0]?.GetValue<string>() == "geosite-openai");
    }

    [Fact]
    public void Xbox_SelectiveWithoutVpsSites_StillHijacksDns()
    {
        var json = JsonNode.Parse(SingBoxConfig.Build(new[] { Server }, null,
            new SingBoxOptions(true, 20000, Array.Empty<string>(), Array.Empty<string>(), "log.txt", new ProbeCredentials("u", "p")) { XboxDns = true }))!;
        Assert.Contains(Rules(json), r => (string?)r!["action"] == "hijack-dns");
        Assert.True((bool)json["inbounds"]!.AsArray().First(i => (string?)i!["type"] == "tun")!["strict_route"]!);
        Assert.Null(json["dns"]!["servers"]!.AsArray().FirstOrDefault(s => (string?)s!["tag"] == "remote"));
    }

    [Fact]
    public void Xbox_ProxyMode_ResolvesProxyInbound()
    {
        Assert.Contains(Rules(Build(full: true, xbox: true, proxyPort: 8780)),
            r => (string?)r!["action"] == "resolve" && (string?)r["inbound"]![0] == "proxy-in");
        Assert.DoesNotContain(Rules(Build(full: true, xbox: false, proxyPort: 8780)), r => (string?)r!["action"] == "resolve");
    }

    [Fact]
    public void Plan_WarpOnlyWhenEnabled_AndNeedsTunnel()
    {
        var settings = new AppSettings { WarpEnabled = false, WarpDomains = new() { "gemini.google.com" } };
        var off = VpnPlan.From(settings, Array.Empty<string>());
        Assert.False(off.UsesWarp);
        Assert.False(off.NeedsTunnel);

        settings.WarpEnabled = true;
        var on = VpnPlan.From(settings, Array.Empty<string>());
        Assert.True(on.UsesWarp);
        Assert.True(on.NeedsTunnel);
        Assert.Contains("ai", on.WarpCategories);
        Assert.Contains("ai", on.ActiveCategories);

        Assert.True(VpnPlan.From(new AppSettings { XboxDnsEnabled = true }, Array.Empty<string>()).NeedsTunnel);
    }

    [Fact]
    public void Settings_SanitizeAndBundle()
    {
        var s = AppSettingsStore.Sanitize(new AppSettings { WarpDomains = new() { "*.Gemini.Google.com", "bad domain" }, WarpCategories = new() { "ai", "nope" } });
        Assert.Equal(new[] { "gemini.google.com" }, s.WarpDomains);
        Assert.Equal(new[] { "ai" }, s.WarpCategories);

        s.WarpEnabled = true;
        s.XboxDnsEnabled = true;
        var bundle = SettingsBundle.Parse(SettingsBundle.Create(s, new Dictionary<string, string>(), new Dictionary<string, string>(), null, "0.10.0",
            DateTimeOffset.Now).Serialize());
        var target = new AppSettings();
        bundle.Settings.ApplyTo(target);
        Assert.True(target.WarpEnabled);
        Assert.True(target.XboxDnsEnabled);
        Assert.Equal(new[] { "gemini.google.com" }, target.WarpDomains);
    }

    [Fact]
    public void NoServers_SelectorFallsBackToDirect_WarpAndXboxStillApply()
    {
        var json = JsonNode.Parse(SingBoxConfig.Build(Array.Empty<VpnServerEntry>(), "gone",
            new SingBoxOptions(true, 20000, Array.Empty<string>(), Array.Empty<string>(), "log.txt", new ProbeCredentials("u", "p"))
            {
                WarpPort = 40000,
                WarpDomains = new[] { "gemini.google.com" },
                XboxDns = true,
            }))!;

        var outbounds = json["outbounds"]!.AsArray();
        var selector = Assert.Single(outbounds, o => (string?)o!["tag"] == "proxy")!;
        Assert.Equal(new[] { "direct" }, selector["outbounds"]!.AsArray().Select(o => (string?)o));
        Assert.Equal("direct", (string?)selector["default"]);
        Assert.Equal(new[] { "selector", "direct", "socks" }, outbounds.Select(o => (string?)o!["type"]));

        Assert.Equal("direct", (string?)json["route"]!["final"]);
        Assert.Contains(Rules(json), r => (string?)r!["outbound"] == "warp" && (string?)r["domain_suffix"]![0] == "gemini.google.com");
        // Only the probe port's own user still points at the selector; nothing else goes to "proxy".
        Assert.Single(Rules(json), r => (string?)r!["outbound"] == "proxy");
        Assert.Single(json["inbounds"]!.AsArray().First(i => (string?)i!["tag"] == "probe-in")!["users"]!.AsArray());

        var dns = json["dns"]!;
        Assert.Equal("xbox", (string?)dns["final"]);
        Assert.DoesNotContain(dns["servers"]!.AsArray(), s => (string?)s!["tag"] == "remote");
        Assert.Contains(Rules(json), r => (string?)r!["action"] == "hijack-dns");
    }

    [Fact]
    public void Plan_WithoutVps_KeepsOnlyWarpXboxAndBypass()
    {
        var settings = new AppSettings
        {
            VpnFullTunnel = true,
            VpnProcesses = new() { "chrome.exe" },
            VpnDomains = new() { "chatgpt.com" },
            VpnProxyCategories = new() { "ai" },
            VpnBypassProcesses = new() { "steam.exe" },
            VpnBypassEntries = new() { "sberbank.ru" },
            VpnProxyMode = true,
            WarpEnabled = true,
            WarpDomains = new() { "gemini.google.com" },
            XboxDnsEnabled = true,
        };
        var plan = VpnPlan.From(settings, new[] { "game.exe" }).WithoutVps();

        Assert.False(plan.FullTunnel);
        Assert.Empty(plan.Processes);
        Assert.Empty(plan.Domains);
        Assert.Empty(plan.DirectProcesses);
        Assert.Empty(plan.ProxyCategories);
        Assert.Empty(plan.DirectCategories);
        Assert.Equal(new[] { "gemini.google.com" }, plan.WarpDomains);
        Assert.True(plan.XboxDns);
        Assert.True(plan.UsesExtras);
        Assert.True(plan.ProxyMode);
        Assert.Equal(new[] { "steam.exe" }, plan.BypassProcesses);
        Assert.Equal(new[] { "sberbank.ru" }, plan.BypassEntries);
        // Only WARP's categories are left to download.
        Assert.Equal(plan.WarpCategories, plan.ActiveCategories);

        var none = VpnPlan.From(new AppSettings { VpnDomains = new() { "chatgpt.com" } }, Array.Empty<string>()).WithoutVps();
        Assert.False(none.UsesExtras);
        Assert.False(none.NeedsTunnel);
    }

    [Fact]
    public void TrafficMeter_DoesNotCountWarpAsVps()
    {
        Assert.False(new ClashConnection("1", 10, 10, new[] { "warp" }).ViaVpn);
        Assert.False(new ClashConnection("2", 10, 10, new[] { "direct", "proxy" }).ViaVpn);
        Assert.True(new ClashConnection("3", 10, 10, new[] { "srv", "proxy" }).ViaVpn);

        var meter = new VpnTrafficMeter();
        meter.Update(new[] { new ClashConnection("1", 100, 200, new[] { "warp" }), new ClashConnection("3", 5, 7, new[] { "srv", "proxy" }) });
        Assert.Equal(5, meter.Upload);
        Assert.Equal(7, meter.Download);
    }

    [Fact]
    public void BadWarpPort_Throws()
    {
        Assert.Throws<ArgumentException>(() => Build(full: true, warpPort: 80));
    }
}

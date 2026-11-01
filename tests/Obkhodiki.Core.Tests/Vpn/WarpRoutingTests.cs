using System.Text.Json.Nodes;
using Obkhodiki.Core.Settings;
using Obkhodiki.Core.Vpn;

namespace Obkhodiki.Core.Tests.Vpn;

/// <summary>WARP as a path of its own: everything through it, or chosen programs.</summary>
public class WarpRoutingTests
{
    private static readonly VpnServerEntry Server = VpnServerEntry.FromLink("hysteria2://pass@hy.example.com:443?sni=hy.example.com#hy");
    private static readonly string RuSet = Path.Combine(Path.GetTempPath(), "geosite-category-ru.srs");

    private static SingBoxOptions Options(bool warpFull = true, IReadOnlyList<string>? warpProcesses = null, bool xbox = false) =>
        new(true, 20000, Array.Empty<string>(), Array.Empty<string>(), "log.txt", new ProbeCredentials("u", "p"))
        {
            WarpPort = 40000,
            WarpFullTunnel = warpFull,
            WarpProcesses = warpProcesses ?? Array.Empty<string>(),
            DirectProcesses = new[] { VpnPlan.SelfProcess },
            DirectRuleSets = new[] { new LocalRuleSet("geosite-category-ru", RuSet) },
            XboxDns = xbox,
        };

    private static JsonNode Build(SingBoxOptions options, params VpnServerEntry[] servers) =>
        JsonNode.Parse(SingBoxConfig.Build(servers, null, options))!;

    private static List<JsonNode> Rules(JsonNode json) => json["route"]!["rules"]!.AsArray().Select(r => r!).ToList();

    private static bool IsUdp(JsonNode r) => r["network"]?[0]?.GetValue<string>() == "udp";

    [Fact]
    public void FullTunnel_FinalIsWarp_AndWorksWithoutServers()
    {
        var json = Build(Options());

        Assert.Equal("warp", (string?)json["route"]!["final"]);
        Assert.True((bool)json["inbounds"]![0]!["strict_route"]!);
        Assert.Contains(Rules(json), r => (string?)r["protocol"] == "dns" && (string?)r["action"] == "hijack-dns");
    }

    [Fact]
    public void FullTunnel_WarpClientAppAndDirectListsComeBeforeTheFinalUdpRules()
    {
        var rules = Rules(Build(Options()));
        int Index(Func<JsonNode, bool> match) => rules.FindIndex(r => match(r));

        var warpClient = Index(r => r["process_name"]?[0]?.GetValue<string>() == SingBoxConfig.WarpProcess);
        var self = Index(r => r["process_name"]?[0]?.GetValue<string>() == VpnPlan.SelfProcess);
        var ruSites = Index(r => (string?)r["outbound"] == "direct" && r["rule_set"]?[0]?.GetValue<string>() == "geosite-category-ru");
        var quic = Index(r => IsUdp(r) && (string?)r["action"] == "reject" && r["process_name"] is null);

        Assert.True(warpClient >= 0 && warpClient < self);
        Assert.True(self < ruSites);
        Assert.True(ruSites < quic);
    }

    [Fact]
    public void FullTunnel_QuicRefused_OtherUdpGoesDirect_BecauseWarpProxyIsTcpOnly()
    {
        var rules = Rules(Build(Options()));

        var quic = Assert.Single(rules, r => IsUdp(r) && (string?)r["action"] == "reject");
        Assert.Equal(443, (int)quic["port"]![0]!);
        var udp = Assert.Single(rules, r => IsUdp(r) && (string?)r["outbound"] == "direct");
        Assert.True(rules.IndexOf(quic) < rules.IndexOf(udp));
        Assert.DoesNotContain(rules, r => IsUdp(r) && (string?)r["outbound"] == "warp");
    }

    [Fact]
    public void FullTunnel_LookupsGoThroughWarp_ButItsOwnServiceResolvesLocally()
    {
        var dns = Build(Options())["dns"]!;

        Assert.Equal(SingBoxConfig.WarpDnsTag, (string?)dns["final"]);
        var server = Assert.Single(dns["servers"]!.AsArray(), s => (string?)s!["tag"] == SingBoxConfig.WarpDnsTag)!;
        Assert.Equal("warp", (string?)server["detour"]);
        var local = dns["rules"]!.AsArray().First(r => (string?)r!["server"] == "local" && r["domain_suffix"] is not null)!;
        Assert.Contains(local["domain_suffix"]!.AsArray(), d => (string?)d == SingBoxConfig.WarpServiceDomain);
        Assert.Contains(dns["rules"]!.AsArray(), r => (string?)r!["server"] == "local" && r["rule_set"]?[0]?.GetValue<string>() == "geosite-category-ru");
    }

    [Fact]
    public void FullTunnel_WithXboxDns_LookupsGoThroughXbox()
    {
        var dns = Build(Options(xbox: true))["dns"]!;

        Assert.Equal(SingBoxConfig.XboxDnsTag, (string?)dns["final"]);
        Assert.DoesNotContain(dns["servers"]!.AsArray(), s => (string?)s!["tag"] == SingBoxConfig.WarpDnsTag);
    }

    [Fact]
    public void FullTunnel_WithVps_ChosenProgramsStillGoThroughTheServer()
    {
        var options = Options() with { Processes = new[] { "chrome.exe" } };
        var json = Build(options, Server);

        Assert.Equal("warp", (string?)json["route"]!["final"]);
        Assert.Contains(Rules(json), r => (string?)r["outbound"] == "proxy" && r["process_name"]?[0]?.GetValue<string>() == "chrome.exe");
    }

    [Fact]
    public void FullTunnel_ExcludesVpsFullTunnel_AndNeedsWarpPort()
    {
        Assert.Throws<ArgumentException>(() => Build(Options() with { FullTunnel = true }, Server));
        Assert.Throws<ArgumentException>(() => Build(Options() with { WarpPort = null }));
    }

    [Fact]
    public void Selective_Programs_TcpThroughWarp_UdpNot_AndBeforeVpsLists()
    {
        var options = Options(warpFull: false, warpProcesses: new[] { "chrome.exe" }) with { Processes = new[] { "chrome.exe" } };
        var json = Build(options, Server);
        var rules = Rules(json);
        var chrome = rules.Where(r => r["process_name"]?[0]?.GetValue<string>() == "chrome.exe").ToList();

        Assert.Equal("direct", (string?)json["route"]!["final"]);
        Assert.Equal(4, chrome.Count);
        Assert.True(IsUdp(chrome[0]) && (string?)chrome[0]["action"] == "reject");
        Assert.True(IsUdp(chrome[1]) && (string?)chrome[1]["outbound"] == "direct");
        Assert.Equal("warp", (string?)chrome[2]["outbound"]);
        Assert.Equal("proxy", (string?)chrome[3]["outbound"]);
    }

    [Fact]
    public void Selective_LeavesFinalDnsAndCategoriesAlone()
    {
        var json = Build(Options(warpFull: false, warpProcesses: new[] { "chrome.exe" }));

        Assert.Equal("local", (string?)json["dns"]!["final"]);
        Assert.False((bool)json["inbounds"]![0]!["strict_route"]!);
        Assert.Null(json["route"]!["rule_set"]);
    }

    [Fact]
    public void InvalidWarpProcess_Throws()
    {
        Assert.Throws<ArgumentException>(() => Build(Options(warpProcesses: new[] { "..\\evil.exe" })));
    }

    private static AppSettings FullSettings() => new()
    {
        WarpEnabled = true,
        WarpFullTunnel = true,
        WarpCategories = new() { "ai" },
        WarpDirectCategories = new() { "ru" },
        WarpBypassProcesses = new() { "steam.exe" },
        WarpBypassEntries = new() { "sberbank.ru" },
        VpnDirectCategories = new(),
    };

    [Fact]
    public void Plan_Full_UsesItsOwnDirectLists_AndDropsWarpCategories()
    {
        var plan = VpnPlan.From(FullSettings(), Array.Empty<string>());

        Assert.True(plan.WarpFullTunnel);
        Assert.True(plan.UsesWarp);
        Assert.True(plan.NeedsTunnel);
        Assert.Equal(new[] { "ru" }, plan.ActiveDirectCategories);
        Assert.Empty(plan.WarpCategories);
        Assert.Equal(new[] { "steam.exe" }, plan.BypassProcesses);
        Assert.Equal(new[] { "sberbank.ru" }, plan.BypassEntries);
    }

    [Fact]
    public void Plan_Full_WithoutVps_StillLeavesThisAppAndDirectListsAlone()
    {
        var settings = FullSettings();
        settings.VpnProcesses = new() { "chrome.exe" };
        var plan = VpnPlan.From(settings, Array.Empty<string>()).WithoutVps();

        Assert.True(plan.WarpFullTunnel);
        Assert.Contains(VpnPlan.SelfProcess, plan.DirectProcesses);
        Assert.Equal(new[] { "ru" }, plan.DirectCategories);
        Assert.Empty(plan.Processes);
        Assert.Equal(new[] { "ru" }, plan.ActiveCategories);
    }

    [Fact]
    public void Plan_WarpOff_IgnoresAllWarpChoices()
    {
        var settings = FullSettings();
        settings.WarpEnabled = false;
        settings.WarpProcesses = new() { "chrome.exe" };
        var plan = VpnPlan.From(settings, Array.Empty<string>());

        Assert.False(plan.UsesWarp);
        Assert.False(plan.WarpFullTunnel);
        Assert.Empty(plan.WarpProcesses);
        Assert.Empty(plan.BypassProcesses);
        Assert.Empty(plan.BypassEntries);
    }

    [Fact]
    public void Plan_Selective_WarpBypassListsDoNotTouchTheVps_AndGlobalBypassBeatsWarp()
    {
        var settings = FullSettings();
        settings.WarpFullTunnel = false;
        settings.WarpProcesses = new() { "chrome.exe", "bank.exe" };
        settings.VpnBypassProcesses = new() { "Bank.exe" };
        var plan = VpnPlan.From(settings, Array.Empty<string>());

        Assert.Equal(new[] { "chrome.exe" }, plan.WarpProcesses);
        Assert.Equal(new[] { "Bank.exe" }, plan.BypassProcesses);
        Assert.Empty(plan.BypassEntries);
        Assert.Contains("ai", plan.WarpCategories);
        Assert.Empty(plan.ActiveDirectCategories);
    }

    [Fact]
    public void Sanitize_OnlyOnePathTakesEverything_AndBadEntriesDropped()
    {
        var s = AppSettingsStore.Sanitize(new AppSettings
        {
            VpnFullTunnel = true,
            WarpFullTunnel = true,
            WarpProcesses = new() { "chrome.exe", "CHROME.exe", "bad/name.exe" },
            WarpDirectCategories = new() { "ru", "nope" },
            WarpBypassProcesses = new() { "steam.exe", "nope" },
            WarpBypassEntries = new() { "*.Sberbank.ru", "10.0.0.0/8", "not an entry" },
        });

        Assert.True(s.VpnFullTunnel);
        Assert.False(s.WarpFullTunnel);
        Assert.Equal(new[] { "chrome.exe" }, s.WarpProcesses);
        Assert.Equal(new[] { "ru" }, s.WarpDirectCategories);
        Assert.Equal(new[] { "steam.exe" }, s.WarpBypassProcesses);
        Assert.Equal(new[] { "sberbank.ru", "10.0.0.0/8" }, s.WarpBypassEntries);
    }

    [Fact]
    public void Bundle_CarriesTheNewChoices()
    {
        var s = AppSettingsStore.Sanitize(FullSettings());
        s.WarpProcesses = new() { "chrome.exe" };
        var bundle = SettingsBundle.Parse(SettingsBundle.Create(s, new Dictionary<string, string>(), new Dictionary<string, string>(), null, "0.14.0",
            DateTimeOffset.Now).Serialize());
        var target = new AppSettings();
        bundle.Settings.ApplyTo(target);

        Assert.True(target.WarpFullTunnel);
        Assert.Equal(new[] { "chrome.exe" }, target.WarpProcesses);
        Assert.Equal(new[] { "ru" }, target.WarpDirectCategories);
        Assert.Equal(new[] { "steam.exe" }, target.WarpBypassProcesses);
        Assert.Equal(new[] { "sberbank.ru" }, target.WarpBypassEntries);
    }
}

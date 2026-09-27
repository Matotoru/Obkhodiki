using System.Net;
using Obkhodiki.Core.Games;
using Obkhodiki.Core.Settings;
using Obkhodiki.Core.Vpn;

namespace Obkhodiki.Core.Tests.Vpn;

public class VpnModelTests
{
    private static GameProfile Game(string exe, GameRoute route, bool enabled = true) =>
        new() { Id = exe.Replace(".exe", "").ToLowerInvariant(), Name = exe, Enabled = enabled, ProcessName = exe, Route = route };

    [Fact]
    public void Plan_CombinesProgramsFixedVpnGamesAndAutoDecisions()
    {
        var settings = new AppSettings
        {
            VpnProcesses = { "chrome.exe" },
            VpnDomains = { "chatgpt.com" },
            GameProfiles =
            {
                Game("WarDogs.exe", GameRoute.Vpn),
                Game("Cs2.exe", GameRoute.Direct),
                Game("Auto.exe", GameRoute.Auto),
                Game("Off.exe", GameRoute.Vpn, enabled: false),
            },
        };

        var plan = VpnPlan.From(settings, new[] { "Auto.exe", "CHROME.EXE" });

        Assert.Equal(new[] { "chrome.exe", "WarDogs.exe", "Auto.exe" }, plan.Processes);
        Assert.Equal(new[] { "chatgpt.com" }, plan.Domains);
        Assert.True(plan.NeedsTunnel);
    }

    // A decision for a game that is no longer an enabled Auto profile must not keep its traffic on the VPS.
    [Theory]
    [InlineData(GameRoute.Auto, false)]
    [InlineData(GameRoute.Direct, true)]
    public void Plan_StaleAutoDecision_Ignored(GameRoute route, bool enabled)
    {
        var settings = new AppSettings { GameProfiles = { Game("Auto.exe", route, enabled) } };

        Assert.Empty(VpnPlan.From(settings, new[] { "Auto.exe", "Unknown.exe" }).Processes);
    }

    [Fact]
    public void Plan_AutoGameWithoutDecision_NotTunneled()
    {
        var settings = new AppSettings { GameProfiles = { Game("Auto.exe", GameRoute.Auto) } };

        var plan = VpnPlan.From(settings, Array.Empty<string>());

        Assert.Empty(plan.Processes);
        Assert.False(plan.NeedsTunnel);
    }

    [Fact]
    public void Profiles_SanitizeRouteEndpointsAndProcess()
    {
        var profiles = GameProfiles.Sanitize(new GameProfile?[]
        {
            new()
            {
                Id = "wd", Route = (GameRoute)42, ProcessName = @"..\x.exe",
                ProbeEndpoints = { "3.120.1.1:443", "3.120.1.1:443", "10.0.0.1:443", "garbage", "3.120.1.2", "[2a05:d014::1]:443" },
            },
        });

        Assert.Equal(GameRoute.Direct, profiles[0].Route);
        Assert.Null(profiles[0].ProcessName);
        Assert.Equal(new[] { "3.120.1.1:443", "[2a05:d014::1]:443" }, profiles[0].ProbeEndpoints);
    }

    [Fact]
    public void Settings_VpnListsSanitizedOnLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), "zh-vpnset-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "s.json");
            File.WriteAllText(path, """{ "VpnProcesses": ["chrome.exe", "..\\evil.exe", "Chrome.exe"], "VpnDomains": ["*.ChatGPT.com", "bad domain", null] }""");

            var s = new AppSettingsStore(path).Load();

            Assert.Equal(new[] { "chrome.exe" }, s.VpnProcesses);
            Assert.Equal(new[] { "chatgpt.com" }, s.VpnDomains);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Learner_CollectsDistinctTcpEndpointsOnly()
    {
        var learner = new TrafficLearner();

        learner.Add(new FlowObservation(IPAddress.Parse("3.120.1.1"), 443, FlowProtocol.Tcp));
        learner.Add(new FlowObservation(IPAddress.Parse("3.120.1.1"), 443, FlowProtocol.Tcp));
        learner.Add(new FlowObservation(IPAddress.Parse("3.120.1.2"), 7777, FlowProtocol.Udp));

        Assert.Equal(new[] { "3.120.1.1:443" }, learner.TcpEndpoints.Select(e => e.ToString()));
    }
    [Fact]
    public void Plan_FullTunnel_DirectGamesAndSelfStayOff_ProxyGamesOn()
    {
        var settings = new AppSettings
        {
            VpnFullTunnel = true,
            VpnProxyCategories = { "ru-blocked" },
            GameProfiles =
            {
                Game("WarDogs.exe", GameRoute.Vpn),
                Game("Cs2.exe", GameRoute.Direct),
                Game("AutoDirect.exe", GameRoute.Auto),
                Game("AutoVpn.exe", GameRoute.Auto),
                Game("AutoNew.exe", GameRoute.Auto),
            },
        };

        var plan = VpnPlan.From(settings, new[] { "AutoVpn.exe" }, new[] { "AutoVpn.exe", "AutoDirect.exe" });

        Assert.True(plan.NeedsTunnel);
        Assert.Equal(new[] { VpnPlan.SelfProcess, "Cs2.exe", "AutoDirect.exe" }, plan.DirectProcesses);
        Assert.Equal(new[] { "WarDogs.exe", "AutoVpn.exe" }, plan.Processes);
        // Default: Russian sites stay direct; selective categories are not used in this mode.
        Assert.Equal(new[] { "ru" }, plan.ActiveCategories);
    }

    [Fact]
    public void Plan_Selective_CategoriesAloneNeedTunnel()
    {
        var plan = VpnPlan.From(new AppSettings { VpnProxyCategories = { "ai" } }, Array.Empty<string>());
        Assert.True(plan.NeedsTunnel);
        Assert.Equal(new[] { "ai" }, plan.ActiveCategories);
        Assert.False(VpnPlan.From(new AppSettings(), Array.Empty<string>()).NeedsTunnel);
    }

    [Fact]
    public void Settings_UnknownCategoriesAndBadServerTagDropped()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "settings.json");
            File.WriteAllText(path, """
                { "VpnProxyCategories": ["ai", "nope", "ai"], "VpnDirectCategories": ["ru", "x"], "VpnSelectedServer": "../evil", "VpnFullTunnel": true }
                """);
            var s = new AppSettingsStore(path).Load();
            Assert.Equal(new[] { "ai" }, s.VpnProxyCategories);
            Assert.Equal(new[] { "ru" }, s.VpnDirectCategories);
            Assert.Null(s.VpnSelectedServer);
            Assert.True(s.VpnFullTunnel);
            Assert.True(s.VpnAutoBest);
            Assert.True(s.VpnEnabled); // absent in older settings files: the VPS stays on as before
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

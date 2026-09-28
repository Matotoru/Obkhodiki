using Obkhodiki.Core.Games;
using Obkhodiki.Core.Settings;

namespace Obkhodiki.Core.Tests.Settings;

public class SettingsBundleTests
{
    private static AppSettings Sample() => new()
    {
        SelectedStrategy = "general (ALT11)",
        VpnProcesses = { "chrome.exe" },
        VpnBypassEntries = { "sberbank.ru" },
        VpnSelectedServer = "s-0123456789",
        AppSkippedVersion = "0.9.9",
        AppPalette = "ocean",
        GameProfiles = { new GameProfile { Id = "wardogs", Name = "War Dogs", Enabled = true, ProcessName = "WarDogs.exe", UdpPorts = "27015", Route = GameRoute.Vpn } },
    };

    [Fact]
    public void RoundTrip_KeepsChoices_DropsMachineState()
    {
        var json = SettingsBundle.Create(Sample(), new Dictionary<string, string> { ["wardogs"] = "# War Dogs\n52.215.0.0/16" },
            new Dictionary<string, string> { ["list-general-user.txt"] = "example.com", ["evil.bat"] = "x" }, "Yt = \"https://youtube.com\"", "0.6.0", DateTimeOffset.UnixEpoch).Serialize();

        Assert.DoesNotContain("s-0123456789", json);
        Assert.DoesNotContain("0.9.9", json);
        Assert.DoesNotContain("evil.bat", json);

        var target = new AppSettings { VpnSelectedServer = "s-aaaaaaaaaa" };
        var bundle = SettingsBundle.Parse(json);
        bundle.Settings.ApplyTo(target);

        Assert.Equal("general (ALT11)", target.SelectedStrategy);
        Assert.Equal("ocean", target.AppPalette);
        Assert.Equal("s-aaaaaaaaaa", target.VpnSelectedServer);
        Assert.Equal(GameRoute.Vpn, Assert.Single(target.GameProfiles).Route);
        Assert.Equal("# War Dogs\n52.215.0.0/16", bundle.GameAddresses["wardogs"]);
        Assert.Equal("example.com", bundle.UserLists["list-general-user.txt"]);
    }

    [Fact]
    public void Import_ValidatesUntrustedContent()
    {
        var json = """
        {
          "Format": "obkhodiki-settings", "Version": 1,
          "Settings": {
            "VpnProcesses": ["ok.exe", "..\\evil.exe", "cmd /c x"],
            "AppTheme": "neon", "AppPalette": "../../x",
            "GameProfiles": [ { "Id": "good", "Name": "G", "TcpPorts": "not ports" }, { "Id": "../bad", "Name": "B" } ]
          },
          "GameAddresses": { "good": "1.2.3.4\n--dpi-desync=fake\n10.0.0.0/8", "../bad": "1.1.1.1", "other": "2.2.2.2" },
          "UserLists": { "list-exclude-user.txt": "a.ru", "..\\..\\winws.exe": "x" }
        }
        """;

        var bundle = SettingsBundle.Parse(json);
        var target = new AppSettings();
        bundle.Settings.ApplyTo(target);

        Assert.Equal(new[] { "ok.exe" }, target.VpnProcesses);
        Assert.Equal("system", target.AppTheme);
        Assert.Equal("cat", target.AppPalette);
        var game = Assert.Single(target.GameProfiles);
        Assert.Equal("good", game.Id);
        Assert.False(game.Enabled);
        Assert.Equal("1.2.3.4\n10.0.0.0/8", Assert.Single(bundle.GameAddresses).Value);
        Assert.Equal(new[] { "list-exclude-user.txt" }, bundle.UserLists.Keys);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"Format":"something-else","Version":1}""")]
    [InlineData("""{"Format":"obkhodiki-settings","Version":99}""")]
    [InlineData("not json")]
    public void Import_RejectsForeignOrNewerFiles(string json)
    {
        Assert.Throws<FormatException>(() => SettingsBundle.Parse(json));
    }
}

using Obkhodiki.Core.Games;

namespace Obkhodiki.Core.Tests.Games;

public class GameShareTests
{
    [Fact]
    public void RoundTrip_ArrivesSwitchedOff_WithAddresses()
    {
        var profile = new GameProfile { Id = "apex", Name = "Apex Legends", Enabled = true, ProcessName = "r5apex_dx12.exe", UdpPorts = "37000-40000", Route = GameRoute.Vpn };

        var share = GameShare.Parse(GameShare.Create(profile, "# Apex\n35.200.0.0/16\n1.2.3.4", "0.7.0").Serialize());

        Assert.Equal("apex", share.Profile.Id);
        Assert.False(share.Profile.Enabled);
        Assert.Equal(GameRoute.Vpn, share.Profile.Route);
        Assert.Equal("# Apex\n35.200.0.0/16\n1.2.3.4", share.Addresses);
    }

    [Fact]
    public void Import_ValidatesUntrustedContent()
    {
        var json = """
        {"Format":"obkhodiki-game","Version":1,
         "Profile":{"Id":"good","Name":"G","Enabled":true,"ProcessName":"../evil.exe","UdpPorts":"not ports"},
         "Addresses":"1.2.3.4\n--wf-tcp=1-65535\n10.0.0.0/8"}
        """;

        var share = GameShare.Parse(json);

        Assert.False(share.Profile.Enabled);
        Assert.Null(share.Profile.ProcessName);
        Assert.Equal("", share.Profile.UdpPorts);
        Assert.Equal("1.2.3.4\n10.0.0.0/8", share.Addresses);
    }

    [Theory]
    [InlineData("""{"Format":"obkhodiki-settings","Version":1}""")]
    [InlineData("""{"Format":"obkhodiki-game","Version":1,"Profile":{"Id":"../x","Name":"x"}}""")]
    [InlineData("""{"Format":"obkhodiki-game","Version":9}""")]
    [InlineData("{")]
    public void Import_RejectsBadFiles(string json)
    {
        Assert.Throws<FormatException>(() => GameShare.Parse(json));
    }
}

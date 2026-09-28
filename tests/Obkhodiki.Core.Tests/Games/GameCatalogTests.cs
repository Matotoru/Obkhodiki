using Obkhodiki.Core.Games;
using Obkhodiki.Core.Vpn;

namespace Obkhodiki.Core.Tests.Games;

public class GameCatalogTests
{
    [Fact]
    public void Entries_AreUsableProfiles()
    {
        Assert.Equal(GameCatalog.Entries.Count, GameCatalog.Entries.Select(e => e.Id).Distinct().Count());
        foreach (var e in GameCatalog.Entries)
        {
            Assert.True(GameProfiles.IsValidId(e.Id), e.Id);
            Assert.NotEmpty(e.ProcessNames);
            Assert.All(e.ProcessNames, p => Assert.True(SingBoxConfig.IsValidProcessName(p), p));
            PortSet.Parse(e.TcpPorts);
            PortSet.Parse(e.UdpPorts);
            // A game with its own network must come with ports, or the profile would have no rule.
            if (!e.NeedsRecording) Assert.False(PortSet.Parse(e.UdpPorts).IsEmpty && PortSet.Parse(e.TcpPorts).IsEmpty, e.Id);
            Assert.Matches("^#[0-9A-F]{6}$", e.Colors.From);
            Assert.Matches("^#[0-9A-F]{6}$", e.Colors.To);
        }
    }

    [Fact]
    public void For_LinksHandMadeProfileByExecutable()
    {
        var profile = new GameProfile { Id = "wardogs-2", ProcessName = "wardogsclient-win64-shipping.exe" };

        Assert.Equal("wardogs", GameCatalog.For(profile)?.Id);
        Assert.Null(GameCatalog.For(new GameProfile { Id = "mygame", ProcessName = "my.exe" }));
    }

    [Fact]
    public void ParsePrefixes_KeepsOnlyCidrs()
    {
        var json = """{"data":{"prefixes":[{"prefix":"155.133.230.0/24"},{"prefix":"2a01:bc80::/32"},{"prefix":"garbage"},{"prefix":"10.0.0.1"},{"x":1},{"prefix":"155.133.230.0/24"}]}}""";

        Assert.Equal(new[] { "155.133.230.0/24", "2a01:bc80::/32" }, GameCatalog.ParsePrefixes(json));
    }

    [Fact]
    public void ParsePrefixes_UnexpectedShape_Throws()
    {
        Assert.Throws<FormatException>(() => GameCatalog.ParsePrefixes("""{"status":"error"}"""));
    }
}

public class GameCoverTests
{
    [Fact]
    public void HeaderImage_FromStoreAnswer_OnlySteamCdn()
    {
        var ok = """{"1867240":{"success":true,"data":{"header_image":"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1867240/59d4/header.jpg?t=1"}}}""";
        var evil = """{"1867240":{"success":true,"data":{"header_image":"https://evil.example.com/header.jpg"}}}""";

        Assert.Equal("https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1867240/59d4/header.jpg", GameCatalog.ParseHeaderImage(ok, 1867240));
        Assert.Null(GameCatalog.ParseHeaderImage(evil, 1867240));
        Assert.Null(GameCatalog.ParseHeaderImage("""{"1867240":{"success":false}}""", 1867240));
    }

    [Fact]
    public void CoverUrls_TryFastlyCopyFirst()
    {
        var urls = GameCatalog.CoverUrls(1867240, "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/1867240/59d4/header.jpg");

        Assert.Equal("https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/1867240/59d4/header.jpg", urls[0]);
        Assert.All(urls, u => Assert.Contains(".steamstatic.com/", u));
    }
}

public class SdrConfigTests
{
    [Fact]
    public void Parse_RelaysAndPortUnion()
    {
        var json = """
        {"revision":1,"pops":{
          "ams":{"desc":"Amsterdam","relays":[{"ipv4":"155.133.248.36","port_range":[27015,27060]},{"ipv4":"155.133.248.37","port_range":[27015,27060]}]},
          "sto":{"desc":"Stockholm","relays":[{"ipv4":"162.254.197.36","port_range":[27015,27140]},{"ipv4":"bad","port_range":[1,2]},{"ipv4":"10.0.0.0/8","port_range":[1,2]},{"ipv4":"1.2.3.4","port_range":[70000,1]}]},
          "alias":{"desc":"no relays"}}}
        """;

        var (addresses, ports) = GameCatalog.ParseSdrConfig(json);

        Assert.Equal(new[] { "155.133.248.36/32", "155.133.248.37/32", "162.254.197.36/32" }, addresses);
        Assert.Equal("27015-27140", ports);
    }

    [Fact]
    public void Parse_NoRelays_Throws()
    {
        Assert.Throws<FormatException>(() => GameCatalog.ParseSdrConfig("""{"pops":{}}"""));
        Assert.Throws<FormatException>(() => GameCatalog.ParseSdrConfig("""{"success":false}"""));
    }

    [Fact]
    public void ValveGamesAndWardogs_UseSdr()
    {
        Assert.All(new[] { "wardogs", "cs2", "dota2", "deadlock" }, id => Assert.True(GameCatalog.Find(id)!.UsesSdr, id));
        Assert.False(GameCatalog.Find("valorant")!.UsesSdr);
    }
}

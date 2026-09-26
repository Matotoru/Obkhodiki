using ZapretHub.Core.Games;

namespace ZapretHub.Core.Tests.Games;

public class GameProfilesTests
{
    [Theory]
    [InlineData("War Dogs", "war-dogs")]
    [InlineData("  CS2!! ", "cs2")]
    [InlineData("Танки", "tanki")]
    [InlineData("🎮", "game")]
    public void MakeId_ProducesFileSafeSlug(string name, string expected)
    {
        Assert.Equal(expected, GameProfiles.MakeId(name, Array.Empty<string>()));
    }

    [Fact]
    public void MakeId_VeryLongName_StaysValidEvenWithCollisionSuffix()
    {
        var longName = new string('a', 100);
        var taken = Enumerable.Range(1, 12).Select(i => i == 1 ? new string('a', 30) : $"{new string('a', 30)}-{i}").ToList();

        var id = GameProfiles.MakeId(longName, taken);

        Assert.True(GameProfiles.IsValidId(id), id);
    }

    [Fact]
    public void MakeId_Collision_AppendsNumber()
    {
        Assert.Equal("war-dogs-3", GameProfiles.MakeId("War Dogs", new[] { "war-dogs", "war-dogs-2" }));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData("UPPER")]
    public void IpsetFileName_InvalidId_Throws(string id)
    {
        Assert.Throws<ArgumentException>(() => GameProfiles.IpsetFileName(id));
    }

    [Fact]
    public void IpsetFileName_ValidId()
    {
        Assert.Equal("ipset-game-war-dogs.txt", GameProfiles.IpsetFileName("war-dogs"));
    }

    // Window titles are controlled by the game process; a newline must not inject an address line.
    [Fact]
    public void SafeComment_StripsLineBreaksAndControlCharsAndCapsLength()
    {
        var comment = GameProfiles.SafeComment("War Dogs\r\n0.0.0.0/0\t" + new string('x', 100));

        Assert.DoesNotContain('\n', comment);
        Assert.DoesNotContain('\r', comment);
        Assert.StartsWith("War Dogs0.0.0.0/0", comment);
        Assert.Equal(60, comment.Length);
    }

    [Fact]
    public void Sanitize_DropsInvalidAndDuplicateIdsDisablesBadPorts()
    {
        var profiles = new GameProfile?[]
        {
            new() { Id = "war-dogs", Name = "War Dogs", Enabled = true, UdpPorts = "7777, 7778" },
            new() { Id = "war-dogs", Name = "dup", Enabled = true },
            new() { Id = "../evil", Enabled = true },
            new() { Id = "broken", Enabled = true, TcpPorts = "99999" },
            null,
        };

        var result = GameProfiles.Sanitize(profiles);

        Assert.Equal(new[] { "war-dogs", "broken" }, result.Select(p => p.Id));
        Assert.Equal("7777-7778", result[0].UdpPorts);
        Assert.True(result[0].Enabled);
        Assert.False(result[1].Enabled);
        Assert.Equal("", result[1].TcpPorts);
        Assert.Equal("", result[1].UdpPorts);
    }

    [Fact]
    public void Sanitize_NormalizesTcpPortsAndFillsMissingName()
    {
        var result = GameProfiles.Sanitize(new[] { new GameProfile { Id = "cs2", Name = " ", TcpPorts = "443, 442" } });

        Assert.Equal("442-443", result[0].TcpPorts);
        Assert.Equal("cs2", result[0].Name);
    }
}

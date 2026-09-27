using Obkhodiki.Core.Games;

namespace Obkhodiki.Core.Tests.Games;

public sealed class GameRuleCompilerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "zh-rules-" + Guid.NewGuid().ToString("N"));
    private string Games => Path.Combine(_root, "games");
    private string Runtime => Path.Combine(_root, "runtime");

    public GameRuleCompilerTests() => Directory.CreateDirectory(Games);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static GameProfile Profile(string id = "war-dogs", bool enabled = true, string tcp = "443", string udp = "7777-7800") =>
        new() { Id = id, Name = id, Enabled = enabled, TcpPorts = tcp, UdpPorts = udp };

    private void WriteIpset(string id, params string[] lines) =>
        File.WriteAllLines(Path.Combine(Games, GameProfiles.IpsetFileName(id)), lines);

    [Fact]
    public void EnabledProfile_GetsRuleOnNormalizedRuntimeCopy()
    {
        WriteIpset("war-dogs", "# War Dogs", "3.120.0.0/14", "garbage", "95.1.2.3");

        var compiled = GameRuleCompiler.Compile(new[] { Profile() }, Games, Runtime);

        var rule = Assert.Single(compiled.Rules);
        Assert.StartsWith(Runtime, rule.IpsetPath);
        Assert.Equal(new[] { "3.120.0.0/14", "95.1.2.3/32" }, File.ReadAllLines(rule.IpsetPath));
        Assert.Equal("443", rule.Tcp.ToString());
        Assert.Equal("7777-7800", rule.Udp.ToString());
    }

    // The safety invariant: an empty ipset means "every address" to winws.
    [Theory]
    [InlineData]
    [InlineData("")]
    [InlineData("# only a comment")]
    [InlineData("0.0.0.0/0")]
    [InlineData("not an address")]
    public void ProfileWithoutValidAddresses_ProducesNoRule(params string[] lines)
    {
        WriteIpset("war-dogs", lines);

        var compiled = GameRuleCompiler.Compile(new[] { Profile() }, Games, Runtime);

        Assert.Empty(compiled.Rules);
        Assert.Contains(compiled.Skipped, s => s.StartsWith("war-dogs"));
    }

    [Fact]
    public void MissingFile_ProducesNoRule()
    {
        Assert.Empty(GameRuleCompiler.Compile(new[] { Profile() }, Games, Runtime).Rules);
    }

    [Fact]
    public void DisabledProfile_Ignored()
    {
        WriteIpset("war-dogs", "3.120.0.0/14");

        var compiled = GameRuleCompiler.Compile(new[] { Profile(enabled: false) }, Games, Runtime);

        Assert.Empty(compiled.Rules);
        Assert.Empty(compiled.Skipped);
    }

    [Fact]
    public void ProfileWithoutPorts_ProducesNoRule()
    {
        WriteIpset("war-dogs", "3.120.0.0/14");

        Assert.Empty(GameRuleCompiler.Compile(new[] { Profile(tcp: "", udp: "") }, Games, Runtime).Rules);
    }

    [Fact]
    public void UnreadableProfile_SkippedOthersStillCompiled()
    {
        WriteIpset("locked", "3.120.0.0/14");
        WriteIpset("ok", "95.1.2.0/24");
        using var _ = new FileStream(Path.Combine(Games, GameProfiles.IpsetFileName("locked")), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var compiled = GameRuleCompiler.Compile(new[] { Profile("locked"), Profile("ok") }, Games, Runtime);

        Assert.Single(compiled.Rules);
        Assert.Contains(compiled.Skipped, s => s.StartsWith("locked"));
    }

    [Fact]
    public void BadPortsOrId_SkippedNotThrown()
    {
        WriteIpset("ok", "95.1.2.0/24");

        var compiled = GameRuleCompiler.Compile(new[] { Profile("ok", tcp: "99999"), Profile("../evil") }, Games, Runtime);

        Assert.Empty(compiled.Rules);
        Assert.Equal(2, compiled.Skipped.Count);
    }

    // Editing the source afterwards must not affect what winws is already reading.
    [Fact]
    public void EditingSourceAfterCompile_DoesNotChangeRuntimeCopy()
    {
        WriteIpset("war-dogs", "3.120.0.0/14");
        var rule = GameRuleCompiler.Compile(new[] { Profile() }, Games, Runtime).Rules.Single();

        WriteIpset("war-dogs");

        Assert.Equal(new[] { "3.120.0.0/14" }, File.ReadAllLines(rule.IpsetPath));
    }
}

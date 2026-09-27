using Obkhodiki.Core.Testing;

namespace Obkhodiki.Core.Tests.Testing;

public class TargetsFileTests
{
    [Fact]
    public void Parse_FlowsealTargets_ReturnsHttpsTargetsAndSkipsPingOnly()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "targets.txt"));

        var targets = TargetsFile.Parse(text);

        Assert.Contains(targets, t => t.Name == "DiscordMain" && t.Url == new Uri("https://discord.com"));
        Assert.Contains(targets, t => t.Name == "YouTubeWeb");
        Assert.DoesNotContain(targets, t => t.Name.StartsWith("GoogleDNS"));
    }

    [Fact]
    public void Parse_IgnoresCommentsBlankAndMalformedLines()
    {
        const string text = "# c\n\n  ### section\nGood = \"https://a.test\"\nnot a target\nBad = \"ftp://x\"\nNoQuotes = https://b.test\nTwo Words = \"https://c.test\"\n";

        var targets = TargetsFile.Parse(text);

        Assert.Equal(new[] { "Good", "NoQuotes" }, targets.Select(t => t.Name));
    }
}

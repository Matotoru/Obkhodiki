using Obkhodiki.Core.Strategies;

namespace Obkhodiki.Core.Tests.Strategies;

// Regression net over every strategy shipped by the Flowseal release the fixtures were taken from.
public class AllFlowsealStrategiesTests
{
    private static readonly EnginePaths Paths = new(@"C:\eng\bin", @"C:\eng\lists", @"C:\data\lists");

    public static IEnumerable<object[]> BatFiles() =>
        Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures"), "general*.bat")
            .Select(f => new object[] { Path.GetFileName(f) });

    public static IEnumerable<object[]> BatFilesByMode() =>
        from f in BatFiles()
        from m in Enum.GetValues<GameFilterMode>()
        select new[] { f[0], m };

    [Theory]
    [MemberData(nameof(BatFilesByMode))]
    public void EveryStrategy_ParsesAndResolvesWithoutLeftoverPlaceholders(string file, GameFilterMode mode)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        var strategy = BatStrategyParser.Parse(BatStrategyParser.NameFromFileName(file), text);

        var args = StrategyArgsBuilder.Build(strategy, Paths, new GameFilterOptions(mode));

        Assert.All(args, a => Assert.DoesNotContain("%", a));
        Assert.All(args, a => Assert.DoesNotContain("^", a));
        Assert.All(args, a => Assert.StartsWith("--", a));
        var expectedTcp = mode is GameFilterMode.Tcp or GameFilterMode.All ? "1024-65535" : "12";
        Assert.Contains(args, a => a.StartsWith("--wf-tcp=") && a.EndsWith("," + expectedTcp));

        // User lists must come from the user folder (survives updates), engine lists from the engine folder.
        Assert.All(args.Where(a => a.Contains("-user.txt")), a => Assert.Contains(@"C:\data\lists\", a));
        Assert.All(args.Where(a => a.Contains(@"C:\eng\lists\")), a => Assert.DoesNotContain("-user.txt", a));
    }

    [Fact]
    public void FixtureSetIsComplete()
    {
        Assert.True(BatFiles().Count() >= 20);
    }
}

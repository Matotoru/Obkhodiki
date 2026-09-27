using Obkhodiki.Core.Games;
using Obkhodiki.Core.Strategies;

namespace Obkhodiki.Core.Tests.Strategies;

public class GameRulesArgsTests
{
    private static readonly EnginePaths Paths = new(@"C:\eng\bin", @"C:\eng\lists", @"C:\data\lists");
    private const string GameIpset = @"C:\data\lists\ipset-game-wardogs.txt";

    // Shape of Flowseal's general.bat: hostlist block, ipset-all TCP block, and the two game-port ipset-all blocks.
    private static readonly StrategyDefinition Strategy = new("s", new[]
    {
        "--wf-tcp=80,443,%GameFilterTCP%", "--wf-udp=443,%GameFilterUDP%",
        "--filter-tcp=443", "--hostlist=%LISTS%list-general.txt", "--dpi-desync=multisplit", "--new",
        "--filter-tcp=80,443", "--ipset=%LISTS%ipset-all.txt", "--ipset-exclude=%LISTS%ipset-exclude.txt", "--dpi-desync=fake", "--new",
        "--filter-tcp=%GameFilterTCP%", "--ipset=%LISTS%ipset-all.txt", "--dpi-desync=multisplit", "--dpi-desync-any-protocol=1", "--new",
        "--filter-udp=%GameFilterUDP%", "--ipset=%LISTS%ipset-all.txt", "--dpi-desync=fake", "--dpi-desync-fake-unknown-udp=%BIN%g.bin",
    });

    private static GameRule Rule(string tcp = "443,7000-7100", string udp = "7777-7800") =>
        new(GameIpset, PortSet.Parse(tcp), PortSet.Parse(udp));

    private static List<string[]> Blocks(IReadOnlyList<string> args)
    {
        var blocks = new List<string[]>();
        var current = new List<string>();
        foreach (var a in args)
        {
            if (a == "--new") { blocks.Add(current.ToArray()); current.Clear(); }
            else current.Add(a);
        }
        blocks.Add(current.ToArray());
        return blocks;
    }

    [Fact]
    public void NoRules_OutputUnchanged()
    {
        var plain = StrategyArgsBuilder.Build(Strategy, Paths, GameFilterOptions.Disabled);
        var withEmpty = StrategyArgsBuilder.Build(Strategy, Paths, GameFilterOptions.Disabled, Array.Empty<GameRule>());

        Assert.Equal(plain, withEmpty);
    }

    [Fact]
    public void Rule_ClonesEveryIpsetAllBlockRestrictedToGameIpset()
    {
        var blocks = Blocks(StrategyArgsBuilder.Build(Strategy, Paths, GameFilterOptions.Disabled, new[] { Rule() }));

        Assert.Equal(4 + 3, blocks.Count);
        var added = blocks.Skip(4).ToList();
        Assert.All(added, b => Assert.Contains("--ipset=" + GameIpset, b));
        Assert.All(added, b => Assert.DoesNotContain(b, a => a.Contains("ipset-all.txt")));
    }

    [Fact]
    public void Rule_GamePortBlocksUseRulePortsEvenWhenGlobalGameFilterDisabled()
    {
        var blocks = Blocks(StrategyArgsBuilder.Build(Strategy, Paths, GameFilterOptions.Disabled, new[] { Rule() }));

        Assert.Contains(blocks.Skip(4), b => b.Contains("--filter-tcp=443,7000-7100") && b.Contains("--dpi-desync-any-protocol=1"));
        Assert.Contains(blocks.Skip(4), b => b.Contains("--filter-udp=7777-7800") && b.Contains(@"--dpi-desync-fake-unknown-udp=C:\eng\bin\g.bin"));
        // Global game filter stays inert for everything else.
        Assert.Contains("--filter-tcp=12", blocks[2]);
    }

    [Fact]
    public void Rule_OriginalBlocksKeptUnchangedAndFirst()
    {
        var plain = StrategyArgsBuilder.Build(Strategy, Paths, GameFilterOptions.Disabled);
        var args = StrategyArgsBuilder.Build(Strategy, Paths, GameFilterOptions.Disabled, new[] { Rule() });

        var plainBlocks = Blocks(plain);
        var blocks = Blocks(args);
        for (var i = 1; i < plainBlocks.Count; i++) Assert.Equal(plainBlocks[i], blocks[i]);
    }

    [Fact]
    public void Rule_PortsAddedToWinDivertCaptureFilter()
    {
        var args = StrategyArgsBuilder.Build(Strategy, Paths, GameFilterOptions.Disabled, new[] { Rule() });

        Assert.Equal("--wf-tcp=80,443,12,443,7000-7100", args.Single(a => a.StartsWith("--wf-tcp=")));
        Assert.Equal("--wf-udp=443,12,7777-7800", args.Single(a => a.StartsWith("--wf-udp=")));
    }

    [Fact]
    public void Rule_WithoutUdpPorts_SkipsUdpGameBlockAndLeavesWfUdp()
    {
        var args = StrategyArgsBuilder.Build(Strategy, Paths, GameFilterOptions.Disabled, new[] { Rule(udp: "") });

        Assert.Equal("--wf-udp=443,12", args.Single(a => a.StartsWith("--wf-udp=")));
        Assert.DoesNotContain(Blocks(args).Skip(4), b => b.Any(a => a.StartsWith("--filter-udp=")));
    }

    [Fact]
    public void Rule_StrategyWithoutWfUdp_AddsItAndKeepsSingleWfTcp()
    {
        var tcpOnly = new StrategyDefinition("t", new[] { "--wf-tcp=443", "--filter-udp=%GameFilterUDP%", "--ipset=%LISTS%ipset-all.txt", "--dpi-desync=fake" });

        var args = StrategyArgsBuilder.Build(tcpOnly, Paths, GameFilterOptions.Disabled, new[] { Rule() });

        Assert.Contains("--wf-udp=7777-7800", args);
        // The template block carried --wf-tcp; the clone must not repeat the global option.
        Assert.Equal("--wf-tcp=443,443,7000-7100", Assert.Single(args, a => a.StartsWith("--wf-tcp=")));
    }

    // A UDP-only game must not produce "--filter-tcp=" with no ports.
    [Fact]
    public void Rule_WithoutTcpPorts_SkipsTcpGameBlockAndLeavesWfTcp()
    {
        var args = StrategyArgsBuilder.Build(Strategy, Paths, GameFilterOptions.Disabled, new[] { Rule(tcp: "") });

        Assert.DoesNotContain(args, a => a == "--filter-tcp=");
        Assert.Equal("--wf-tcp=80,443,12", args.Single(a => a.StartsWith("--wf-tcp=")));
        var added = Blocks(args).Skip(4).ToList();
        Assert.DoesNotContain(added, b => b.Contains("--dpi-desync-any-protocol=1") && b.Any(a => a.StartsWith("--filter-tcp=")));
        Assert.Contains(added, b => b.Contains("--filter-udp=7777-7800"));
    }

    [Fact]
    public void Rule_LegacyGameFilterWithUdpOnlyRule_UsesUdpPortsNotEmpty()
    {
        var legacy = new StrategyDefinition("l", new[] { "--wf-udp=443", "--filter-udp=%GameFilter%", "--ipset=%LISTS%ipset-all.txt", "--dpi-desync=fake" });

        var args = StrategyArgsBuilder.Build(legacy, Paths, GameFilterOptions.Disabled, new[] { Rule(tcp: "") });

        Assert.Contains("--filter-udp=7777-7800", args);
        Assert.DoesNotContain(args, a => a.EndsWith('='));
    }

    [Fact]
    public void Rule_LegacyGameFilterPlaceholder_UsesRulePortsNotInertPort()
    {
        var legacy = new StrategyDefinition("l", new[] { "--wf-tcp=443", "--filter-tcp=%GameFilter%", "--ipset=%LISTS%ipset-all.txt", "--dpi-desync=fake" });

        var blocks = Blocks(StrategyArgsBuilder.Build(legacy, Paths, GameFilterOptions.Disabled, new[] { Rule() }));

        Assert.Contains("--filter-tcp=12", blocks[0]);
        Assert.Contains("--filter-tcp=443,7000-7100", blocks[1]);
    }

    [Fact]
    public void Rule_StrategyWithoutIpsetBlocks_UsesBuiltInFallbackBlocks()
    {
        var hostlistOnly = new StrategyDefinition("h", new[] { "--wf-tcp=443", "--filter-tcp=443", "--hostlist=%LISTS%list-general.txt", "--dpi-desync=fake" });

        var blocks = Blocks(StrategyArgsBuilder.Build(hostlistOnly, Paths, GameFilterOptions.Disabled, new[] { Rule() }));

        Assert.Contains(blocks, b => b.Contains("--filter-tcp=443,7000-7100") && b.Contains("--ipset=" + GameIpset));
        Assert.Contains(blocks, b => b.Contains("--filter-udp=7777-7800") && b.Contains("--ipset=" + GameIpset));
    }

    [Fact]
    public void TwoRules_EachGetsOwnBlocksAndPortsAreUnioned()
    {
        var other = new GameRule(@"C:\data\lists\ipset-game-other.txt", PortSet.Parse("9000"), PortSet.Parse("7790-7900"));

        var args = StrategyArgsBuilder.Build(Strategy, Paths, GameFilterOptions.Disabled, new[] { Rule(), other });

        Assert.Equal("--wf-udp=443,12,7777-7900", args.Single(a => a.StartsWith("--wf-udp=")));
        Assert.Contains(Blocks(args), b => b.Contains(@"--ipset=C:\data\lists\ipset-game-other.txt"));
    }

    [Theory]
    [MemberData(nameof(AllFlowsealStrategiesTests.BatFiles), MemberType = typeof(AllFlowsealStrategiesTests))]
    public void EveryRealStrategy_ProducesGameBlocksWithoutLeftoverPlaceholders(string file)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file));
        var strategy = BatStrategyParser.Parse(file, text);

        var args = StrategyArgsBuilder.Build(strategy, Paths, GameFilterOptions.Disabled, new[] { Rule() });

        Assert.All(args, a => Assert.DoesNotContain("%", a));
        Assert.Contains(args, a => a == "--ipset=" + GameIpset);
        Assert.Contains(args, a => a == "--filter-udp=7777-7800");
    }
}

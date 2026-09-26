using ZapretHub.Core.Strategies;

namespace ZapretHub.Core.Tests.Strategies;

public class StrategyArgsBuilderTests
{
    private static readonly EnginePaths Paths = new(@"C:\eng\bin", @"C:\eng\lists", @"C:\data\lists");

    private static StrategyDefinition Strategy(params string[] args) => new("s", args);

    [Fact]
    public void Build_ReplacesBinAndListsWithDirectoriesEndingInSeparator()
    {
        var args = StrategyArgsBuilder.Build(
            Strategy("--fake=%BIN%a.bin", "--hostlist=%LISTS%list-general.txt"),
            Paths, GameFilterOptions.Disabled);

        Assert.Equal(new[] { @"--fake=C:\eng\bin\a.bin", @"--hostlist=C:\eng\lists\list-general.txt" }, args);
    }

    [Fact]
    public void Build_UserListFiles_ResolveToUserDirectory()
    {
        var args = StrategyArgsBuilder.Build(
            Strategy("--hostlist=%LISTS%list-general-user.txt", "--ipset-exclude=%LISTS%ipset-exclude-user.txt"),
            Paths, GameFilterOptions.Disabled);

        Assert.Equal(new[]
        {
            @"--hostlist=C:\data\lists\list-general-user.txt",
            @"--ipset-exclude=C:\data\lists\ipset-exclude-user.txt",
        }, args);
    }

    [Fact]
    public void Build_CommaJoinedLists_EachRoutedByItsOwnName()
    {
        var args = StrategyArgsBuilder.Build(
            Strategy("--hostlist=%LISTS%list-general.txt,%LISTS%list-general-user.txt"),
            Paths, GameFilterOptions.Disabled);

        Assert.Equal(new[] { @"--hostlist=C:\eng\lists\list-general.txt,C:\data\lists\list-general-user.txt" }, args);
    }

    [Fact]
    public void Build_GameFilterDisabled_UsesDummyPortLikeFlowseal()
    {
        var args = StrategyArgsBuilder.Build(
            Strategy("--wf-tcp=80,443,%GameFilterTCP%", "--filter-udp=%GameFilterUDP%"),
            Paths, GameFilterOptions.Disabled);

        Assert.Equal(new[] { "--wf-tcp=80,443,12", "--filter-udp=12" }, args);
    }

    [Theory]
    [InlineData(GameFilterMode.All, "1024-65535", "1024-65535")]
    [InlineData(GameFilterMode.Tcp, "1024-65535", "12")]
    [InlineData(GameFilterMode.Udp, "12", "1024-65535")]
    public void Build_GameFilterModes_SetOnlySelectedProtocols(GameFilterMode mode, string tcp, string udp)
    {
        var args = StrategyArgsBuilder.Build(
            Strategy("--filter-tcp=%GameFilterTCP%", "--filter-udp=%GameFilterUDP%"),
            Paths, new GameFilterOptions(mode));

        Assert.Equal(new[] { $"--filter-tcp={tcp}", $"--filter-udp={udp}" }, args);
    }

    [Fact]
    public void Build_CustomGameRanges_AreUsed()
    {
        var args = StrategyArgsBuilder.Build(
            Strategy("--filter-tcp=%GameFilterTCP%", "--filter-udp=%GameFilterUDP%"),
            Paths, new GameFilterOptions(GameFilterMode.All, "27000-27100", "7000-8000"));

        Assert.Equal(new[] { "--filter-tcp=27000-27100", "--filter-udp=7000-8000" }, args);
    }

    [Theory]
    [InlineData(GameFilterMode.Disabled, "12")]
    [InlineData(GameFilterMode.Tcp, "27000-27100")]
    [InlineData(GameFilterMode.Udp, "7000-8000")]
    [InlineData(GameFilterMode.All, "27000-27100")]
    public void Build_LegacyGameFilterPlaceholder_FollowsFlowsealRules(GameFilterMode mode, string expected)
    {
        var args = StrategyArgsBuilder.Build(
            Strategy("--wf-tcp=80,%GameFilter%"), Paths, new GameFilterOptions(mode, "27000-27100", "7000-8000"));

        Assert.Equal(new[] { $"--wf-tcp=80,{expected}" }, args);
    }

    [Fact]
    public void Build_PathsWithSpacesAndCyrillic_KeptVerbatimInsideSingleArgument()
    {
        var paths = new EnginePaths(@"C:\Мои программы\zapret hub\bin", @"C:\x\lists", @"C:\Users\Иван\lists");

        var args = StrategyArgsBuilder.Build(
            Strategy("--fake=%BIN%a.bin", "--hostlist=%LISTS%list-general-user.txt"), paths, GameFilterOptions.Disabled);

        Assert.Equal(new[] { @"--fake=C:\Мои программы\zapret hub\bin\a.bin", @"--hostlist=C:\Users\Иван\lists\list-general-user.txt" }, args);
    }

    [Theory]
    [InlineData("1-65535")]
    [InlineData("1024-1024")]
    [InlineData("27015-27030")]
    public void GameFilterOptions_ValidRange_Accepted(string range)
    {
        var options = new GameFilterOptions(GameFilterMode.All, range, range);

        Assert.Equal(range, options.TcpRange);
    }

    [Fact]
    public void Build_PlaceholdersAreCaseInsensitive()
    {
        var args = StrategyArgsBuilder.Build(Strategy("--x=%bin%a.bin"), Paths, GameFilterOptions.Disabled);

        Assert.Equal(new[] { @"--x=C:\eng\bin\a.bin" }, args);
    }

    [Fact]
    public void Build_UnknownPlaceholder_ThrowsSoFormatChangesAreNoticed()
    {
        var ex = Assert.Throws<FormatException>(() =>
            StrategyArgsBuilder.Build(Strategy("--x=%NEWVAR%"), Paths, GameFilterOptions.Disabled));

        Assert.Contains("NEWVAR", ex.Message);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("100-50")]
    [InlineData("0-10")]
    [InlineData("1-70000")]
    public void GameFilterOptions_InvalidRange_Throws(string range)
    {
        Assert.Throws<ArgumentException>(() => new GameFilterOptions(GameFilterMode.All, range, "1024-65535"));
    }
}

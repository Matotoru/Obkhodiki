using ZapretHub.Core.Strategies;

namespace ZapretHub.Core.Tests.Strategies;

public class BatStrategyParserTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Theory]
    [InlineData("general.bat")]
    [InlineData("general (ALT).bat")]
    [InlineData("general (FAKE TLS AUTO).bat")]
    public void Parse_RealFlowsealStrategy_ExtractsWinwsArgumentsOnly(string file)
    {
        var strategy = BatStrategyParser.Parse(BatStrategyParser.NameFromFileName(file), Fixture(file));

        Assert.StartsWith("--wf-tcp=", strategy.Args[0]);
        Assert.All(strategy.Args, a => Assert.DoesNotContain("^", a));
        Assert.All(strategy.Args, a => Assert.DoesNotContain("\"", a));
        Assert.DoesNotContain(strategy.Args, a => a.Contains("winws.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("--new", strategy.Args);
    }

    [Fact]
    public void Parse_GeneralBat_ReadsEveryContinuationLine()
    {
        var strategy = BatStrategyParser.Parse("general", Fixture("general.bat"));

        Assert.Equal(8, strategy.Args.Count(a => a == "--new"));
        Assert.Equal("--dpi-desync-cutoff=n2", strategy.Args[^1]);
    }

    [Fact]
    public void Parse_LfLineEndingsAndSpacesAfterCaret_SameResultAsCrlf()
    {
        var crlf = Fixture("general.bat").Replace("\r\n", "\n").Replace("\n", "\r\n");
        var lfWithSpaces = crlf.Replace("^\r\n", "^  \n");

        Assert.Equal(
            BatStrategyParser.Parse("a", crlf).Args,
            BatStrategyParser.Parse("b", lfWithSpaces).Args);
    }

    [Fact]
    public void Parse_GeneralBat_KeepsQuotedPathAsSingleArgumentWithoutQuotes()
    {
        var strategy = BatStrategyParser.Parse("general", Fixture("general.bat"));

        Assert.Contains("--hostlist=%LISTS%list-general.txt", strategy.Args);
        Assert.Contains("--dpi-desync-fake-quic=%BIN%quic_initial_www_google_com.bin", strategy.Args);
        Assert.Equal("--filter-udp=%GameFilterUDP%", strategy.Args.First(a => a.StartsWith("--filter-udp=%Game")));
    }

    [Fact]
    public void Parse_QuotedValueWithSpaces_StaysOneArgument()
    {
        const string bat = "start \"x\" /min \"%BIN%winws.exe\" --a=\"%LISTS%my list.txt\" --b=1";

        var strategy = BatStrategyParser.Parse("s", bat);

        Assert.Equal(new[] { "--a=%LISTS%my list.txt", "--b=1" }, strategy.Args);
    }

    [Fact]
    public void Parse_ContinuationLines_AreJoined()
    {
        const string bat = "start \"x\" /min \"%BIN%winws.exe\" --a=1 ^\r\n--b=2 ^\r\n--c=3\r\necho done";

        var strategy = BatStrategyParser.Parse("s", bat);

        Assert.Equal(new[] { "--a=1", "--b=2", "--c=3" }, strategy.Args);
    }

    [Fact]
    public void Parse_CmdCaretEscape_BecomesLiteralCharacter()
    {
        const string bat = "\"%BIN%winws.exe\" --dpi-desync-fake-tls=^! --x=\"a^b\"";

        var strategy = BatStrategyParser.Parse("s", bat);

        Assert.Equal(new[] { "--dpi-desync-fake-tls=!", "--x=a^b" }, strategy.Args);
    }

    [Fact]
    public void Parse_FakeTlsAuto_UnescapesDefaultFakeMarker()
    {
        var strategy = BatStrategyParser.Parse("x", Fixture("general (FAKE TLS AUTO).bat"));

        Assert.Contains("--dpi-desync-fake-tls=!", strategy.Args);
    }

    [Fact]
    public void Parse_TwoWinwsCommands_AmbiguousThrows()
    {
        const string bat = "if exist x \"%BIN%winws.exe\" --check\r\nstart \"z\" \"%BIN%winws.exe\" --real=1";

        Assert.Throws<FormatException>(() => BatStrategyParser.Parse("s", bat));
    }

    [Fact]
    public void Parse_NoWinwsCommand_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => BatStrategyParser.Parse("s", "@echo off\r\necho hi"));
    }

    [Fact]
    public void Parse_WinwsWithoutArguments_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => BatStrategyParser.Parse("s", "start \"x\" \"%BIN%winws.exe\"   \r\n"));
    }

    [Fact]
    public void Parse_UnbalancedQuote_ThrowsFormatException()
    {
        Assert.Throws<FormatException>(() => BatStrategyParser.Parse("s", "\"%BIN%winws.exe\" --a=\"broken"));
    }

    [Theory]
    [InlineData("general.bat", "general")]
    [InlineData("general (ALT11).bat", "general (ALT11)")]
    [InlineData(@"C:\x\general (EXP).bat", "general (EXP)")]
    public void NameFromFileName_StripsDirectoryAndExtension(string file, string expected)
    {
        Assert.Equal(expected, BatStrategyParser.NameFromFileName(file));
    }
}

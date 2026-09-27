using Obkhodiki.Core.Games;

namespace Obkhodiki.Core.Tests.Games;

public class PortSetTests
{
    [Theory]
    [InlineData("443", "443")]
    [InlineData("7000-7100,443", "443,7000-7100")]
    [InlineData("80, 81,82 ,90", "80-82,90")]
    [InlineData("1000-2000,1500-2500", "1000-2500")]
    [InlineData("", "")]
    public void Parse_NormalizesToSortedMergedRanges(string input, string expected)
    {
        Assert.Equal(expected, PortSet.Parse(input).ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("200-100")]
    [InlineData("abc")]
    [InlineData("1-2-3")]
    public void Parse_Invalid_Throws(string input)
    {
        Assert.Throws<FormatException>(() => PortSet.Parse(input));
    }

    [Fact]
    public void FromPorts_CollapsesConsecutivePorts()
    {
        Assert.Equal("443,7777-7779,27015", PortSet.FromPorts(new[] { 7778, 27015, 443, 7777, 7779, 443 }).ToString());
    }

    [Fact]
    public void Union_MergesBothSets()
    {
        var union = PortSet.Parse("443,7000-7010").Union(PortSet.Parse("7005-7020,9000"));

        Assert.Equal("443,7000-7020,9000", union.ToString());
    }

    [Fact]
    public void IsEmpty_OnlyForNoPorts()
    {
        Assert.True(PortSet.Parse("").IsEmpty);
        Assert.False(PortSet.Parse("1").IsEmpty);
    }
}

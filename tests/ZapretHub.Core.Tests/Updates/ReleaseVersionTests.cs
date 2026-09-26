using ZapretHub.Core.Updates;

namespace ZapretHub.Core.Tests.Updates;

public class ReleaseVersionTests
{
    [Theory]
    [InlineData("1.10.3", "1.9.9")]
    [InlineData("1.10.10", "1.10.9")]
    [InlineData("2.0", "1.99.99")]
    [InlineData("1.10.3.1", "1.10.3")]
    public void IsNewer_HigherNumericVersion_ReturnsTrue(string candidate, string current)
    {
        Assert.True(ReleaseVersion.IsNewer(candidate, current));
        Assert.False(ReleaseVersion.IsNewer(current, candidate));
    }

    [Theory]
    [InlineData("1.10.3", "1.10.3")]
    [InlineData("1.10.3", "1.10.3.")]
    [InlineData("v1.10.3", "1.10.3")]
    public void IsNewer_SameVersionInDifferentSpelling_ReturnsFalse(string candidate, string current)
    {
        Assert.False(ReleaseVersion.IsNewer(candidate, current));
    }

    [Fact]
    public void IsNewer_NothingInstalled_ReturnsTrue()
    {
        Assert.True(ReleaseVersion.IsNewer("1.0.0", null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.x.3")]
    public void Normalize_Garbage_Throws(string version)
    {
        Assert.Throws<FormatException>(() => ReleaseVersion.Normalize(version));
    }

    [Fact]
    public void Normalize_TrimsPrefixAndTrailingDots()
    {
        Assert.Equal("1.10.3", ReleaseVersion.Normalize(" v1.10.3. "));
    }
}

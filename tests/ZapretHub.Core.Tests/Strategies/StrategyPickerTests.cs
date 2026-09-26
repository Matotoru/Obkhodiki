using ZapretHub.Core.Strategies;

namespace ZapretHub.Core.Tests.Strategies;

public class StrategyPickerTests
{
    private static StrategyDefinition S(string n) => new(n, new[] { "--x" });

    [Theory]
    [InlineData("general (ALT)", "general (ALT)")]
    [InlineData("removed in new release", "general")]
    [InlineData(null, "general")]
    public void Pick_SavedOrDefault(string? saved, string expected)
    {
        var picked = StrategyPicker.Pick(new[] { S("general (ALT)"), S("general"), S("general (EXP)") }, saved);

        Assert.Equal(expected, picked.Name);
    }

    [Fact]
    public void Pick_NoDefaultInRelease_FallsBackToFirst()
    {
        Assert.Equal("a", StrategyPicker.Pick(new[] { S("a"), S("b") }, "gone").Name);
    }

    [Fact]
    public void Pick_Empty_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => StrategyPicker.Pick(Array.Empty<StrategyDefinition>(), null));
    }
}

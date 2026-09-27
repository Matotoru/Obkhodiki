namespace Obkhodiki.Core.Strategies;

public static class StrategyPicker
{
    public const string DefaultName = "general";

    /// <summary>Saved choice if it still exists, else Flowseal's default "general", else the first one.
    /// A new Flowseal release may rename or drop strategies.</summary>
    public static StrategyDefinition Pick(IReadOnlyList<StrategyDefinition> strategies, string? saved)
    {
        if (strategies.Count == 0) throw new InvalidOperationException("No strategies available.");
        return strategies.FirstOrDefault(s => s.Name == saved)
               ?? strategies.FirstOrDefault(s => s.Name == DefaultName)
               ?? strategies[0];
    }
}

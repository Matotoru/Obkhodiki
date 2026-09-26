using ZapretHub.Core.Engine;
using ZapretHub.Core.Strategies;

namespace ZapretHub.Core.Testing;

public sealed record StrategyScore(StrategyDefinition Strategy, int Passed, int Total, TimeSpan TotalLatency, string? Error);

public sealed record SelectionResult(StrategyDefinition? Best, IReadOnlyList<StrategyScore> Scores);

/// <param name="Completed">Set on the report that finishes a strategy.</param>
public sealed record SelectionProgress(
    int StrategyIndex,
    int StrategyCount,
    StrategyDefinition Strategy,
    int TargetsDone,
    int TargetCount,
    StrategyScore? Completed)
{
    /// <summary>Overall completion in [0, 1], advancing per probed target.</summary>
    public double Fraction
    {
        get
        {
            if (StrategyCount == 0) return 1;
            var within = Completed is not null || TargetCount == 0 ? 1.0 : (double)TargetsDone / TargetCount;
            return (StrategyIndex + within) / StrategyCount;
        }
    }
}

/// <summary>Tries each strategy in turn and picks the one that opens the most targets.</summary>
public sealed class StrategyAutoSelector
{
    private readonly IEngineRunner _runner;
    private readonly IConnectivityProbe _probe;
    private readonly Func<StrategyDefinition, IReadOnlyList<string>> _buildArgs;
    private readonly TimeSpan _settleDelay;

    public StrategyAutoSelector(
        IEngineRunner runner,
        IConnectivityProbe probe,
        Func<StrategyDefinition, IReadOnlyList<string>> buildArgs,
        TimeSpan settleDelay)
    {
        _runner = runner;
        _probe = probe;
        _buildArgs = buildArgs;
        _settleDelay = settleDelay;
    }

    public async Task<SelectionResult> SelectAsync(
        IReadOnlyList<StrategyDefinition> strategies,
        IReadOnlyList<ProbeTarget> targets,
        IProgress<SelectionProgress>? progress,
        CancellationToken ct)
    {
        var scores = new List<StrategyScore>();
        for (var i = 0; i < strategies.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var index = i;
            var strategy = strategies[i];
            progress?.Report(new SelectionProgress(index, strategies.Count, strategy, 0, targets.Count, null));

            var score = await TryStrategyAsync(
                strategy,
                targets,
                done => progress?.Report(new SelectionProgress(index, strategies.Count, strategy, done, targets.Count, null)),
                ct).ConfigureAwait(false);
            scores.Add(score);
            progress?.Report(new SelectionProgress(index, strategies.Count, strategy, targets.Count, targets.Count, score));
        }

        var best = scores
            .Where(s => s.Passed > 0)
            .OrderByDescending(s => s.Passed)
            .ThenBy(s => s.TotalLatency)
            .FirstOrDefault();
        return new SelectionResult(best?.Strategy, scores);
    }

    private async Task<StrategyScore> TryStrategyAsync(
        StrategyDefinition strategy, IReadOnlyList<ProbeTarget> targets, Action<int> targetDone, CancellationToken ct)
    {
        try
        {
            await _runner.StartAsync(_buildArgs(strategy), ct).ConfigureAwait(false);
            if (_settleDelay > TimeSpan.Zero) await Task.Delay(_settleDelay, ct).ConfigureAwait(false);

            // Sequential on purpose: parallel requests through one DPI path skew latency and trip rate limits.
            var passed = 0;
            var latency = TimeSpan.Zero;
            for (var t = 0; t < targets.Count; t++)
            {
                var result = await _probe.ProbeAsync(targets[t], ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (result.Ok)
                {
                    passed++;
                    latency += result.Latency;
                }
                targetDone(t + 1);
            }
            return new StrategyScore(strategy, passed, targets.Count, latency, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new StrategyScore(strategy, 0, targets.Count, TimeSpan.Zero, ex.Message);
        }
        finally
        {
            await _runner.StopAsync().ConfigureAwait(false);
        }
    }
}

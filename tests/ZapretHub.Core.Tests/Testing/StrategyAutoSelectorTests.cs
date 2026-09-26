using ZapretHub.Core.Engine;
using ZapretHub.Core.Strategies;
using ZapretHub.Core.Testing;

namespace ZapretHub.Core.Tests.Testing;

public class StrategyAutoSelectorTests
{
    private static readonly ProbeTarget[] Targets =
    {
        new("YouTube", new Uri("https://www.youtube.com")),
        new("Discord", new Uri("https://discord.com")),
        new("WarDogs", new Uri("https://example.test")),
    };

    private sealed class FakeRunner : IEngineRunner
    {
        public List<string> Log { get; } = new();
        public string? Current { get; private set; }
        public bool FailOnStart { get; init; }
        public HashSet<string> FailingStrategies { get; } = new();

        public Task StartAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            if (FailOnStart || FailingStrategies.Contains(args[0]))
            {
                Log.Add("failed " + args[0]);
                throw new InvalidOperationException("winws crashed");
            }
            Current = args[0];
            Log.Add("start " + Current);
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            Log.Add("stop " + Current);
            Current = null;
            return Task.CompletedTask;
        }
    }

    // Answers based on which strategy is currently running in the runner.
    private sealed class FakeProbe : IConnectivityProbe
    {
        private readonly FakeRunner _runner;
        private readonly Dictionary<string, Func<ProbeTarget, ProbeResult>> _byStrategy;

        public FakeProbe(FakeRunner runner, Dictionary<string, Func<ProbeTarget, ProbeResult>> byStrategy)
        {
            _runner = runner;
            _byStrategy = byStrategy;
        }

        public Task<ProbeResult> ProbeAsync(ProbeTarget target, CancellationToken ct) =>
            Task.FromResult(_byStrategy[_runner.Current!](target));
    }

    private static StrategyDefinition S(string name) => new(name, new[] { name });

    private static ProbeResult Ok(int ms) => new(true, TimeSpan.FromMilliseconds(ms));
    private static readonly ProbeResult Fail = new(false, TimeSpan.Zero);

    private static StrategyAutoSelector Selector(FakeRunner runner, IConnectivityProbe probe) =>
        new(runner, probe, s => s.Args, settleDelay: TimeSpan.Zero);

    [Fact]
    public async Task Select_PicksStrategyThatOpensMostTargets()
    {
        var runner = new FakeRunner();
        var probe = new FakeProbe(runner, new()
        {
            ["a"] = t => t.Name == "YouTube" ? Ok(50) : Fail,
            ["b"] = t => t.Name == "WarDogs" ? Fail : Ok(300),
            ["c"] = _ => Fail,
        });

        var result = await Selector(runner, probe).SelectAsync(new[] { S("a"), S("b"), S("c") }, Targets, null, CancellationToken.None);

        Assert.Equal("b", result.Best?.Name);
        Assert.Equal(2, result.Scores.Single(x => x.Strategy.Name == "b").Passed);
    }

    [Fact]
    public async Task Select_TieOnPassed_PrefersLowerLatency()
    {
        var runner = new FakeRunner();
        var probe = new FakeProbe(runner, new()
        {
            ["slow"] = _ => Ok(400),
            ["fast"] = _ => Ok(40),
        });

        var result = await Selector(runner, probe).SelectAsync(new[] { S("slow"), S("fast") }, Targets, null, CancellationToken.None);

        Assert.Equal("fast", result.Best?.Name);
    }

    [Fact]
    public async Task Select_NothingWorks_ReturnsNullBest()
    {
        var runner = new FakeRunner();
        var probe = new FakeProbe(runner, new() { ["a"] = _ => Fail });

        var result = await Selector(runner, probe).SelectAsync(new[] { S("a") }, Targets, null, CancellationToken.None);

        Assert.Null(result.Best);
    }

    [Fact]
    public async Task Select_StopsEngineAfterEveryTrial()
    {
        var runner = new FakeRunner();
        var probe = new FakeProbe(runner, new() { ["a"] = _ => Ok(1), ["b"] = _ => Ok(1) });

        await Selector(runner, probe).SelectAsync(new[] { S("a"), S("b") }, Targets, null, CancellationToken.None);

        Assert.Equal(new[] { "start a", "stop a", "start b", "stop b" }, runner.Log);
    }

    [Fact]
    public async Task Select_StrategyThatFailsToStart_ScoredZeroAndOthersStillTried()
    {
        var runner = new FakeRunner { FailOnStart = true };
        var probe = new FakeProbe(runner, new());

        var result = await Selector(runner, probe).SelectAsync(new[] { S("a"), S("b") }, Targets, null, CancellationToken.None);

        Assert.Null(result.Best);
        Assert.Equal(2, result.Scores.Count);
        Assert.All(result.Scores, s => Assert.Equal(0, s.Passed));
    }

    [Fact]
    public async Task Select_OneStrategyFailsToStart_WorkingOneStillPickedAndEngineStopped()
    {
        var runner = new FakeRunner { FailingStrategies = { "broken" } };
        var probe = new FakeProbe(runner, new() { ["good"] = _ => Ok(10) });

        var result = await Selector(runner, probe).SelectAsync(new[] { S("broken"), S("good") }, Targets, null, CancellationToken.None);

        Assert.Equal("good", result.Best?.Name);
        Assert.NotNull(result.Scores.Single(s => s.Strategy.Name == "broken").Error);
        Assert.Equal(new[] { "failed broken", "stop ", "start good", "stop good" }, runner.Log);
    }

    [Fact]
    public async Task Select_ArgsBuilderThrows_StrategyScoredZeroOthersTried()
    {
        var runner = new FakeRunner();
        var probe = new FakeProbe(runner, new() { ["good"] = _ => Ok(10) });
        var selector = new StrategyAutoSelector(runner, probe,
            s => s.Name == "newformat" ? throw new FormatException("Unknown placeholder") : s.Args,
            TimeSpan.Zero);

        var result = await selector.SelectAsync(new[] { S("newformat"), S("good") }, Targets, null, CancellationToken.None);

        Assert.Equal("good", result.Best?.Name);
        Assert.Contains("Unknown placeholder", result.Scores[0].Error);
    }

    [Fact]
    public async Task Select_ProbeThrows_StrategyScoredZeroAndEngineStopped()
    {
        var runner = new FakeRunner();
        var probe = new FakeProbe(runner, new()
        {
            ["a"] = _ => throw new InvalidOperationException("boom"),
            ["b"] = _ => Ok(10),
        });

        var result = await Selector(runner, probe).SelectAsync(new[] { S("a"), S("b") }, Targets, null, CancellationToken.None);

        Assert.Equal("b", result.Best?.Name);
        Assert.Equal(new[] { "start a", "stop a", "start b", "stop b" }, runner.Log);
    }

    [Fact]
    public async Task Select_NoTargets_NoPick()
    {
        var runner = new FakeRunner();
        var probe = new FakeProbe(runner, new() { ["a"] = _ => Ok(1) });

        var result = await Selector(runner, probe).SelectAsync(new[] { S("a") }, Array.Empty<ProbeTarget>(), null, CancellationToken.None);

        Assert.Null(result.Best);
    }

    [Fact]
    public async Task Select_TieOnPassed_FailedTargetsDoNotCountTowardsLatency()
    {
        var runner = new FakeRunner();
        var probe = new FakeProbe(runner, new()
        {
            // Both pass 2 of 3. "x" is faster on successful targets; its slow failure must not penalise it.
            ["x"] = t => t.Name == "WarDogs" ? new ProbeResult(false, TimeSpan.FromSeconds(6)) : Ok(100),
            ["y"] = t => t.Name == "YouTube" ? Fail : Ok(150),
        });

        var result = await Selector(runner, probe).SelectAsync(new[] { S("y"), S("x") }, Targets, null, CancellationToken.None);

        Assert.Equal("x", result.Best?.Name);
    }

    [Fact]
    public async Task Select_Cancelled_StopsEngineAndThrows()
    {
        var runner = new FakeRunner();
        using var cts = new CancellationTokenSource();
        var probe = new FakeProbe(runner, new() { ["a"] = _ => { cts.Cancel(); return Ok(1); } });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Selector(runner, probe).SelectAsync(new[] { S("a"), S("b") }, Targets, null, cts.Token));

        Assert.Equal("stop a", runner.Log.Last());
        Assert.DoesNotContain("start b", runner.Log);
    }

    [Fact]
    public async Task Select_CancelledDuringLastStrategy_ThrowsInsteadOfReturningPartialResult()
    {
        var runner = new FakeRunner();
        using var cts = new CancellationTokenSource();
        var probe = new FakeProbe(runner, new() { ["only"] = _ => { cts.Cancel(); return Ok(1); } });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Selector(runner, probe).SelectAsync(new[] { S("only") }, Targets, null, cts.Token));

        Assert.Equal("stop only", runner.Log.Last());
    }

    [Fact]
    public async Task Select_ProbeThrowsCancellation_PropagatesAndStopsProbing()
    {
        var runner = new FakeRunner();
        using var cts = new CancellationTokenSource();
        var calls = 0;
        var probe = new FakeProbe(runner, new()
        {
            ["only"] = _ =>
            {
                calls++;
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            },
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Selector(runner, probe).SelectAsync(new[] { S("only") }, Targets, null, cts.Token));

        Assert.Equal(1, calls);
        Assert.Equal("stop only", runner.Log.Last());
    }

    [Fact]
    public async Task Select_WaitsSettleDelayBeforeProbing()
    {
        var runner = new FakeRunner();
        var startedAt = DateTime.MinValue;
        var probedAt = DateTime.MinValue;
        var probe = new FakeProbe(runner, new() { ["a"] = _ => { if (probedAt == DateTime.MinValue) probedAt = DateTime.UtcNow; return Ok(1); } });
        var selector = new StrategyAutoSelector(runner, probe, s => { startedAt = DateTime.UtcNow; return s.Args; }, TimeSpan.FromMilliseconds(300));

        await selector.SelectAsync(new[] { S("a") }, Targets, null, CancellationToken.None);

        Assert.True(probedAt - startedAt >= TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task Select_CancelledDuringSettleDelay_ReturnsPromptly()
    {
        var runner = new FakeRunner();
        var probe = new FakeProbe(runner, new() { ["a"] = _ => Ok(1) });
        var selector = new StrategyAutoSelector(runner, probe, s => s.Args, TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var sw = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            selector.SelectAsync(new[] { S("a") }, Targets, null, cts.Token));

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal("stop a", runner.Log.Last());
    }

    [Fact]
    public async Task Select_ReportsCompletedScorePerStrategy()
    {
        var runner = new FakeRunner();
        var probe = new FakeProbe(runner, new() { ["a"] = _ => Ok(1), ["b"] = _ => Fail });
        var completed = new List<StrategyScore>();

        await Selector(runner, probe).SelectAsync(
            new[] { S("a"), S("b") }, Targets,
            new SyncProgress<SelectionProgress>(p => { if (p.Completed is not null) completed.Add(p.Completed); }),
            CancellationToken.None);

        Assert.Equal(new[] { "a", "b" }, completed.Select(s => s.Strategy.Name));
        Assert.Equal(new[] { 3, 0 }, completed.Select(s => s.Passed));
    }

    // Per-target steps let the progress bar move smoothly instead of jumping once per strategy.
    [Fact]
    public async Task Select_FractionGrowsMonotonicallyPerTargetAndEndsAtOne()
    {
        var runner = new FakeRunner();
        var probe = new FakeProbe(runner, new() { ["a"] = _ => Ok(1), ["b"] = _ => Ok(1) });
        var fractions = new List<double>();

        await Selector(runner, probe).SelectAsync(
            new[] { S("a"), S("b") }, Targets, new SyncProgress<SelectionProgress>(p => fractions.Add(p.Fraction)), CancellationToken.None);

        Assert.True(fractions.Count >= 2 * Targets.Length);
        Assert.Equal(fractions.Order(), fractions);
        Assert.Equal(1.0, fractions[^1], precision: 6);
        Assert.Contains(fractions, f => f is > 0 and < 0.5);
    }

    [Theory]
    [InlineData(0, 2, 0, 4, false, 0.0)]
    [InlineData(0, 2, 2, 4, false, 0.25)]
    [InlineData(1, 2, 0, 4, false, 0.5)]
    [InlineData(1, 2, 4, 4, true, 1.0)]
    [InlineData(0, 2, 0, 0, false, 0.5)]  // no targets: a strategy counts as done, never NaN
    [InlineData(0, 0, 0, 0, false, 1.0)]  // nothing to test
    public void Fraction_Computation(int index, int count, int done, int targets, bool completed, double expected)
    {
        var s = S("a");
        var p = new SelectionProgress(index, count, s, done, targets,
            completed ? new StrategyScore(s, 1, targets, TimeSpan.Zero, null) : null);

        Assert.Equal(expected, p.Fraction, precision: 6);
    }

    [Fact]
    public async Task Select_ProgressNamesCurrentStrategyAndPosition()
    {
        var runner = new FakeRunner();
        var probe = new FakeProbe(runner, new() { ["a"] = _ => Ok(1), ["b"] = _ => Ok(1) });
        var seen = new List<SelectionProgress>();

        await Selector(runner, probe).SelectAsync(
            new[] { S("a"), S("b") }, Targets, new SyncProgress<SelectionProgress>(seen.Add), CancellationToken.None);

        Assert.Contains(seen, p => p.Strategy.Name == "b" && p.StrategyIndex == 1 && p.StrategyCount == 2);
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _a;
        public SyncProgress(Action<T> a) => _a = a;
        public void Report(T value) => _a(value);
    }
}

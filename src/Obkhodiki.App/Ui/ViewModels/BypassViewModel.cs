using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obkhodiki.Core.Strategies;
using Obkhodiki.Core.Testing;

namespace Obkhodiki.App.Ui.ViewModels;

public sealed record GameFilterOption(GameFilterMode Mode, string Title);

public sealed partial class StrategyScoreRow : ObservableObject
{
    public required string Name { get; init; }
    public required int Passed { get; init; }
    public required int Total { get; init; }
    public double? AvgMs { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<string> Failed { get; init; } = Array.Empty<string>();
    [ObservableProperty] private bool _isBest;

    /// <summary>Which targets did not open; shown under partly working strategies (all of them is just noise).</summary>
    public string? FailedText => Passed > 0 && Failed.Count > 0 ? "Не открылись: " + string.Join(", ", Failed) : null;

    public string? Details => Error ?? (Failed.Count > 0 ? "Не открылись: " + string.Join(", ", Failed) : null);

    public string PassedText => $"{Passed} из {Total}";
    public string LatencyText => AvgMs is { } ms ? $"{ms:F0} мс" : "—";
    public double Ratio => Total == 0 ? 0 : (double)Passed / Total;
}

/// <summary>Strategies, auto-select with inline progress, game filter.</summary>
public sealed partial class BypassViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _syncing;
    private CancellationTokenSource? _autoSelectCts;

    public ObservableCollection<string> Strategies { get; } = new();
    public ObservableCollection<StrategyScoreRow> Results { get; } = new();

    public IReadOnlyList<GameFilterOption> GameFilterOptions { get; } = new[]
    {
        new GameFilterOption(GameFilterMode.Disabled, "Выключен"),
        new GameFilterOption(GameFilterMode.Tcp, "TCP"),
        new GameFilterOption(GameFilterMode.Udp, "UDP"),
        new GameFilterOption(GameFilterMode.All, "TCP и UDP"),
    };

    [ObservableProperty] private string? _selectedStrategy;
    [ObservableProperty] private GameFilterOption? _selectedGameFilter;
    [ObservableProperty] private bool _isSelecting;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string? _selectionSummary;
    [ObservableProperty] private string _flowsealVersion = "—";
    [ObservableProperty] private int _problemsCount;

    public BypassViewModel(ShellViewModel shell) => _shell = shell;

    partial void OnSelectedStrategyChanged(string? value)
    {
        if (_syncing || value is null) return;
        _ = _shell.RunAsync(c => c.SelectStrategyAsync(value));
    }

    partial void OnSelectedGameFilterChanged(GameFilterOption? value)
    {
        if (_syncing || value is null) return;
        _ = _shell.RunAsync(c => c.SetGameFilterAsync(value.Mode));
    }

    internal void Refresh(AppController c)
    {
        _syncing = true;
        try
        {
            var names = c.Engine?.Strategies.Select(s => s.Name).ToList() ?? new List<string>();
            if (!names.SequenceEqual(Strategies))
            {
                Strategies.Clear();
                foreach (var n in names) Strategies.Add(n);
            }
            SelectedStrategy = c.ActiveStrategyName;
            SelectedGameFilter = GameFilterOptions.First(o => o.Mode == c.Settings.GameFilter);
            FlowsealVersion = c.Engine?.Version ?? "—";
            ProblemsCount = c.Engine?.Problems.Count ?? 0;
        }
        finally
        {
            _syncing = false;
        }
    }

    // Concurrent: the same button stops a running selection, so it must stay clickable while one runs.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task AutoSelectAsync() => RunAutoSelectAsync(full: false);

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task AutoSelectFullAsync() => RunAutoSelectAsync(full: true);

    private async Task RunAutoSelectAsync(bool full)
    {
        if (IsSelecting)
        {
            CancelAutoSelect();
            return;
        }

        Results.Clear();
        SelectionSummary = null;
        IsSelecting = true;
        Progress = 0;
        using var cts = new CancellationTokenSource();
        _autoSelectCts = cts;
        try
        {
            var progress = new Progress<SelectionProgress>(ShowProgress);
            await _shell.RunAsync(c => c.AutoSelectAsync(progress, cts.Token, full));
            if (cts.IsCancellationRequested) SelectionSummary = "Подбор отменён, оставлена прежняя стратегия.";
            else if (Results.Count == 0) SelectionSummary = "Подбор не выполнен — подробности в событиях.";
            else if (Results.All(r => r.Passed == 0)) SelectionSummary = "Ни одна стратегия не открыла цели. Проверьте интернет и список целей.";
            else
            {
                var best = SelectedStrategy;
                foreach (var r in Results) r.IsBest = r.Name == best;
                SelectionSummary = $"Готово. Выбрана стратегия «{best}».";
            }
        }
        finally
        {
            _autoSelectCts = null;
            IsSelecting = false;
            Progress = 1;
        }
    }

    internal void CancelAutoSelect()
    {
        if (_autoSelectCts is not { IsCancellationRequested: false } cts) return;
        cts.Cancel();
        ProgressText = "Отмена… дожидаемся текущей проверки";
    }

    private void ShowProgress(SelectionProgress p)
    {
        Progress = p.Fraction;
        ProgressText = $"Стратегия {p.StrategyIndex + 1} из {p.StrategyCount}: {p.Strategy.Name} · проверено целей {p.TargetsDone} из {p.TargetCount}";
        if (p.Completed is { } s)
        {
            Results.Add(new StrategyScoreRow
            {
                Name = s.Strategy.Name,
                Passed = s.Passed,
                Total = s.Total,
                AvgMs = s.Passed > 0 ? s.TotalLatency.TotalMilliseconds / s.Passed : null,
                Error = s.Error,
                Failed = s.Failed ?? Array.Empty<string>(),
            });
        }
    }

    [RelayCommand]
    private static void OpenTargets() => ShellActions.OpenInNotepad(AppPaths.Targets);

    [RelayCommand]
    private static void OpenLists() => ShellActions.OpenFolder(AppPaths.UserLists);

    /// <summary>Preview data.</summary>
    internal void LoadSample()
    {
        _syncing = true;
        foreach (var n in new[] { "general", "general (ALT)", "general (ALT2)", "general (FAKE TLS AUTO)", "general (SIMPLE FAKE)" }) Strategies.Add(n);
        SelectedStrategy = "general (ALT)";
        SelectedGameFilter = GameFilterOptions[0];
        FlowsealVersion = "1.10.3";
        Results.Add(new StrategyScoreRow { Name = "general", Passed = 9, Total = 12, AvgMs = 142, Failed = new[] { "DiscordGateway", "YouTubeVideoRedirect", "CloudflareCDN" } });
        Results.Add(new StrategyScoreRow { Name = "general (ALT)", Passed = 12, Total = 12, AvgMs = 118, IsBest = true });
        Results.Add(new StrategyScoreRow { Name = "general (ALT2)", Passed = 0, Total = 12, Error = "winws.exe exited with code 1" });
        IsSelecting = true;
        Progress = 0.42;
        ProgressText = "Стратегия 4 из 22: general (ALT3) · проверено целей 5 из 12";
        _syncing = false;
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZapretHub.Core.Games;

namespace ZapretHub.App.Ui.ViewModels;

/// <summary>Dashboard: one glance at every module, with the main switch.</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _syncing;

    [ObservableProperty] private bool _bypassOn;
    [ObservableProperty] private bool _engineReady;
    [ObservableProperty] private string _strategyName = "";
    [ObservableProperty] private string _flowsealVersion = "—";
    [ObservableProperty] private string _gamesSummary = "";
    [ObservableProperty] private int _gamesOn;
    [ObservableProperty] private bool _telegramOn;
    [ObservableProperty] private string _telegramSummary = "";
    [ObservableProperty] private bool _vpnOn;
    [ObservableProperty] private string _vpnSummary = "";
    [ObservableProperty] private int _updatesAvailable;
    [ObservableProperty] private bool _isBusy;

    public bool CanToggle => EngineReady && !IsBusy;

    public string StatusTitle => BypassOn ? "Обход включён" : "Обход выключен";

    public string StatusSubtitle => !EngineReady
        ? "Загружаются стратегии Flowseal…"
        : BypassOn ? $"Стратегия {StrategyName} · Flowseal {FlowsealVersion}" : "Нажмите переключатель, чтобы включить";

    public HomeViewModel(ShellViewModel shell) => _shell = shell;

    partial void OnBypassOnChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusTitle));
        OnPropertyChanged(nameof(StatusSubtitle));
        if (_syncing) return;
        _ = _shell.RunAsync(c => value ? c.EnableAsync() : c.DisableAsync());
    }

    partial void OnStrategyNameChanged(string value) => OnPropertyChanged(nameof(StatusSubtitle));
    partial void OnEngineReadyChanged(bool value)
    {
        OnPropertyChanged(nameof(StatusSubtitle));
        OnPropertyChanged(nameof(CanToggle));
    }

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanToggle));
    partial void OnFlowsealVersionChanged(string value) => OnPropertyChanged(nameof(StatusSubtitle));

    internal void Refresh(AppController c)
    {
        _syncing = true;
        try
        {
            EngineReady = c.Engine is not null;
            IsBusy = c.BusyText is not null;
            // While an operation runs the switch shows what the user asked for; the result arrives with the next refresh.
            if (!IsBusy) BypassOn = c.IsRunning;
            StrategyName = c.ActiveStrategyName;
            FlowsealVersion = c.Engine?.Version ?? "—";

            var profiles = c.Settings.GameProfiles;
            GamesOn = profiles.Count(p => p.Enabled);
            GamesSummary = profiles.Count == 0
                ? "Игры не добавлены"
                : $"Включено {GamesOn} из {profiles.Count}" + (profiles.Any(p => p.Enabled && p.Route != GameRoute.Direct) ? " · есть маршруты через VPS" : "");

            TelegramOn = c.IsTgRunning;
            TelegramSummary = c.TgVersion is null ? "Не установлен" : TelegramOn ? $"Прокси работает · {c.TgVersion}" : "Прокси выключен";

            VpnOn = c.IsVpnRunning;
            VpnSummary = c.VpnServerName is null ? "Сервер не добавлен" : VpnOn ? "Туннель работает" : "Готов, сейчас не нужен";

            UpdatesAvailable = new object?[] { c.AvailableUpdate, c.AvailableTgUpdate, c.AvailableSbUpdate }.Count(u => u is not null);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>For previews and first paint.</summary>
    internal void SetState(bool bypassOn, bool engineReady)
    {
        _syncing = true;
        BypassOn = bypassOn;
        EngineReady = engineReady;
        _syncing = false;
    }

    internal void LoadSample()
    {
        StrategyName = "general (ALT2)";
        FlowsealVersion = "1.9.7";
        GamesOn = 1;
        GamesSummary = "Включено 1 из 2 · War Dogs";
        TelegramOn = true;
        TelegramSummary = "Прокси работает · 1.3.0";
        VpnOn = true;
        VpnSummary = "Туннель работает";
        UpdatesAvailable = 1;
    }

    [RelayCommand]
    private void Navigate(string page) => NavigationRequested?.Invoke(page);

    public static event Action<string>? NavigationRequested;
}

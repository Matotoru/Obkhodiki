using Obkhodiki.App.Ui;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Obkhodiki.App.Ui.ViewModels;

public sealed partial class TelegramViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _syncing;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _version = "не установлен";
    [ObservableProperty] private bool _isBusy;

    public TelegramViewModel(ShellViewModel shell) => _shell = shell;

    partial void OnEnabledChanged(bool value)
    {
        if (_syncing) return;
        _ = _shell.RunAsync(c => value ? c.EnableTelegramAsync() : c.DisableTelegramAsync());
    }

    internal void Refresh(AppController c)
    {
        _syncing = true;
        IsBusy = c.BusyText is not null;
        // While an operation runs the switch shows what the user asked for; the result arrives with the next refresh.
        if (!IsBusy) Enabled = c.IsTgRunning;
        Version = c.TgVersion ?? "не установлен";
        _syncing = false;
    }

    [RelayCommand]
    private Task ConnectAsync() => _shell.RunAsync(c => c.ConnectTelegramAsync());

    internal void LoadSample()
    {
        _syncing = true;
        Enabled = true;
        Version = "1.10.4";
        _syncing = false;
    }
}

public sealed partial class ComponentViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    internal Func<AppController, Task> Check { get; init; } = _ => Task.CompletedTask;
    internal Func<AppController, Task> Install { get; init; } = _ => Task.CompletedTask;

    [ObservableProperty] private string _version = "—";
    [ObservableProperty] private string? _updateVersion;

    public bool HasUpdate => UpdateVersion is not null;

    partial void OnUpdateVersionChanged(string? value) => OnPropertyChanged(nameof(HasUpdate));
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _syncing;

    public ObservableCollection<ComponentViewModel> Components { get; } = new();

    [ObservableProperty] private bool _autostart;
    [ObservableProperty] private bool _checkUpdatesOnStart = true;
    [ObservableProperty] private string _theme = ThemeService.System;
    [ObservableProperty] private AccentPalette _palette = ThemeService.Palette(ThemeService.DefaultPalette);

    public IReadOnlyList<ThemeOption> ThemeOptions => ThemeService.Themes;
    public IReadOnlyList<AccentPalette> Palettes => ThemeService.Palettes;
    [ObservableProperty] private string _appVersion = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    public SettingsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Components.Add(new ComponentViewModel
        {
            Name = Obkhodiki.Core.Updates.AppInfo.Name,
            Description = "сама программа",
            Check = c => c.CheckAppUpdateManuallyAsync(),
            Install = c => c.InstallAvailableAppUpdateAsync(),
        });
        Components.Add(new ComponentViewModel
        {
            Name = "Стратегии Flowseal",
            Description = "zapret + стратегии обхода блокировок",
            Check = c => c.CheckForUpdateAsync(userInitiated: true),
            Install = c => c.InstallAvailableUpdateAsync(),
        });
        Components.Add(new ComponentViewModel
        {
            Name = "TG WS Proxy",
            Description = "прокси для Telegram",
            Check = c => c.CheckTgUpdateManuallyAsync(),
            Install = c => c.InstallAvailableTgUpdateAsync(),
        });
        Components.Add(new ComponentViewModel
        {
            Name = "sing-box",
            Description = "туннель к вашему VPS",
            Check = c => c.CheckSingBoxUpdateManuallyAsync(),
            Install = c => c.InstallAvailableSbUpdateAsync(),
        });
    }

    partial void OnAutostartChanged(bool value)
    {
        if (_syncing) return;
        _ = SetAutostartAsync(value);
    }

    partial void OnCheckUpdatesOnStartChanged(bool value)
    {
        if (_syncing) return;
        _ = _shell.RunAsync(c => c.SetCheckUpdatesOnStartAsync(value));
    }

    partial void OnThemeChanged(string value) => ApplyAppearance();

    [RelayCommand]
    private void SelectPalette(AccentPalette? palette)
    {
        if (palette is null) return;
        Palette = palette;
        ApplyAppearance();
    }

    private void ApplyAppearance()
    {
        if (_syncing) return;
        if (System.Windows.Application.Current?.MainWindow is { } window) ThemeService.Apply(window, Theme, Palette.Id);
        _ = _shell.RunAsync(c => c.SetAppearanceAsync(Theme, Palette.Id));
    }

    private async Task SetAutostartAsync(bool enable)
    {
        try
        {
            var copied = await Task.Run(() =>
            {
                if (enable) return Obkhodiki.App.Autostart.Enable();
                Obkhodiki.App.Autostart.Disable();
                return false;
            });
            if (copied) _shell.AddEvent("Автозапуск", $"Приложение скопировано в {AppPaths.InstallDir}; при входе в Windows запускается эта копия.", EventKind.Info);
        }
        catch (Exception ex)
        {
            Log.Error("Autostart change failed", ex);
            _shell.AddEvent("Автозапуск", ex.Message, EventKind.Error);
            await RefreshAutostartAsync();
        }
    }

    internal async Task RefreshAutostartAsync()
    {
        bool enabled;
        try
        {
            enabled = await Task.Run(Obkhodiki.App.Autostart.IsEnabled);
        }
        catch (Exception ex)
        {
            Log.Error("Autostart query failed", ex);
            return;
        }
        _syncing = true;
        Autostart = enabled;
        _syncing = false;
    }

    internal void Refresh(AppController c)
    {
        _syncing = true;
        CheckUpdatesOnStart = c.Settings.CheckUpdatesOnStart;
        Theme = ThemeService.Themes.Any(t => t.Id == c.Settings.AppTheme) ? c.Settings.AppTheme : ThemeService.System;
        Palette = ThemeService.Palette(c.Settings.AppPalette);
        _syncing = false;
        Components[0].Version = SelfUpdate.CurrentVersion;
        Components[0].UpdateVersion = c.AvailableAppUpdate?.Version;
        Components[1].Version = c.Engine?.Version ?? "не установлены";
        Components[1].UpdateVersion = c.AvailableUpdate?.Version;
        Components[2].Version = c.TgVersion ?? "не установлен";
        Components[2].UpdateVersion = c.AvailableTgUpdate?.Version;
        Components[3].Version = c.SingBoxVersion ?? "не установлен";
        Components[3].UpdateVersion = c.AvailableSbUpdate?.Version;
    }

    public IReadOnlyList<Obkhodiki.Core.Updates.AppInfo.Credit> Credits => Obkhodiki.Core.Updates.AppInfo.Credits;

    // Opened as the desktop user: a browser started from this elevated process would run as admin.
    [RelayCommand]
    private static async Task OpenCredit(Obkhodiki.Core.Updates.AppInfo.Credit? credit)
    {
        if (credit is null) return;
        try
        {
            await Task.Run(() => TgProxyRunner.OpenAsUser(credit.Url));
        }
        catch (Exception ex)
        {
            Log.Error("Credit link could not be opened", ex);
        }
    }

    [RelayCommand]
    private static Task ShowWhatsNew() => WhatsNewDialog.ShowAsync(Obkhodiki.Core.Updates.Changelog.Entries.Take(3).ToList());

    [RelayCommand]
    private Task CheckAsync(ComponentViewModel component) => _shell.RunAsync(component.Check);

    [RelayCommand]
    private Task InstallAsync(ComponentViewModel component) => _shell.RunAsync(component.Install);

    internal void LoadSample()
    {
        _syncing = true;
        Autostart = true;
        _syncing = false;
        Components[0].Version = SelfUpdate.CurrentVersion;
        Components[1].Version = "1.10.3";
        Components[1].UpdateVersion = "1.10.4";
        Components[2].Version = "1.10.4";
        Components[3].Version = "1.14.2";
    }
}

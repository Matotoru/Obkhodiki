using System.Runtime.InteropServices;
using ZapretHub.Core.Telegram;
using ZapretHub.Core.Updates;

namespace ZapretHub.App;

/// <summary>Telegram module: manages Flowseal's tg-ws-proxy (download, confirmed updates, start/stop).</summary>
internal sealed partial class AppController
{
    public const string TgProductName = "TG WS Proxy";

    private readonly TgProxyStore _tgStore = new(AppPaths.TgRoot);
    private TgProxyUpdater? _tgUpdaterInstance;
    private volatile ReleaseInfo? _availableTgUpdate;
    private volatile bool _tgRunning;
    private bool _confirmingTgUpdate;

    private TgProxyUpdater TgUpdater =>
        _tgUpdaterInstance ??= new TgProxyUpdater(new TgProxyReleaseClient(_http, RuntimeInformation.OSArchitecture), _tgStore);

    /// <summary>Asked (on the UI thread) before stopping a separately started tg-ws-proxy.</summary>
    public Func<bool> ConfirmStopForeignTg { get; set; } = () => false;

    public ReleaseInfo? AvailableTgUpdate => _availableTgUpdate;
    public string? TgVersion => _tgStore.ActiveVersion;

    /// <summary>Last known state; refreshed in the background (process enumeration is too slow for the UI thread).</summary>
    public bool IsTgRunning => _tgRunning;

    public async Task RefreshTgStateAsync()
    {
        var running = await Task.Run(TgProxyRunner.IsRunning);
        if (running == _tgRunning) return;
        _tgRunning = running;
        Changed();
    }

    /// <summary>Downloads the proxy on first use (after confirmation), then starts it.</summary>
    public Task EnableTelegramAsync() => Serialized("Запуск TG WS Proxy…", silentErrors: false, async () =>
    {
        _tgRunning = await Task.Run(TgProxyRunner.IsRunning);
        if (_tgStore.ActiveExe is null && !await InstallFirstTgAsync()) return;
        if (!await ResolveTgConflictsAsync()) return;

        var exe = _tgStore.ActiveExe!;
        var listening = await Task.Run(() => TgProxyRunner.StartAsync(exe, requireListener: false));
        _tgRunning = true;
        Settings.TelegramEnabled = true;
        _settingsStore.Save(Settings);
        Log.Info($"TG WS Proxy {_tgStore.ActiveVersion} started, listening: {listening}");
        Notify?.Invoke("Telegram", listening
            ? "TG WS Proxy запущен. Если Telegram ещё не настроен, выберите «Подключить Telegram к прокси»."
            : "TG WS Proxy запущен, но пока не принимает подключения. Если открылось его окно первого запуска, пройдите его.",
            ToolTipIcon.Info);
    });

    public Task DisableTelegramAsync() => Serialized("Остановка TG WS Proxy…", silentErrors: false, async () =>
    {
        await Task.Run(TgProxyRunner.Stop);
        _tgRunning = false;
        Settings.TelegramEnabled = false;
        _settingsStore.Save(Settings);
        Log.Info("TG WS Proxy stopped");
    });

    /// <summary>Opens the tg://proxy link so Telegram adds the local proxy (as the desktop user, never elevated).</summary>
    public async Task ConnectTelegramAsync()
    {
        var link = await Task.Run(() => TgProxyRunner.ReadConfigForSessionUser() is { } json ? TgProxyConfig.BuildLink(json) : null);
        if (link is null)
        {
            Notify?.Invoke("Telegram", "Настройки прокси ещё не созданы. Включите TG WS Proxy и подождите несколько секунд.", ToolTipIcon.Warning);
            return;
        }
        if (!await Task.Run(() => TgProxyRunner.OpenAsUser(link)))
        {
            Notify?.Invoke("Telegram", "Не найден рабочий стол Windows, ссылку открыть нельзя.", ToolTipIcon.Warning);
            return;
        }
        Log.Info("Opened Telegram proxy link");
    }

    /// <summary>Starts the proxy at app start if the user had it on.</summary>
    private async Task StartTelegramIfWantedAsync()
    {
        await RefreshTgStateAsync();
        if (!Settings.TelegramEnabled || _tgStore.ActiveExe is not { } exe || _tgRunning) return;
        await Serialized("Запуск TG WS Proxy…", silentErrors: false, async () =>
        {
            // Right after logon the app may start before the desktop shell.
            if (!await TgProxyRunner.WaitForShellAsync(TimeSpan.FromSeconds(60)))
            {
                throw new InvalidOperationException("Рабочий стол Windows не появился за минуту, TG WS Proxy не запущен.");
            }
            if (!await ResolveTgConflictsAsync()) return;
            await Task.Run(() => TgProxyRunner.StartAsync(exe, requireListener: false));
            _tgRunning = true;
        });
    }

    private async Task<bool> InstallFirstTgAsync()
    {
        var release = await Task.Run(() => TgUpdater.CheckAsync(null, CancellationToken.None))
                      ?? throw new InvalidOperationException("Не удалось найти релиз TG WS Proxy.");
        // The very first download also shows the user what exactly will be installed.
        if (!ConfirmUpdate(TgProductName, release.Version, null)) return false;
        SetBusy($"Загрузка TG WS Proxy {release.Version}…");
        await Task.Run(() => TgUpdater.InstallAsync(release, null, null, CancellationToken.None));
        Log.Info($"Installed TG WS Proxy {release.Version}");
        return true;
    }

    private async Task CheckTgUpdateAsync(bool userInitiated)
    {
        if (_tgStore.ActiveVersion is null) return; // not installed: nothing to update
        ReleaseInfo? found = null;
        await Serialized("Проверка обновлений TG WS Proxy…", silentErrors: !userInitiated, async () =>
        {
            found = await Task.Run(() => TgUpdater.CheckAsync(userInitiated ? null : Settings.TelegramSkippedVersion, CancellationToken.None));
            _availableTgUpdate = found;
            if (found is null)
            {
                if (userInitiated) Notify?.Invoke("Обновлений нет", $"Установлена последняя версия TG WS Proxy {_tgStore.ActiveVersion}.", ToolTipIcon.Info);
                return;
            }
            Log.Info($"TG WS Proxy {found.Version} is available (installed {_tgStore.ActiveVersion})");
            if (!userInitiated) AnnounceUpdate(TgProductName, found.Version);
        });
        if (found is not null && userInitiated) await InstallAvailableTgUpdateAsync();
    }

    public Task CheckTgUpdateManuallyAsync() => CheckTgUpdateAsync(userInitiated: true);

    public async Task InstallAvailableTgUpdateAsync()
    {
        var release = _availableTgUpdate;
        if (release is null || BusyText is not null || _confirmingTgUpdate) return;
        _confirmingTgUpdate = true;
        bool confirmed;
        try
        {
            confirmed = ConfirmUpdate(TgProductName, release.Version, _tgStore.ActiveVersion);
        }
        finally
        {
            _confirmingTgUpdate = false;
        }
        if (!ReleaseVersion.IsNewer(release.Version, _tgStore.ActiveVersion)) return;
        if (!confirmed)
        {
            Settings.TelegramSkippedVersion = release.Version;
            _settingsStore.Save(Settings);
            Log.Info($"User declined TG WS Proxy {release.Version}");
            return;
        }
        await Serialized($"Установка TG WS Proxy {release.Version}…", silentErrors: false, () => InstallTgReleaseAsync(release));
    }

    private async Task InstallTgReleaseAsync(ReleaseInfo release)
    {
        var wasRunning = await Task.Run(TgProxyRunner.IsRunning);
        try
        {
            // The new build goes to its own folder, so Telegram keeps working during the download. The new
            // version is always test-started (even if the proxy was off) so a broken release is caught while
            // the previous one still exists to fall back to.
            var installed = await Task.Run(() => TgUpdater.InstallAsync(
                release,
                switchTo: async exe =>
                {
                    await RestartTgAsync(exe, requireListener: true);
                    if (!wasRunning) await Task.Run(TgProxyRunner.Stop);
                },
                rollback: exe => wasRunning ? RestartTgAsync(exe, requireListener: false) : Task.CompletedTask,
                CancellationToken.None));
            _availableTgUpdate = null;
            if (installed is null) return;
            Settings.TelegramSkippedVersion = null;
            _settingsStore.Save(Settings);
            Log.Info($"TG WS Proxy updated to {installed}");
            Notify?.Invoke("TG WS Proxy обновлён", $"Версия {installed}.", ToolTipIcon.Info);
        }
        catch (UpdateException ex)
        {
            Log.Error("TG WS Proxy update failed", ex);
            if (ex.ReleaseDefect && ex.Version is not null)
            {
                Settings.TelegramSkippedVersion = ex.Version;
                _settingsStore.Save(Settings);
                _availableTgUpdate = null;
            }
            Notify?.Invoke("Обновление TG WS Proxy не удалось", $"{ex.Message}\nОставлена версия {_tgStore.ActiveVersion}.", ToolTipIcon.Warning);
        }
        finally
        {
            // Whatever happened above, a proxy that was running before is running afterwards if at all possible.
            await EnsureTgRunningAsync(wasRunning);
        }
    }

    private static async Task RestartTgAsync(string exe, bool requireListener)
    {
        await Task.Run(TgProxyRunner.Stop).ConfigureAwait(false);
        await Task.Run(() => TgProxyRunner.StartAsync(exe, requireListener)).ConfigureAwait(false);
    }

    private async Task EnsureTgRunningAsync(bool wanted)
    {
        try
        {
            if (wanted && !await Task.Run(TgProxyRunner.IsRunning) && _tgStore.ActiveExe is { } exe)
            {
                await Task.Run(() => TgProxyRunner.StartAsync(exe, requireListener: false));
            }
        }
        catch (Exception ex)
        {
            Log.Error("Restarting TG WS Proxy failed", ex);
            Notify?.Invoke("TG WS Proxy не запущен", ex.Message, ToolTipIcon.Warning);
        }
        finally
        {
            _tgRunning = await Task.Run(TgProxyRunner.IsRunning);
        }
    }

    private async Task<bool> ResolveTgConflictsAsync()
    {
        if ((await Task.Run(TgProxyRunner.FindForeignPids)).Count == 0) return true;
        if (!ConfirmStopForeignTg())
        {
            Notify?.Invoke("TG WS Proxy не запущен", "Сначала закройте отдельно запущенный TG WS Proxy.", ToolTipIcon.Warning);
            return false;
        }
        await Task.Run(TgProxyRunner.StopForeign);
        return true;
    }
}

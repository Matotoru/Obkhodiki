using System.Windows.Forms;
using Obkhodiki.Core.Updates;

namespace Obkhodiki.App;

/// <summary>Updates of the app itself, with the same confirmation flow as the helpers it downloads.</summary>
internal sealed partial class AppController
{
    public const string AppProductName = AppInfo.Name;

    private AppReleaseClient? _appClient;
    private volatile ReleaseInfo? _availableAppUpdate;
    private bool _confirmingAppUpdate;

    private AppReleaseClient AppClient => _appClient ??= new AppReleaseClient(_http);

    public ReleaseInfo? AvailableAppUpdate => _availableAppUpdate;

    /// <summary>The new version is unpacked and verified; the app must exit so it can replace the files.</summary>
    public event Action? ExitForUpdateRequested;

    private async Task CheckAppUpdateAsync(bool userInitiated)
    {
        ReleaseInfo? found = null;
        await Serialized($"Проверка обновлений {AppInfo.Name}…", silentErrors: !userInitiated, async () =>
        {
            var latest = await Task.Run(() => AppClient.GetLatestAsync(CancellationToken.None));
            var newer = ReleaseVersion.IsNewer(latest.Version, SelfUpdate.CurrentVersion);
            found = newer && (userInitiated || latest.Version != Settings.AppSkippedVersion) ? latest : null;
            _availableAppUpdate = found;
            if (found is null)
            {
                if (userInitiated) Notify?.Invoke("Обновлений нет", $"Установлена последняя версия {AppInfo.Name} {SelfUpdate.CurrentVersion}.", ToolTipIcon.Info);
                return;
            }
            if (!userInitiated) AnnounceUpdate(AppProductName, found.Version);
        });
        if (found is not null && userInitiated) await InstallAvailableAppUpdateAsync();
    }

    public Task CheckAppUpdateManuallyAsync() => CheckAppUpdateAsync(userInitiated: true);

    public async Task InstallAvailableAppUpdateAsync()
    {
        var release = _availableAppUpdate;
        if (release is null || BusyText is not null || _confirmingAppUpdate) return;
        _confirmingAppUpdate = true;
        bool confirmed;
        try
        {
            confirmed = ConfirmUpdate(AppProductName, release.Version, SelfUpdate.CurrentVersion);
        }
        finally
        {
            _confirmingAppUpdate = false;
        }
        if (!confirmed)
        {
            Settings.AppSkippedVersion = release.Version;
            _settingsStore.Save(Settings);
            return;
        }

        string? ready = null;
        await Serialized($"Загрузка {AppInfo.Name} {release.Version}…", silentErrors: false, async () =>
        {
            try
            {
                await using var zip = await Task.Run(() => AppClient.DownloadAsync(release, CancellationToken.None));
                var dir = await Task.Run(() => SelfUpdate.Extract(zip, release.Version));
                SetBusy("Проверка новой версии…");
                await SelfUpdate.TestAsync(dir, release.Version);
                ready = dir;
            }
            catch (UpdateException ex)
            {
                Log.Error("App update failed", ex);
                if (ex.ReleaseDefect)
                {
                    Settings.AppSkippedVersion = release.Version;
                    _settingsStore.Save(Settings);
                    _availableAppUpdate = null;
                }
                Notify?.Invoke($"Обновление {AppInfo.Name} не удалось", $"{ex.Message}\nОставлена версия {SelfUpdate.CurrentVersion}.", ToolTipIcon.Warning);
            }
        });
        if (ready is null) return;

        Log.Info($"Installing {AppInfo.Name} {release.Version}: restarting");
        try
        {
            SelfUpdate.LaunchApplier(ready);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // E.g. an antivirus blocked the new exe: keep running the current version and say so.
            Log.Error("Update applier could not be started", ex);
            Notify?.Invoke($"Обновление {AppInfo.Name} не удалось", $"Не удалось запустить установку: {ex.Message}\nОставлена версия {SelfUpdate.CurrentVersion}.", ToolTipIcon.Warning);
            try { Directory.Delete(ready, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            return;
        }
        ExitForUpdateRequested?.Invoke();
    }

    /// <summary>Remembers that the user has seen the "what's new" of this version.</summary>
    public Task MarkVersionSeenAsync() => Serialized("Сохранение настроек…", silentErrors: true, () =>
    {
        Settings.LastSeenVersion = SelfUpdate.CurrentVersion;
        _settingsStore.Save(Settings);
        return Task.CompletedTask;
    });
}

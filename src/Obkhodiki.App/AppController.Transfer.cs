using System.Windows.Forms;
using Obkhodiki.Core.Games;
using Obkhodiki.Core.Settings;

namespace Obkhodiki.App;

/// <summary>Settings export and import (no secrets: VPS subscriptions and server links stay on this computer).</summary>
internal sealed partial class AppController
{
    public Task ExportSettingsAsync(string path) => Serialized("Сохранение настроек в файл…", silentErrors: false, async () =>
    {
        var games = new Dictionary<string, string>();
        foreach (var p in Settings.GameProfiles)
        {
            var file = GameIpsetPath(p.Id);
            if (File.Exists(file)) games[p.Id] = await File.ReadAllTextAsync(file);
        }
        var lists = new Dictionary<string, string>();
        foreach (var name in SettingsBundle.UserListNames)
        {
            var file = Path.Combine(AppPaths.UserLists, name);
            if (File.Exists(file)) lists[name] = await File.ReadAllTextAsync(file);
        }
        var targets = File.Exists(AppPaths.Targets) ? await File.ReadAllTextAsync(AppPaths.Targets) : null;
        var bundle = SettingsBundle.Create(Settings, games, lists, targets, SelfUpdate.CurrentVersion, DateTimeOffset.Now);
        await File.WriteAllTextAsync(path, bundle.Serialize());
        Log.Info($"Settings exported to {path}");
        Notify?.Invoke("Настройки сохранены", "Подписки и ссылки серверов VPS в файл не попадают.", ToolTipIcon.Info);
    });

    /// <summary>Reads and validates a settings file without applying it (for the confirmation).</summary>
    public static async Task<SettingsBundle> ReadSettingsFileAsync(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > SettingsBundle.MaxFileBytes) throw new FormatException("Файл слишком большой для файла настроек.");
        return SettingsBundle.Parse(await File.ReadAllTextAsync(path));
    }

    public Task ImportSettingsAsync(SettingsBundle bundle) => Serialized("Загрузка настроек…", silentErrors: false, async () =>
    {
        var wasRunning = _runner.IsRunning;
        bundle.Settings.ApplyTo(Settings);
        _settingsStore.Save(Settings);

        Directory.CreateDirectory(AppPaths.GamesDir);
        foreach (var (id, text) in bundle.GameAddresses)
        {
            if (Settings.GameProfiles.Any(p => p.Id == id)) await File.WriteAllTextAsync(GameIpsetPath(id), text);
        }
        Directory.CreateDirectory(AppPaths.UserLists);
        foreach (var (name, text) in bundle.UserLists)
        {
            await File.WriteAllTextAsync(Path.Combine(AppPaths.UserLists, name), text);
        }
        if (bundle.Targets is { } targets) await File.WriteAllTextAsync(AppPaths.Targets, targets);
        Log.Info($"Settings imported (made by {bundle.CreatedWith ?? "?"}, {Settings.GameProfiles.Count} games)");

        // The new strategy, game rules and lists take effect now, not at the next start.
        if (wasRunning && _engine is not null) await StartCoreAsync();
        await ApplyVpnCoreAsync(interactive: true, needProbe: false);
        Notify?.Invoke("Настройки загружены", $"Игр: {Settings.GameProfiles.Count}. Подписки VPS остались прежними.", ToolTipIcon.Info);
    });
}

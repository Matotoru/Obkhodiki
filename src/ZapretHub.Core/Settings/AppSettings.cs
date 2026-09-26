using System.Text.Json;
using System.Text.Json.Serialization;
using ZapretHub.Core.Games;
using ZapretHub.Core.Strategies;

namespace ZapretHub.Core.Settings;

public sealed class AppSettings
{
    public string? SelectedStrategy { get; set; }
    public GameFilterMode GameFilter { get; set; } = GameFilterMode.Disabled;
    public string GameTcpRange { get; set; } = GameFilterOptions.DefaultRange;
    public string GameUdpRange { get; set; } = GameFilterOptions.DefaultRange;

    /// <summary>Turn bypass on automatically when the app starts (used with autostart).</summary>
    public bool EnableOnStart { get; set; }

    public bool CheckUpdatesOnStart { get; set; } = true;

    /// <summary>A Flowseal release that failed to install or start; background checks skip it
    /// instead of re-downloading it at every launch. A manual check retries it.</summary>
    public string? SkippedUpdateVersion { get; set; }

    public List<GameProfile> GameProfiles { get; set; } = new();

    /// <summary>Keep Flowseal's tg-ws-proxy running (Telegram bypass).</summary>
    public bool TelegramEnabled { get; set; }

    /// <summary>A tg-ws-proxy release the user declined or that failed; background checks skip it.</summary>
    public string? TelegramSkippedVersion { get; set; }
}

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public AppSettingsStore(string path) => _path = path;

    public AppSettings Load()
    {
        if (!File.Exists(_path)) return new AppSettings();
        string text;
        try
        {
            text = File.ReadAllText(_path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or unreadable: run with defaults rather than fail to start.
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(text, Json);
            if (settings is not null) return Sanitize(settings);
        }
        catch (JsonException)
        {
        }

        // Keep the unusable file for inspection instead of silently losing it on next save.
        try
        {
            File.Copy(_path, _path + ".corrupt", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Backup is best effort; starting with defaults matters more.
        }
        return new AppSettings();
    }

    // Syntactically valid but unusable values would make every winws start fail; fall back per field.
    private static AppSettings Sanitize(AppSettings s)
    {
        if (!Enum.IsDefined(s.GameFilter)) s.GameFilter = GameFilterMode.Disabled;
        if (!IsValidRange(s.GameTcpRange)) s.GameTcpRange = GameFilterOptions.DefaultRange;
        if (!IsValidRange(s.GameUdpRange)) s.GameUdpRange = GameFilterOptions.DefaultRange;
        s.GameProfiles = Games.GameProfiles.Sanitize(s.GameProfiles);
        return s;
    }

    private static bool IsValidRange(string? range)
    {
        if (range is null) return false;
        try
        {
            _ = new GameFilterOptions(GameFilterMode.All, range, GameFilterOptions.DefaultRange);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}

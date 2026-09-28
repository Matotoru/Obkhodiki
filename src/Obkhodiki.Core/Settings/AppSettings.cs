using System.Text.Json;
using System.Text.Json.Serialization;
using Obkhodiki.Core.Games;
using Obkhodiki.Core.Strategies;

namespace Obkhodiki.Core.Settings;

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

    /// <summary>Programs (exe names) whose traffic always goes through the VPS.</summary>
    public List<string> VpnProcesses { get; set; } = new();

    /// <summary>Sites (domains incl. subdomains) that always go through the VPS.</summary>
    public List<string> VpnDomains { get; set; } = new();

    /// <summary>Programs that never go through the VPS, in either mode (beats every other rule).</summary>
    public List<string> VpnBypassProcesses { get; set; } = new();

    /// <summary>Sites (domains incl. subdomains) and addresses (IP or CIDR) that never go through the VPS.</summary>
    public List<string> VpnBypassEntries { get; set; } = new();

    /// <summary>
    /// Master switch. Off: sing-box never runs (no tunnel, no measurements, Auto games go direct), but every
    /// VPS setting (mode, categories, programs, servers) is kept for when it is turned back on.
    /// </summary>
    public bool VpnEnabled { get; set; } = true;

    /// <summary>"Включить VPS": everything goes through the VPS except <see cref="VpnDirectCategories"/> and direct games.</summary>
    public bool VpnFullTunnel { get; set; }

    /// <summary>Rule-set categories sent through the VPS in selective mode (ids from RuleCatalog.Proxy).</summary>
    public List<string> VpnProxyCategories { get; set; } = new();

    /// <summary>Rule-set categories kept direct in full-tunnel mode (ids from RuleCatalog.Direct).</summary>
    public List<string> VpnDirectCategories { get; set; } = new() { "ru" };

    /// <summary>Tag of the chosen server (see VpnServerEntry.TagFor).</summary>
    public string? VpnSelectedServer { get; set; }

    /// <summary>Every few minutes switch to the fastest server (paused while a game runs).</summary>
    public bool VpnAutoBest { get; set; } = true;

    /// <summary>An app release the user declined or that failed; background checks skip it.</summary>
    public string? AppSkippedVersion { get; set; }

    /// <summary>Last version whose "what's new" the user has seen (null: fresh install, nothing to show).</summary>
    public string? LastSeenVersion { get; set; }

    /// <summary>A sing-box release the user declined or that failed; background checks skip it.</summary>
    public string? SingBoxSkippedVersion { get; set; }
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
        s.VpnProcesses = (s.VpnProcesses ?? new()).Where(p => p is not null && Vpn.SingBoxConfig.IsValidProcessName(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.VpnDomains = (s.VpnDomains ?? new()).Select(d => d is null ? null : Vpn.SingBoxConfig.NormalizeDomain(d))
            .Where(d => d is not null).Select(d => d!).Distinct().ToList();
        s.VpnBypassProcesses = (s.VpnBypassProcesses ?? new()).Where(p => p is not null && Vpn.SingBoxConfig.IsValidProcessName(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        s.VpnBypassEntries = (s.VpnBypassEntries ?? new()).Select(e => e is null ? null : Vpn.SingBoxConfig.NormalizeBypassEntry(e))
            .Where(e => e is not null).Select(e => e!).Distinct().ToList();
        s.VpnProxyCategories = Vpn.RuleCatalog.Sanitize(s.VpnProxyCategories, Vpn.RuleCatalog.Proxy);
        s.VpnDirectCategories = Vpn.RuleCatalog.Sanitize(s.VpnDirectCategories, Vpn.RuleCatalog.Direct);
        if (s.VpnSelectedServer is { } tag && !System.Text.RegularExpressions.Regex.IsMatch(tag, "^s-[0-9a-f]{10}$")) s.VpnSelectedServer = null;
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

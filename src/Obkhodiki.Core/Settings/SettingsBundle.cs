using System.Text.Json;
using System.Text.Json.Serialization;
using Obkhodiki.Core.Games;
using Obkhodiki.Core.Strategies;

namespace Obkhodiki.Core.Settings;

/// <summary>
/// Settings exported to a file for another computer or a reinstall. Never contains secrets (VPS subscription or
/// server links, which live encrypted elsewhere) or per-machine state (chosen server, skipped versions).
/// An imported file is untrusted input for an elevated app: everything is size-capped and re-validated.
/// </summary>
public sealed class SettingsBundle
{
    public const string FormatName = "obkhodiki-settings";
    public const int CurrentVersion = 1;
    public const int MaxFileBytes = 8 * 1024 * 1024;
    private const int MaxListBytes = 2 * 1024 * 1024;

    /// <summary>User list files that may travel (the ones the "My lists" page edits).</summary>
    public static readonly IReadOnlyList<string> UserListNames = new[] { "list-general-user.txt", "list-exclude-user.txt", "ipset-exclude-user.txt" };

    // Absent from a foreign JSON file, so an empty object is not mistaken for settings.
    public string? Format { get; set; }
    public int Version { get; set; }
    public string? CreatedWith { get; set; }
    public DateTimeOffset Created { get; set; }

    public PortableSettings Settings { get; set; } = new();

    /// <summary>Address list of each game profile (profile id → file text).</summary>
    public Dictionary<string, string> GameAddresses { get; set; } = new();

    /// <summary>User lists by file name (see <see cref="UserListNames"/>).</summary>
    public Dictionary<string, string> UserLists { get; set; } = new();

    /// <summary>targets.txt for the strategy auto-select.</summary>
    public string? Targets { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static SettingsBundle Create(AppSettings settings, IReadOnlyDictionary<string, string> gameAddresses,
        IReadOnlyDictionary<string, string> userLists, string? targets, string appVersion, DateTimeOffset now) => new()
    {
        Format = FormatName,
        Version = CurrentVersion,
        CreatedWith = appVersion,
        Created = now,
        Settings = PortableSettings.From(settings),
        GameAddresses = settings.GameProfiles.Where(p => gameAddresses.ContainsKey(p.Id)).ToDictionary(p => p.Id, p => gameAddresses[p.Id]),
        UserLists = userLists.Where(kv => UserListNames.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value),
        Targets = targets,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <exception cref="FormatException">Not a settings file of a supported version, or too large.</exception>
    public static SettingsBundle Parse(string text)
    {
        if (text.Length > MaxFileBytes) throw new FormatException("Файл слишком большой для файла настроек.");
        SettingsBundle? bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<SettingsBundle>(text, Json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Это не файл настроек Obkhodiki: " + ex.Message, ex);
        }
        if (bundle is null || bundle.Format != FormatName) throw new FormatException("Это не файл настроек Obkhodiki.");
        if (bundle.Version is < 1 or > CurrentVersion) throw new FormatException($"Файл сохранён более новой версией программы (формат {bundle.Version}). Обновите Obkhodiki.");

        bundle.Settings ??= new PortableSettings();
        var ids = (bundle.Settings.GameProfiles ?? new()).Select(p => p?.Id).ToHashSet();
        bundle.GameAddresses = (bundle.GameAddresses ?? new())
            .Where(kv => GameProfiles.IsValidId(kv.Key) && ids.Contains(kv.Key) && kv.Value is not null && kv.Value.Length <= MaxListBytes)
            .ToDictionary(kv => kv.Key, kv => OnlyAddresses(kv.Value));
        bundle.UserLists = (bundle.UserLists ?? new())
            .Where(kv => UserListNames.Contains(kv.Key) && kv.Value is not null && kv.Value.Length <= MaxListBytes)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        if (bundle.Targets is { Length: > MaxListBytes }) bundle.Targets = null;
        return bundle;
    }

    /// <summary>A game's address list keeps comments and valid IP/CIDR lines only.</summary>
    private static string OnlyAddresses(string text) => string.Join('\n', text.Split('\n')
        .Select(l => l.Trim())
        .Where(l => l.Length > 0 && (l.StartsWith('#') || Vpn.SingBoxConfig.NormalizeCidr(l) is not null)));
}

/// <summary>The part of <see cref="AppSettings"/> that is the user's choice rather than machine state.</summary>
public sealed class PortableSettings
{
    public string? SelectedStrategy { get; set; }
    public GameFilterMode GameFilter { get; set; } = GameFilterMode.Disabled;
    public string GameTcpRange { get; set; } = GameFilterOptions.DefaultRange;
    public string GameUdpRange { get; set; } = GameFilterOptions.DefaultRange;
    public bool EnableOnStart { get; set; }
    public bool CheckUpdatesOnStart { get; set; } = true;
    public List<GameProfile> GameProfiles { get; set; } = new();
    public bool TelegramEnabled { get; set; }
    public List<string> VpnProcesses { get; set; } = new();
    public List<string> VpnDomains { get; set; } = new();
    public List<string> VpnBypassProcesses { get; set; } = new();
    public List<string> VpnBypassEntries { get; set; } = new();
    public bool VpnEnabled { get; set; } = true;
    public bool VpnFullTunnel { get; set; }
    public List<string> VpnProxyCategories { get; set; } = new();
    public List<string> VpnDirectCategories { get; set; } = new() { "ru" };
    public bool VpnAutoBest { get; set; } = true;
    public string AppTheme { get; set; } = "system";
    public string AppPalette { get; set; } = "cat";

    public static PortableSettings From(AppSettings s) => new()
    {
        SelectedStrategy = s.SelectedStrategy,
        GameFilter = s.GameFilter,
        GameTcpRange = s.GameTcpRange,
        GameUdpRange = s.GameUdpRange,
        EnableOnStart = s.EnableOnStart,
        CheckUpdatesOnStart = s.CheckUpdatesOnStart,
        GameProfiles = s.GameProfiles.ToList(),
        TelegramEnabled = s.TelegramEnabled,
        VpnProcesses = s.VpnProcesses.ToList(),
        VpnDomains = s.VpnDomains.ToList(),
        VpnBypassProcesses = s.VpnBypassProcesses.ToList(),
        VpnBypassEntries = s.VpnBypassEntries.ToList(),
        VpnEnabled = s.VpnEnabled,
        VpnFullTunnel = s.VpnFullTunnel,
        VpnProxyCategories = s.VpnProxyCategories.ToList(),
        VpnDirectCategories = s.VpnDirectCategories.ToList(),
        VpnAutoBest = s.VpnAutoBest,
        AppTheme = s.AppTheme,
        AppPalette = s.AppPalette,
    };

    /// <summary>Copies the choices into <paramref name="target"/>, keeping its machine state, and validates the result.</summary>
    public void ApplyTo(AppSettings target)
    {
        target.SelectedStrategy = SelectedStrategy;
        target.GameFilter = GameFilter;
        target.GameTcpRange = GameTcpRange;
        target.GameUdpRange = GameUdpRange;
        target.EnableOnStart = EnableOnStart;
        target.CheckUpdatesOnStart = CheckUpdatesOnStart;
        target.GameProfiles = GameProfiles ?? new();
        target.TelegramEnabled = TelegramEnabled;
        target.VpnProcesses = VpnProcesses ?? new();
        target.VpnDomains = VpnDomains ?? new();
        target.VpnBypassProcesses = VpnBypassProcesses ?? new();
        target.VpnBypassEntries = VpnBypassEntries ?? new();
        target.VpnEnabled = VpnEnabled;
        target.VpnFullTunnel = VpnFullTunnel;
        target.VpnProxyCategories = VpnProxyCategories ?? new();
        target.VpnDirectCategories = VpnDirectCategories ?? new();
        target.VpnAutoBest = VpnAutoBest;
        target.AppTheme = AppTheme is "system" or "light" or "dark" ? AppTheme : "system";
        target.AppPalette = AppPalette is { Length: > 0 and <= 32 } p && p.All(c => char.IsAsciiLetterLower(c) || c == '-') ? p : "cat";
        AppSettingsStore.Sanitize(target);
    }
}

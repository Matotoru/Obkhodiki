using System.Text.Json;
using System.Text.Json.Serialization;
using Obkhodiki.Core.Settings;

namespace Obkhodiki.Core.Games;

/// <summary>
/// One game profile with its recorded addresses, for passing to a friend: one person records a match, the others
/// import the file. Untrusted when imported, so it is size-capped and validated like a settings file.
/// </summary>
public sealed class GameShare
{
    public const string FormatName = "obkhodiki-game";
    public const int CurrentVersion = 1;
    public const int MaxFileBytes = 2 * 1024 * 1024;
    public const string FileExtension = ".obkgame";

    public string? Format { get; set; }
    public int Version { get; set; }
    public string? CreatedWith { get; set; }
    public GameProfile Profile { get; set; } = new();
    public string Addresses { get; set; } = "";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static GameShare Create(GameProfile profile, string addresses, string appVersion) => new()
    {
        Format = FormatName,
        Version = CurrentVersion,
        CreatedWith = appVersion,
        // Only what describes the game; on/off is the receiver's choice (it arrives off until they turn it on).
        Profile = new GameProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            ProcessName = profile.ProcessName,
            TcpPorts = profile.TcpPorts,
            UdpPorts = profile.UdpPorts,
            Route = profile.Route,
            ProbeEndpoints = profile.ProbeEndpoints.ToList(),
        },
        Addresses = addresses,
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <exception cref="FormatException">Not a game file, too large, or without a usable profile.</exception>
    public static GameShare Parse(string text)
    {
        if (text.Length > MaxFileBytes) throw new FormatException("Файл слишком большой для профиля игры.");
        GameShare? share;
        try
        {
            share = JsonSerializer.Deserialize<GameShare>(text, Json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Это не файл игры Obkhodiki: " + ex.Message, ex);
        }
        if (share is null || share.Format != FormatName) throw new FormatException("Это не файл игры Obkhodiki.");
        if (share.Version is < 1 or > CurrentVersion) throw new FormatException($"Файл сохранён более новой версией программы (формат {share.Version}). Обновите Obkhodiki.");

        var profile = share.Profile ?? throw new FormatException("В файле нет профиля игры.");
        profile.Enabled = false;
        var clean = GameProfiles.Sanitize(new[] { profile });
        if (clean.Count == 0) throw new FormatException("Профиль игры в файле повреждён.");
        share.Profile = clean[0];
        share.Addresses = SettingsBundle.OnlyAddresses(share.Addresses ?? "");
        return share;
    }
}

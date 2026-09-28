using System.Text.Json;

namespace Obkhodiki.Core.Games;

/// <param name="Id">Profile id the game gets when added (also links a hand-made profile with the same id).</param>
/// <param name="ProcessNames">Executables of the game; the first one that is running (or the first listed) is used.</param>
/// <param name="Asns">Networks the game's servers live in; their announced prefixes become the address list.
/// Empty: the servers are on shared clouds or community hosts, so the addresses are recorded during a match.</param>
/// <param name="SteamAppId">For the cover picture.</param>
/// <param name="UsesSdr">Talks to Steam Datagram Relay: the exact relay addresses and ports come from Steam's
/// GetSDRConfig for this app (<see cref="Asns"/> is the fallback when Steam does not answer).</param>
/// <param name="Colors">Two brand colours ("#RRGGBB") for the drawn card when there is no picture.</param>
public sealed record GameCatalogEntry(
    string Id,
    string Name,
    IReadOnlyList<string> ProcessNames,
    string TcpPorts,
    string UdpPorts,
    IReadOnlyList<int> Asns,
    int? SteamAppId,
    string Publisher,
    (string From, string To) Colors,
    bool UsesSdr = false)
{
    public bool NeedsRecording => Asns.Count == 0;
}

/// <summary>Built-in profiles of popular games, so most players do not have to record anything.</summary>
public static class GameCatalog
{
    public const int ValveAsn = 32590;
    public const int RiotAsn = 6507;

    // Steam Datagram Relay: Valve's own games and others built on Steam networking (WARDOGS) talk UDP to these relays.
    private const string SdrUdp = "27015-27200";

    public static IReadOnlyList<GameCatalogEntry> Entries { get; } = new GameCatalogEntry[]
    {
        new("wardogs", "WARDOGS", new[] { "WardogsClient-Win64-Shipping.exe" }, "", SdrUdp, new[] { ValveAsn }, 1867240, "Bulkhead", ("#3B4A2A", "#C9A227"), UsesSdr: true),
        new("cs2", "Counter-Strike 2", new[] { "cs2.exe" }, "", SdrUdp, new[] { ValveAsn }, 730, "Valve", ("#1B2838", "#DE9B35"), UsesSdr: true),
        new("dota2", "Dota 2", new[] { "dota2.exe" }, "", SdrUdp, new[] { ValveAsn }, 570, "Valve", ("#1A0B0B", "#B8321E"), UsesSdr: true),
        new("deadlock", "Deadlock", new[] { "project8.exe", "deadlock.exe" }, "", SdrUdp, new[] { ValveAsn }, 1422450, "Valve", ("#1E1A14", "#C8A26B"), UsesSdr: true),
        new("valorant", "VALORANT", new[] { "VALORANT-Win64-Shipping.exe" }, "", "7000-8000,8180-8181", new[] { RiotAsn }, null, "Riot Games", ("#0F1923", "#FF4655")),
        new("lol", "League of Legends", new[] { "League of Legends.exe" }, "", "5000-5500", new[] { RiotAsn }, null, "Riot Games", ("#0A1428", "#C8AA6E")),
        new("apex", "Apex Legends", new[] { "r5apex_dx12.exe", "r5apex.exe" }, "", "", Array.Empty<int>(), 1172470, "Respawn / EA", ("#2B0B0B", "#DA292A")),
        new("pubg", "PUBG: BATTLEGROUNDS", new[] { "TslGame.exe" }, "", "", Array.Empty<int>(), 578080, "KRAFTON", ("#1A1A1A", "#F2A900")),
        new("fortnite", "Fortnite", new[] { "FortniteClient-Win64-Shipping.exe" }, "", "", Array.Empty<int>(), null, "Epic Games", ("#1B1464", "#8E44FF")),
        new("rust", "Rust", new[] { "RustClient.exe" }, "", "", Array.Empty<int>(), 252490, "Facepunch", ("#1E1E1E", "#CD412B")),
        new("marvel-rivals", "Marvel Rivals", new[] { "Marvel-Win64-Shipping.exe" }, "", "", Array.Empty<int>(), 2767030, "NetEase", ("#10131F", "#E62429")),
        new("tarkov", "Escape from Tarkov", new[] { "EscapeFromTarkov.exe" }, "", "", Array.Empty<int>(), 3932890, "Battlestate Games", ("#15130F", "#9A8866")),
    };

    public static GameCatalogEntry? Find(string? id) => Entries.FirstOrDefault(e => e.Id == id);

    /// <summary>The catalog entry a profile belongs to: same id, or the same game executable.</summary>
    public static GameCatalogEntry? For(GameProfile profile) =>
        Find(profile.Id) ?? Entries.FirstOrDefault(e => profile.ProcessName is { } exe && e.ProcessNames.Contains(exe, StringComparer.OrdinalIgnoreCase));

    /// <summary>Steam's relay list for one app (public, no key).</summary>
    public static string SdrConfigUrl(int steamAppId) => $"https://api.steampowered.com/ISteamApps/GetSDRConfig/v1/?appid={steamAppId}";

    /// <summary>Relay addresses (/32) and the union of their UDP port ranges from a GetSDRConfig answer.</summary>
    /// <exception cref="FormatException">Not a usable relay list.</exception>
    public static (IReadOnlyList<string> Addresses, string UdpPorts) ParseSdrConfig(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("pops", out var pops) || pops.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Unexpected GetSDRConfig answer.");
        }
        var addresses = new List<string>();
        var ports = PortSet.Empty;
        foreach (var pop in pops.EnumerateObject())
        {
            if (pop.Value.ValueKind != JsonValueKind.Object || !pop.Value.TryGetProperty("relays", out var relays) || relays.ValueKind != JsonValueKind.Array) continue;
            foreach (var relay in relays.EnumerateArray())
            {
                if (relay.ValueKind != JsonValueKind.Object || !relay.TryGetProperty("ipv4", out var ip) || ip.GetString() is not { } text
                    || Vpn.SingBoxConfig.NormalizeCidr(text) is not { } cidr || !cidr.EndsWith("/32", StringComparison.Ordinal))
                {
                    continue;
                }
                if (!relay.TryGetProperty("port_range", out var range) || range.ValueKind != JsonValueKind.Array || range.GetArrayLength() != 2
                    || !range[0].TryGetInt32(out var from) || !range[1].TryGetInt32(out var to) || from < 1 || to > 65535 || to < from)
                {
                    continue;
                }
                addresses.Add(cidr);
                ports = ports.Union(PortSet.Parse($"{from}-{to}"));
            }
        }
        if (addresses.Count == 0) throw new FormatException("GetSDRConfig has no relays.");
        return (addresses.Distinct().ToList(), ports.ToString());
    }

    /// <summary>RIPEstat "announced-prefixes" for one network.</summary>
    public static string PrefixesUrl(int asn) => $"https://stat.ripe.net/data/announced-prefixes/data.json?resource=AS{asn}";

    /// <summary>Prefixes from a RIPEstat answer, normalized; anything that is not a plain CIDR is dropped.</summary>
    public static IReadOnlyList<string> ParsePrefixes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) || !data.TryGetProperty("prefixes", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Unexpected RIPEstat answer.");
        }
        var result = new List<string>();
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("prefix", out var p) && p.GetString() is { } text
                && text.Contains('/') && Vpn.SingBoxConfig.NormalizeCidr(text) is { } cidr)
            {
                result.Add(cidr);
            }
        }
        return result.Distinct().ToList();
    }

    /// <summary>Store API answer that names the current cover file (newer games keep it under a hashed path).</summary>
    public static string AppDetailsUrl(int steamAppId) => $"https://store.steampowered.com/api/appdetails?appids={steamAppId}&filters=basic";

    /// <summary>The cover URL from an appdetails answer; null unless it is an https image on Steam's CDN.</summary>
    public static string? ParseHeaderImage(string json, int steamAppId)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty(steamAppId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var app)
            && app.TryGetProperty("data", out var data) && data.TryGetProperty("header_image", out var h) && h.GetString() is { } url
            && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            && uri.Host.EndsWith(".steamstatic.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri.GetLeftPart(UriPartial.Path);
        }
        return null;
    }

    /// <summary>
    /// Where to fetch the landscape cover, in order. Steam's CDN hosts differ in reachability between networks
    /// (Fastly answered where Akamai and Cloudflare hung), so the same file is tried on several of them.
    /// </summary>
    public static IReadOnlyList<string> CoverUrls(int steamAppId, string? headerImage)
    {
        var urls = new List<string>();
        if (headerImage is not null)
        {
            var path = new Uri(headerImage).AbsolutePath;
            urls.Add("https://shared.fastly.steamstatic.com" + path);
            urls.Add(headerImage);
        }
        urls.Add($"https://cdn.fastly.steamstatic.com/steam/apps/{steamAppId}/header.jpg");
        urls.Add($"https://shared.fastly.steamstatic.com/store_item_assets/steam/apps/{steamAppId}/header.jpg");
        return urls.Distinct().ToList();
    }
}

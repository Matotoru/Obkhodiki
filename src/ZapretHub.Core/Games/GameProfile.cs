using System.Text;
using System.Text.RegularExpressions;

namespace ZapretHub.Core.Games;

/// <summary>A user-defined game module: when enabled, game rules apply only to its learned addresses and ports.</summary>
public sealed class GameProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    /// <summary>Executable name without path (e.g. "wardogs.exe"), used to relearn.</summary>
    public string? ProcessName { get; set; }
    public string TcpPorts { get; set; } = "";
    public string UdpPorts { get; set; } = "";
}

public static partial class GameProfiles
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex ValidId();

    public static bool IsValidId(string? id) => id is not null && ValidId().IsMatch(id);

    /// <summary>The profile's address list, kept with the user's lists so it can be edited by hand.</summary>
    public static string IpsetFileName(string id) =>
        IsValidId(id) ? $"ipset-game-{id}.txt" : throw new ArgumentException($"Invalid profile id '{id}'.");

    /// <summary>Stable file-safe id from a display name; Cyrillic is transliterated.</summary>
    public static string MakeId(string name, IEnumerable<string> existingIds)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (Translit.TryGetValue(c, out var t)) sb.Append(t);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var baseId = sb.ToString().Trim('-');
        if (baseId.Length > 30) baseId = baseId[..30].Trim('-');
        if (baseId.Length == 0) baseId = "game";

        var taken = existingIds.ToHashSet();
        var id = baseId;
        for (var n = 2; taken.Contains(id); n++) id = $"{baseId}-{n}";
        return id;
    }

    /// <summary>A display name safe to put on one comment line: no control characters (no line breaks), capped length.</summary>
    public static string SafeComment(string name)
    {
        var clean = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        return clean.Length > 60 ? clean[..60] : clean;
    }

    /// <summary>Drops profiles with unusable ids or duplicates; disables ones whose ports do not parse.</summary>
    public static List<GameProfile> Sanitize(IEnumerable<GameProfile?>? profiles)
    {
        var result = new List<GameProfile>();
        var seen = new HashSet<string>();
        foreach (var p in profiles ?? Enumerable.Empty<GameProfile?>())
        {
            if (p is null || !IsValidId(p.Id) || !seen.Add(p.Id)) continue;
            p.Name = string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name;
            try
            {
                p.TcpPorts = PortSet.Parse(p.TcpPorts ?? "").ToString();
                p.UdpPorts = PortSet.Parse(p.UdpPorts ?? "").ToString();
            }
            catch (FormatException)
            {
                p.TcpPorts = "";
                p.UdpPorts = "";
                p.Enabled = false;
            }
            result.Add(p);
        }
        return result;
    }

    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh",
        ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
        ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu",
        ['я'] = "ya",
    };
}

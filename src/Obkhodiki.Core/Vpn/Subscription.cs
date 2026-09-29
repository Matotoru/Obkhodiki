using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Obkhodiki.Core.Vpn;

/// <summary>A server with a stable tag: the same link always gets the same tag, so a choice survives restarts.</summary>
public sealed record VpnServerEntry(string Tag, IProxyServer Server)
{
    /// <summary>From the link without its "#name": panels rewrite names (remaining traffic, days) on every fetch.</summary>
    public static string TagFor(string rawLink)
    {
        var link = rawLink.Trim().ReplaceLineEndings("\n");
        // A WireGuard config is not a link: its "#" lines are comments, and the whole text is the identity.
        var hash = AwgServer.LooksLikeConfig(link) ? -1 : link.IndexOf('#');
        if (hash >= 0) link = link[..hash];
        return "s-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(link)))[..10].ToLowerInvariant();
    }

    public static VpnServerEntry FromLink(string rawLink) => new(TagFor(rawLink), ProxyLinks.Parse(rawLink));
}

/// <summary>Traffic and expiry from the "subscription-userinfo" header (3x-ui, Marzban and others).</summary>
public sealed record SubscriptionUsage(long Upload, long Download, long Total, DateTimeOffset? Expire)
{
    public long Used => Upload + Download;

    public static SubscriptionUsage? Parse(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        long up = 0, down = 0, total = 0;
        DateTimeOffset? expire = null;
        foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || !long.TryParse(part[(eq + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value < 0) continue;
            switch (part[..eq].Trim().ToLowerInvariant())
            {
                case "upload": up = value; break;
                case "download": down = value; break;
                case "total": total = value; break;
                case "expire" when value > 0 && value < 253402300799: expire = DateTimeOffset.FromUnixTimeSeconds(value); break;
            }
        }
        return new SubscriptionUsage(up, down, total, expire);
    }
}

/// <summary>Result of parsing a subscription body.</summary>
public sealed record SubscriptionContent(IReadOnlyList<string> Links, IReadOnlyList<VpnServerEntry> Servers, IReadOnlyList<string> Skipped)
{
    public const int MaxServers = 200;

    /// <summary>
    /// The body is a list of share links, one per line, usually base64-encoded as a whole. Unsupported or broken
    /// entries are skipped with a reason (never with the link itself: it holds credentials).
    /// </summary>
    public static SubscriptionContent Parse(string body)
    {
        var text = body.Trim();
        if (!text.Contains("://") && ShadowsocksLink.DecodeBase64(string.Concat(text.Where(c => !char.IsWhiteSpace(c)))) is { } decoded
            && decoded.Contains("://"))
        {
            text = decoded;
        }

        var links = new List<string>();
        var servers = new List<VpnServerEntry>();
        var skipped = new List<string>();
        var seen = new HashSet<string>();
        IEnumerable<string> lines = text.Split('\n');
        if (XrayJsonSubscription.LooksLikeJson(text))
        {
            try
            {
                lines = XrayJsonSubscription.ToLinks(text, skipped);
            }
            catch (System.Text.Json.JsonException)
            {
                skipped.Add("ответ похож на JSON, но не читается");
                lines = Array.Empty<string>();
            }
        }
        foreach (var line in lines.Select(l => l.Trim()))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (servers.Count >= MaxServers)
            {
                skipped.Add($"больше {MaxServers} серверов");
                break;
            }
            try
            {
                var entry = VpnServerEntry.FromLink(line);
                if (!seen.Add(entry.Tag)) continue;
                links.Add(line);
                servers.Add(entry);
            }
            catch (FormatException ex)
            {
                var scheme = line.IndexOf("://", StringComparison.Ordinal) is var i and > 0 and < 16 ? line[..i] : "?";
                skipped.Add($"{scheme}: {ex.Message}");
            }
        }
        return new SubscriptionContent(links, servers, skipped);
    }

    /// <summary>Only http(s) URLs without embedded credentials.</summary>
    public static Uri ValidateUrl(string text)
    {
        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new FormatException("Ссылка подписки должна начинаться с https:// или http://");
        }
        if (uri.UserInfo.Length > 0) throw new FormatException("Логин и пароль в ссылке подписки не поддерживаются.");
        return uri;
    }

    /// <summary>"profile-title" may be plain or "base64:…".</summary>
    public static string? ParseTitle(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var value = header.Trim();
        if (value.StartsWith("base64:", StringComparison.OrdinalIgnoreCase)) value = ShadowsocksLink.DecodeBase64(value[7..]) ?? "";
        return LinkParsing.SafeName(value);
    }

    /// <summary>"profile-update-interval" in hours, clamped to 1–168; 12 h when absent.</summary>
    public static TimeSpan ParseUpdateInterval(string? header) =>
        int.TryParse(header?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            ? TimeSpan.FromHours(Math.Clamp(hours, 1, 168))
            : TimeSpan.FromHours(12);
}

using System.Text.RegularExpressions;

namespace Obkhodiki.Core.Diagnostics;

/// <summary>
/// Removes what must not end up in a diagnostics report the user may post publicly (e.g. a GitHub issue):
/// share-link passwords and UUIDs, subscription paths and query tokens, bearer secrets, the Windows user name in
/// paths, and the user's own VPS servers — a published server address is easy to block, so each becomes a stable
/// tag ("srv-1a2b") that still shows which server a log line is about.
/// </summary>
public static partial class Redactor
{
    // scheme://user@ → scheme://***@ (hysteria2, vless, trojan, ss, tuic…); the app's own logs already mask most.
    [GeneratedRegex(@"\b((?:hysteria2|hy2|vless|vmess|trojan|ss|tuic|socks5?|https?)://)[^\s@/""']+@", RegexOptions.IgnoreCase)]
    private static partial Regex UserInfo();

    // https://host/any/path?query → https://host/*** (subscription URLs carry the token in the path or query).
    [GeneratedRegex(@"\b(https?://[A-Za-z0-9.\-]+(?::\d+)?)(/[^\s""'<>]*)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPath();

    [GeneratedRegex(@"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Uuid();

    [GeneratedRegex(@"(?i)\b(password|passwd|secret|token|auth|obfs-password|pbk|sid|key)(\s*[=:]\s*|""\s*:\s*"")([^\s,;&""']+)")]
    private static partial Regex KeyValue();

    [GeneratedRegex(@"(?i)(Bearer\s+)[A-Za-z0-9._\-]+")]
    private static partial Regex Bearer();

    // Legacy Shadowsocks links ss://BASE64(method:password@host:port) and vmess://BASE64(JSON with the UUID):
    // everything, the secret included, is encoded.
    [GeneratedRegex(@"(?i)\b(ss|vmess)://[A-Za-z0-9+/_=\-]{8,}(?=[\s#""'<>]|$)")]
    private static partial Regex LegacySs();

    // C:\Users\<name>\… (also with forward slashes; names may contain spaces when a path follows) → %USERPROFILE%\…
    [GeneratedRegex(@"(?i)\b[A-Z]:[\\/]Users[\\/](?:[^\\/\r\n""'<>:*?|]+(?=[\\/])|[^\\/\s""'<>:*?|]+)")]
    private static partial Regex UserProfile();

    /// <summary>Public URLs that are safe and useful to keep whole (releases, docs).</summary>
    private static readonly string[] KeepHosts = { "github.com", "api.github.com", "raw.githubusercontent.com", "objects.githubusercontent.com" };

    /// <summary>Stable short tag for a private server name or address.</summary>
    public static string ServerTag(string host) =>
        "srv-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(host.Trim().ToLowerInvariant())))[..4].ToLowerInvariant();

    /// <param name="privateHosts">The user's VPS servers and subscription host: names and resolved addresses.</param>
    public static string Redact(string text, IEnumerable<string>? privateHosts = null)
    {
        var result = LegacySs().Replace(text, "$1://***");
        result = UserInfo().Replace(result, "$1***@");
        result = UrlPath().Replace(result, m =>
        {
            // A malformed "URL" in a log line must not stop the whole report: mask it.
            if (!Uri.TryCreate(m.Groups[1].Value, UriKind.Absolute, out var uri)) return "https://***";
            return KeepHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) || m.Groups[2].Value == "/" ? m.Value : m.Groups[1].Value + "/***";
        });
        result = Uuid().Replace(result, "********-****-****-****-************");
        result = KeyValue().Replace(result, "$1$2***");
        result = Bearer().Replace(result, "$1***");
        result = UserProfile().Replace(result, "%USERPROFILE%");
        // Longest first, so "a.b.example.com" is not half-replaced by "example.com".
        foreach (var host in (privateHosts ?? Array.Empty<string>()).Where(h => h.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(h => h.Length))
        {
            result = Regex.Replace(result, @"(?<![A-Za-z0-9.\-])" + Regex.Escape(host) + @"(?![A-Za-z0-9\-]|\.[A-Za-z0-9])", ServerTag(host), RegexOptions.IgnoreCase);
        }
        return result;
    }
}

using System.Text.RegularExpressions;

namespace Obkhodiki.Core.Diagnostics;

/// <summary>
/// Removes credentials from text that goes into a diagnostics report the user will send to someone: share-link
/// passwords and UUIDs, subscription paths and query tokens, bearer secrets. Host names stay (they are needed
/// to understand a problem and are not a key to anything).
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

    /// <summary>Public URLs that are safe and useful to keep whole (releases, docs).</summary>
    private static readonly string[] KeepHosts = { "github.com", "api.github.com", "raw.githubusercontent.com", "objects.githubusercontent.com" };

    public static string Redact(string text)
    {
        var result = UserInfo().Replace(text, "$1***@");
        result = UrlPath().Replace(result, m =>
        {
            var host = new Uri(m.Groups[1].Value).Host;
            return KeepHosts.Contains(host, StringComparer.OrdinalIgnoreCase) || m.Groups[2].Value == "/" ? m.Value : m.Groups[1].Value + "/***";
        });
        result = Uuid().Replace(result, "********-****-****-****-************");
        result = KeyValue().Replace(result, "$1$2***");
        result = Bearer().Replace(result, "$1***");
        return result;
    }
}

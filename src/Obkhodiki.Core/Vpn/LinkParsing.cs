using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Obkhodiki.Core.Vpn;

/// <summary>A proxy server from a share link or a subscription. Every field ends up in the sing-box config.</summary>
public interface IProxyServer
{
    string Protocol { get; }
    string Host { get; }
    int Port { get; }
    string? Name { get; }

    /// <summary>Certificate checks are off: the path can impersonate the server.</summary>
    bool Insecure { get; }

    /// <summary>The sing-box outbound for this server.</summary>
    JsonObject ToOutbound(string tag);
}

/// <summary>Parses any supported share link.</summary>
public static class ProxyLinks
{
    public static IProxyServer Parse(string text)
    {
        var raw = text.Trim();
        var scheme = raw.IndexOf("://", StringComparison.Ordinal) is var i and > 0 ? raw[..i].ToLowerInvariant() : "";
        return scheme switch
        {
            "hysteria2" or "hy2" => Hysteria2Link.Parse(raw),
            "vless" => VlessLink.Parse(raw),
            "trojan" => TrojanLink.Parse(raw),
            "ss" => ShadowsocksLink.Parse(raw),
            "vmess" => throw new FormatException("VMess пока не поддерживается."),
            _ => throw new FormatException("Поддерживаются ссылки vless://, hysteria2://, trojan:// и ss://"),
        };
    }

    /// <summary>Safe for logs and UI: never includes passwords or ids.</summary>
    public static string Describe(IProxyServer s) =>
        $"{s.Protocol}://***@{(s.Host.Contains(':') ? $"[{s.Host}]" : s.Host)}:{s.Port}" + (s.Name is null ? "" : $" ({s.Name})");
}

/// <summary>Pieces shared by all link formats.</summary>
internal static partial class LinkParsing
{
    [GeneratedRegex(@"^(?=.{1,253}\z)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\z")]
    public static partial Regex DnsName();

    [GeneratedRegex(@"^[A-Za-z0-9./_-]{1,32}\z")]
    public static partial Regex AlpnToken();

    /// <summary>Splits "scheme://userinfo@host:port/?query#name".</summary>
    public static (string UserInfo, string HostPort, Dictionary<string, string> Query, string? Name) Split(string raw, string what)
    {
        var afterScheme = raw[(raw.IndexOf("://", StringComparison.Ordinal) + 3)..];

        string? name = null;
        var hash = afterScheme.IndexOf('#');
        if (hash >= 0)
        {
            name = Uri.UnescapeDataString(afterScheme[(hash + 1)..]);
            afterScheme = afterScheme[..hash];
        }

        var query = "";
        var q = afterScheme.IndexOf('?');
        if (q >= 0)
        {
            query = afterScheme[(q + 1)..];
            afterScheme = afterScheme[..q];
        }
        afterScheme = afterScheme.TrimEnd('/');

        var at = afterScheme.LastIndexOf('@');
        if (at <= 0) throw new FormatException($"В ссылке нет {what} (часть перед @).");
        return (afterScheme[..at], afterScheme[(at + 1)..], ParseQuery(query), SafeName(name));
    }

    /// <summary>
    /// Host and a port list such as "443,20000-30000" (Hysteria2 port hopping). A single port is a one-element list.
    /// </summary>
    public static (string Host, IReadOnlyList<(int From, int To)> Ports) SplitHostPorts(string hostPort)
    {
        var colon = hostPort.LastIndexOf(':');
        if (colon <= 0) throw new FormatException("В ссылке нет порта сервера.");
        var portText = hostPort[(colon + 1)..];
        var (host, _) = SplitHostPort(hostPort[..colon] + ":1");
        return (host, ParsePortRanges(portText));
    }

    public static IReadOnlyList<(int From, int To)> ParsePortRanges(string text)
    {
        static int Port(string p) =>
            int.TryParse(p, System.Globalization.NumberStyles.None, null, out var port) && port is >= 1 and <= 65535
                ? port
                : throw new FormatException("Неверный порт сервера.");
        var parts = text.Split(',');
        if (parts.Length is 0 or > 16) throw new FormatException("Неверный список портов сервера.");
        var result = new List<(int, int)>();
        foreach (var part in parts)
        {
            var dash = part.IndexOf('-');
            var from = Port(dash < 0 ? part : part[..dash]);
            var to = dash < 0 ? from : Port(part[(dash + 1)..]);
            if (to < from) throw new FormatException("Неверный диапазон портов сервера.");
            result.Add((from, to));
        }
        return result;
    }

    public static (string Host, int Port) SplitHostPort(string hostPort)
    {
        string host;
        string portText;
        if (hostPort.StartsWith('['))
        {
            var close = hostPort.IndexOf(']');
            if (close < 0 || close + 1 >= hostPort.Length || hostPort[close + 1] != ':') throw new FormatException("Неверный адрес сервера.");
            host = hostPort[1..close];
            portText = hostPort[(close + 2)..];
            if (!IPAddress.TryParse(host, out var ip6) || ip6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 || host.Contains('%'))
            {
                throw new FormatException("Неверный IPv6-адрес сервера.");
            }
        }
        else
        {
            var colon = hostPort.LastIndexOf(':');
            if (colon <= 0) throw new FormatException("В ссылке нет порта сервера.");
            host = hostPort[..colon];
            portText = hostPort[(colon + 1)..];
            if (!DnsName().IsMatch(host)) throw new FormatException("Неверное имя сервера.");
        }

        if (portText.Contains(',') || portText.Contains('-')) throw new FormatException("Диапазоны портов (port hopping) пока не поддерживаются.");
        if (!int.TryParse(portText, System.Globalization.NumberStyles.None, null, out var port) || port is < 1 or > 65535)
        {
            throw new FormatException("Неверный порт сервера.");
        }
        return (host, port);
    }

    public static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(eq < 0 ? pair : pair[..eq]);
            var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            result[key] = value;
        }
        return result;
    }

    public static string? Get(this Dictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public static bool Flag(this Dictionary<string, string> query, string key) =>
        query.TryGetValue(key, out var v) && (v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase));

    // The name ends up in logs, menus and dialogs: no line breaks, bidi or other invisible characters.
    public static string? SafeName(string? name)
    {
        if (name is null) return null;
        var clean = new string(name.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();
        if (clean.Length > 64) clean = clean[..64];
        return clean.Length == 0 ? null : clean;
    }

    public static void ValidateSecret(string value, string what)
    {
        if (value.Length is 0 or > 512 || value.Any(char.IsControl))
        {
            throw new FormatException($"{what}: пустое, слишком длинное или содержит управляющие символы.");
        }
    }

    public static IReadOnlyList<string> ParseAlpn(string? text)
    {
        var alpn = text is null
            ? new List<string>()
            : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (alpn.Any(x => !AlpnToken().IsMatch(x))) throw new FormatException("Неверное значение alpn.");
        return alpn;
    }

    public static string? ValidSni(string? sni)
    {
        if (sni is not null && !DnsName().IsMatch(sni)) throw new FormatException("Неверное значение sni.");
        return sni;
    }

    public static JsonArray Array(IEnumerable<string> items) => new(items.Select(i => (JsonNode)i!).ToArray());
}

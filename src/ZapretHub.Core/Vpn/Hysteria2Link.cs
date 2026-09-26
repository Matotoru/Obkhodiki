using System.Net;
using System.Text.RegularExpressions;

namespace ZapretHub.Core.Vpn;

/// <summary>
/// A parsed hysteria2:// (or hy2://) share link. Every field ends up in a config file and on a command path,
/// so each one is validated; anything unsupported is rejected rather than silently dropped.
/// </summary>
public sealed partial record Hysteria2Link(
    string Host,
    int Port,
    string Password,
    string? ObfsPassword,
    string? Sni,
    bool Insecure,
    IReadOnlyList<string> Alpn,
    string? Name)
{
    [GeneratedRegex(@"^(?=.{1,253}\z)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\z")]
    private static partial Regex DnsName();

    [GeneratedRegex(@"^[A-Za-z0-9./_-]{1,32}\z")]
    private static partial Regex AlpnToken();

    public static Hysteria2Link Parse(string text)
    {
        var raw = text.Trim();
        if (!raw.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Ссылка должна начинаться с hysteria2:// или hy2://");
        }
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
        if (at <= 0) throw new FormatException("В ссылке нет пароля (часть перед @).");
        var password = Uri.UnescapeDataString(afterScheme[..at]);
        var hostPort = afterScheme[(at + 1)..];

        var (host, port) = SplitHostPort(hostPort);

        var parameters = ParseQuery(query);
        string? obfs = null;
        if (parameters.TryGetValue("obfs", out var obfsType) && !string.Equals(obfsType, "salamander", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Тип obfs '{obfsType}' не поддерживается (только salamander).");
        }
        if (parameters.TryGetValue("obfs-password", out var obfsPassword) && obfsPassword.Length > 0) obfs = obfsPassword;

        var sni = parameters.TryGetValue("sni", out var s) && s.Length > 0 ? s : null;
        if (sni is not null && !DnsName().IsMatch(sni)) throw new FormatException("Неверное значение sni.");

        var insecure = parameters.TryGetValue("insecure", out var ins) && (ins == "1" || ins.Equals("true", StringComparison.OrdinalIgnoreCase));

        var alpn = parameters.TryGetValue("alpn", out var a) && a.Length > 0
            ? a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : new List<string>();
        if (alpn.Any(x => !AlpnToken().IsMatch(x))) throw new FormatException("Неверное значение alpn.");

        if (parameters.ContainsKey("pinSHA256")) throw new FormatException("Параметр pinSHA256 пока не поддерживается.");

        ValidateSecret(password, "Пароль");
        if (obfs is not null) ValidateSecret(obfs, "obfs-password");

        return new Hysteria2Link(host, port, password, obfs, sni, insecure, alpn, SafeName(name));
    }

    /// <summary>Safe for logs and UI: never includes the passwords.</summary>
    public override string ToString() => $"hysteria2://***@{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}" + (Name is null ? "" : $" ({Name})");

    private static (string Host, int Port) SplitHostPort(string hostPort)
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

    private static Dictionary<string, string> ParseQuery(string query)
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

    // The name ends up in logs, menus and dialogs: no line breaks, bidi or other invisible characters.
    private static string? SafeName(string? name)
    {
        if (name is null) return null;
        var clean = new string(name.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();
        if (clean.Length > 64) clean = clean[..64];
        return clean.Length == 0 ? null : clean;
    }

    private static void ValidateSecret(string value, string what)
    {
        if (value.Length is 0 or > 512 || value.Any(char.IsControl))
        {
            throw new FormatException($"{what}: пустое, слишком длинное или содержит управляющие символы.");
        }
    }
}

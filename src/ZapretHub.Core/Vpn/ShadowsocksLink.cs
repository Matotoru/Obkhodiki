using System.Text;
using System.Text.Json.Nodes;

namespace ZapretHub.Core.Vpn;

/// <summary>ss:// links: SIP002 (base64 "method:password" or percent-encoded userinfo) and the legacy all-base64 form.</summary>
public sealed record ShadowsocksLink(string Host, int Port, string Method, string Password, string? Name) : IProxyServer
{
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "aes-128-gcm", "aes-192-gcm", "aes-256-gcm", "chacha20-ietf-poly1305", "xchacha20-ietf-poly1305",
        "2022-blake3-aes-128-gcm", "2022-blake3-aes-256-gcm", "2022-blake3-chacha20-poly1305",
    };

    public string Protocol => "shadowsocks";
    public bool Insecure => false;

    public static ShadowsocksLink Parse(string text)
    {
        var raw = text.Trim();
        if (!raw.StartsWith("ss://", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Ссылка должна начинаться с ss://");

        var body = raw[5..];
        string? name = null;
        var hash = body.IndexOf('#');
        if (hash >= 0)
        {
            name = Uri.UnescapeDataString(body[(hash + 1)..]);
            body = body[..hash];
        }
        var q = body.IndexOf('?');
        var query = q >= 0 ? LinkParsing.ParseQuery(body[(q + 1)..]) : new Dictionary<string, string>();
        if (q >= 0) body = body[..q];
        body = body.TrimEnd('/');
        if (query.Get("plugin") is not null) throw new FormatException("Плагины Shadowsocks не поддерживаются.");

        // Legacy form: the whole "method:password@host:port" is base64.
        if (!body.Contains('@')) body = DecodeBase64(body) ?? throw new FormatException("Не удалось разобрать ссылку ss://");

        var at = body.LastIndexOf('@');
        if (at <= 0) throw new FormatException("В ссылке нет метода и пароля.");
        var userInfo = body[..at];
        var (host, port) = LinkParsing.SplitHostPort(body[(at + 1)..]);

        var plain = Uri.UnescapeDataString(userInfo);
        if (!plain.Contains(':')) plain = DecodeBase64(userInfo) ?? throw new FormatException("Не удалось разобрать метод и пароль.");
        var colon = plain.IndexOf(':');
        if (colon <= 0) throw new FormatException("Не удалось разобрать метод и пароль.");
        var method = plain[..colon].ToLowerInvariant();
        var password = plain[(colon + 1)..];
        if (!Methods.Contains(method)) throw new FormatException($"Шифр Shadowsocks '{method}' не поддерживается.");
        LinkParsing.ValidateSecret(password, "Пароль");
        if (method.StartsWith("2022-", StringComparison.Ordinal)) ValidateKeys2022(method, password);

        return new ShadowsocksLink(host, port, method, password, LinkParsing.SafeName(name));
    }

    // 2022 methods take base64 keys of an exact size ("iPSK:uPSK" for multi-user); a wrong one stops sing-box
    // from starting at all, taking every other server of a subscription down with it.
    private static void ValidateKeys2022(string method, string password)
    {
        var size = method == "2022-blake3-aes-128-gcm" ? 16 : 32;
        var keys = password.Split(':');
        // sing-box supports multi-user (EIH) keys only with the AES ciphers.
        if (keys.Length > 1 && method == "2022-blake3-chacha20-poly1305") throw new FormatException("Несколько ключей Shadowsocks 2022 работают только с AES.");
        foreach (var key in keys)
        {
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(key);
            }
            catch (FormatException)
            {
                throw new FormatException("Ключ Shadowsocks 2022 должен быть в base64.");
            }
            if (bytes.Length != size) throw new FormatException($"Ключ Shadowsocks 2022 для {method} должен быть {size} байт.");
        }
    }

    /// <summary>Standard or URL-safe base64, padding optional; null when it is not base64 text.</summary>
    internal static string? DecodeBase64(string text)
    {
        var s = text.Trim().Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public JsonObject ToOutbound(string tag) => new()
    {
        ["type"] = "shadowsocks",
        ["tag"] = tag,
        ["server"] = Host,
        ["server_port"] = Port,
        ["method"] = Method,
        ["password"] = Password,
    };

    public override string ToString() => ProxyLinks.Describe(this);
}

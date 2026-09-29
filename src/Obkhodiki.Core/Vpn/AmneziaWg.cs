using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Obkhodiki.Core.Vpn;

/// <summary>
/// An AmneziaWG (or plain WireGuard) server: the "[Interface]/[Peer]" config that AmneziaVPN exports, or the one
/// inside a vpn:// key. Runs as an "awg" endpoint of amnezia-box (Amnezia's sing-box fork); plain WireGuard is
/// AmneziaWG with every obfuscation field left at zero.
/// </summary>
public sealed record AwgServer(
    string Host,
    int Port,
    string PrivateKey,
    string PublicKey,
    string? PresharedKey,
    IReadOnlyList<string> Addresses,
    IReadOnlyList<string> AllowedIps,
    int Mtu,
    int Keepalive,
    IReadOnlyDictionary<string, string> Obfuscation,
    string? Name) : IProxyServer
{
    public const int DefaultMtu = 1376;
    public const int DefaultKeepalive = 25;

    // AmneziaWG fields in config spelling -> amnezia-box option name, with the allowed range for numbers
    // (null: a string checked by the pattern of its kind).
    private static readonly (string Key, string Option, int Min, int Max)[] NumberFields =
    {
        ("Jc", "jc", 0, 128), ("Jmin", "jmin", 0, 1280), ("Jmax", "jmax", 0, 1280),
        ("S1", "s1", 0, 65535), ("S2", "s2", 0, 65535), ("S3", "s3", 0, 65535), ("S4", "s4", 0, 65535),
    };

    // Magic headers: a 32-bit number, or a range of them (AmneziaWG 2.0).
    private static readonly (string Key, string Option)[] HeaderFields = { ("H1", "h1"), ("H2", "h2"), ("H3", "h3"), ("H4", "h4") };

    // Signature packets ("<b 0x…><r 16>…") and the newer tuning knobs: passed through as text.
    private static readonly (string Key, string Option)[] TextFields =
    {
        ("I1", "i1"), ("I2", "i2"), ("I3", "i3"), ("I4", "i4"), ("I5", "i5"),
        ("ContentPaddingAddition", "content_padding_addition"), ("RekeyAfterTime", "rekey_after_time"),
        ("RekeyTimeout", "rekey_timeout"), ("RejectAfterTime", "reject_after_time"), ("KeepaliveTimeout", "keepalive_timeout"),
        ("MaxHandshakeAttempts", "max_handshake_attempts"),
    };

    private static readonly (string Key, string Option)[] FlagFields = { ("RandomTrailers", "random_trailers"), ("DisableCookies", "disable_cookies") };
    private const string HeaderProtectionKey = "HeaderProtectionKey";

    public string Protocol => Obfuscation.Count > 0 ? "awg" : "wireguard";
    public bool Insecure => false;
    public bool IsEndpoint => true;
    public bool NeedsAmnezia => true;

    /// <summary>A config in the WireGuard/AmneziaWG file format ("[Interface]" and one "[Peer]").</summary>
    public static bool LooksLikeConfig(string text) =>
        text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase) && text.Contains("[Peer]", StringComparison.OrdinalIgnoreCase);

    /// <param name="fallbackHost">Server address when the config's Endpoint has none (vpn:// keys keep it apart).</param>
    /// <exception cref="FormatException">Not a usable config.</exception>
    public static AwgServer ParseConfig(string text, string? name = null, string? fallbackHost = null, int? fallbackPort = null, int? mtu = null)
    {
        if (text.Length > 64 * 1024) throw new FormatException("Конфиг WireGuard слишком длинный.");
        var iface = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var peer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? section = null;
        var peers = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('['))
            {
                section = line.ToLowerInvariant() switch
                {
                    "[interface]" => iface,
                    "[peer]" => ++peers == 1 ? peer : null, // the first server only
                    _ => null,
                };
                continue;
            }
            var eq = line.IndexOf('='); // keys end in "=": split at the first one only
            if (eq <= 0 || section is null) continue;
            section[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        if (iface.Count == 0 || peers == 0) throw new FormatException("В конфиге нет секций [Interface] и [Peer].");

        string host;
        int port;
        if (peer.TryGetValue("Endpoint", out var endpoint) && endpoint.Length > 0)
        {
            (host, port) = LinkParsing.SplitHostPort(endpoint);
        }
        else if (fallbackHost is not null && fallbackPort is { } p)
        {
            (host, port) = LinkParsing.SplitHostPort((fallbackHost.Contains(':') ? $"[{fallbackHost}]" : fallbackHost) + ":" + p);
        }
        else
        {
            throw new FormatException("В конфиге нет адреса сервера (Endpoint).");
        }

        var obfuscation = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, option, min, max) in NumberFields)
        {
            if (!iface.TryGetValue(key, out var v) || v.Length == 0) continue;
            if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min || n > max)
            {
                throw new FormatException($"Неверное значение {key} в конфиге AmneziaWG.");
            }
            if (n != 0) obfuscation[option] = n.ToString(CultureInfo.InvariantCulture);
        }
        if (obfuscation.TryGetValue("jmin", out var jmin) && obfuscation.TryGetValue("jmax", out var jmax) && int.Parse(jmin) > int.Parse(jmax))
        {
            throw new FormatException("В конфиге AmneziaWG Jmin больше Jmax.");
        }
        foreach (var (key, option) in HeaderFields)
        {
            if (!iface.TryGetValue(key, out var v) || v.Length == 0) continue;
            if (!IsHeader(v)) throw new FormatException($"Неверное значение {key} в конфиге AmneziaWG.");
            obfuscation[option] = v;
        }
        foreach (var (key, option) in TextFields)
        {
            if (!iface.TryGetValue(key, out var v) || v.Length == 0) continue;
            if (!IsPlainText(v)) throw new FormatException($"Неверное значение {key} в конфиге AmneziaWG.");
            obfuscation[option] = v;
        }
        foreach (var (key, option) in FlagFields)
        {
            if (!iface.TryGetValue(key, out var v) || v.Length == 0) continue;
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1") obfuscation[option] = "true";
            else if (!v.Equals("false", StringComparison.OrdinalIgnoreCase) && v != "0") throw new FormatException($"Неверное значение {key} в конфиге AmneziaWG.");
        }
        if (iface.TryGetValue(HeaderProtectionKey, out var hpk) && hpk.Length > 0)
        {
            obfuscation["header_protection_key"] = Key(hpk, HeaderProtectionKey, anyLength: true);
        }

        var addresses = Prefixes(iface.GetValueOrDefault("Address"), "Address");
        if (addresses.Count == 0) throw new FormatException("В конфиге нет адреса клиента (Address).");
        var allowed = Prefixes(peer.GetValueOrDefault("AllowedIPs"), "AllowedIPs");
        if (allowed.Count == 0) allowed = new List<string> { "0.0.0.0/0", "::/0" };

        var mtuValue = mtu ?? DefaultMtu;
        if (iface.TryGetValue("MTU", out var mtuText) && mtuText.Length > 0 &&
            !int.TryParse(mtuText, NumberStyles.None, CultureInfo.InvariantCulture, out mtuValue))
        {
            throw new FormatException("Неверное значение MTU в конфиге.");
        }
        if (mtuValue is < 576 or > 1500) throw new FormatException("Неверное значение MTU в конфиге.");

        // Only a hint for NAT: "off", "25s" or anything odd falls back instead of rejecting the whole config.
        var keepalive = DefaultKeepalive;
        if (peer.TryGetValue("PersistentKeepalive", out var ka) && ka.Length > 0)
        {
            var digits = new string(ka.TakeWhile(char.IsAsciiDigit).ToArray());
            keepalive = ka.Equals("off", StringComparison.OrdinalIgnoreCase) ? 0
                : int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var k) && k <= 65535 ? k
                : DefaultKeepalive;
        }

        return new AwgServer(
            host,
            port,
            Key(iface.GetValueOrDefault("PrivateKey"), "PrivateKey"),
            Key(peer.GetValueOrDefault("PublicKey"), "PublicKey"),
            peer.TryGetValue("PresharedKey", out var psk) && psk.Length > 0 ? Key(psk, "PresharedKey") : null,
            addresses,
            allowed,
            mtuValue,
            keepalive,
            obfuscation,
            LinkParsing.SafeName(name));
    }

    private static bool IsHeader(string v)
    {
        var dash = v.IndexOf('-');
        static bool Number(string s) => s.Length is > 0 and <= 10 && uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        return dash < 0 ? Number(v) : Number(v[..dash]) && Number(v[(dash + 1)..]);
    }

    // The values end up in a "key=value" line of the tunnel's control protocol: printable ASCII only.
    private static bool IsPlainText(string v) => v.Length <= 4096 && v.All(c => c is >= ' ' and <= '~');

    private static string Key(string? text, string what, bool anyLength = false)
    {
        var key = text?.Trim() ?? "";
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(key);
        }
        catch (FormatException)
        {
            throw new FormatException($"Неверный ключ {what} в конфиге.");
        }
        if (anyLength ? bytes.Length is 0 or > 64 : bytes.Length != 32) throw new FormatException($"Неверный ключ {what} в конфиге.");
        return key;
    }

    private static List<string> Prefixes(string? text, string what)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result.Add(SingBoxConfig.NormalizeCidr(part) ?? throw new FormatException($"Неверный адрес в {what}: {part}"));
        }
        return result.Distinct().ToList();
    }

    public JsonObject ToOutbound(string tag)
    {
        // amnezia-box hands the peer to the tunnel as "address:port" and only takes an IP there.
        var ip = ResolveHost(Host);
        var peer = new JsonObject
        {
            ["address"] = ip.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{ip}]" : ip.ToString(),
            ["port"] = Port,
            ["public_key"] = PublicKey,
            ["allowed_ips"] = LinkParsing.Array(AllowedIps),
        };
        if (PresharedKey is not null) peer["preshared_key"] = PresharedKey;
        if (Keepalive > 0) peer["persistent_keepalive_interval"] = Keepalive;

        var o = new JsonObject
        {
            ["type"] = "awg",
            ["tag"] = tag,
            ["address"] = LinkParsing.Array(Addresses),
            ["private_key"] = PrivateKey,
            ["mtu"] = Mtu,
        };
        foreach (var (option, value) in Obfuscation)
        {
            o[option] = option switch
            {
                "jc" or "jmin" or "jmax" or "s1" or "s2" or "s3" or "s4" => int.Parse(value, CultureInfo.InvariantCulture),
                "random_trailers" or "disable_cookies" => true,
                _ => value,
            };
        }
        o["peers"] = new JsonArray(peer);
        return o;
    }

    /// <summary>Overridable for tests; the real lookup prefers IPv4 (most VPS answer on it).</summary>
    public static Func<string, IPAddress> ResolveHost { get; set; } = host =>
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        try
        {
            var all = Dns.GetHostAddresses(host);
            return all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? all.FirstOrDefault()
                ?? throw new FormatException($"Не удалось найти адрес сервера {host}.");
        }
        catch (SocketException ex)
        {
            throw new FormatException($"Не удалось найти адрес сервера {host}: {ex.Message}", ex);
        }
    };

    public override string ToString() => ProxyLinks.Describe(this);
}

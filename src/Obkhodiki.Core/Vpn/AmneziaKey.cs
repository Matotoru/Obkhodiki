using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Obkhodiki.Core.Vpn;

/// <summary>
/// AmneziaVPN's "vpn://" key: base64url of Qt's qCompress (4-byte big-endian size, then a zlib stream) of a JSON
/// description of one server with its protocols ("containers"). The server's own protocol choice is used when it
/// is one this app runs: AmneziaWG/WireGuard (through amnezia-box) or Xray (VLESS, Shadowsocks).
/// </summary>
public static class AmneziaKey
{
    public const string Scheme = "vpn://";
    private const int MaxKeyLength = 64 * 1024;
    private const int MaxJsonBytes = 256 * 1024;

    public static bool IsKey(string text) => text.TrimStart().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

    /// <exception cref="FormatException">Broken key, or none of its protocols is supported.</exception>
    public static IProxyServer Parse(string text)
    {
        var json = Decode(text);
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new FormatException("Ключ vpn:// повреждён.", ex);
        }
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Ключ vpn:// повреждён.");

        if (!root.TryGetProperty("containers", out var containers) || containers.ValueKind != JsonValueKind.Array || containers.GetArrayLength() == 0)
        {
            // Amnezia Premium and Amnezia Free keys hold an account token; the server settings come from Amnezia's API.
            if (root.TryGetProperty("api_key", out _) || root.TryGetProperty("auth_data", out _) || root.TryGetProperty("config_version", out _) ||
                root.TryGetProperty("api_config", out _))
            {
                throw new FormatException("Это ключ Amnezia Premium/Free: настройки сервера по нему выдаёт сервис Amnezia, такой ключ не поддерживается. " +
                                          "Нужен ключ своего сервера или конфиг AmneziaWG (.conf).");
            }
            throw new FormatException("В ключе vpn:// нет протоколов.");
        }

        var name = Str(root, "description") ?? Str(root, "name");
        var host = Str(root, "hostName");
        var preferred = Str(root, "defaultContainer");
        var ordered = containers.EnumerateArray()
            .Where(c => c.ValueKind == JsonValueKind.Object)
            .OrderBy(c => Str(c, "container") == preferred ? 0 : 1)
            .ToList();

        var problems = new List<string>();
        foreach (var container in ordered)
        {
            var kind = Str(container, "container") ?? "?";
            try
            {
                if (FromContainer(container, kind, name, host) is { } server) return server;
                problems.Add(Describe(kind) + " не поддерживается");
            }
            catch (FormatException ex)
            {
                problems.Add($"{Describe(kind)}: {ex.Message}");
            }
        }
        throw new FormatException("В ключе нет подходящего протокола (" + string.Join("; ", problems) +
                                  "). Поддерживаются AmneziaWG, WireGuard и XRay.");
    }

    private static IProxyServer? FromContainer(JsonElement container, string kind, string? name, string? host)
    {
        switch (kind)
        {
            case "amnezia-awg" or "amnezia-awg2" or "amnezia-wireguard":
            {
                var body = Obj(container, "awg") ?? Obj(container, "wireguard") ?? throw new FormatException("нет настроек");
                var last = LastConfig(body);
                int? port = int.TryParse(Str(last, "port") ?? Str(body, "port"), out var p) ? p : null;
                int? mtu = int.TryParse(Str(last, "mtu") ?? Str(body, "mtu"), out var m) ? m : null;
                var config = Str(last, "config") ?? throw new FormatException("в ключе нет конфига клиента (подключение ещё не создано на сервере)");
                return AwgServer.ParseConfig(config, name, Str(last, "hostName") ?? host, port, mtu);
            }
            case "amnezia-xray" or "amnezia-ssxray":
            {
                var body = Obj(container, "xray") ?? Obj(container, "ssxray") ?? throw new FormatException("нет настроек");
                if (!body.TryGetProperty("last_config", out var raw) || raw.ValueKind != JsonValueKind.String) throw new FormatException("нет конфига клиента");
                var skipped = new List<string>();
                var links = XrayJsonSubscription.ToLinks(raw.GetString()!, skipped);
                if (links.Count == 0) throw new FormatException(skipped.FirstOrDefault() ?? "в конфиге XRay нет сервера");
                var server = ProxyLinks.Parse(links[0]);
                return name is null ? server : WithName(server, name);
            }
            default:
                return null;
        }
    }

    private static IProxyServer WithName(IProxyServer server, string name) => server switch
    {
        VlessLink v => v with { Name = LinkParsing.SafeName(name) },
        TrojanLink t => t with { Name = LinkParsing.SafeName(name) },
        ShadowsocksLink s => s with { Name = LinkParsing.SafeName(name) },
        VmessLink v => v with { Name = LinkParsing.SafeName(name) },
        _ => server,
    };

    private static string Describe(string kind) => kind switch
    {
        "amnezia-openvpn" => "OpenVPN",
        "amnezia-shadowsocks" => "OpenVPN over Shadowsocks",
        "amnezia-openvpn-cloak" => "OpenVPN over Cloak",
        "amnezia-ipsec" => "IKEv2",
        "amnezia-awg" or "amnezia-awg2" => "AmneziaWG",
        "amnezia-wireguard" => "WireGuard",
        "amnezia-xray" or "amnezia-ssxray" => "XRay",
        var other => other.StartsWith("amnezia-", StringComparison.Ordinal) ? other[8..] : other,
    };

    /// <summary>"last_config" is a JSON object serialized into a string.</summary>
    private static JsonElement LastConfig(JsonElement body)
    {
        if (!body.TryGetProperty("last_config", out var raw) || raw.ValueKind != JsonValueKind.String) throw new FormatException("нет конфига клиента");
        try
        {
            using var doc = JsonDocument.Parse(raw.GetString()!);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : throw new FormatException("конфиг клиента повреждён");
        }
        catch (JsonException ex)
        {
            throw new FormatException("конфиг клиента повреждён", ex);
        }
    }

    /// <summary>The JSON inside a key.</summary>
    public static string Decode(string text)
    {
        var raw = text.Trim();
        if (!raw.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase)) throw new FormatException("Ключ должен начинаться с vpn://");
        var payload = raw[Scheme.Length..].Trim();
        if (payload.Length is 0 or > MaxKeyLength) throw new FormatException("Пустой или слишком длинный ключ vpn://.");

        byte[] bytes;
        try
        {
            var s = payload.Replace('-', '+').Replace('_', '/');
            bytes = Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        }
        catch (FormatException ex)
        {
            throw new FormatException("Ключ vpn:// повреждён (не base64).", ex);
        }

        // Older keys and some third-party tools skip the compression.
        if (bytes.Length > 0 && bytes[0] == (byte)'{') return Utf8(bytes);
        if (bytes.Length < 6) throw new FormatException("Ключ vpn:// повреждён.");
        var declared = (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        if (declared is <= 0 or > MaxJsonBytes) throw new FormatException("Ключ vpn:// повреждён или слишком большой.");
        try
        {
            using var zlib = new ZLibStream(new MemoryStream(bytes, 4, bytes.Length - 4), CompressionMode.Decompress);
            using var output = new MemoryStream();
            var chunk = new byte[16384];
            int read;
            while ((read = zlib.Read(chunk, 0, chunk.Length)) > 0)
            {
                output.Write(chunk, 0, read);
                if (output.Length > MaxJsonBytes) throw new FormatException("Ключ vpn:// слишком большой.");
            }
            return Utf8(output.ToArray());
        }
        catch (InvalidDataException ex)
        {
            throw new FormatException("Ключ vpn:// повреждён (не распаковывается).", ex);
        }
    }

    /// <summary>The reverse of <see cref="Decode"/>, for tests.</summary>
    public static string Encode(string json)
    {
        var data = Encoding.UTF8.GetBytes(json);
        using var output = new MemoryStream();
        output.Write(new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length });
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(data);
        return Scheme + Convert.ToBase64String(output.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string Utf8(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException ex)
        {
            throw new FormatException("Ключ vpn:// повреждён.", ex);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null,
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;
}

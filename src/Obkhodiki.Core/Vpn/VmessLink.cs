using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Obkhodiki.Core.Vpn;

/// <summary>
/// vmess://BASE64(JSON) in the v2rayN format ({"v":"2","ps":…,"add":…,"port":…,"id":…,"aid":…,"scy":…,"net":…,
/// "type":…,"host":…,"path":…,"tls":…,"sni":…,"alpn":…,"fp":…}). The fields are mapped onto the Xray query names so
/// TLS and transport go through the same validation as vless:// and trojan:// links.
/// </summary>
public sealed record VmessLink(
    string Host,
    int Port,
    Guid Uuid,
    int AlterId,
    string Security,
    LinkTls Tls,
    LinkTransport Transport,
    string? Name) : IProxyServer
{
    private static readonly HashSet<string> Ciphers = new(StringComparer.OrdinalIgnoreCase) { "auto", "aes-128-gcm", "chacha20-poly1305", "none", "zero" };
    private const int MaxJsonBytes = 8 * 1024;

    public string Protocol => "vmess";
    public bool Insecure => Tls.Insecure && Tls.Security == LinkSecurity.Tls;

    public static VmessLink Parse(string text)
    {
        var raw = text.Trim();
        if (!raw.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Ссылка должна начинаться с vmess://");
        var payload = raw[8..];
        var hash = payload.IndexOf('#');
        if (hash >= 0) payload = payload[..hash];
        if (payload.Length == 0 || payload.Length > MaxJsonBytes * 2) throw new FormatException("Пустая или слишком длинная ссылка vmess://.");

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(DecodeBase64(Uri.UnescapeDataString(payload)));
            root = doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            throw new FormatException("Ссылка vmess:// повреждена или в неподдерживаемом формате (нужен JSON v2rayN).", ex);
        }
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Ссылка vmess:// повреждена.");

        string? Field(string name) => root.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() is { Length: > 0 } s ? s : null,
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

        var host = Field("add") ?? throw new FormatException("В ссылке vmess:// нет адреса сервера.");
        var portText = Field("port") ?? throw new FormatException("В ссылке vmess:// нет порта.");
        var (checkedHost, port) = LinkParsing.SplitHostPort(host.Contains(':') && !host.StartsWith('[') ? $"[{host}]:{portText}" : $"{host}:{portText}");
        if (!Guid.TryParse(Field("id"), out var uuid)) throw new FormatException("Неверный UUID в ссылке vmess://.");
        var alterId = int.TryParse(Field("aid") ?? "0", System.Globalization.NumberStyles.None, null, out var aid) && aid is >= 0 and <= 65535
            ? aid
            : throw new FormatException("Неверный alterId в ссылке vmess://.");
        var cipher = Field("scy") ?? "auto";
        if (!Ciphers.Contains(cipher)) throw new FormatException($"Шифрование VMess '{cipher}' не поддерживается.");

        // The v2rayN names in Xray query terms.
        var net = (Field("net") ?? "tcp").ToLowerInvariant();
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["type"] = net };
        var tlsField = (Field("tls") ?? "").ToLowerInvariant();
        q["security"] = tlsField switch
        {
            "" or "none" => "none",
            "tls" => "tls",
            var other => throw new FormatException($"Защита '{other}' в ссылке vmess:// не поддерживается."),
        };
        void Copy(string from, string to)
        {
            if (Field(from) is { } value) q[to] = value;
        }
        Copy("sni", "sni");
        Copy("alpn", "alpn");
        Copy("fp", "fp");
        Copy("host", "host");
        if (net == "grpc") Copy("path", "serviceName");
        else Copy("path", "path");
        if (net is "tcp" or "raw") Copy("type", "headerType");
        if (Field("allowInsecure") is "1" or "true" || Field("skip-cert-verify") is "true") q["allowInsecure"] = "1";

        var name = Field("ps");
        if (hash >= 0) name = Uri.UnescapeDataString(raw[(raw.IndexOf('#') + 1)..]);
        return new VmessLink(checkedHost, port, uuid, alterId, cipher.ToLowerInvariant(), LinkTls.Parse(q, LinkSecurity.None), LinkTransport.Parse(q),
            LinkParsing.SafeName(name));
    }

    private static string DecodeBase64(string text)
    {
        var s = text.Trim().Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
        var bytes = Convert.FromBase64String(s);
        if (bytes.Length > MaxJsonBytes) throw new FormatException("Ссылка vmess:// слишком длинная.");
        return Encoding.UTF8.GetString(bytes);
    }

    public JsonObject ToOutbound(string tag)
    {
        var o = new JsonObject
        {
            ["type"] = "vmess",
            ["tag"] = tag,
            ["server"] = Host,
            ["server_port"] = Port,
            ["uuid"] = Uuid.ToString(),
            ["security"] = Security,
            ["alter_id"] = AlterId,
            ["packet_encoding"] = "xudp",
        };
        if (Tls.ToJson(Host) is { } tls) o["tls"] = tls;
        if (Transport.ToJson() is { } transport) o["transport"] = transport;
        return o;
    }

    public override string ToString() => ProxyLinks.Describe(this);
}

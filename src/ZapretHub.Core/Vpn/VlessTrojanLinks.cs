using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZapretHub.Core.Vpn;

public enum LinkSecurity { None, Tls, Reality }

/// <summary>TLS / Reality settings shared by vless:// and trojan:// links (Xray / 3x-ui query format).</summary>
public sealed partial record LinkTls(
    LinkSecurity Security,
    string? Sni,
    string? Fingerprint,
    IReadOnlyList<string> Alpn,
    bool Insecure,
    string? RealityPublicKey,
    string? RealityShortId)
{
    private static readonly HashSet<string> Fingerprints = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "firefox", "edge", "safari", "360", "qq", "ios", "android", "random", "randomized",
    };

    [GeneratedRegex(@"^[A-Za-z0-9_-]{43}\z")]
    private static partial Regex RealityKey();

    // sing-box hex-decodes it: whole bytes only, at most 8.
    [GeneratedRegex(@"^(?:[0-9a-fA-F]{2}){0,8}\z")]
    private static partial Regex ShortId();

    public static LinkTls Parse(Dictionary<string, string> q, LinkSecurity fallback)
    {
        var security = (q.Get("security") ?? "").ToLowerInvariant() switch
        {
            "" => fallback,
            "none" => LinkSecurity.None,
            "tls" => LinkSecurity.Tls,
            "reality" => LinkSecurity.Reality,
            var other => throw new FormatException($"Защита '{other}' не поддерживается (только tls и reality)."),
        };
        var fp = q.Get("fp");
        if (fp is not null && !Fingerprints.Contains(fp)) throw new FormatException($"Отпечаток fp '{fp}' не поддерживается.");

        string? publicKey = null;
        string? shortId = null;
        if (security == LinkSecurity.Reality)
        {
            // sing-box expects unpadded URL-safe base64 of a 32-byte X25519 key; some panels add "=" padding.
            publicKey = (q.Get("pbk") ?? throw new FormatException("Для Reality в ссылке нужен pbk (публичный ключ).")).TrimEnd('=');
            if (!RealityKey().IsMatch(publicKey)) throw new FormatException("Неверный публичный ключ Reality (pbk).");
            shortId = q.Get("sid") ?? "";
            if (!ShortId().IsMatch(shortId)) throw new FormatException("Неверный short id Reality (sid).");
            // Reality always imitates a browser; sing-box needs uTLS for it.
            fp ??= "chrome";
        }

        return new LinkTls(
            security,
            LinkParsing.ValidSni(q.Get("sni") ?? q.Get("peer")),
            fp?.ToLowerInvariant(),
            LinkParsing.ParseAlpn(q.Get("alpn")),
            q.Flag("allowInsecure") || q.Flag("insecure"),
            publicKey,
            shortId);
    }

    public JsonObject? ToJson(string host)
    {
        if (Security == LinkSecurity.None) return null;
        var tls = new JsonObject { ["enabled"] = true, ["server_name"] = Sni ?? host };
        if (Insecure && Security == LinkSecurity.Tls) tls["insecure"] = true;
        if (Alpn.Count > 0) tls["alpn"] = LinkParsing.Array(Alpn);
        if (Fingerprint is not null) tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = Fingerprint };
        if (Security == LinkSecurity.Reality)
        {
            tls["reality"] = new JsonObject { ["enabled"] = true, ["public_key"] = RealityPublicKey, ["short_id"] = RealityShortId };
        }
        return tls;
    }
}

public enum LinkTransportType { Tcp, WebSocket, Grpc, HttpUpgrade, Http }

/// <summary>Transport ("type=") of vless:// and trojan:// links.</summary>
public sealed partial record LinkTransport(
    LinkTransportType Type,
    string? Path,
    string? HostHeader,
    string? ServiceName,
    int MaxEarlyData)
{
    [GeneratedRegex(@"^/[\x21-\x7E]{0,255}\z")]
    private static partial Regex HttpPath();

    [GeneratedRegex(@"^[A-Za-z0-9._/-]{1,128}\z")]
    private static partial Regex GrpcService();

    public static LinkTransport Parse(Dictionary<string, string> q)
    {
        var type = (q.Get("type") ?? "tcp").ToLowerInvariant() switch
        {
            "tcp" or "raw" => LinkTransportType.Tcp,
            "ws" => LinkTransportType.WebSocket,
            "grpc" => LinkTransportType.Grpc,
            "httpupgrade" => LinkTransportType.HttpUpgrade,
            "http" or "h2" => LinkTransportType.Http,
            "xhttp" or "splithttp" => throw new FormatException("Транспорт XHTTP пока не поддерживается."),
            var other => throw new FormatException($"Транспорт '{other}' не поддерживается."),
        };
        if (type == LinkTransportType.Tcp && q.Get("headerType") is { } header && !header.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Маскировка TCP '{header}' не поддерживается.");
        }

        var host = q.Get("host");
        if (host is not null && !LinkParsing.DnsName().IsMatch(host)) throw new FormatException("Неверное значение host.");

        string? path = null;
        var earlyData = 0;
        if (type is LinkTransportType.WebSocket or LinkTransportType.HttpUpgrade or LinkTransportType.Http)
        {
            path = q.Get("path") ?? "/";
            // Xray keeps WebSocket early data in the path ("/ws?ed=2048"); sing-box has separate fields for it.
            var qm = path.IndexOf('?');
            if (type == LinkTransportType.WebSocket && qm >= 0)
            {
                var pairs = path[(qm + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries).ToList();
                var ed = pairs.FirstOrDefault(p => p.StartsWith("ed=", StringComparison.Ordinal));
                if (ed is not null && int.TryParse(ed[3..], System.Globalization.NumberStyles.None, null, out var n))
                {
                    earlyData = Math.Min(n, 8192);
                    pairs.Remove(ed);
                    path = path[..qm] + (pairs.Count > 0 ? "?" + string.Join("&", pairs) : "");
                }
            }
            if (!HttpPath().IsMatch(path)) throw new FormatException("Неверный путь (path) транспорта.");
        }

        string? service = null;
        if (type == LinkTransportType.Grpc)
        {
            service = q.Get("serviceName") ?? "";
            if (service.Length > 0 && !GrpcService().IsMatch(service)) throw new FormatException("Неверное имя сервиса gRPC.");
        }
        return new LinkTransport(type, path, host, service, earlyData);
    }

    public JsonObject? ToJson()
    {
        switch (Type)
        {
            case LinkTransportType.WebSocket:
                var ws = new JsonObject { ["type"] = "ws", ["path"] = Path };
                if (HostHeader is not null) ws["headers"] = new JsonObject { ["Host"] = HostHeader };
                if (MaxEarlyData > 0)
                {
                    ws["max_early_data"] = MaxEarlyData;
                    ws["early_data_header_name"] = "Sec-WebSocket-Protocol";
                }
                return ws;
            case LinkTransportType.HttpUpgrade:
                var hu = new JsonObject { ["type"] = "httpupgrade", ["path"] = Path };
                if (HostHeader is not null) hu["host"] = HostHeader;
                return hu;
            case LinkTransportType.Http:
                var http = new JsonObject { ["type"] = "http", ["path"] = Path };
                if (HostHeader is not null) http["host"] = LinkParsing.Array(new[] { HostHeader });
                return http;
            case LinkTransportType.Grpc:
                return new JsonObject { ["type"] = "grpc", ["service_name"] = ServiceName ?? "" };
            default:
                return null;
        }
    }
}

/// <summary>vless://uuid@host:port?type=…&amp;security=…#name, as exported by 3x-ui / Xray.</summary>
public sealed record VlessLink(
    string Host,
    int Port,
    Guid Uuid,
    string? Flow,
    LinkTls Tls,
    LinkTransport Transport,
    string? Name) : IProxyServer
{
    public string Protocol => "vless";
    public bool Insecure => Tls.Insecure && Tls.Security == LinkSecurity.Tls;

    public static VlessLink Parse(string text)
    {
        var raw = text.Trim();
        if (!raw.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Ссылка должна начинаться с vless://");
        var (userInfo, hostPort, q, name) = LinkParsing.Split(raw, "идентификатора (UUID)");
        if (!Guid.TryParse(Uri.UnescapeDataString(userInfo), out var uuid)) throw new FormatException("Неверный UUID в ссылке.");
        var (host, port) = LinkParsing.SplitHostPort(hostPort);

        if (q.Get("encryption") is { } enc && !enc.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Шифрование VLESS '{enc}' не поддерживается.");
        }
        var flow = q.Get("flow");
        if (flow is not null && flow != "xtls-rprx-vision") throw new FormatException($"Flow '{flow}' не поддерживается (только xtls-rprx-vision).");

        var tls = LinkTls.Parse(q, LinkSecurity.None);
        var transport = LinkTransport.Parse(q);
        if (flow is not null && (tls.Security == LinkSecurity.None || transport.Type != LinkTransportType.Tcp))
        {
            throw new FormatException("xtls-rprx-vision работает только поверх TCP с TLS или Reality.");
        }
        return new VlessLink(host, port, uuid, flow, tls, transport, name);
    }

    public JsonObject ToOutbound(string tag)
    {
        var o = new JsonObject
        {
            ["type"] = "vless",
            ["tag"] = tag,
            ["server"] = Host,
            ["server_port"] = Port,
            ["uuid"] = Uuid.ToString(),
            ["packet_encoding"] = "xudp",
        };
        if (Flow is not null) o["flow"] = Flow;
        if (Tls.ToJson(Host) is { } tls) o["tls"] = tls;
        if (Transport.ToJson() is { } transport) o["transport"] = transport;
        return o;
    }

    public override string ToString() => ProxyLinks.Describe(this);
}

/// <summary>trojan://password@host:port?security=tls&amp;type=…#name.</summary>
public sealed record TrojanLink(
    string Host,
    int Port,
    string Password,
    LinkTls Tls,
    LinkTransport Transport,
    string? Name) : IProxyServer
{
    public string Protocol => "trojan";
    public bool Insecure => Tls.Insecure && Tls.Security == LinkSecurity.Tls;

    public static TrojanLink Parse(string text)
    {
        var raw = text.Trim();
        if (!raw.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Ссылка должна начинаться с trojan://");
        var (userInfo, hostPort, q, name) = LinkParsing.Split(raw, "пароля");
        var password = Uri.UnescapeDataString(userInfo);
        LinkParsing.ValidateSecret(password, "Пароль");
        var (host, port) = LinkParsing.SplitHostPort(hostPort);
        // Trojan is TLS by definition; "security" only switches to Reality.
        var tls = LinkTls.Parse(q, LinkSecurity.Tls);
        if (tls.Security == LinkSecurity.None) throw new FormatException("Trojan без TLS не поддерживается.");
        return new TrojanLink(host, port, password, tls, LinkTransport.Parse(q), name);
    }

    public JsonObject ToOutbound(string tag)
    {
        var o = new JsonObject
        {
            ["type"] = "trojan",
            ["tag"] = tag,
            ["server"] = Host,
            ["server_port"] = Port,
            ["password"] = Password,
            ["tls"] = Tls.ToJson(Host),
        };
        if (Transport.ToJson() is { } transport) o["transport"] = transport;
        return o;
    }

    public override string ToString() => ProxyLinks.Describe(this);
}

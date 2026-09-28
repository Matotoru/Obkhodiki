using System.Text.Json;

namespace Obkhodiki.Core.Vpn;

/// <summary>
/// Subscriptions that answer with Xray JSON configs instead of share links (Remnawave's format for Happ,
/// v2RayTun, Incy…): a JSON array of full configs, or one config. Each proxy outbound is turned into the
/// equivalent share link, so the rest of the app (validation, tags, storage) treats it like any other server.
/// </summary>
public static class XrayJsonSubscription
{
    public static bool LooksLikeJson(string body)
    {
        var t = body.TrimStart();
        return t.StartsWith('[') || t.StartsWith('{');
    }

    /// <summary>Share links for every proxy outbound; outbounds that cannot be expressed are reported by reason.</summary>
    public static List<string> ToLinks(string body, List<string> skipped)
    {
        using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
        var configs = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().ToList()
            : new List<JsonElement> { doc.RootElement };

        // Configs with a single server carry its human name in "remarks"; configs with several (a load-balancing
        // "auto" profile) only have outbound tags. Single ones go first so a server shared by both keeps its name.
        var ordered = configs
            .Where(c => c.ValueKind == JsonValueKind.Object)
            .Select(c => (Config: c, Proxies: ProxyOutbounds(c).ToList()))
            .OrderBy(x => x.Proxies.Count == 1 ? 0 : 1)
            .ToList();

        var links = new List<string>();
        foreach (var (config, proxies) in ordered)
        {
            var remarks = Str(config, "remarks");
            foreach (var outbound in proxies)
            {
                var name = proxies.Count == 1 && remarks is not null ? remarks : Str(outbound, "tag") ?? remarks;
                try
                {
                    links.Add(ToLink(outbound, name));
                }
                catch (Exception ex) when (ex is FormatException or InvalidOperationException or KeyNotFoundException)
                {
                    skipped.Add($"{Str(outbound, "protocol") ?? "?"}: {ex.Message}");
                }
            }
        }
        return links;
    }

    private static readonly HashSet<string> ProxyProtocols = new(StringComparer.OrdinalIgnoreCase)
    {
        "vless", "trojan", "shadowsocks", "vmess", "hysteria2", "hysteria",
    };

    private static IEnumerable<JsonElement> ProxyOutbounds(JsonElement config) =>
        config.TryGetProperty("outbounds", out var outbounds) && outbounds.ValueKind == JsonValueKind.Array
            ? outbounds.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object && Str(o, "protocol") is { } p && ProxyProtocols.Contains(p))
            : Enumerable.Empty<JsonElement>();

    private static string ToLink(JsonElement outbound, string? name)
    {
        var protocol = Str(outbound, "protocol")!.ToLowerInvariant();
        var settings = Obj(outbound, "settings");
        var stream = Obj(outbound, "streamSettings");
        var q = new List<(string Key, string Value)>();
        string userInfo;
        string address;
        int port;

        switch (protocol)
        {
            case "vless":
            {
                var server = First(settings, "vnext");
                (address, port) = (Req(server, "address"), Int(server, "port"));
                var user = First(server, "users");
                userInfo = Req(user, "id");
                q.Add(("encryption", Str(user, "encryption") ?? "none"));
                if (Str(user, "flow") is { Length: > 0 } flow) q.Add(("flow", flow));
                AddStream(stream, q);
                break;
            }
            case "trojan":
            {
                var server = First(settings, "servers");
                (address, port) = (Req(server, "address"), Int(server, "port"));
                userInfo = Req(server, "password");
                AddStream(stream, q);
                break;
            }
            case "shadowsocks":
            {
                var server = First(settings, "servers");
                (address, port) = (Req(server, "address"), Int(server, "port"));
                userInfo = Req(server, "method") + ":" + Req(server, "password");
                break;
            }
            case "vmess":
            {
                var server = First(settings, "vnext");
                var user = First(server, "users");
                AddStream(stream, q);
                return VmessShareLink(Req(server, "address"), Int(server, "port"), Req(user, "id"),
                    Int(user, "alterId", 0), Str(user, "security") ?? "auto", q, name);
            }
            default:
                throw new FormatException($"Протокол {protocol} из JSON-подписки пока не поддерживается.");
        }

        var host = address.Contains(':') ? $"[{address}]" : address;
        var query = q.Count == 0 ? "" : "?" + string.Join("&", q.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        var fragment = name is null ? "" : "#" + Uri.EscapeDataString(name);
        var scheme = protocol == "shadowsocks" ? "ss" : protocol;
        return $"{scheme}://{Uri.EscapeDataString(userInfo)}@{host}:{port}{query}{fragment}";
    }

    /// <summary>A vmess:// link in the v2rayN JSON format, built from an Xray outbound (the VMess parser validates it).</summary>
    private static string VmessShareLink(string address, int port, string id, int alterId, string cipher, List<(string Key, string Value)> q, string? name)
    {
        string? Q(string key) => q.FirstOrDefault(p => p.Key == key).Value;
        if (Q("security") == "reality") throw new FormatException("VMess с Reality не поддерживается.");
        var network = Q("type") ?? "tcp";
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["v"] = "2",
            ["ps"] = name ?? "",
            ["add"] = address,
            ["port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["id"] = id,
            ["aid"] = alterId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["scy"] = cipher,
            ["net"] = network,
            ["type"] = Q("headerType") ?? "none",
            ["host"] = Q("host") ?? "",
            ["path"] = (network == "grpc" ? Q("serviceName") : Q("path")) ?? "",
            ["tls"] = Q("security") == "tls" ? "tls" : "",
            ["sni"] = Q("sni") ?? "",
            ["alpn"] = Q("alpn") ?? "",
            ["fp"] = Q("fp") ?? "",
        };
        if (Q("allowInsecure") == "1") json["allowInsecure"] = "1";
        return "vmess://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
    }

    /// <summary>Xray streamSettings → the query parameters of a share link (the parsers validate every value).</summary>
    private static void AddStream(JsonElement? stream, List<(string, string)> q)
    {
        var network = Str(stream, "network") ?? "tcp";
        var security = Str(stream, "security") ?? "none";
        q.Add(("type", network));
        q.Add(("security", security));

        var tls = security switch
        {
            "reality" => Obj(stream, "realitySettings"),
            "tls" => Obj(stream, "tlsSettings"),
            _ => null,
        };
        if (Str(tls, "serverName") is { Length: > 0 } sni) q.Add(("sni", sni));
        if (Str(tls, "fingerprint") is { Length: > 0 } fp) q.Add(("fp", fp));
        if (Arr(tls, "alpn") is { Count: > 0 } alpn) q.Add(("alpn", string.Join(",", alpn)));
        if (Bool(tls, "allowInsecure")) q.Add(("allowInsecure", "1"));
        if (security == "reality")
        {
            if (Str(tls, "publicKey") is { } pbk) q.Add(("pbk", pbk));
            if (Str(tls, "shortId") is { } sid) q.Add(("sid", sid));
        }

        switch (network)
        {
            case "ws":
                var ws = Obj(stream, "wsSettings");
                if (Str(ws, "path") is { } wsPath) q.Add(("path", wsPath));
                if ((Str(ws, "host") ?? Str(Obj(ws, "headers"), "Host")) is { Length: > 0 } wsHost) q.Add(("host", wsHost));
                break;
            case "httpupgrade":
                var hu = Obj(stream, "httpupgradeSettings");
                if (Str(hu, "path") is { } huPath) q.Add(("path", huPath));
                if (Str(hu, "host") is { Length: > 0 } huHost) q.Add(("host", huHost));
                break;
            case "grpc":
                if (Str(Obj(stream, "grpcSettings"), "serviceName") is { } service) q.Add(("serviceName", service));
                break;
            case "http" or "h2":
                var h2 = Obj(stream, "httpSettings");
                if (Str(h2, "path") is { } h2Path) q.Add(("path", h2Path));
                if (Arr(h2, "host") is { Count: > 0 } hosts) q.Add(("host", hosts[0]));
                break;
            case "tcp" or "raw":
                if (Str(Obj(Obj(stream, "tcpSettings"), "header"), "type") is { } header) q.Add(("headerType", header));
                break;
        }
    }

    private static JsonElement? Obj(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static JsonElement First(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0
            ? v[0]
            : throw new FormatException($"В сервере нет поля {name}.");

    private static string? Str(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Req(JsonElement e, string name) => Str(e, name) ?? throw new FormatException($"В сервере нет поля {name}.");

    private static int Int(JsonElement e, string name, int fallback) => e.TryGetProperty(name, out _) ? Int(e, name) : fallback;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i
        : e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out i) ? i
        : throw new FormatException($"В сервере нет поля {name}.");

    private static bool Bool(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static List<string>? Arr(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList()
            : null;
}

using System.Text.Json.Nodes;

namespace Obkhodiki.Core.Vpn;

/// <summary>
/// A parsed hysteria2:// (or hy2://) share link. Every field ends up in a config file and on a command path,
/// so each one is validated; anything unsupported is rejected rather than silently dropped.
/// </summary>
public sealed record Hysteria2Link(
    string Host,
    int Port,
    string Password,
    string? ObfsPassword,
    string? Sni,
    bool Insecure,
    IReadOnlyList<string> Alpn,
    string? Name) : IProxyServer
{
    public string Protocol => "hysteria2";

    /// <summary>
    /// Port hopping: the client moves between these UDP ports (sing-box server_ports), which makes blocking one
    /// port useless. Null for a single port.
    /// </summary>
    public IReadOnlyList<(int From, int To)>? PortRanges { get; init; }

    public static Hysteria2Link Parse(string text)
    {
        var raw = text.Trim();
        if (!raw.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase) && !raw.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Ссылка должна начинаться с hysteria2:// или hy2://");
        }
        var (userInfo, hostPort, parameters, name) = LinkParsing.Split(raw, "пароля");
        var password = Uri.UnescapeDataString(userInfo);
        var (host, ports) = LinkParsing.SplitHostPorts(hostPort);
        // Some clients (v2rayN) put the hopping range into "mport" and keep one port in the address.
        if (parameters.Get("mport") is { Length: > 0 } mport) ports = ports.Concat(LinkParsing.ParsePortRanges(mport)).Distinct().ToList();
        var port = ports[0].From;

        string? obfs = null;
        if (parameters.TryGetValue("obfs", out var obfsType) && obfsType.Length > 0 && !string.Equals(obfsType, "salamander", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"Тип obfs '{obfsType}' не поддерживается (только salamander).");
        }
        if (parameters.Get("obfs-password") is { } obfsPassword) obfs = obfsPassword;
        if (parameters.Get("obfs") is not null && obfs is null) throw new FormatException("Для obfs salamander в ссылке нужен obfs-password.");

        var sni = LinkParsing.ValidSni(parameters.Get("sni"));
        var alpn = LinkParsing.ParseAlpn(parameters.Get("alpn"));
        if (parameters.ContainsKey("pinSHA256")) throw new FormatException("Параметр pinSHA256 пока не поддерживается.");

        LinkParsing.ValidateSecret(password, "Пароль");
        if (obfs is not null) LinkParsing.ValidateSecret(obfs, "obfs-password");

        return new Hysteria2Link(host, port, password, obfs, sni, parameters.Flag("insecure"), alpn, name)
        {
            PortRanges = ports.Count == 1 && ports[0].From == ports[0].To ? null : ports,
        };
    }

    public JsonObject ToOutbound(string tag)
    {
        var tls = new JsonObject
        {
            ["enabled"] = true,
            ["server_name"] = Sni ?? Host,
        };
        if (Insecure) tls["insecure"] = true;
        if (Alpn.Count > 0) tls["alpn"] = LinkParsing.Array(Alpn);

        var outbound = new JsonObject
        {
            ["type"] = "hysteria2",
            ["tag"] = tag,
            ["server"] = Host,
            ["server_port"] = Port,
            ["password"] = Password,
            ["tls"] = tls,
        };
        if (PortRanges is { } ranges)
        {
            outbound.Remove("server_port");
            outbound["server_ports"] = LinkParsing.Array(ranges.Select(r => $"{r.From}:{r.To}"));
            outbound["hop_interval"] = "30s";
        }
        if (ObfsPassword is not null) outbound["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = ObfsPassword };
        return outbound;
    }

    /// <summary>Safe for logs and UI: never includes the passwords.</summary>
    public override string ToString() => ProxyLinks.Describe(this);
}

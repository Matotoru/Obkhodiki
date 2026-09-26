using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZapretHub.Core.Vpn;

/// <param name="Tun">Capture traffic through a virtual adapter (needed to route programs and sites).
/// Without it only the local measurement proxy runs.</param>
/// <param name="ProbePort">Local SOCKS/HTTP port whose traffic always goes through the VPS (used for measurements).</param>
/// <param name="Processes">Executable names whose traffic goes through the VPS.</param>
/// <param name="Domains">Domains (and their subdomains) that go through the VPS.</param>
public sealed record SingBoxOptions(
    bool Tun,
    int ProbePort,
    IReadOnlyList<string> Processes,
    IReadOnlyList<string> Domains,
    string LogPath,
    ProbeCredentials ProbeAuth);

/// <summary>Per-run credentials for the local probe port, so other local programs cannot ride the tunnel.</summary>
public sealed record ProbeCredentials(string User, string Password)
{
    public static ProbeCredentials Random() =>
        new(Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));

    public override string ToString() => $"{User}:***";
}

/// <summary>Builds a sing-box (1.12+) configuration: everything direct except the selected programs, sites and the probe port.</summary>
public static partial class SingBoxConfig
{
    public const string TunInterface = "ZapretHub";
    public const string ProxyTag = "proxy";

    // Letters of any script (games and programs are often named in Cyrillic), digits and a few safe symbols.
    [GeneratedRegex(@"^[\p{L}\p{N} ._()+'&!,\[\]-]{1,100}\.exe\z", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessName();

    [GeneratedRegex(@"^(?=.{1,253}\z)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+\z")]
    private static partial Regex Domain();

    public static bool IsValidProcessName(string name) => ProcessName().IsMatch(name) && Path.GetFileName(name) == name;

    /// <summary>Lower-cases and strips "*." / leading dots; null when not a plain domain name.</summary>
    public static string? NormalizeDomain(string text)
    {
        var d = text.Trim().ToLowerInvariant();
        if (d.StartsWith("*.")) d = d[2..];
        d = d.Trim('.');
        return Domain().IsMatch(d) ? d : null;
    }

    private static JsonObject BuildDns(IReadOnlyList<string> domains)
    {
        var servers = new JsonArray(new JsonObject { ["type"] = "local", ["tag"] = "local" });
        var dns = new JsonObject { ["servers"] = servers, ["final"] = "local" };
        if (domains.Count > 0)
        {
            servers.Add(new JsonObject { ["type"] = "https", ["tag"] = "remote", ["server"] = "1.1.1.1", ["detour"] = ProxyTag });
            dns["rules"] = new JsonArray(new JsonObject
            {
                ["domain_suffix"] = new JsonArray(domains.Select(d => (JsonNode)d!).ToArray()),
                ["server"] = "remote",
            });
        }
        return dns;
    }

    public static string Build(Hysteria2Link server, SingBoxOptions options)
    {
        if (options.ProbePort is < 1024 or > 65535) throw new ArgumentException("Probe port must be 1024-65535.");
        var processes = options.Processes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (processes.FirstOrDefault(p => !IsValidProcessName(p)) is { } badProcess)
        {
            throw new ArgumentException($"Invalid process name '{badProcess}'.");
        }
        var domains = new List<string>();
        foreach (var d in options.Domains)
        {
            domains.Add(NormalizeDomain(d) ?? throw new ArgumentException($"Invalid domain '{d}'."));
        }
        domains = domains.Distinct().ToList();

        var inbounds = new JsonArray();
        if (options.Tun)
        {
            inbounds.Add(new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = TunInterface,
                // Both families: with IPv4 only, selected programs would leak straight out over IPv6.
                ["address"] = new JsonArray("172.19.0.1/30", "fdfe:dcba:9876::1/126"),
                ["auto_route"] = true,
                // All traffic enters the tunnel adapter, but only selected programs/sites go to the VPS; the rest
                // leaves directly (final: direct). On Windows "strict" adds firewall rules against DNS queries
                // bypassing the adapter: needed when sites are selected, or Windows' parallel lookup to the ISP
                // (possibly poisoned) would win and the VPS would carry the connection to the wrong address.
                ["strict_route"] = domains.Count > 0,
            });
        }
        inbounds.Add(new JsonObject
        {
            ["type"] = "mixed",
            ["tag"] = "probe-in",
            ["listen"] = "127.0.0.1",
            ["listen_port"] = options.ProbePort,
            ["users"] = new JsonArray(new JsonObject { ["username"] = options.ProbeAuth.User, ["password"] = options.ProbeAuth.Password }),
        });

        var tls = new JsonObject
        {
            ["enabled"] = true,
            ["server_name"] = server.Sni ?? server.Host,
        };
        if (server.Insecure) tls["insecure"] = true;
        if (server.Alpn.Count > 0) tls["alpn"] = new JsonArray(server.Alpn.Select(a => (JsonNode)a!).ToArray());

        var proxy = new JsonObject
        {
            ["type"] = "hysteria2",
            ["tag"] = ProxyTag,
            ["server"] = server.Host,
            ["server_port"] = server.Port,
            ["password"] = server.Password,
            ["tls"] = tls,
        };
        if (server.ObfsPassword is not null)
        {
            proxy["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = server.ObfsPassword };
        }

        var rules = new JsonArray
        {
            new JsonObject { ["inbound"] = new JsonArray("probe-in"), ["outbound"] = ProxyTag },
            // Reads the site name from TLS/QUIC so domain rules match whatever address the app connected to.
            new JsonObject { ["action"] = "sniff" },
        };
        if (domains.Count > 0)
        {
            // Selected sites must also be resolved through the VPS: the ISP would see the lookups, and for
            // DNS-poisoned domains the wrong address would be carried through the tunnel.
            rules.Add(new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" });
        }
        if (processes.Count > 0)
        {
            rules.Add(new JsonObject { ["process_name"] = new JsonArray(processes.Select(p => (JsonNode)p!).ToArray()), ["outbound"] = ProxyTag });
        }
        if (domains.Count > 0)
        {
            rules.Add(new JsonObject { ["domain_suffix"] = new JsonArray(domains.Select(d => (JsonNode)d!).ToArray()), ["outbound"] = ProxyTag });
        }

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true, ["output"] = options.LogPath },
            ["dns"] = BuildDns(domains),
            ["inbounds"] = inbounds,
            ["outbounds"] = new JsonArray(proxy, new JsonObject { ["type"] = "direct", ["tag"] = "direct" }),
            ["route"] = new JsonObject
            {
                ["rules"] = rules,
                ["final"] = "direct",
                // Binds outgoing connections (including the tunnel itself) to the real adapter: no routing loop.
                ["auto_detect_interface"] = true,
                ["default_domain_resolver"] = "local",
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}

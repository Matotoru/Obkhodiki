using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Obkhodiki.Core.Vpn;

/// <summary>A downloaded rule-set file, as sing-box will read it.</summary>
public sealed record LocalRuleSet(string Tag, string Path)
{
    public bool HasDomains => Tag.StartsWith("geosite-", StringComparison.Ordinal);
}

/// <param name="Tun">Capture traffic through a virtual adapter (needed to route programs and sites).
/// Without it only the local measurement proxy runs.</param>
/// <param name="ProbePort">Local SOCKS/HTTP port whose traffic always goes through the VPS (used for measurements).</param>
/// <param name="FullTunnel">Everything goes through the VPS except <paramref name="DirectProcesses"/> and
/// <paramref name="DirectRuleSets"/>; otherwise only the selected programs, sites and rule-sets do.</param>
/// <param name="Processes">Executable names whose traffic goes through the VPS.</param>
/// <param name="Domains">Domains (and their subdomains) that go through the VPS.</param>
public sealed record SingBoxOptions(
    bool Tun,
    int ProbePort,
    IReadOnlyList<string> Processes,
    IReadOnlyList<string> Domains,
    string LogPath,
    ProbeCredentials ProbeAuth)
{
    public bool FullTunnel { get; init; }
    public IReadOnlyList<string> DirectProcesses { get; init; } = Array.Empty<string>();

    /// <summary>Never through the VPS, in either mode: programs, and domains or IP/CIDR entries.</summary>
    public IReadOnlyList<string> BypassProcesses { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> BypassEntries { get; init; } = Array.Empty<string>();
    public IReadOnlyList<LocalRuleSet> ProxyRuleSets { get; init; } = Array.Empty<LocalRuleSet>();
    public IReadOnlyList<LocalRuleSet> DirectRuleSets { get; init; } = Array.Empty<LocalRuleSet>();
    public ClashApiOptions? ClashApi { get; init; }

    /// <summary>
    /// Always resolved locally, even in full-tunnel mode: hosts the app itself downloads from (subscription,
    /// rule-sets). If the active server dies, the app must still be able to fetch a fresh server list.
    /// </summary>
    public IReadOnlyList<string> LocalDnsDomains { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The computer really reaches the internet over IPv6. When it does not, the tunnel must not offer IPv6:
    /// Windows would then route IPv6 into it, apps would try their AAAA addresses first and every such
    /// connection would fail (seen as "Yandex does not open, Ozon does" — only some sites have IPv6).
    /// </summary>
    public bool HostIpv6 { get; init; } = true;
}

/// <summary>Per-run credentials for the local probe port, so other local programs cannot ride the tunnel.</summary>
public sealed record ProbeCredentials(string User, string Password)
{
    public static ProbeCredentials Random() =>
        new(Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8)),
            Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)));

    public override string ToString() => $"{User}:***";
}

/// <summary>
/// Builds a sing-box (1.12+) configuration. All servers sit behind one selector ("proxy"), so the active server
/// can be switched through the Clash API without restarting the tunnel.
/// </summary>
public static partial class SingBoxConfig
{
    public const string TunInterface = "Obkhodiki";
    public const string ProxyTag = "proxy";

    // Letters of any script (games and programs are often named in Cyrillic), digits and a few safe symbols.
    [GeneratedRegex(@"^[\p{L}\p{N} ._()+'&!,\[\]-]{1,100}\.exe\z", RegexOptions.IgnoreCase)]
    private static partial Regex ProcessName();

    [GeneratedRegex(@"^(?=.{1,253}\z)[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?(\.[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?)+\z")]
    private static partial Regex Domain();

    [GeneratedRegex(@"^geo(site|ip)-[a-z0-9-]{1,64}\z")]
    private static partial Regex RuleSetTag();

    /// <summary>Probe-port user that is routed to exactly this server (for per-server pings).</summary>
    public static string ServerProbeUser(string tag) => "srv-" + tag;

    public static bool IsValidProcessName(string name) => ProcessName().IsMatch(name) && Path.GetFileName(name) == name;

    /// <summary>Lower-cases and strips "*." / leading dots; null when not a plain domain name.</summary>
    public static string? NormalizeDomain(string text)
    {
        var d = text.Trim().ToLowerInvariant();
        if (d.StartsWith("*.")) d = d[2..];
        d = d.Trim('.');
        return Domain().IsMatch(d) ? d : null;
    }

    /// <summary>An IPv4/IPv6 address or CIDR in canonical form; null when it is not one.</summary>
    public static string? NormalizeCidr(string text)
    {
        var t = text.Trim();
        var slash = t.IndexOf('/');
        var addressText = slash < 0 ? t : t[..slash];
        if (!System.Net.IPAddress.TryParse(addressText, out var address) || addressText.Contains('%')) return null;
        // IPAddress.TryParse accepts "1" or "1.2" as IPv4; only full dotted quads or real IPv6 count.
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && addressText.Count(c => c == '.') != 3) return null;
        var max = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        var bits = max;
        if (slash >= 0 && (!int.TryParse(t[(slash + 1)..], System.Globalization.NumberStyles.None, null, out bits) || bits < 0 || bits > max)) return null;
        return $"{address}/{bits}";
    }

    /// <summary>A "never through the VPS" entry: a domain (normalized) or an address/CIDR; null when neither.</summary>
    public static string? NormalizeBypassEntry(string text)
    {
        if (NormalizeCidr(text) is { } cidr) return cidr;
        // "1.2.3" is a mistyped address, not a domain: real top-level domains are never all digits.
        return NormalizeDomain(text) is { } domain && !domain[(domain.LastIndexOf('.') + 1)..].All(char.IsAsciiDigit) ? domain : null;
    }

    public static bool IsCidr(string entry) => entry.Contains('/');

    /// <summary>Some server needs amnezia-box instead of the official sing-box.</summary>
    public static bool NeedsAmnezia(IEnumerable<VpnServerEntry> servers) => servers.Any(s => s.Server.NeedsAmnezia);

    /// <summary>
    /// The server behind "initialize outbound[i]" / "initialize endpoint[i]" in a sing-box check error: outbounds
    /// and endpoints are numbered separately, each in the order of <paramref name="servers"/>.
    /// </summary>
    public static VpnServerEntry? ServerAt(IReadOnlyList<VpnServerEntry> servers, bool endpoint, int index) =>
        servers.Where(s => s.Server.IsEndpoint == endpoint).ElementAtOrDefault(index);

    /// <summary>Single-server convenience (tests, older callers).</summary>
    public static string Build(IProxyServer server, SingBoxOptions options) =>
        Build(new[] { new VpnServerEntry("server", server) }, "server", options);

    public static string Build(IReadOnlyList<VpnServerEntry> servers, string? activeTag, SingBoxOptions options)
    {
        if (servers.Count == 0) throw new ArgumentException("At least one server is required.");
        if (servers.Select(s => s.Tag).Distinct().Count() != servers.Count) throw new ArgumentException("Server tags must be unique.");
        if (servers.Any(s => s.Tag is ProxyTag or "direct")) throw new ArgumentException("Reserved server tag.");
        if (options.ProbePort is < 1024 or > 65535) throw new ArgumentException("Probe port must be 1024-65535.");

        var processes = ValidProcesses(options.Processes);
        var directProcesses = ValidProcesses(options.DirectProcesses);
        var bypassProcesses = ValidProcesses(options.BypassProcesses);
        var bypassEntries = new List<string>();
        foreach (var e in options.BypassEntries) bypassEntries.Add(NormalizeBypassEntry(e) ?? throw new ArgumentException($"Invalid entry '{e}'."));
        var bypassDomains = bypassEntries.Where(e => !IsCidr(e)).Distinct().ToList();
        var bypassCidrs = bypassEntries.Where(IsCidr).Distinct().ToList();
        var domains = new List<string>();
        foreach (var d in options.Domains) domains.Add(NormalizeDomain(d) ?? throw new ArgumentException($"Invalid domain '{d}'."));
        domains = domains.Distinct().ToList();
        foreach (var rs in options.ProxyRuleSets.Concat(options.DirectRuleSets))
        {
            if (!RuleSetTag().IsMatch(rs.Tag) || !System.IO.Path.IsPathFullyQualified(rs.Path)) throw new ArgumentException($"Invalid rule-set '{rs.Tag}'.");
        }

        var full = options.FullTunnel;
        // Sets that only matter in the active mode.
        var proxySets = full ? new List<LocalRuleSet>() : options.ProxyRuleSets.DistinctBy(r => r.Tag).ToList();
        var directSets = full ? options.DirectRuleSets.DistinctBy(r => r.Tag).ToList() : new List<LocalRuleSet>();
        var proxyDnsSets = proxySets.Where(r => r.HasDomains).ToList();
        var directDnsSets = directSets.Where(r => r.HasDomains).ToList();
        // Lookups for what goes through the VPS are made through the VPS too.
        var remoteDns = full || domains.Count > 0 || proxyDnsSets.Count > 0;

        var inbounds = new JsonArray();
        if (options.Tun)
        {
            inbounds.Add(new JsonObject
            {
                ["type"] = "tun",
                ["tag"] = "tun-in",
                ["interface_name"] = TunInterface,
                // Both families when the computer has IPv6: with IPv4 only, selected programs would leak straight out
                // over IPv6. Without IPv6 there is nothing to leak through, and an IPv6 address here would only lure
                // apps into connections that cannot work.
                ["address"] = options.HostIpv6
                    ? new JsonArray("172.19.0.1/30", "fdfe:dcba:9876::1/126")
                    : new JsonArray("172.19.0.1/30"),
                ["auto_route"] = true,
                // On Windows "strict" adds firewall rules against DNS queries bypassing the adapter: needed whenever
                // lookups go through the VPS, or Windows' parallel lookup to the ISP (possibly poisoned) would win.
                ["strict_route"] = remoteDns,
            });
        }
        inbounds.Add(new JsonObject
        {
            ["type"] = "mixed",
            ["tag"] = "probe-in",
            ["listen"] = "127.0.0.1",
            ["listen_port"] = options.ProbePort,
            // The main user follows the selected server; one extra user per server reaches exactly that server, so
            // every server can be pinged without switching the one in use.
            ["users"] = new JsonArray(new[] { new JsonObject { ["username"] = options.ProbeAuth.User, ["password"] = options.ProbeAuth.Password } }
                .Concat(servers.Select(s => new JsonObject { ["username"] = ServerProbeUser(s.Tag), ["password"] = options.ProbeAuth.Password }))
                .Select(u => (JsonNode)u).ToArray()),
        });

        var outbounds = new JsonArray();
        var endpoints = new JsonArray();
        // WireGuard-style servers are endpoints; the selector and rules address them by tag like outbounds.
        foreach (var s in servers) (s.Server.IsEndpoint ? endpoints : outbounds).Add(s.Server.ToOutbound(s.Tag));
        var active = servers.Any(s => s.Tag == activeTag) ? activeTag! : servers[0].Tag;
        outbounds.Add(new JsonObject
        {
            ["type"] = "selector",
            ["tag"] = ProxyTag,
            ["outbounds"] = LinkParsing.Array(servers.Select(s => s.Tag)),
            ["default"] = active,
            // Switching servers keeps running connections (a game in progress) on the server they started on.
            ["interrupt_exist_connections"] = false,
        });
        outbounds.Add(new JsonObject { ["type"] = "direct", ["tag"] = "direct" });

        var rules = new JsonArray();
        // amnezia-box's AmneziaWG endpoint cannot dial a name (its tunnel has no resolver), so names asked for on
        // the probe port (downloads through the VPS) are resolved first. Tunnel traffic already arrives as addresses.
        if (NeedsAmnezia(servers)) rules.Add(new JsonObject { ["inbound"] = new JsonArray("probe-in"), ["action"] = "resolve" });
        foreach (var s in servers)
        {
            rules.Add(new JsonObject { ["inbound"] = new JsonArray("probe-in"), ["auth_user"] = new JsonArray(ServerProbeUser(s.Tag)), ["outbound"] = s.Tag });
        }
        rules.Add(new JsonObject { ["inbound"] = new JsonArray("probe-in"), ["auth_user"] = new JsonArray(options.ProbeAuth.User), ["outbound"] = ProxyTag });
        // Reads the site name from TLS/QUIC so domain rules match whatever address the app connected to.
        rules.Add(new JsonObject { ["action"] = "sniff" });
        if (remoteDns) rules.Add(new JsonObject { ["protocol"] = "dns", ["action"] = "hijack-dns" });

        // The user's "never through the VPS" list comes first: it beats programs, sites, categories and games.
        if (bypassProcesses.Count > 0) rules.Add(new JsonObject { ["process_name"] = LinkParsing.Array(bypassProcesses), ["outbound"] = "direct" });
        if (bypassDomains.Count > 0) rules.Add(new JsonObject { ["domain_suffix"] = LinkParsing.Array(bypassDomains), ["outbound"] = "direct" });
        if (bypassCidrs.Count > 0) rules.Add(new JsonObject { ["ip_cidr"] = LinkParsing.Array(bypassCidrs), ["outbound"] = "direct" });
        if (full)
        {
            rules.Add(new JsonObject { ["ip_is_private"] = true, ["outbound"] = "direct" });
            // Games set to "direct" (and the ones Auto measured faster direct) stay off the VPS.
            if (directProcesses.Count > 0) rules.Add(new JsonObject { ["process_name"] = LinkParsing.Array(directProcesses), ["outbound"] = "direct" });
        }
        // Explicit choices win over categories: a Russian site the user listed still goes through the VPS.
        if (processes.Count > 0) rules.Add(new JsonObject { ["process_name"] = LinkParsing.Array(processes), ["outbound"] = ProxyTag });
        if (domains.Count > 0) rules.Add(new JsonObject { ["domain_suffix"] = LinkParsing.Array(domains), ["outbound"] = ProxyTag });
        if (proxySets.Count > 0) rules.Add(new JsonObject { ["rule_set"] = LinkParsing.Array(proxySets.Select(r => r.Tag)), ["outbound"] = ProxyTag });
        if (directSets.Count > 0) rules.Add(new JsonObject { ["rule_set"] = LinkParsing.Array(directSets.Select(r => r.Tag)), ["outbound"] = "direct" });

        var route = new JsonObject
        {
            ["rules"] = rules,
            ["final"] = full ? ProxyTag : "direct",
            // Binds outgoing connections (including the tunnel itself) to the real adapter: no routing loop.
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = "local",
        };
        var allSets = proxySets.Concat(directSets).ToList();
        if (allSets.Count > 0)
        {
            route["rule_set"] = new JsonArray(allSets.Select(r => (JsonNode)new JsonObject
            {
                ["type"] = "local",
                ["tag"] = r.Tag,
                ["format"] = "binary",
                ["path"] = r.Path,
            }).ToArray());
        }

        var root = new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn", ["timestamp"] = true, ["output"] = options.LogPath },
            ["dns"] = BuildDns(full, remoteDns, options.HostIpv6, domains, proxyDnsSets, directDnsSets,
                options.LocalDnsDomains.Select(NormalizeDomain).Where(d => d is not null).Select(d => d!).Concat(bypassDomains).Distinct().ToList()),
            ["inbounds"] = inbounds,
            ["outbounds"] = outbounds,
            ["route"] = route,
        };
        if (endpoints.Count > 0) root["endpoints"] = endpoints;
        if (options.ClashApi is { } api)
        {
            if (api.Port is < 1024 or > 65535 || api.Secret.Length < 16) throw new ArgumentException("Invalid Clash API options.");
            root["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject { ["external_controller"] = $"127.0.0.1:{api.Port}", ["secret"] = api.Secret },
            };
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static List<string> ValidProcesses(IReadOnlyList<string> list)
    {
        var result = list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (result.FirstOrDefault(p => !IsValidProcessName(p)) is { } bad) throw new ArgumentException($"Invalid process name '{bad}'.");
        return result;
    }

    private static JsonObject BuildDns(bool full, bool remoteDns, bool hostIpv6, List<string> domains, List<LocalRuleSet> proxySets,
        List<LocalRuleSet> directSets, List<string> localDomains)
    {
        var servers = new JsonArray(new JsonObject { ["type"] = "local", ["tag"] = "local" });
        var dns = new JsonObject
        {
            ["servers"] = servers,
            ["final"] = full ? "remote" : "local",
            // Answers to apps: no AAAA at all without IPv6; with it, IPv4 first (many VPS have no IPv6 either).
            ["strategy"] = hostIpv6 ? "prefer_ipv4" : "ipv4_only",
        };
        if (!remoteDns) return dns;

        servers.Add(new JsonObject { ["type"] = "https", ["tag"] = "remote", ["server"] = "1.1.1.1", ["detour"] = ProxyTag });
        var rules = new JsonArray();
        if (localDomains.Count > 0) rules.Add(new JsonObject { ["domain_suffix"] = LinkParsing.Array(localDomains), ["server"] = "local" });
        if (domains.Count > 0) rules.Add(new JsonObject { ["domain_suffix"] = LinkParsing.Array(domains), ["server"] = "remote" });
        if (proxySets.Count > 0) rules.Add(new JsonObject { ["rule_set"] = LinkParsing.Array(proxySets.Select(r => r.Tag)), ["server"] = "remote" });
        // Russian sites resolve locally: their CDNs hand out the nearest (Russian) address.
        if (directSets.Count > 0) rules.Add(new JsonObject { ["rule_set"] = LinkParsing.Array(directSets.Select(r => r.Tag)), ["server"] = "local" });
        if (rules.Count > 0) dns["rules"] = rules;
        return dns;
    }
}

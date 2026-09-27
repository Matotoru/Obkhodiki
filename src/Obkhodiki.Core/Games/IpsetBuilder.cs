using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Obkhodiki.Core.Games;

/// <summary>Amazon's published address ranges (https://ip-ranges.amazonaws.com/ip-ranges.json).</summary>
public sealed class AwsIpRanges
{
    public static readonly Uri Source = new("https://ip-ranges.amazonaws.com/ip-ranges.json");

    // Where game servers actually run. "AMAZON" is an aggregate of everything, and CLOUDFRONT/S3 are CDNs:
    // expanding into those would pull unrelated websites into game rules.
    private static readonly HashSet<string> GameServices = new(StringComparer.OrdinalIgnoreCase) { "EC2", "GAMELIFT", "GLOBALACCELERATOR" };

    private readonly IReadOnlyList<IPNetwork> _networks;

    private AwsIpRanges(IReadOnlyList<IPNetwork> networks) => _networks = networks;

    public int Count => _networks.Count;

    public static AwsIpRanges Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var networks = new HashSet<IPNetwork>();
            foreach (var p in root.GetProperty("prefixes").EnumerateArray())
            {
                if (IsGameService(p)) networks.Add(IPNetwork.Parse(p.GetProperty("ip_prefix").GetString()!));
            }
            if (root.TryGetProperty("ipv6_prefixes", out var v6))
            {
                foreach (var p in v6.EnumerateArray())
                {
                    if (IsGameService(p)) networks.Add(IPNetwork.Parse(p.GetProperty("ipv6_prefix").GetString()!));
                }
            }
            return new AwsIpRanges(networks.ToList());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException)
        {
            throw new FormatException("Unexpected AWS ip-ranges.json format.", ex);
        }
    }

    private static bool IsGameService(JsonElement prefix) =>
        prefix.TryGetProperty("service", out var service) && GameServices.Contains(service.GetString() ?? "");

    /// <summary>The most specific published prefix containing the address, or null.</summary>
    public IPNetwork? SmallestContaining(IPAddress ip) =>
        _networks
            .Where(n => n.BaseAddress.AddressFamily == ip.AddressFamily && n.Contains(ip))
            .OrderByDescending(n => n.PrefixLength)
            .Cast<IPNetwork?>()
            .FirstOrDefault();
}

/// <summary>Turns observed game server addresses into ipset lines winws can load.</summary>
public static partial class IpsetBuilder
{
    // Game servers are re-allocated between matches. Expanding to Amazon's regional prefix keeps the
    // profile working next time; anything broader than these limits is an Amazon-wide aggregate
    // that would drag unrelated services into game rules.
    private const int MinAwsPrefixV4 = 12;
    private const int MinAwsPrefixV6 = 32;
    private const int FallbackPrefixV4 = 24;
    private const int FallbackPrefixV6 = 64;

    /// <summary>
    /// Addresses inside Amazon's game-hosting services (EC2/GameLift/Global Accelerator) widen to their regional
    /// prefix whatever the protocol: game backends there (matchmaking over HTTPS included) move between IPs from
    /// session to session. Other UDP addresses (game servers on other hosters) widen to /24 (/64); other TCP-only
    /// addresses (APIs, CDNs) stay exact, so a CDN node never drags its neighbours into game rules.
    /// </summary>
    public static IReadOnlyList<string> Build(IEnumerable<LearnedAddress> addresses, AwsIpRanges? aws) =>
        Collapse(addresses.Select(a => ToNetwork(a, aws)));

    // Anything broader than this is not "a game's servers" and would silently widen game rules to half the internet.
    public const int MinPrefixV4 = 8;
    public const int MinPrefixV6 = 16;

    [GeneratedRegex(@"^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}(/\d{1,2})?$")]
    private static partial Regex StrictV4();

    [GeneratedRegex(@"^[0-9a-fA-F:.]*:[0-9a-fA-F:.]*(/\d{1,3})?$")]
    private static partial Regex StrictV6();

    /// <summary>Adds new lines to an existing ipset file's lines. Comments, garbage, lenient forms
    /// (e.g. "1", zone ids) and overly broad prefixes are dropped.</summary>
    public static IReadOnlyList<string> Merge(IEnumerable<string> existingLines, IEnumerable<string> newLines)
    {
        var networks = new List<IPNetwork>();
        foreach (var line in existingLines.Concat(newLines))
        {
            if (TryParseStrict(line, out var n)) networks.Add(n);
        }
        return Collapse(networks);
    }

    public static bool TryParseStrict(string line, out IPNetwork network)
    {
        network = default;
        var t = line.Trim();
        if (t.Length == 0 || t.StartsWith('#')) return false;
        if (!StrictV4().IsMatch(t) && !StrictV6().IsMatch(t)) return false;

        if (t.Contains('/'))
        {
            if (!IPNetwork.TryParse(t, out network)) return false;
        }
        else
        {
            if (!IPAddress.TryParse(t, out var ip)) return false;
            network = new IPNetwork(ip, ip.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
        }
        var v4 = network.BaseAddress.AddressFamily == AddressFamily.InterNetwork;
        return network.PrefixLength >= (v4 ? MinPrefixV4 : MinPrefixV6);
    }

    private static IPNetwork ToNetwork(LearnedAddress learned, AwsIpRanges? aws)
    {
        var ip = learned.Address;
        var v4 = ip.AddressFamily == AddressFamily.InterNetwork;
        var found = aws?.SmallestContaining(ip);
        if (found is { } n && n.PrefixLength >= (v4 ? MinAwsPrefixV4 : MinAwsPrefixV6)) return n;
        if (!learned.SeenUdp) return new IPNetwork(ip, v4 ? 32 : 128);
        return Truncate(ip, v4 ? FallbackPrefixV4 : FallbackPrefixV6);
    }

    private static IPNetwork Truncate(IPAddress ip, int prefix)
    {
        var bytes = ip.GetAddressBytes();
        for (var bit = prefix; bit < bytes.Length * 8; bit++)
        {
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        }
        return new IPNetwork(new IPAddress(bytes), prefix);
    }

    // Drop networks already covered by a broader one, then sort for a stable, diff-friendly file.
    private static IReadOnlyList<string> Collapse(IEnumerable<IPNetwork> networks)
    {
        var ordered = networks.Distinct().OrderBy(n => n.PrefixLength).ToList();
        var kept = new List<IPNetwork>();
        foreach (var n in ordered)
        {
            if (!kept.Any(k => k.BaseAddress.AddressFamily == n.BaseAddress.AddressFamily && k.Contains(n.BaseAddress)))
            {
                kept.Add(n);
            }
        }
        return kept.Select(n => n.ToString()).Order(StringComparer.Ordinal).ToList();
    }
}

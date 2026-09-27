using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Obkhodiki.Core.Updates;

namespace Obkhodiki.Core.Telegram;

/// <summary>Releases of Flowseal/tg-ws-proxy: a local MTProto proxy that tunnels Telegram through WebSockets.</summary>
public sealed partial class TgProxyReleaseClient : GitHubReleaseSource
{
    public const long MaxDownloadBytes = 100L * 1024 * 1024;

    [GeneratedRegex(@"^TgWsProxy_windows\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex X64Asset();

    [GeneratedRegex(@"^TgWsProxy_windows_arm64\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex Arm64Asset();

    public TgProxyReleaseClient(HttpClient http, Architecture architecture, TimeSpan? downloadDeadline = null)
        : base(http, "Flowseal/tg-ws-proxy", architecture == Architecture.Arm64 ? Arm64Asset() : X64Asset(), MaxDownloadBytes, downloadDeadline)
    {
    }
}

/// <summary>Keeps downloaded proxy builds in versions/&lt;ver&gt;/TgWsProxy.exe and remembers the active one.</summary>
public sealed class TgProxyStore : VersionedFileStore
{
    public const string ExeName = "TgWsProxy.exe";

    public TgProxyStore(string root) : base(root, ExeName)
    {
    }

    public string? ActiveExe => ActiveMain;

    public string ExePath(string version) => MainPath(version);

    public string Install(Stream exe, string version)
    {
        using var buffer = new MemoryStream();
        exe.CopyTo(buffer);
        return Install(new Dictionary<string, byte[]> { [ExeName] = buffer.ToArray() }, version);
    }
}

/// <summary>Confirmed updates for the Telegram proxy (a single .exe release asset).</summary>
public sealed class TgProxyUpdater : FileReleaseUpdater
{
    public TgProxyUpdater(TgProxyReleaseClient releases, TgProxyStore store) : base(releases, store, "TG WS Proxy")
    {
    }

    protected override IReadOnlyDictionary<string, byte[]> Unpack(Stream download)
    {
        using var buffer = new MemoryStream();
        download.CopyTo(buffer);
        return new Dictionary<string, byte[]> { [TgProxyStore.ExeName] = buffer.ToArray() };
    }
}

/// <summary>Reads the proxy's own config (%APPDATA%\TgWsProxy\config.json) to build the link Telegram imports.</summary>
public static partial class TgProxyConfig
{
    public const int DefaultPort = 1443;
    public const long MaxConfigBytes = 64 * 1024;

    [GeneratedRegex("^[0-9a-fA-F]{32}\\z")]
    private static partial Regex HexSecret();

    [GeneratedRegex(@"^[0-9]{1,3}(\.[0-9]{1,3}){3}\z")]
    private static partial Regex DottedQuad();

    // The only shape of link ever handed to Telegram / Explorer.
    [GeneratedRegex(@"^tg://proxy\?server=[0-9]{1,3}(\.[0-9]{1,3}){3}&port=[0-9]{1,5}&secret=dd[0-9a-f]{32}\z")]
    private static partial Regex SafeLink();

    /// <returns>tg://proxy link, or null when the config is missing values or looks tampered with.</returns>
    public static string? BuildLink(string configJson)
    {
        if (Parse(configJson) is not { Secret: { } secret } c) return null;
        var link = $"tg://proxy?server={c.Host}&port={c.Port}&secret=dd{secret}";
        return SafeLink().IsMatch(link) ? link : null;
    }

    /// <summary>Where the proxy listens, as reachable from this PC (defaults when the config is missing or odd).</summary>
    public static System.Net.IPEndPoint Endpoint(string? configJson)
    {
        var c = configJson is null ? null : Parse(configJson);
        return new System.Net.IPEndPoint(System.Net.IPAddress.Parse(c?.Host ?? "127.0.0.1"), c?.Port ?? DefaultPort);
    }

    private sealed record Parsed(string Host, int Port, string? Secret);

    // The file is user-writable and read by the elevated app: every value is type- and format-checked, and any
    // unexpected shape yields null rather than an exception. Only IPv4 is accepted: .NET's IPv6 parser lets
    // arbitrary text through as a scope id ("::1%anything"), which would end up inside the link.
    private static Parsed? Parse(string configJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(configJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var host = "127.0.0.1";
            if (root.TryGetProperty("host", out var h))
            {
                if (h.ValueKind != JsonValueKind.String) return null;
                host = h.GetString()!;
            }
            var port = DefaultPort;
            if (root.TryGetProperty("port", out var p))
            {
                if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out port)) return null;
            }
            string? secret = null;
            if (root.TryGetProperty("secret", out var s))
            {
                if (s.ValueKind != JsonValueKind.String) return null;
                secret = s.GetString();
            }

            if (secret is not null && !HexSecret().IsMatch(secret)) return null;
            if (port is < 1 or > 65535) return null;
            if (host == "0.0.0.0") host = "127.0.0.1"; // listening everywhere; this PC reaches it on loopback
            // Plain dotted quad only: legacy forms like "1.2.3" parse to something else entirely.
            if (!DottedQuad().IsMatch(host)) return null;
            if (!System.Net.IPAddress.TryParse(host, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;

            // Rebuilt from the parsed address, never the raw string.
            return new Parsed(ip.ToString(), port, secret?.ToLowerInvariant());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

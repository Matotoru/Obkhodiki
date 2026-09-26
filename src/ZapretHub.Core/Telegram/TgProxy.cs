using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZapretHub.Core.Updates;

namespace ZapretHub.Core.Telegram;

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
public sealed class TgProxyStore
{
    public const string ExeName = "TgWsProxy.exe";
    private const string PointerFile = "active.txt";

    private readonly string _root;
    private readonly string _versionsDir;

    public TgProxyStore(string root)
    {
        _root = root;
        _versionsDir = Path.Combine(root, "versions");
    }

    public string? ActiveVersion
    {
        get
        {
            var pointer = Path.Combine(_root, PointerFile);
            try
            {
                if (!File.Exists(pointer)) return null;
                var version = ReleaseVersion.Normalize(File.ReadAllText(pointer));
                return File.Exists(ExePath(version)) ? version : null;
            }
            catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    public string? ActiveExe => ActiveVersion is { } v ? ExePath(v) : null;

    public string ExePath(string version) => Path.Combine(_versionsDir, ReleaseVersion.Normalize(version), ExeName);

    /// <summary>Stores a verified executable and makes it active. The previous version stays on disk until cleanup.</summary>
    public string Install(Stream exe, string version)
    {
        version = ReleaseVersion.Normalize(version);
        Directory.CreateDirectory(_versionsDir);
        var staging = Path.Combine(_versionsDir, ".staging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var stagedExe = Path.Combine(staging, ExeName);
            using (var file = File.Create(stagedExe)) exe.CopyTo(file);
            if (!LooksLikeWindowsExecutable(stagedExe))
            {
                throw new InvalidDataException("Downloaded file is not a Windows executable.");
            }

            var target = Path.Combine(_versionsDir, version);
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
            Directory.Move(staging, target);
            Activate(version);
            return ExePath(version);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    public void Activate(string version)
    {
        version = ReleaseVersion.Normalize(version);
        if (!File.Exists(ExePath(version))) throw new FileNotFoundException($"TG WS Proxy {version} is not installed.");
        var tmp = Path.Combine(_root, PointerFile + ".tmp");
        File.WriteAllText(tmp, version);
        File.Move(tmp, Path.Combine(_root, PointerFile), overwrite: true);
    }

    /// <summary>Removes non-active versions; ones locked by a running proxy are left for next time.</summary>
    public void CleanupInactive()
    {
        var active = ActiveVersion;
        if (active is null || !Directory.Exists(_versionsDir)) return;
        foreach (var dir in Directory.GetDirectories(_versionsDir))
        {
            if (string.Equals(Path.GetFileName(dir), active, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool LooksLikeWindowsExecutable(string path)
    {
        using var f = File.OpenRead(path);
        if (f.Length < 64) return false;
        Span<byte> header = stackalloc byte[2];
        return f.Read(header) == 2 && header[0] == (byte)'M' && header[1] == (byte)'Z';
    }
}

/// <summary>Same check → confirm → install → switch → cleanup flow as the engine, for the Telegram proxy.</summary>
public sealed class TgProxyUpdater
{
    private readonly TgProxyReleaseClient _releases;
    private readonly TgProxyStore _store;

    public TgProxyUpdater(TgProxyReleaseClient releases, TgProxyStore store)
    {
        _releases = releases;
        _store = store;
    }

    public async Task<ReleaseInfo?> CheckAsync(string? skipVersion, CancellationToken ct)
    {
        var latest = await _releases.GetLatestAsync(ct).ConfigureAwait(false);
        if (!ReleaseVersion.IsNewer(latest.Version, _store.ActiveVersion)) return null;
        if (skipVersion is not null && latest.Version == ReleaseVersion.Normalize(skipVersion)) return null;
        return latest;
    }

    /// <param name="switchTo">Restarts the proxy on the new exe; if it throws, the previous version is re-activated,
    /// passed to <paramref name="rollback"/>, and the failure is reported as a defective release.</param>
    /// <returns>The installed version, or null when <paramref name="release"/> is not newer than the active one.</returns>
    public async Task<string?> InstallAsync(ReleaseInfo release, Func<string, Task>? switchTo, Func<string, Task>? rollback, CancellationToken ct)
    {
        if (!ReleaseVersion.IsNewer(release.Version, _store.ActiveVersion)) return null;

        var previous = _store.ActiveVersion;
        string exe;
        try
        {
            await using var download = await _releases.DownloadAsync(release, ct).ConfigureAwait(false);
            exe = _store.Install(download, release.Version);
        }
        catch (InvalidDataException ex)
        {
            throw new UpdateException($"TG WS Proxy {release.Version} is not usable: {ex.Message}", ex, release.Version, releaseDefect: true);
        }
        catch (UpdateException ex) when (ex.Version is null)
        {
            throw new UpdateException(ex.Message, ex.InnerException, release.Version, ex.ReleaseDefect);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Local disk trouble, not a bad release: report it through the normal update error path.
            throw new UpdateException($"TG WS Proxy {release.Version} could not be stored: {ex.Message}", ex, release.Version);
        }

        if (switchTo is not null)
        {
            try
            {
                await switchTo(exe).ConfigureAwait(false);
            }
            catch (Exception ex) when (previous is not null)
            {
                _store.Activate(previous);
                var note = "";
                if (rollback is not null)
                {
                    try
                    {
                        await rollback(_store.ExePath(previous)).ConfigureAwait(false);
                    }
                    catch (Exception rollbackEx)
                    {
                        // Still a defective release; the caller learns the old version did not come back either.
                        note = $" Restarting {previous} also failed: {rollbackEx.Message}";
                    }
                }
                throw new UpdateException($"TG WS Proxy {release.Version} failed to start; rolled back to {previous}: {ex.Message}{note}",
                    ex, release.Version, releaseDefect: true);
            }
        }

        _store.CleanupInactive();
        return release.Version;
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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Obkhodiki.Core.Vpn;

namespace Obkhodiki.App;

/// <summary>
/// Where the servers come from: one share link, or a subscription URL plus its last downloaded server list.
/// Both hold credentials (the subscription URL is a token too), so the whole record is stored encrypted.
/// </summary>
internal sealed class VpnSource
{
    public string? Link { get; set; }
    public string? SubscriptionUrl { get; set; }

    /// <summary>Last good subscription content (share links).</summary>
    public List<string> Links { get; set; } = new();

    /// <summary>The user accepted servers without certificate checks; otherwise such servers are left out.</summary>
    public bool AllowInsecure { get; set; }

    public string? Title { get; set; }
    public long? Used { get; set; }
    public long? Total { get; set; }
    public DateTimeOffset? Expire { get; set; }
    public DateTimeOffset? FetchedAt { get; set; }
    public double UpdateHours { get; set; } = 12;
    public int SkippedCount { get; set; }

    public bool IsSubscription => SubscriptionUrl is not null;

    private (string? Link, List<string> Links, bool Insecure, IReadOnlyList<VpnServerEntry> Servers)? _cache;

    /// <summary>Usable servers; broken or (unless accepted) insecure entries are left out. Parsed once per content.</summary>
    public IReadOnlyList<VpnServerEntry> Servers()
    {
        if (_cache is { } c && c.Link == Link && ReferenceEquals(c.Links, Links) && c.Insecure == AllowInsecure) return c.Servers;
        var result = new List<VpnServerEntry>();
        foreach (var raw in Link is not null ? new List<string> { Link } : Links)
        {
            try
            {
                var entry = VpnServerEntry.FromLink(raw);
                if (entry.Server.Insecure && !AllowInsecure) continue;
                if (result.All(e => e.Tag != entry.Tag)) result.Add(entry);
            }
            catch (FormatException)
            {
            }
        }
        _cache = (Link, Links, AllowInsecure, result);
        return result;
    }

    public bool UpdateDue(DateTimeOffset now) =>
        IsSubscription && (FetchedAt is null || now - FetchedAt.Value >= TimeSpan.FromHours(Math.Clamp(UpdateHours, 1, 168)));

    /// <summary>
    /// Fingerprint of the servers as the config sees them. Tags cover every connection setting but not the
    /// display name, so a panel that rewrites names on each fetch does not restart the tunnel.
    /// </summary>
    public string Signature() => string.Join(",", Servers().Select(s => s.Tag));

    /// <summary>Host of the subscription (resolved outside the tunnel so a dead server cannot block updates).</summary>
    public string? SubscriptionHost => Uri.TryCreate(SubscriptionUrl, UriKind.Absolute, out var u) ? u.Host : null;

    /// <summary>For the UI and logs: never the URL, links or passwords.</summary>
    public string Describe()
    {
        var servers = Servers();
        if (!IsSubscription) return servers.Count == 1 ? servers[0].Server.ToString()! : "Сервер не читается";
        var host = Uri.TryCreate(SubscriptionUrl, UriKind.Absolute, out var u) ? u.Host : "подписка";
        return $"{Title ?? host} · {Plural(servers.Count)}";
    }

    public static string Plural(int n) =>
        n % 10 == 1 && n % 100 != 11 ? $"{n} сервер"
        : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? $"{n} сервера"
        : $"{n} серверов";
}

/// <summary>
/// Encrypted with Windows DPAPI for the current user (the app and its autostart task run as that user) inside
/// the admin-only data folder, so a copy of the file is useless to other accounts or machines.
/// </summary>
internal static class VpnSourceStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ZapretHub.VpnServer.v1") /* legacy name kept: changing it would make stored servers unreadable */;

    public static void Save(VpnSource source)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.VpnServerFile)!);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(source)), Entropy, DataProtectionScope.CurrentUser);
        var tmp = AppPaths.VpnServerFile + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, AppPaths.VpnServerFile, overwrite: true);
        Log.Info($"VPS source saved: {source.Describe()}");
    }

    public static VpnSource? Load()
    {
        string text;
        var migrate = false;
        try
        {
            if (!File.Exists(AppPaths.VpnServerFile)) return null;
            var data = File.ReadAllBytes(AppPaths.VpnServerFile);
            try
            {
                text = Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException)
            {
                // Version 0.2 encrypted for the whole machine.
                text = Encoding.UTF8.GetString(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.LocalMachine));
                migrate = true;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Stored VPS source is unreadable", ex);
            return null;
        }

        try
        {
            // Version 0.2 stored the bare hysteria2:// link.
            var source = text.TrimStart().StartsWith('{')
                ? JsonSerializer.Deserialize<VpnSource>(text)
                : new VpnSource { Link = text.Trim(), AllowInsecure = true };
            if (migrate && source is not null) Save(source);
            return source;
        }
        catch (JsonException ex)
        {
            Log.Error("Stored VPS source is unreadable", ex);
            return null;
        }
    }

    public static void Remove()
    {
        if (File.Exists(AppPaths.VpnServerFile)) File.Delete(AppPaths.VpnServerFile);
        // The generated config also contains the passwords.
        foreach (var f in new[] { AppPaths.VpnConfig, AppPaths.VpnConfig + ".tmp" })
        {
            if (File.Exists(f)) File.Delete(f);
        }
        Log.Info("VPS source removed");
    }
}

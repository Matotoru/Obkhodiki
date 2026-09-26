using System.Security.Cryptography;
using System.Text;
using ZapretHub.Core.Vpn;

namespace ZapretHub.App;

/// <summary>
/// Keeps the hysteria2:// link encrypted with Windows DPAPI (machine scope, app-specific entropy) inside the
/// admin-only data folder. It is decrypted only to build the sing-box config.
/// </summary>
internal static class VpnServerStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ZapretHub.VpnServer.v1");

    public static void Save(Hysteria2Link link, string rawText)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.VpnServerFile)!);
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(rawText.Trim()), Entropy, DataProtectionScope.LocalMachine);
        var tmp = AppPaths.VpnServerFile + ".tmp";
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, AppPaths.VpnServerFile, overwrite: true);
        Log.Info($"VPS server saved: {link}");
    }

    public static Hysteria2Link? Load()
    {
        var text = LoadRaw();
        if (text is null) return null;
        try
        {
            return Hysteria2Link.Parse(text);
        }
        catch (FormatException ex)
        {
            Log.Error("Stored VPS server is unreadable", ex);
            return null;
        }
    }

    /// <summary>The decrypted link text (only used to detect changes and to build the config).</summary>
    public static string? LoadRaw()
    {
        try
        {
            if (!File.Exists(AppPaths.VpnServerFile)) return null;
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(AppPaths.VpnServerFile), Entropy, DataProtectionScope.LocalMachine));
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            Log.Error("Stored VPS server is unreadable", ex);
            return null;
        }
    }

    public static void Remove()
    {
        if (File.Exists(AppPaths.VpnServerFile)) File.Delete(AppPaths.VpnServerFile);
        // The generated config also contains the password.
        foreach (var f in new[] { AppPaths.VpnConfig, AppPaths.VpnConfig + ".tmp" })
        {
            if (File.Exists(f)) File.Delete(f);
        }
        Log.Info("VPS server removed");
    }
}

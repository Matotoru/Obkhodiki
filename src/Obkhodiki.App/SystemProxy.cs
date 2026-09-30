using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Obkhodiki.App;

/// <summary>
/// The Windows system proxy (WinINet, per user) for the VPS "proxy" mode. The user's previous settings are saved to
/// a file before the first change and put back on stop, exit, a sing-box crash or — after an app crash — on the next
/// start. A proxy left pointing at a dead local port cuts off every browser (seen with other clients' 127.0.0.1:2080).
/// </summary>
internal static class SystemProxy
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private static readonly string BackupFile = Path.Combine(AppPaths.VpnRoot, "system-proxy-backup.json");
    private static readonly object Gate = new();

    // Local and private addresses never go to the proxy.
    public const string Bypass = "localhost;127.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;" +
                                 "172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;192.168.*;169.254.*;<local>";

    /// <param name="Ours">The address this app set, to recognise it later.</param>
    private sealed record Saved(int Enable, string? Server, string? Override, string? AutoConfigUrl, string? Ours);

    public static string Address(int port) => $"127.0.0.1:{port}";

    /// <summary>Whether the system proxy currently points at this app's proxy port.</summary>
    public static bool IsOurs(int port)
    {
        using var key = Registry.CurrentUser.OpenSubKey(Key);
        return key?.GetValue("ProxyEnable") is int enable && enable != 0 && key.GetValue("ProxyServer") as string == Address(port);
    }

    public static void Apply(int port)
    {
        lock (Gate)
        {
            if (IsOurs(port) && ReadBackup()?.Ours == Address(port)) return;
            using var key = Registry.CurrentUser.CreateSubKey(Key, writable: true);
            // Save the user's own settings once; re-applying (new port after a restart) keeps the first copy.
            var saved = ReadBackup() ?? new Saved(key.GetValue("ProxyEnable") as int? ?? 0, key.GetValue("ProxyServer") as string,
                key.GetValue("ProxyOverride") as string, key.GetValue("AutoConfigURL") as string, null);
            Directory.CreateDirectory(Path.GetDirectoryName(BackupFile)!);
            File.WriteAllText(BackupFile, JsonSerializer.Serialize(saved with { Ours = Address(port) }));
            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
            key.SetValue("ProxyServer", Address(port), RegistryValueKind.String);
            key.SetValue("ProxyOverride", Bypass, RegistryValueKind.String);
            // A PAC script would win over the manual proxy.
            key.DeleteValue("AutoConfigURL", throwOnMissingValue: false);
        }
        Refresh();
        Log.Info($"System proxy set to {Address(port)}");
    }

    /// <summary>
    /// Puts back what the user had. When the proxy no longer points at a local port of ours (the user changed it
    /// meanwhile), their newer setting is left alone.
    /// </summary>
    public static void Restore()
    {
        lock (Gate)
        {
            if (!File.Exists(BackupFile)) return;
            try
            {
                var saved = ReadBackup();
                using var key = Registry.CurrentUser.CreateSubKey(Key, writable: true);
                if (saved is not null && key.GetValue("ProxyServer") as string == saved.Ours)
                {
                    key.SetValue("ProxyEnable", saved.Enable, RegistryValueKind.DWord);
                    SetOrDelete(key, "ProxyServer", saved.Server);
                    SetOrDelete(key, "ProxyOverride", saved.Override);
                    SetOrDelete(key, "AutoConfigURL", saved.AutoConfigUrl);
                    Log.Info("System proxy restored");
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Log.Error("Restoring the system proxy failed", ex);
            }
            finally
            {
                try
                {
                    File.Delete(BackupFile);
                }
                catch (IOException)
                {
                }
            }
        }
        Refresh();
    }

    private static Saved? ReadBackup()
    {
        try
        {
            return File.Exists(BackupFile) ? JsonSerializer.Deserialize<Saved>(File.ReadAllText(BackupFile)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void SetOrDelete(RegistryKey key, string name, string? value)
    {
        if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
        else key.SetValue(name, value, RegistryValueKind.String);
    }

    // Running browsers pick up the change only after these notifications.
    private static void Refresh()
    {
        InternetSetOption(IntPtr.Zero, 39 /* INTERNET_OPTION_SETTINGS_CHANGED */, IntPtr.Zero, 0);
        InternetSetOption(IntPtr.Zero, 37 /* INTERNET_OPTION_REFRESH */, IntPtr.Zero, 0);
    }

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}

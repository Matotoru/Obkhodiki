using System.Diagnostics;
using System.Text.Json;

namespace Obkhodiki.App;

/// <summary>
/// The installed official Cloudflare WARP / One Client, driven through warp-cli. Obkhodiki switches it to proxy mode
/// (a local SOCKS5 port, no system routes) so sing-box can send chosen sites through it; the user's previous mode is
/// saved and put back when WARP is turned off in Obkhodiki.
/// </summary>
internal static class WarpClient
{
    public const int DefaultPort = 40000;
    private static readonly string BackupFile = Path.Combine(AppPaths.VpnRoot, "warp-mode-backup.txt");
    private static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(20);

    // Modes warp-cli accepts back ("warp-cli mode <name>").
    private static readonly HashSet<string> Modes = new(StringComparer.OrdinalIgnoreCase)
    {
        "warp", "doh", "warp+doh", "dot", "warp+dot", "proxy", "tunnel_only",
    };

    public static string? CliPath
    {
        get
        {
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                if (root.Length == 0) continue;
                var path = Path.Combine(root, "Cloudflare", "Cloudflare WARP", "warp-cli.exe");
                if (File.Exists(path)) return path;
            }
            return null;
        }
    }

    public static bool IsInstalled => CliPath is not null;

    /// <summary>Proxy mode on <paramref name="port"/> and connected; throws with a readable message otherwise.</summary>
    public static async Task EnsureProxyAsync(int port, CancellationToken ct)
    {
        var cli = CliPath ?? throw new InvalidOperationException("Cloudflare WARP не установлен. Скачайте его с https://one.one.one.one и включите WARP в Obkhodiki ещё раз.");
        var (mode, status) = await ReadStateAsync(cli, ct);
        if (!mode.Equals("proxy", StringComparison.OrdinalIgnoreCase))
        {
            if (!File.Exists(BackupFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(BackupFile)!);
                await File.WriteAllTextAsync(BackupFile, mode, ct);
            }
            await RunCheckedAsync(cli, ct, "--accept-tos", "mode", "proxy");
        }
        await RunCheckedAsync(cli, ct, "--accept-tos", "proxy", "port", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (!status.Equals("Connected", StringComparison.OrdinalIgnoreCase)) await RunCheckedAsync(cli, ct, "--accept-tos", "connect");

        var deadline = DateTime.UtcNow + ConnectWait;
        while (true)
        {
            (_, status) = await ReadStateAsync(cli, ct);
            if (status.Equals("Connected", StringComparison.OrdinalIgnoreCase)) break;
            if (DateTime.UtcNow > deadline) throw new InvalidOperationException($"Cloudflare WARP не подключился за {ConnectWait.TotalSeconds:0} с (состояние: {status}). Возможно, WARP заблокирован в вашей сети.");
            await Task.Delay(500, ct);
        }
        Log.Info($"Cloudflare WARP connected in proxy mode on 127.0.0.1:{port}");
    }

    /// <summary>Disconnects and puts back the mode the user had before Obkhodiki switched it (only if it did).</summary>
    public static async Task RestoreAsync(CancellationToken ct)
    {
        if (!File.Exists(BackupFile) || CliPath is not { } cli) return;
        try
        {
            var previous = (await File.ReadAllTextAsync(BackupFile, ct)).Trim();
            await RunAsync(cli, ct, "disconnect");
            await RunAsync(cli, ct, "--accept-tos", "mode", Modes.Contains(previous) ? previous : "warp");
            Log.Info($"Cloudflare WARP disconnected, mode restored to {previous}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
        {
            Log.Error("Restoring Cloudflare WARP failed", ex);
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

    /// <summary>Current mode ("warp", "proxy"…) and connection status ("Connected", "Disconnected"…).</summary>
    private static async Task<(string Mode, string Status)> ReadStateAsync(string cli, CancellationToken ct)
    {
        var (_, settingsJson) = await RunAsync(cli, ct, "-j", "settings");
        var (_, statusJson) = await RunAsync(cli, ct, "-j", "status");
        try
        {
            using var settings = JsonDocument.Parse(settingsJson);
            using var status = JsonDocument.Parse(statusJson);
            var mode = settings.RootElement.GetProperty("settings").GetProperty("operation_mode").GetString() ?? "warp";
            var state = status.RootElement.GetProperty("status").GetString() ?? "";
            return (MapMode(mode), state);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException("Cloudflare WARP ответил непонятно: " + Short(settingsJson + statusJson), ex);
        }
    }

    // Settings JSON spells some modes differently from the command ("WarpWithDnsOverHttps"…); unknown ones count as "warp".
    private static string MapMode(string mode) => mode.ToLowerInvariant() switch
    {
        var m when Modes.Contains(m) => m,
        "warpwithdnsoverhttps" => "warp+doh",
        "warpwithdnsovertls" => "warp+dot",
        "dnsoverhttps" => "doh",
        "dnsovertls" => "dot",
        "warpproxy" or "proxymode" => "proxy",
        "tunnelonly" => "tunnel_only",
        _ => "warp",
    };

    private static async Task RunCheckedAsync(string cli, CancellationToken ct, params string[] args)
    {
        var (code, output) = await RunAsync(cli, ct, args);
        if (code != 0) throw new InvalidOperationException($"warp-cli {string.Join(' ', args.Where(a => a != "--accept-tos"))}: {Short(output)}");
    }

    private static async Task<(int Code, string Output)> RunAsync(string cli, CancellationToken ct, params string[] args)
    {
        var psi = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in new[] { "--no-paginate" }.Concat(args)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("warp-cli не запустился.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            await p.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("warp-cli не ответил за 15 секунд.");
        }
        return (p.ExitCode, await stdout + await stderr);
    }

    private static string Short(string text)
    {
        var t = text.Trim().ReplaceLineEndings(" ");
        return t.Length > 200 ? t[..200] + "…" : t;
    }
}

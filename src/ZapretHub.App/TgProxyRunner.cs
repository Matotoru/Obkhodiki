using System.Diagnostics;
using System.Net;
using ZapretHub.Core.Telegram;

namespace ZapretHub.App;

/// <summary>
/// Runs Flowseal's tg-ws-proxy. It needs no admin rights, so it is started with the desktop shell's own
/// non-elevated token (no Explorer hand-off that could silently fail and run it elevated), and that is verified
/// too. Only processes in this Windows session whose kernel image path lies in our folder count as "ours".
/// </summary>
internal static class TgProxyRunner
{
    private static readonly TimeSpan AppearTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ListenTimeout = TimeSpan.FromSeconds(20);
    private static readonly int SessionId = Process.GetCurrentProcess().SessionId;

    public static bool IsRunning() => FindManagedPids().Count > 0;

    /// <summary>Other copies of tg-ws-proxy in this session (installed separately) that would compete for its port.</summary>
    public static IReadOnlyList<int> FindForeignPids() =>
        Candidates().Where(c => !IsUnder(c.Path, AppPaths.TgRoot)).Select(c => c.Pid).ToList();

    /// <summary>Starts the proxy non-elevated.</summary>
    /// <param name="requireListener">Fail unless the proxy accepts connections (used to detect a broken update:
    /// a crashed PyInstaller build keeps its process alive behind an error box).</param>
    /// <returns>Whether the proxy is accepting connections.</returns>
    public static async Task<bool> StartAsync(string exePath, bool requireListener)
    {
        var shell = UserShell(out var shellElevated) ?? throw new InvalidOperationException(
            "Не найден рабочий стол Windows (Explorer). Прокси запускается от имени пользователя рабочего стола.");
        var endpoint = TgProxyConfig.Endpoint(ReadConfig(shell));

        // Idempotent: already running on this very exe is success.
        if (!FindManagedPids().Any(pid => SamePath(NativeProcess.ImagePath(pid), exePath)))
        {
            if (FindManagedPids().Count > 0) Stop(); // another managed version: replace it
            NativeProcess.StartAsUser(shell, exePath, argument: null);

            var sw = Stopwatch.StartNew();
            while (!FindManagedPids().Any(pid => SamePath(NativeProcess.ImagePath(pid), exePath)))
            {
                if (sw.Elapsed > AppearTimeout)
                {
                    Stop(); // it might still appear later; never leave a stray instance behind
                    throw new InvalidOperationException("TG WS Proxy не запустился.");
                }
                await Task.Delay(250).ConfigureAwait(false);
            }

            // A third-party app must never run with more rights than the user's desktop itself.
            if (!shellElevated && FindManagedPids().Any(pid => NativeProcess.IsElevated(pid) == true))
            {
                Stop();
                throw new InvalidOperationException("TG WS Proxy запустился с правами администратора и был остановлен.");
            }
        }

        var listening = await WaitForListenerAsync(endpoint).ConfigureAwait(false);
        if (requireListener && !listening)
        {
            Stop();
            throw new InvalidOperationException($"TG WS Proxy не начал принимать подключения на {endpoint}.");
        }
        return listening;
    }

    /// <summary>
    /// Stops our proxy. PyInstaller's parent process deletes the unpacked runtime in %TEMP% once its child
    /// exits, so children go first and the parent gets a moment to clean up before it is killed too.
    /// </summary>
    public static void Stop() => StopPids(FindManagedPids());

    public static void StopForeign() => StopPids(FindForeignPids());

    /// <summary>The proxy's config.json of the user whose desktop this is (not the elevated account's).</summary>
    public static string? ReadConfigForSessionUser() => UserShell(out _) is { } shell ? ReadConfig(shell) : null;

    /// <summary>Waits for the desktop shell (right after logon the app can start before Explorer).</summary>
    public static async Task<bool> WaitForShellAsync(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (UserShell(out _) is null)
        {
            if (sw.Elapsed > timeout) return false;
            await Task.Delay(1000).ConfigureAwait(false);
        }
        return true;
    }

    /// <summary>Opens a URL as the desktop user (never elevated). Returns false when no such shell exists.</summary>
    public static bool OpenAsUser(string url)
    {
        if (UserShell(out _) is not { } shell) return false;
        // A fresh Explorer with the user's token resolves the tg: handler as that user.
        NativeProcess.StartAsUser(shell, AppPaths.WindowsTool("explorer.exe"), url);
        return true;
    }

    private static void StopPids(IReadOnlyList<int> pids)
    {
        if (pids.Count == 0) return;
        var set = pids.ToHashSet();
        var children = pids.Where(p => NativeProcess.ParentPid(p) is { } parent && set.Contains(parent)).ToList();

        var images = pids.ToDictionary(p => p, NativeProcess.ImagePath);
        foreach (var pid in children) Kill(pid, images[pid], entireTree: false);
        // Give the parent a chance to remove %TEMP%\_MEI* after its child exits.
        foreach (var pid in pids.Except(children)) WaitExit(pid, TimeSpan.FromSeconds(3));

        var failed = new List<int>();
        foreach (var pid in pids)
        {
            if (!Kill(pid, images[pid], entireTree: true)) failed.Add(pid);
        }
        if (failed.Count > 0)
        {
            throw new InvalidOperationException($"Не удалось остановить TG WS Proxy (PID {string.Join(", ", failed)}).");
        }
    }

    /// <returns>True when the process is gone.</returns>
    private static bool Kill(int pid, string? expectedImage, bool entireTree)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            // The PID may have been reused since it was found: only kill the same program.
            if (expectedImage is null || !SamePath(NativeProcess.ImagePath(pid), expectedImage)) return true;
            if (!p.HasExited) p.Kill(entireTree);
            return p.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }
        catch (InvalidOperationException)
        {
            return true; // exited between lookup and kill
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied or similar: report it only if the process is really still there.
            try
            {
                using var p = Process.GetProcessById(pid);
                return p.HasExited;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }
    }

    private static void WaitExit(int pid, TimeSpan timeout)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.WaitForExit(timeout);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    // "Listening" means one of OUR processes owns the listening socket: any other program (or another user's
    // proxy) on the same port must not make a broken update look healthy.
    private static async Task<bool> WaitForListenerAsync(IPEndPoint endpoint)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < ListenTimeout)
        {
            var ours = FindManagedPids();
            if (NativeProcess.TcpListenerPids(endpoint.Port).Overlaps(ours)) return true;
            await Task.Delay(500).ConfigureAwait(false);
        }
        return false;
    }

    private static string? ReadConfig(int shellPid)
    {
        try
        {
            if (NativeProcess.UserSid(shellPid) is not { } sid || NativeProcess.RoamingAppData(sid) is not { } appData) return null;
            var path = Path.Combine(appData, "TgWsProxy", "config.json");
            var info = new FileInfo(path);
            // A user-controlled file read by the elevated app: no links, no devices, bounded read.
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[TgProxyConfig.MaxConfigBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0) total += read;
            return total > TgProxyConfig.MaxConfigBytes ? null : System.Text.Encoding.UTF8.GetString(buffer, 0, total);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            Log.Error("Reading TG WS Proxy config failed", ex);
            return null;
        }
    }

    // The real desktop shell of this session; null if there is none.
    private static int? UserShell(out bool elevated) => NativeProcess.DesktopShellPid(SessionId, out elevated);

    private static List<int> FindManagedPids() =>
        Candidates().Where(c => IsUnder(c.Path, AppPaths.TgRoot)).Select(c => c.Pid).ToList();

    private static List<(int Pid, string Path)> Candidates()
    {
        var result = new List<(int, string)>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.SessionId != SessionId || !p.ProcessName.StartsWith("TgWsProxy", StringComparison.OrdinalIgnoreCase)) continue;
                    if (NativeProcess.ImagePath(p.Id) is { } path) result.Add((p.Id, path));
                }
                catch (InvalidOperationException)
                {
                    // Exited while enumerating.
                }
            }
        }
        return result;
    }

    private static bool IsUnder(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string? a, string b) =>
        a is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
}

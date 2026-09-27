using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;
using System.Text;
using Obkhodiki.Core.Games;

namespace Obkhodiki.App;

/// <summary>Game profiles: per-game modules that apply game rules only to that game's addresses.</summary>
internal sealed partial class AppController
{
    // Flowseal's game filter covers these ports; used while learning in "bypass everything" mode.
    private static readonly PortSet LearningPorts = PortSet.Parse("1024-65535");

    private volatile GameRule? _learningRule;
    private volatile LearningSession? _learningSession;
    // Whether bypass should run once learning stops. Starts as "was it running", then follows any
    // Enable/Disable/auto-select the user does while recording.
    private volatile bool _bypassWantedAfterLearning;

    public bool IsLearning => _learningSession is not null;

    public static string GameIpsetPath(string id) => Path.Combine(AppPaths.GamesDir, GameProfiles.IpsetFileName(id));

    public Task SetGameProfileEnabledAsync(string id, bool enabled) => Serialized("Переключение игры…", silentErrors: false, async () =>
    {
        var profile = Settings.GameProfiles.FirstOrDefault(p => p.Id == id) ?? throw new InvalidOperationException("Профиль не найден.");
        if (enabled && GameRuleCompiler.Compile(new[] { Copy(profile, enabled: true) }, AppPaths.GamesDir, AppPaths.GamesRuntimeDir).Rules.Count == 0)
        {
            throw new InvalidOperationException($"У профиля «{profile.Name}» нет адресов или портов. Сначала запустите обучение.");
        }
        await ApplySettingWithRollback(() => profile.Enabled, v => profile.Enabled = v, enabled);
        Log.Info($"Game profile {id} {(enabled ? "enabled" : "disabled")}");
        await ReapplyVpnAfterProfileChangeAsync();
    });

    /// <summary>Creates or updates a profile; learned lines are merged into its existing address list.</summary>
    /// <returns>True when saved (errors are shown to the user and logged).</returns>
    public async Task<bool> SaveGameProfileAsync(GameProfile profile, IReadOnlyList<string> learnedLines)
    {
        var saved = false;
        await Serialized("Сохранение игры…", silentErrors: false, async () =>
        {
            Directory.CreateDirectory(AppPaths.GamesDir);
            var path = GameIpsetPath(profile.Id);
            var existing = File.Exists(path) ? await File.ReadAllLinesAsync(path) : Array.Empty<string>();
            var merged = IpsetBuilder.Merge(existing, learnedLines);
            var content = new StringBuilder()
                // The name may come from a window title the game controls: never let it break out of the comment line.
                .AppendLine($"# {GameProfiles.SafeComment(profile.Name)}: game server addresses (one IP or CIDR per line)")
                .AppendJoin(Environment.NewLine, merged)
                .AppendLine()
                .ToString();
            await File.WriteAllTextAsync(path, content);

            profile.Enabled = profile.Enabled && merged.Count > 0;
            var index = Settings.GameProfiles.FindIndex(p => p.Id == profile.Id);
            if (index >= 0) Settings.GameProfiles[index] = profile;
            else Settings.GameProfiles.Add(profile);
            _settingsStore.Save(Settings);
            saved = true;
            Log.Info($"Game profile {profile.Id} saved: {merged.Count} networks, TCP {profile.TcpPorts}, UDP {profile.UdpPorts}");

            await ReapplyVpnAfterProfileChangeAsync();
            if (!_runner.IsRunning || !profile.Enabled) return;
            try
            {
                await StartCoreAsync();
            }
            catch (Exception ex)
            {
                // Keep what was learned, but do not leave bypass off because of the new profile.
                Log.Error($"winws rejected game profile {profile.Id}", ex);
                profile.Enabled = false;
                _settingsStore.Save(Settings);
                await StartCoreAsync();
                Notify?.Invoke("Профиль сохранён, но выключен",
                    $"С правилами «{profile.Name}» winws не запустился: {ex.Message}", ToolTipIcon.Warning);
            }
        });
        return saved;
    }

    public Task DeleteGameProfileAsync(string id) => Serialized("Удаление игры…", silentErrors: false, async () =>
    {
        ForgetAutoRoute(Settings.GameProfiles.FirstOrDefault(p => p.Id == id)?.ProcessName);
        Settings.GameProfiles.RemoveAll(p => p.Id == id);
        await ReapplyVpnAfterProfileChangeAsync();
        _settingsStore.Save(Settings);
        if (_runner.IsRunning) await StartCoreAsync();
        foreach (var path in new[] { GameIpsetPath(id), Path.Combine(AppPaths.GamesRuntimeDir, GameProfiles.IpsetFileName(id)) })
        {
            if (File.Exists(path)) File.Delete(path);
        }
        Log.Info($"Game profile {id} deleted");
    });

    /// <summary>Starts recording the endpoints of processes named <paramref name="processName"/> into <paramref name="learner"/>.</summary>
    /// <param name="bypassAll">Temporarily apply game rules to every address (Flowseal's "game filter + ipset any"),
    /// so a game that cannot connect without bypass still reaches its servers while being learned.</param>
    /// <returns>False if recording could not start (the error is shown); nothing is left half-enabled.</returns>
    public async Task<bool> StartLearningAsync(string processName, bool bypassAll, TrafficLearner learner)
    {
        var started = false;
        await Serialized("Запуск записи…", silentErrors: false, async () =>
        {
            var engine = _engine ?? throw new InvalidOperationException("Движок Flowseal не установлен.");
            if (_learningSession is not null) throw new InvalidOperationException("Запись уже идёт.");
            if (!await ResolveConflictsAsync()) return;

            var wasRunning = _runner.IsRunning;
            var dll = Path.Combine(engine.BinDir, "WinDivert.dll");
            var session = await Task.Run(() => new LearningSession(dll, processName, learner,
                ex => Log.Error("Learning monitor failed", ex)));
            try
            {
                if (bypassAll)
                {
                    await File.WriteAllTextAsync(AppPaths.LearningAnyIpset, "");
                    _learningRule = new GameRule(AppPaths.LearningAnyIpset, LearningPorts, LearningPorts);
                }
                // Learning needs traffic flowing through bypass, so start it even if it was off.
                await StartCoreAsync();
            }
            catch
            {
                _learningRule = null;
                await Task.Run(session.Dispose);
                try
                {
                    await RestoreAfterLearningAsync(wasRunning);
                }
                catch (Exception restoreEx)
                {
                    // Report the original failure, not the follow-up one.
                    Log.Error("Restoring bypass after failed learning start failed", restoreEx);
                }
                throw;
            }

            _bypassWantedAfterLearning = wasRunning;
            _learningSession = session;
            started = true;
            Log.Info($"Learning started for {processName}, bypass all: {bypassAll}");
        });
        return started;
    }

    /// <summary>Stops recording and returns bypass to the state it had before learning.</summary>
    public Task StopLearningAsync() => Serialized("Остановка записи…", silentErrors: false, async () =>
    {
        var session = _learningSession;
        if (session is null) return;
        _learningSession = null;
        _learningRule = null;
        // Joining WinDivert reader threads can take a moment; keep it off the UI thread.
        await Task.Run(session.Dispose);
        await RestoreAfterLearningAsync(_bypassWantedAfterLearning);
        Log.Info($"Learning stopped: {session.Learner.Addresses.Count} addresses");
    });

    private async Task RestoreAfterLearningAsync(bool runBypass)
    {
        if (runBypass) await StartCoreAsync();
        else await Task.Run(_runner.Stop);
    }

    private IReadOnlyList<GameRule> CompileGameRules()
    {
        var compiled = GameRuleCompiler.Compile(Settings.GameProfiles, AppPaths.GamesDir, AppPaths.GamesRuntimeDir);
        foreach (var skipped in compiled.Skipped) Log.Error("Game profile skipped: " + skipped);
        var rules = compiled.Rules.ToList();
        if (_learningRule is { } learning) rules.Add(learning);
        return rules;
    }

    private static GameProfile Copy(GameProfile p, bool enabled) => new()
    {
        Id = p.Id, Name = p.Name, Enabled = enabled, ProcessName = p.ProcessName, TcpPorts = p.TcpPorts, UdpPorts = p.UdpPorts,
    };
}

/// <summary>A running capture of one game's connections.</summary>
internal sealed class LearningSession : IDisposable
{
    private readonly string _processName;
    // PIDs are reused by Windows, so a verdict is only trusted for a short while.
    private static readonly TimeSpan PidCacheTtl = TimeSpan.FromSeconds(30);
    private readonly Dictionary<int, (bool Match, DateTime At)> _pidCache = new();
    private readonly WinDivertFlowMonitor _monitor;

    public LearningSession(string winDivertDll, string processName, TrafficLearner learner, Action<Exception> onError)
    {
        _processName = Path.GetFileNameWithoutExtension(processName);
        Learner = learner;
        _monitor = new WinDivertFlowMonitor(winDivertDll, IsWatched, learner.Add, onError);
    }

    public TrafficLearner Learner { get; }

    // Called from WinDivert reader threads (the lock is uncontended in practice: two readers, few new PIDs). Any failure means "not ours": a process that exits between
    // lookup and name read throws InvalidOperationException, protected ones throw Win32Exception.
    private bool IsWatched(int pid)
    {
        lock (_pidCache)
        {
            var now = DateTime.UtcNow;
            if (_pidCache.TryGetValue(pid, out var known) && now - known.At < PidCacheTtl) return known.Match;
            bool match;
            try
            {
                using var p = Process.GetProcessById(pid);
                match = string.Equals(p.ProcessName, _processName, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                match = false;
            }
            _pidCache[pid] = (match, now);
            return match;
        }
    }

    public void Dispose() => _monitor.Dispose();
}

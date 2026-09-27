using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using ZapretHub.Core.Engine;
using ZapretHub.Core.Games;
using ZapretHub.Core.Updates;
using ZapretHub.Core.Vpn;

namespace ZapretHub.App;

/// <summary>
/// VPS module: runs sing-box with a Hysteria2 outbound. Selected programs, sites and games go through the VPS,
/// everything else stays direct. "Auto" games are measured once when they start and routed the better way.
/// </summary>
internal sealed partial class AppController
{
    public const string SingBoxProductName = "sing-box";
    private static readonly TimeSpan GameWatchInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CrashRestartDelay = TimeSpan.FromSeconds(5);
    private const int SamplesPerEndpoint = 6;
    private const int MaxEndpointsPerMeasurement = 3;
    private const int MaxCrashRestarts = 3;

    private readonly SingBoxStore _sbStore = new(AppPaths.SingBoxRoot);
    private SingBoxUpdater? _sbUpdaterInstance;
    private WinwsRunner? _singBoxInstance;
    private volatile ReleaseInfo? _availableSbUpdate;
    private bool _confirmingSbUpdate;
    private ProbeCredentials? _probeAuth;
    private int _probePort;
    private volatile string? _appliedSignature;
    private (bool Tun, VpnPlan Plan)? _appliedPlan;
    private System.Windows.Forms.Timer? _gameWatch;
    private bool _gameWatchBusy;
    private readonly Queue<DateTime> _recentCrashes = new();
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(10);

    // Auto decisions: made once when the game starts. The game stays on its path after it exits, so leaving the
    // game never restarts the tunnel under other traffic; the next game start measures again.
    private readonly object _autoGate = new();
    private readonly HashSet<string> _autoRunning = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoVpn = new(StringComparer.OrdinalIgnoreCase);

    private SingBoxUpdater SbUpdater =>
        _sbUpdaterInstance ??= new SingBoxUpdater(new SingBoxReleaseClient(_http, RuntimeInformation.OSArchitecture), _sbStore);

    private WinwsRunner SingBox
    {
        get
        {
            if (_singBoxInstance is not null) return _singBoxInstance;
            _singBoxInstance = new WinwsRunner(
                () => _sbStore.ActiveMain ?? throw new InvalidOperationException("sing-box не установлен."),
                startupCheck: TimeSpan.FromSeconds(2));
            _singBoxInstance.Crashed += OnSingBoxCrashed;
            return _singBoxInstance;
        }
    }

    public string? VpnServerName { get; private set; }

    /// <summary>Asked (UI thread) before saving a link that turns certificate checks off.</summary>
    public Func<bool> ConfirmInsecureServer { get; set; } = () => false;

    public bool IsVpnRunning => _singBoxInstance?.IsRunning == true;
    public ReleaseInfo? AvailableSbUpdate => _availableSbUpdate;
    public string? SingBoxVersion => _sbStore.ActiveVersion;

    /// <summary>Called on the UI thread; the game watch runs there too, so it reads settings safely.</summary>
    private void InitializeVpn()
    {
        VpnServerName = DisplayName(VpnServerStore.Load());
        _gameWatch = new System.Windows.Forms.Timer { Interval = (int)GameWatchInterval.TotalMilliseconds };
        _gameWatch.Tick += async (_, _) => await WatchGamesAsync();
        _gameWatch.Start();
    }

    // ---------- settings the menu changes ----------

    /// <returns>Error text for the dialog, or null when saved and applied.</returns>
    public async Task<string?> SetVpnServerAsync(string linkText)
    {
        Hysteria2Link link;
        try
        {
            link = Hysteria2Link.Parse(linkText);
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
        if (link.Insecure && !ConfirmInsecureServer()) return "Ссылка не сохранена.";

        string? error = "Не удалось применить настройки, подробности в уведомлении.";
        await Serialized("Подключение сервера VPS…", silentErrors: false, async () =>
        {
            VpnServerStore.Save(link, linkText);
            VpnServerName = DisplayName(link);
            _appliedSignature = null; // force a restart with the new server
            // Adding a server is the moment to fetch sing-box (with confirmation), not later in the background.
            if (_sbStore.ActiveMain is null && !await InstallFirstSingBoxAsync())
            {
                error = "Сервер сохранён, но sing-box не установлен: без него VPS работать не будет.";
                return;
            }
            await ApplyVpnCoreAsync(interactive: true, needProbe: false);
            error = null;
        });
        return error;
    }

    public Task RemoveVpnServerAsync() => Serialized("Отключение VPS…", silentErrors: false, async () =>
    {
        await Task.Run(SingBox.Stop);
        VpnServerStore.Remove();
        VpnServerName = null;
        _appliedSignature = null;
        _appliedPlan = null;
    });

    /// <returns>True when saved and applied; errors are shown as a notification.</returns>
    public async Task<bool> SetVpnListsAsync(IReadOnlyList<string> processes, IReadOnlyList<string> domains)
    {
        var ok = false;
        await Serialized("Настройка VPS…", silentErrors: false, async () =>
        {
            Settings.VpnProcesses = processes.Where(SingBoxConfig.IsValidProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Settings.VpnDomains = domains.Select(SingBoxConfig.NormalizeDomain).Where(d => d is not null).Select(d => d!).Distinct().ToList();
            _settingsStore.Save(Settings);
            await ApplyVpnCoreAsync(interactive: true, needProbe: false);
            ok = true;
        });
        return ok;
    }

    public Task SetGameRouteAsync(string id, GameRoute route) => Serialized("Настройка маршрута…", silentErrors: false, async () =>
    {
        var profile = Settings.GameProfiles.FirstOrDefault(p => p.Id == id);
        if (profile is null) return;
        profile.Route = route;
        _settingsStore.Save(Settings);
        if (profile.ProcessName is { } exe)
        {
            // A new choice is decided afresh the next time the game starts.
            lock (_autoGate)
            {
                _autoRunning.Remove(exe);
                _autoVpn.Remove(exe);
            }
        }
        if (route == GameRoute.Auto && !profile.ProbeEndpoints.Any(IsMeasurable))
        {
            Notify?.Invoke("Авто-маршрут", $"Для «{profile.Name}» нет адресов для замера (нужны TLS-серверы игры). " +
                                          "Выполните «Дообучить»; пока игра будет идти напрямую.", ToolTipIcon.Warning);
        }
        await ApplyVpnCoreAsync(interactive: true, needProbe: false);
    });

    /// <summary>Re-applies the plan after something outside this file changed profiles (enable, delete, relearn).</summary>
    private Task ReapplyVpnAfterProfileChangeAsync() => ApplyVpnCoreAsync(interactive: false, needProbe: false);

    // ---------- running sing-box ----------

    /// <summary>Starts, restarts or stops sing-box so that it carries exactly what the settings ask for.</summary>
    public Task ApplyVpnAsync(bool interactive) =>
        Serialized("Настройка VPS…", silentErrors: !interactive, () => ApplyVpnCoreAsync(interactive, needProbe: false));

    /// <param name="interactive">True when a user action triggered this (only then may dialogs appear).</param>
    private async Task ApplyVpnCoreAsync(bool interactive, bool needProbe)
    {
        var raw = VpnServerStore.LoadRaw();
        var server = raw is null ? null : VpnServerStore.Load();
        List<string> auto;
        lock (_autoGate) auto = _autoVpn.ToList();
        var plan = VpnPlan.From(Settings, auto);

        if (server is null || (!plan.NeedsTunnel && !needProbe))
        {
            if (IsVpnRunning) await Task.Run(SingBox.Stop);
            _appliedSignature = null;
            _appliedPlan = null;
            return;
        }

        if (_sbStore.ActiveMain is null)
        {
            if (!interactive || !await InstallFirstSingBoxAsync())
            {
                Notify?.Invoke("VPS", "sing-box не установлен: откройте «VPS → Сервер» и сохраните ссылку ещё раз.", ToolTipIcon.Warning);
                return;
            }
        }

        var tun = plan.NeedsTunnel;
        // Hash of the stored link (password, SNI… included), so any change to the server restarts sing-box.
        var signature = $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw!)))}|{tun}|" +
                        $"{string.Join(",", plan.Processes)}|{string.Join(",", plan.Domains)}";
        if (IsVpnRunning && signature == _appliedSignature) return;

        await Task.Run(() => StartSingBoxAsync(server, tun, plan));
        _appliedSignature = signature;
        _appliedPlan = (tun, plan);
        Log.Info($"sing-box {_sbStore.ActiveVersion} started: {server}, tunnel {tun}, processes [{string.Join(", ", plan.Processes)}], domains {plan.Domains.Count}");
        Changed();
    }

    /// <summary>
    /// Writes a fresh config (new probe port and credentials), starts sing-box and deletes the config again
    /// once sing-box has read it: the VPS password does not stay on disk in plain text.
    /// </summary>
    private async Task StartSingBoxAsync(Hysteria2Link server, bool tun, VpnPlan plan)
    {
        _probeAuth = ProbeCredentials.Random();
        _probePort = FreeLocalPort();
        var json = SingBoxConfig.Build(server, new SingBoxOptions(tun, _probePort, plan.Processes, plan.Domains, AppPaths.SingBoxLog, _probeAuth));
        var tmp = AppPaths.VpnConfig + ".tmp";
        try
        {
            await File.WriteAllTextAsync(tmp, json).ConfigureAwait(false);
            File.Move(tmp, AppPaths.VpnConfig, overwrite: true);
            await SingBox.StartAsync(new[] { "run", "-c", AppPaths.VpnConfig, "--disable-color" }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            foreach (var f in new[] { tmp, AppPaths.VpnConfig })
            {
                try
                {
                    if (File.Exists(f)) File.Delete(f);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private void OnSingBoxCrashed(string runnerOutput)
    {
        _appliedSignature = null;
        var tail = LogTail(AppPaths.SingBoxLog, 15);
        Log.Error("sing-box exited unexpectedly. Its log ends with:\n" + tail);
        Changed();

        bool tooMany;
        lock (_recentCrashes)
        {
            // Budget per time window: an old crash must not disable recovery for the rest of the app's life.
            var now = DateTime.UtcNow;
            while (_recentCrashes.Count > 0 && now - _recentCrashes.Peek() > CrashWindow) _recentCrashes.Dequeue();
            _recentCrashes.Enqueue(now);
            tooMany = _recentCrashes.Count > MaxCrashRestarts;
        }
        if (tooMany)
        {
            Notify?.Invoke("VPS отключён", "sing-box несколько раз остановился. Подробности в логе; измените настройки VPS, чтобы попробовать снова.", ToolTipIcon.Error);
            return;
        }
        Notify?.Invoke("VPS переподключается", "sing-box остановился, перезапускаю…", ToolTipIcon.Warning);
        _ = Task.Delay(CrashRestartDelay).ContinueWith(_ => ApplyVpnAsync(interactive: false));
    }

    private static string LogTail(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) return "(log is empty)";
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return string.Join(Environment.NewLine, reader.ReadToEnd().Split('\n').TakeLast(lines));
        }
        catch (IOException)
        {
            return "(log unreadable)";
        }
    }

    private static string? DisplayName(Hysteria2Link? link) =>
        link is null ? null : link.Insecure ? $"{link} — без проверки сертификата" : link.ToString();

    private static int FreeLocalPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // ---------- measuring ----------

    public sealed record QualityResult(PathStats Direct, PathStats Tunnel, PathDecision Decision);

    /// <summary>Only TLS endpoints give a trustworthy round trip on both paths (see TlsPing).</summary>
    private static bool IsMeasurable(string endpoint) => IPEndPoint.TryParse(endpoint, out var ep) && TlsPing.IsTlsPort(ep.Port);

    /// <summary>Measures the direct path and the VPS path to the given TLS endpoints.</summary>
    /// <param name="interactive">Whether this was started by the user (may install sing-box after asking).</param>
    public async Task<QualityResult?> MeasureAsync(IReadOnlyList<IPEndPoint> endpoints, bool interactive, CancellationToken ct)
    {
        QualityResult? result = null;
        await Serialized("Замер качества канала…", silentErrors: !interactive, async () =>
        {
            if (VpnServerStore.Load() is null) throw new InvalidOperationException("Сначала добавьте сервер VPS.");
            try
            {
                // Make sure the probe port exists (starts sing-box without the tunnel if nothing else needs it).
                await ApplyVpnCoreAsync(interactive, needProbe: true);
                if (!IsVpnRunning || _probeAuth is null) throw new InvalidOperationException("sing-box не запущен.");
                // The port was free when chosen; make sure it is really sing-box answering before sending credentials.
                // Exactly one listener, and it is sing-box: a second socket sharing the port could receive the credentials.
                var listeners = NativeProcess.TcpListenerPids(_probePort);
                if (SingBox.ProcessId is not { } sbPid || listeners.Count != 1 || !listeners.Contains(sbPid))
                {
                    _appliedSignature = null;
                    throw new InvalidOperationException("Порт замера занят другой программой. Повторите замер.");
                }

                var tunnelProbe = new Socks5TcpProbe(new IPEndPoint(IPAddress.Loopback, _probePort), _probeAuth);
                var timeout = TimeSpan.FromSeconds(3);
                var pause = TimeSpan.FromMilliseconds(150);
                var direct = await Task.Run(() => PathMeasurer.MeasureAsync(new DirectTcpProbe(), endpoints, SamplesPerEndpoint, timeout, pause, ct), ct);
                var tunnel = await Task.Run(() => PathMeasurer.MeasureAsync(tunnelProbe, endpoints, SamplesPerEndpoint, timeout, pause, ct), ct);
                result = new QualityResult(direct, tunnel, PathChooser.Choose(direct, tunnel));
                Log.Info($"Path quality to {string.Join(", ", endpoints)}: direct {direct}, VPS {tunnel} => {result.Decision}");
            }
            finally
            {
                // Never leave a probe-only sing-box running (also after cancel or errors) — without letting a
                // failure here hide the original error.
                try
                {
                    await ApplyVpnCoreAsync(interactive: false, needProbe: false);
                }
                catch (Exception cleanupEx)
                {
                    Log.Error("Restoring VPS state after measurement failed", cleanupEx);
                }
            }
        });
        return result;
    }

    // ---------- auto route: once per game start ----------

    private async Task WatchGamesAsync()
    {
        if (_gameWatchBusy) return;
        _gameWatchBusy = true;
        try
        {
            var sessionId = Environment.ProcessId is var self ? SessionOf(self) : 0;
            var changed = false;
            // UI thread: the profile list is only ever changed here, so this snapshot is consistent.
            foreach (var profile in Settings.GameProfiles.ToList())
            {
                if (profile.ProcessName is not { } exe || !profile.Enabled || profile.Route != GameRoute.Auto) continue;
                var running = await Task.Run(() => IsProcessRunning(exe, sessionId));
                bool known;
                lock (_autoGate) known = _autoRunning.Contains(exe);

                if (running && !known)
                {
                    lock (_autoGate) _autoRunning.Add(exe);
                    changed |= await DecideRouteAsync(profile, exe);
                }
                else if (!running && known)
                {
                    lock (_autoGate) _autoRunning.Remove(exe);
                }
            }
            if (changed) await ApplyVpnAsync(interactive: false);
        }
        catch (Exception ex)
        {
            Log.Error("Game watch failed", ex);
        }
        finally
        {
            _gameWatchBusy = false;
        }
    }

    /// <returns>True when the game's route changed.</returns>
    private async Task<bool> DecideRouteAsync(GameProfile profile, string exe)
    {
        bool wasVpn;
        lock (_autoGate) wasVpn = _autoVpn.Contains(exe);

        var endpoints = profile.ProbeEndpoints.Where(IsMeasurable).Select(IPEndPoint.Parse).Take(MaxEndpointsPerMeasurement).ToList();
        if (endpoints.Count == 0 || VpnServerStore.Load() is null || _sbStore.ActiveMain is null)
        {
            Notify?.Invoke(profile.Name, endpoints.Count == 0
                ? "Напрямую: нет адресов для замера (выполните «Дообучить»)."
                : "Напрямую: VPS не настроен.", ToolTipIcon.Info);
            lock (_autoGate) _autoVpn.Remove(exe);
            return wasVpn;
        }

        var result = await MeasureAsync(endpoints, interactive: false, CancellationToken.None);
        var viaVpn = result?.Decision.Choice == PathChoice.Vpn;
        lock (_autoGate)
        {
            if (viaVpn) _autoVpn.Add(exe);
            else _autoVpn.Remove(exe);
        }
        Notify?.Invoke(profile.Name, result is null
            ? "Напрямую: замер не удался."
            : (viaVpn ? "Через VPS: " : "Напрямую: ") + result.Decision.Reason, ToolTipIcon.Info);
        return viaVpn != wasVpn;
    }

    private static int SessionOf(int pid)
    {
        using var p = Process.GetProcessById(pid);
        return p.SessionId;
    }

    private static bool IsProcessRunning(string exe, int sessionId)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            using (p)
            {
                if (p.SessionId == sessionId) return true;
            }
        }
        return false;
    }

    // ---------- sing-box install / updates (same confirmed flow as the other helpers) ----------

    private async Task<bool> InstallFirstSingBoxAsync()
    {
        var release = await Task.Run(() => SbUpdater.CheckAsync(null, CancellationToken.None))
                      ?? throw new InvalidOperationException("Не удалось найти релиз sing-box.");
        if (!ConfirmUpdate(SingBoxProductName, release.Version, null)) return false;
        SetBusy($"Загрузка sing-box {release.Version}…");
        await Task.Run(() => SbUpdater.InstallAsync(release, TestStartSingBoxAsync, null, CancellationToken.None));
        Log.Info($"Installed sing-box {release.Version}");
        return true;
    }

    /// <summary>Checks a freshly installed build actually runs ("sing-box version" must succeed).</summary>
    private static async Task TestStartSingBoxAsync(string exe)
    {
        using var p = Process.Start(new ProcessStartInfo(exe)
        {
            ArgumentList = { "version" },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("sing-box не запустился.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(15000))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            // Not an OperationCanceledException: that would be taken for a user cancel and swallowed.
            throw new TimeoutException("sing-box version не ответил за 15 секунд.");
        }
        var output = await stdout.ConfigureAwait(false);
        await stderr.ConfigureAwait(false);
        if (p.ExitCode != 0 || !output.Contains("sing-box", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"sing-box version завершился с кодом {p.ExitCode}.");
        }
    }

    private async Task CheckSingBoxUpdateAsync(bool userInitiated)
    {
        if (_sbStore.ActiveVersion is null) return;
        ReleaseInfo? found = null;
        await Serialized("Проверка обновлений sing-box…", silentErrors: !userInitiated, async () =>
        {
            found = await Task.Run(() => SbUpdater.CheckAsync(userInitiated ? null : Settings.SingBoxSkippedVersion, CancellationToken.None));
            _availableSbUpdate = found;
            if (found is null)
            {
                if (userInitiated) Notify?.Invoke("Обновлений нет", $"Установлена последняя версия sing-box {_sbStore.ActiveVersion}.", ToolTipIcon.Info);
                return;
            }
            if (!userInitiated) AnnounceUpdate(SingBoxProductName, found.Version);
        });
        if (found is not null && userInitiated) await InstallAvailableSbUpdateAsync();
    }

    public Task CheckSingBoxUpdateManuallyAsync() => CheckSingBoxUpdateAsync(userInitiated: true);

    public async Task InstallAvailableSbUpdateAsync()
    {
        var release = _availableSbUpdate;
        if (release is null || BusyText is not null || _confirmingSbUpdate) return;
        _confirmingSbUpdate = true;
        bool confirmed;
        try
        {
            confirmed = ConfirmUpdate(SingBoxProductName, release.Version, _sbStore.ActiveVersion);
        }
        finally
        {
            _confirmingSbUpdate = false;
        }
        if (!ReleaseVersion.IsNewer(release.Version, _sbStore.ActiveVersion)) return;
        if (!confirmed)
        {
            Settings.SingBoxSkippedVersion = release.Version;
            _settingsStore.Save(Settings);
            return;
        }
        await Serialized($"Установка sing-box {release.Version}…", silentErrors: false, () => InstallSingBoxReleaseAsync(release));
    }

    private async Task InstallSingBoxReleaseAsync(ReleaseInfo release)
    {
        var server = VpnServerStore.Load();
        var applied = _appliedPlan;
        var wasRunning = IsVpnRunning && server is not null && applied is not null;

        // The runner always starts the active version, so a restart picks up the new or the rolled-back one.
        // When the VPN is off the new build is still test-run, so a broken release is caught before cleanup.
        async Task Switch(string exe)
        {
            await TestStartSingBoxAsync(exe);
            if (wasRunning) await StartSingBoxAsync(server!, applied!.Value.Tun, applied.Value.Plan);
        }
        Task Rollback(string _) => wasRunning ? StartSingBoxAsync(server!, applied!.Value.Tun, applied.Value.Plan) : Task.CompletedTask;

        try
        {
            var installed = await Task.Run(() => SbUpdater.InstallAsync(release, Switch, Rollback, CancellationToken.None));
            _availableSbUpdate = null;
            if (installed is null) return;
            Settings.SingBoxSkippedVersion = null;
            _settingsStore.Save(Settings);
            Notify?.Invoke("sing-box обновлён", $"Версия {installed}.", ToolTipIcon.Info);
        }
        catch (UpdateException ex)
        {
            Log.Error("sing-box update failed", ex);
            if (ex.ReleaseDefect && ex.Version is not null)
            {
                Settings.SingBoxSkippedVersion = ex.Version;
                _settingsStore.Save(Settings);
                _availableSbUpdate = null;
            }
            Notify?.Invoke("Обновление sing-box не удалось", $"{ex.Message}\nОставлена версия {_sbStore.ActiveVersion}.", ToolTipIcon.Warning);
        }
    }

    private void ForgetAutoRoute(string? exe)
    {
        if (exe is null) return;
        lock (_autoGate)
        {
            _autoRunning.Remove(exe);
            _autoVpn.Remove(exe);
        }
    }

    private void DisposeVpn()
    {
        _gameWatch?.Dispose();
        _singBoxInstance?.Dispose();
    }
}

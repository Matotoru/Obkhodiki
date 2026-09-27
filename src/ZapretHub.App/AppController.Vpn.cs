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
/// VPS module: runs sing-box with the servers of a share link or a subscription behind one selector.
/// Selectively (chosen programs, sites, categories) or everything except Russian sites goes through the VPS.
/// "Auto" games are measured once when they start and routed the better way.
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
    private SingBoxLaunch? _appliedLaunch;
    private System.Windows.Forms.Timer? _gameWatch;
    private bool _gameWatchBusy;
    private readonly Queue<DateTime> _recentCrashes = new();
    private static readonly TimeSpan CrashWindow = TimeSpan.FromMinutes(10);

    // Loopback-only client for the sing-box control API (never through a system proxy).
    private readonly HttpClient _localHttp = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private volatile ClashApiClient? _clash;

    // Servers as last loaded from the encrypted store (UI thread / inside the gate).
    private VpnSource? _source;
    private IReadOnlyList<VpnServerEntry> _servers = Array.Empty<VpnServerEntry>();

    // Auto decisions: made once when the game starts. The game stays on its path after it exits, so leaving the
    // game never restarts the tunnel under other traffic; the next game start measures again.
    private readonly object _autoGate = new();
    private readonly HashSet<string> _autoRunning = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoVpn = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoDirect = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Everything one sing-box start needs; kept to restart the same way after an update.</summary>
    private sealed record SingBoxLaunch(
        IReadOnlyList<VpnServerEntry> Servers,
        string? ActiveTag,
        bool Tun,
        VpnPlan Plan,
        IReadOnlyList<LocalRuleSet> ProxySets,
        IReadOnlyList<LocalRuleSet> DirectSets,
        IReadOnlyList<string> LocalDns);

    // Servers sing-box refused to load (checked before each start); left out for the rest of the session.
    private readonly HashSet<string> _rejectedServers = new();
    private SynchronizationContext? _uiContext;

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

    /// <summary>What the user sees for the configured source; null when no server is set up.</summary>
    public string? VpnServerName { get; private set; }

    public VpnSourceView? VpnSourceInfo { get; private set; }

    public IReadOnlyList<VpnServerEntry> VpnServers => _servers;

    /// <summary>The server new connections use (the chosen one, or the first when it is gone).</summary>
    public string? ActiveServerTag =>
        _servers.Any(s => s.Tag == Settings.VpnSelectedServer) ? Settings.VpnSelectedServer : _servers.FirstOrDefault()?.Tag;

    /// <summary>Asked (UI thread) before accepting something that weakens the connection's protection.</summary>
    public Func<string, bool> ConfirmVpnRisk { get; set; } = _ => false;

    public bool IsVpnRunning => _singBoxInstance?.IsRunning == true;
    public ReleaseInfo? AvailableSbUpdate => _availableSbUpdate;
    public string? SingBoxVersion => _sbStore.ActiveVersion;

    /// <summary>Called on the UI thread; the game watch runs there too, so it reads settings safely.</summary>
    private void InitializeVpn()
    {
        _uiContext = SynchronizationContext.Current;
        ReloadSource();
        _gameWatch = new System.Windows.Forms.Timer { Interval = (int)GameWatchInterval.TotalMilliseconds };
        _gameWatch.Tick += async (_, _) => await WatchGamesAsync();
        _gameWatch.Start();
    }

    private void ReloadSource() => UseSource(VpnSourceStore.Load());

    private void UseSource(VpnSource? source)
    {
        _source = source;
        _servers = source?.Servers().Where(s => !_rejectedServers.Contains(s.Tag)).ToList() ?? (IReadOnlyList<VpnServerEntry>)Array.Empty<VpnServerEntry>();
        VpnServerName = source is null ? null : source.Describe();
        VpnSourceInfo = source is null ? null : new VpnSourceView(
            source.IsSubscription, source.Describe(), source.Used, source.Total, source.Expire, source.FetchedAt,
            source.SkippedCount, _servers.Any(s => s.Server.Insecure));
    }

    // ---------- settings the UI changes ----------

    /// <summary>Accepts a subscription URL (http/https) or a single share link.</summary>
    /// <returns>Error text for the form, or null when saved and applied.</returns>
    public async Task<string?> SetVpnSourceAsync(string text)
    {
        var input = text.Trim();
        if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return await SetSubscriptionAsync(input);
        }

        VpnServerEntry entry;
        try
        {
            entry = VpnServerEntry.FromLink(input);
        }
        catch (FormatException ex)
        {
            return ex.Message;
        }
        if (entry.Server.Insecure && !ConfirmVpnRisk(InsecureWarning(1))) return "Ссылка не сохранена.";
        return await SaveSourceAsync(new VpnSource { Link = input, AllowInsecure = entry.Server.Insecure }, "Подключение сервера VPS…");
    }

    private static string InsecureWarning(int count) =>
        (count == 1 ? "В ссылке отключена проверка сертификата (insecure)." : $"У {VpnSource.Plural(count)} отключена проверка сертификата (insecure).") +
        "\n\nТогда оборудование по пути (например, ТСПУ) может выдать себя за сервер, узнать пароль и видеть трафик туннеля. " +
        "Надёжнее выпустить на сервере настоящий сертификат (например, Let's Encrypt).\n\nВсё равно использовать такие серверы?";

    private async Task<string?> SaveSourceAsync(VpnSource source, string busyText)
    {
        string? error = "Не удалось применить настройки, подробности в уведомлении.";
        await Serialized(busyText, silentErrors: false, async () =>
        {
            VpnSourceStore.Save(source);
            UseSource(source);
            _appliedSignature = null; // force a restart with the new servers
            _pings.Clear();
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
        _clash = null;
        VpnSourceStore.Remove();
        UseSource(null);
        _pings.Clear();
        _appliedSignature = null;
        _appliedLaunch = null;
    });

    /// <returns>True when saved and applied; errors are shown as a notification.</returns>
    public async Task<bool> SetVpnListsAsync(IReadOnlyList<string> processes, IReadOnlyList<string> domains,
        IReadOnlyList<string> proxyCategories, IReadOnlyList<string> directCategories)
    {
        var ok = false;
        await Serialized("Настройка VPS…", silentErrors: false, async () =>
        {
            Settings.VpnProcesses = processes.Where(SingBoxConfig.IsValidProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            Settings.VpnDomains = domains.Select(SingBoxConfig.NormalizeDomain).Where(d => d is not null).Select(d => d!).Distinct().ToList();
            Settings.VpnProxyCategories = RuleCatalog.Sanitize(proxyCategories, RuleCatalog.Proxy);
            Settings.VpnDirectCategories = RuleCatalog.Sanitize(directCategories, RuleCatalog.Direct);
            _settingsStore.Save(Settings);
            await ApplyVpnCoreAsync(interactive: true, needProbe: false);
            ok = true;
        });
        return ok;
    }

    /// <summary>"Включить VPS": everything through the VPS except Russian sites and direct games.</summary>
    public Task SetVpnFullTunnelAsync(bool on) => Serialized(on ? "Включение VPS…" : "Выключение VPS…", silentErrors: false, async () =>
    {
        if (_servers.Count == 0) throw new InvalidOperationException("Сначала добавьте сервер или подписку на странице «VPS».");
        Settings.VpnFullTunnel = on;
        _settingsStore.Save(Settings);
        try
        {
            await ApplyVpnCoreAsync(interactive: true, needProbe: false);
            if (on && !IsVpnRunning) throw new InvalidOperationException("VPS не запустился.");
        }
        catch
        {
            // The switch must not claim a mode that failed to start.
            Settings.VpnFullTunnel = !on;
            _settingsStore.Save(Settings);
            throw;
        }
    });

    public Task SetGameRouteAsync(string id, GameRoute route) => Serialized("Настройка маршрута…", silentErrors: false, async () =>
    {
        var profile = Settings.GameProfiles.FirstOrDefault(p => p.Id == id);
        if (profile is null) return;
        profile.Route = route;
        _settingsStore.Save(Settings);
        // A new choice is decided afresh the next time the game starts.
        ForgetAutoRoute(profile.ProcessName);
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
    public Task ApplyVpnAsync(bool interactive, bool refreshRules = false) =>
        Serialized("Настройка VPS…", silentErrors: !interactive, () => ApplyVpnCoreAsync(interactive, needProbe: false, refreshRules));

    private VpnPlan CurrentPlan()
    {
        List<string> vpn, decided;
        lock (_autoGate)
        {
            vpn = _autoVpn.ToList();
            decided = _autoVpn.Concat(_autoDirect).ToList();
        }
        return VpnPlan.From(Settings, vpn, decided);
    }

    /// <param name="interactive">True when a user action triggered this (only then may dialogs appear).</param>
    /// <param name="refreshRules">Also re-download rule-sets older than a week.</param>
    private async Task ApplyVpnCoreAsync(bool interactive, bool needProbe, bool refreshRules = false)
    {
        ReloadSource();
        var plan = CurrentPlan();

        if (_source is null || _servers.Count == 0 || (!plan.NeedsTunnel && !needProbe))
        {
            if (IsVpnRunning) await Task.Run(SingBox.Stop);
            _autoPingCts?.Cancel();
            _clash = null;
            _appliedSignature = null;
            _appliedLaunch = null;
            return;
        }

        if (_sbStore.ActiveMain is null)
        {
            if (!interactive || !await InstallFirstSingBoxAsync())
            {
                Notify?.Invoke("VPS", "sing-box не установлен: откройте «VPS» и сохраните ссылку ещё раз.", ToolTipIcon.Warning);
                return;
            }
        }

        var (proxySets, directSets, stamps) = await PrepareRuleSetsAsync(plan, refreshRules);
        var tun = plan.NeedsTunnel;
        var signature = $"{_source.Signature()}|{tun}|{plan.FullTunnel}|{string.Join(",", plan.Processes)}|{string.Join(",", plan.Domains)}|" +
                        $"{string.Join(",", plan.DirectProcesses)}|{stamps}";
        if (IsVpnRunning && signature == _appliedSignature) return;

        var localDns = new[] { "raw.githubusercontent.com", _source.SubscriptionHost }.Where(h => h is not null).Select(h => h!).ToList();
        var launch = await DropRejectedServersAsync(new SingBoxLaunch(_servers, ActiveServerTag, tun, plan, proxySets, directSets, localDns));
        await Task.Run(() => StartSingBoxAsync(launch));
        _appliedSignature = signature;
        _appliedLaunch = launch;
        Log.Info($"sing-box {_sbStore.ActiveVersion} started: {_source.Describe()}, tunnel {tun}, full {plan.FullTunnel}, " +
                 $"processes [{string.Join(", ", plan.Processes)}], domains {plan.Domains.Count}, rule-sets [{string.Join(", ", proxySets.Concat(directSets).Select(r => r.Tag))}]");
        Changed();
    }

    /// <summary>
    /// Makes sure the files of the active categories are on disk (downloading missing ones, and stale ones when
    /// asked). A category whose files cannot be fetched is left out with a warning instead of failing the start.
    /// </summary>
    private async Task<(List<LocalRuleSet> Proxy, List<LocalRuleSet> Direct, string Stamps)> PrepareRuleSetsAsync(VpnPlan plan, bool refresh)
    {
        var proxy = new List<LocalRuleSet>();
        var direct = new List<LocalRuleSet>();
        var stamps = new StringBuilder();
        var missing = new List<string>();
        foreach (var (ids, target) in new[] { (plan.FullTunnel ? plan.DirectCategories : Array.Empty<string>(), direct), (plan.FullTunnel ? Array.Empty<string>() : plan.ProxyCategories, proxy) })
        {
            foreach (var category in ids.Select(RuleCatalog.Find).Where(c => c is not null).Select(c => c!))
            {
                var ok = true;
                foreach (var file in category.Files)
                {
                    var missingFile = !RuleSetStore.Has(file);
                    var retryDue = !_ruleAttempts.TryGetValue(file.Tag, out var last) || DateTime.UtcNow - last > RuleRetryMissing;
                    if ((missingFile && (refresh || retryDue)) || (refresh && RuleSetStore.IsStale(file)))
                    {
                        _ruleAttempts[file.Tag] = DateTime.UtcNow;
                        try
                        {
                            await DownloadThroughAnyPathAsync((client, ct) => RuleSetStore.DownloadAsync(file, client, ValidateRuleSetAsync, ct));
                        }
                        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException)
                        {
                            Log.Error($"Rule-set {file.Tag} download failed", ex);
                        }
                    }
                    ok &= RuleSetStore.Has(file);
                }
                if (!ok)
                {
                    missing.Add(category.Title);
                    continue;
                }
                foreach (var file in category.Files)
                {
                    target.Add(new LocalRuleSet(file.Tag, RuleSetStore.PathFor(file)));
                    stamps.Append(file.Tag).Append('=').Append(RuleSetStore.Stamp(file)).Append(';');
                }
            }
        }
        _rulesMissing = missing.Count > 0;
        var notice = string.Join(",", missing);
        // Once per failure episode, not on every apply.
        if (missing.Count > 0 && notice != _missingNotice)
        {
            Notify?.Invoke("Правила маршрутизации", $"Не удалось скачать: {string.Join(", ", missing)}. Пока эти категории не действуют, повторю позже.",
                ToolTipIcon.Warning);
        }
        _missingNotice = notice;
        return (proxy, direct, stamps.ToString());
    }

    private bool _rulesMissing;
    private string _missingNotice = "";
    private readonly Dictionary<string, DateTime> _ruleAttempts = new();

    /// <summary>Asks the installed sing-box to read the file; a newer format or a corrupt file is refused.</summary>
    private async Task ValidateRuleSetAsync(string path)
    {
        if (_sbStore.ActiveMain is not { } exe) return;
        var output = path + ".json";
        try
        {
            var (code, text) = await RunSingBoxToolAsync(exe, "rule-set", "decompile", path, "-o", output);
            if (code != 0) throw new InvalidDataException("sing-box не смог прочитать файл правил: " + text.Trim());
        }
        finally
        {
            if (File.Exists(output)) File.Delete(output);
        }
    }

    private static async Task<(int Code, string Output)> RunSingBoxToolAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("sing-box не запустился.");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!await Task.Run(() => p.WaitForExit(20000)))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException("sing-box не ответил за 20 секунд.");
        }
        return (p.ExitCode, await stdout + await stderr);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"initialize outbound\[(\d+)\]")]
    private static partial System.Text.RegularExpressions.Regex RejectedOutbound();

    /// <summary>
    /// Runs "sing-box check" on the config first. A server sing-box refuses (a field the parsers let through)
    /// would otherwise stop the start for every server: it is dropped, reported once, and the check repeats.
    /// </summary>
    private async Task<SingBoxLaunch> DropRejectedServersAsync(SingBoxLaunch launch)
    {
        if (_sbStore.ActiveMain is not { } exe) return launch;
        var path = Path.Combine(AppPaths.VpnRoot, "check.json");
        try
        {
            while (true)
            {
                var json = BuildConfig(launch, 20000, new ProbeCredentials("check", "check"), ClashApiOptions.Random(20001));
                await File.WriteAllTextAsync(path, json);
                var (code, output) = await RunSingBoxToolAsync(exe, "check", "-c", path, "--disable-color");
                if (code == 0) return launch;
                if (RejectedOutbound().Match(output) is not { Success: true } m || int.Parse(m.Groups[1].Value) is var i && i >= launch.Servers.Count)
                {
                    // Not a single server's fault: report it as is (the output holds no secrets, only field errors).
                    throw new InvalidOperationException("sing-box отклонил настройки: " + LastLine(output));
                }
                var bad = launch.Servers[i];
                _rejectedServers.Add(bad.Tag);
                Log.Error($"sing-box rejected server {bad.Server}: {LastLine(output)}", null);
                Notify?.Invoke("Сервер пропущен", $"{bad.Server.Name ?? bad.Server.Host}: sing-box не принимает его настройки.", ToolTipIcon.Warning);
                var rest = launch.Servers.Where(s => s.Tag != bad.Tag).ToList();
                if (rest.Count == 0) throw new InvalidOperationException("Ни один сервер не подошёл sing-box.");
                launch = launch with { Servers = rest, ActiveTag = rest.Any(s => s.Tag == launch.ActiveTag) ? launch.ActiveTag : rest[0].Tag };
                _servers = rest;
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static string LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    private static string BuildConfig(SingBoxLaunch launch, int probePort, ProbeCredentials probeAuth, ClashApiOptions api)
    {
        var plan = launch.Plan;
        var options = new SingBoxOptions(launch.Tun, probePort, plan.Processes, plan.Domains, AppPaths.SingBoxLog, probeAuth)
        {
            FullTunnel = plan.FullTunnel,
            DirectProcesses = plan.DirectProcesses,
            ProxyRuleSets = launch.ProxySets,
            DirectRuleSets = launch.DirectSets,
            ClashApi = api,
            LocalDnsDomains = launch.LocalDns,
        };
        return SingBoxConfig.Build(launch.Servers, launch.ActiveTag, options);
    }

    /// <summary>
    /// Downloads directly first; if that fails and the tunnel is up, retries through it (GitHub and the
    /// subscription host may be blocked, and in full-tunnel mode this app itself is routed direct).
    /// </summary>
    private async Task<T> DownloadThroughAnyPathAsync<T>(Func<HttpClient, CancellationToken, Task<T>> download)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try
        {
            using var direct = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20) };
            return await download(direct, cts.Token);
        }
        // Also on "wrong content": a block page or DPI stub answers 200 with HTML.
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException && IsVpnRunning && _probeAuth is not null)
        {
            Log.Info("Direct download failed, retrying through the VPS: " + ex.Message);
            var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{_probePort}") { Credentials = new NetworkCredential(_probeAuth.User, _probeAuth.Password) },
                UseProxy = true,
            };
            using var viaVpn = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            return await download(viaVpn, CancellationToken.None);
        }
    }

    /// <summary>
    /// Writes a fresh config (new probe/API ports and secrets), starts sing-box and deletes the config again
    /// once sing-box has read it: the VPS passwords do not stay on disk in plain text.
    /// </summary>
    private async Task StartSingBoxAsync(SingBoxLaunch launch)
    {
        _autoPingCts?.Cancel();
        _clash = null;
        _probeAuth = ProbeCredentials.Random();
        _probePort = FreeLocalPort();
        var api = ClashApiOptions.Random(FreeLocalPort());
        var json = BuildConfig(launch, _probePort, _probeAuth, api);
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
        _clash = new ClashApiClient(_localHttp, api);
        // First server comparison shortly after the tunnel comes up, then every few minutes.
        _nextBestCheck = DateTime.UtcNow + TimeSpan.FromSeconds(20);
    }

    private void OnSingBoxCrashed(string runnerOutput)
    {
        _appliedSignature = null;
        _autoPingCts?.Cancel();
        _clash = null;
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
        // Back on the UI thread: applying reads and writes settings and source state owned by it.
        _ = Task.Delay(CrashRestartDelay).ContinueWith(_ =>
        {
            if (_uiContext is { } ui) ui.Post(_ => _ = ApplyVpnAsync(interactive: false), null);
            else _ = ApplyVpnAsync(interactive: false);
        });
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
            if (_servers.Count == 0) throw new InvalidOperationException("Сначала добавьте сервер или подписку VPS.");
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
            var anyGame = false;
            // UI thread: the profile list is only ever changed here, so this snapshot is consistent.
            foreach (var profile in Settings.GameProfiles.ToList())
            {
                if (profile.ProcessName is not { } exe || !profile.Enabled) continue;
                var running = await Task.Run(() => IsProcessRunning(exe, sessionId));
                anyGame |= running;
                // A game started: stop any server check right away, it must not add load during play.
                if (running) _autoPingCts?.Cancel();
                if (profile.Route != GameRoute.Auto) continue;
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
            // Server pings, switching and downloads wait until no game is running.
            GameRunning = anyGame;
            if (!anyGame) await VpnUpkeepAsync();
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
        if (endpoints.Count == 0 || _servers.Count == 0 || _sbStore.ActiveMain is null)
        {
            Notify?.Invoke(profile.Name, endpoints.Count == 0
                ? "Напрямую: нет адресов для замера (выполните «Дообучить»)."
                : "Напрямую: VPS не настроен.", ToolTipIcon.Info);
            lock (_autoGate)
            {
                _autoVpn.Remove(exe);
                _autoDirect.Add(exe);
            }
            return wasVpn;
        }

        var result = await MeasureAsync(endpoints, interactive: false, CancellationToken.None);
        var viaVpn = result?.Decision.Choice == PathChoice.Vpn;
        lock (_autoGate)
        {
            if (viaVpn)
            {
                _autoVpn.Add(exe);
                _autoDirect.Remove(exe);
            }
            else
            {
                _autoVpn.Remove(exe);
                _autoDirect.Add(exe);
            }
        }
        Notify?.Invoke(profile.Name, result is null
            ? "Напрямую: замер не удался."
            : (viaVpn ? "Через VPS: " : "Напрямую: ") + result.Decision.Reason, ToolTipIcon.Info);
        // In full-tunnel mode a "direct" decision also changes the config (the game gets its own direct rule).
        return viaVpn != wasVpn || Settings.VpnFullTunnel;
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
        var applied = _appliedLaunch;
        var wasRunning = IsVpnRunning && applied is not null;

        // The runner always starts the active version, so a restart picks up the new or the rolled-back one.
        // When the VPN is off the new build is still test-run, so a broken release is caught before cleanup.
        async Task Switch(string exe)
        {
            await TestStartSingBoxAsync(exe);
            if (wasRunning) await StartSingBoxAsync(applied!);
        }
        Task Rollback(string _) => wasRunning ? StartSingBoxAsync(applied!) : Task.CompletedTask;

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
            _autoDirect.Remove(exe);
        }
    }

    /// <summary>A game from the profiles is running (background VPS work is paused).</summary>
    public bool GameRunning { get; private set; }

    private void DisposeVpn()
    {
        _gameWatch?.Dispose();
        _localHttp.Dispose();
        _singBoxInstance?.Dispose();
    }
}

using System.Drawing;
using System.Windows.Forms;
using System.Security.Cryptography;
using System.ServiceProcess;
using Obkhodiki.Core.Engine;
using Obkhodiki.Core.Games;
using Obkhodiki.Core.Settings;
using Obkhodiki.Core.Strategies;
using Obkhodiki.Core.Testing;
using Obkhodiki.Core.Updates;

namespace Obkhodiki.App;

/// <summary>
/// Orchestrates engine updates, winws lifecycle and settings. Operations are serialized.
/// Public methods are called on the UI thread; blocking work is pushed to the thread pool.
/// StateChanged/Notify may fire on any thread — subscribers marshal to the UI.
/// </summary>
internal sealed partial class AppController : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    // Never the Windows/environment proxy: another VPN client often leaves one set to a local port it no longer
    // listens on (and .NET reads it once per process), which broke every update check. The app routes its own
    // traffic (zapret, the sing-box tunnel).
    private readonly HttpClient _http = new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(60) };
    private readonly EngineStore _store = new(AppPaths.EngineRoot);
    private readonly AppSettingsStore _settingsStore = new(AppPaths.Settings);
    private readonly WinwsRunner _runner;
    private readonly EngineUpdater _updater;
    private volatile string? _busyText;
    private volatile EngineLayout? _engine;
    private volatile bool _autoSelecting;
    private volatile ReleaseInfo? _availableUpdate;
    private bool _confirmingUpdate;

    public AppController()
    {
        _runner = new WinwsRunner(() => _engine?.WinwsPath ?? throw new InvalidOperationException("Engine is not installed."));
        _runner.Crashed += OnCrashed;
        _updater = new EngineUpdater(new FlowsealReleaseClient(_http), _store);
        Settings = _settingsStore.Load();
    }

    public event Action? StateChanged;
    public event Action<string, string, ToolTipIcon>? Notify;

    /// <summary>An update balloon for this product is about to be shown (lets the tray route a click on it).</summary>
    public event Action<string>? UpdateAnnounced;

    /// <summary>Asked (on the UI thread) before stopping other DPI tools. Argument: human-readable list.</summary>
    public Func<string, bool> ConfirmStopConflicts { get; set; } = _ => false;

    /// <summary>Asked (on the UI thread) before installing an update. Arguments: product, new and current version.</summary>
    public Func<string, string, string?, bool> ConfirmUpdate { get; set; } = (_, _, _) => false;

    /// <summary>A newer Flowseal release the user has not installed yet (shown in the menu).</summary>
    public ReleaseInfo? AvailableUpdate => _availableUpdate;

    public AppSettings Settings { get; }

    public Task SetCheckUpdatesOnStartAsync(bool value) => Serialized("Сохранение настроек…", silentErrors: false, () =>
    {
        Settings.CheckUpdatesOnStart = value;
        _settingsStore.Save(Settings);
        return Task.CompletedTask;
    });
    public Task SetAppearanceAsync(string theme, string palette) => Serialized("Сохранение настроек…", silentErrors: false, () =>
    {
        Settings.AppTheme = theme;
        Settings.AppPalette = palette;
        _settingsStore.Save(Settings);
        return Task.CompletedTask;
    });

    public EngineLayout? Engine => _engine;
    public bool IsRunning => _runner.IsRunning;
    public string? BusyText => _busyText;

    public string ActiveStrategyName =>
        _engine is { Strategies.Count: > 0 } e ? StrategyPicker.Pick(e.Strategies, Settings.SelectedStrategy).Name
        : Settings.SelectedStrategy ?? StrategyPicker.DefaultName;

    public async Task InitializeAsync()
    {
        try
        {
            // First, and on its own: a failure further down must not leave the VPS module without its game watch.
            InitializeVpn();
        }
        catch (Exception ex)
        {
            Log.Error("VPS module initialization failed", ex);
        }
        try
        {
            UserLists.EnsureDefaults(AppPaths.UserLists);
            _engine = _store.GetActive();
            if (_engine is null)
            {
                // First run: nothing works without an engine, so this download needs no extra confirmation.
                await InstallFirstEngineAsync();
            }
            SeedTargets();

            // Bypass first: at logon the user wants a working network now, not after a GitHub round-trip.
            if (Settings.EnableOnStart && _engine is not null && !_runner.IsRunning)
            {
                await EnableAsync();
            }
            await StartTelegramIfWantedAsync();
            await ApplyVpnAsync(interactive: false);
            if (Settings.CheckUpdatesOnStart && _engine is not null)
            {
                await CheckForUpdateAsync(userInitiated: false);
            }
            if (Settings.CheckUpdatesOnStart)
            {
                await CheckTgUpdateAsync(userInitiated: false);
                await CheckSingBoxUpdateAsync(userInitiated: false);
                await CheckAppUpdateAsync(userInitiated: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Initialization failed", ex);
            Notify?.Invoke("Ошибка запуска", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            Changed();
        }
    }

    /// <summary>Looks for a newer Flowseal release. Nothing is installed without <see cref="ConfirmUpdate"/>.</summary>
    public async Task CheckForUpdateAsync(bool userInitiated)
    {
        ReleaseInfo? found = null;
        await Serialized("Проверка обновлений Flowseal…", silentErrors: !userInitiated, async () =>
        {
            // A version the user declined (or that failed) is not announced again in the background.
            found = await Task.Run(() => _updater.CheckAsync(userInitiated ? null : Settings.SkippedUpdateVersion, CancellationToken.None));
            _availableUpdate = found;
            if (found is null)
            {
                if (userInitiated) Notify?.Invoke("Обновлений нет", $"Установлена последняя версия Flowseal {_engine?.Version}.", ToolTipIcon.Info);
                return;
            }
            Log.Info($"Flowseal {found.Version} is available (installed {_engine?.Version})");
            if (!userInitiated) AnnounceUpdate("Flowseal", found.Version);
        });

        if (found is not null && userInitiated) await InstallAvailableUpdateAsync();
    }

    /// <summary>Asks the user (on the UI thread) and installs <see cref="AvailableUpdate"/>.</summary>
    public async Task InstallAvailableUpdateAsync()
    {
        var release = _availableUpdate;
        if (release is null || BusyText is not null || _confirmingUpdate) return;
        // Menu item and balloon may both be clicked: only one confirmation dialog at a time.
        _confirmingUpdate = true;
        bool confirmed;
        try
        {
            confirmed = ConfirmUpdate("Flowseal", release.Version, _engine?.Version);
        }
        finally
        {
            _confirmingUpdate = false;
        }
        // Installed meanwhile (e.g. via the other entry point): nothing left to do.
        if (!ReleaseVersion.IsNewer(release.Version, _engine?.Version)) return;
        if (!confirmed)
        {
            // Declined: stop announcing this version in the background; a manual check still offers it.
            Settings.SkippedUpdateVersion = release.Version;
            _settingsStore.Save(Settings);
            Log.Info($"User declined Flowseal {release.Version}");
            return;
        }
        await Serialized($"Установка Flowseal {release.Version}…", silentErrors: false, () => InstallCoreAsync(release));
    }

    private Task InstallFirstEngineAsync() => Serialized("Загрузка Flowseal…", silentErrors: false, async () =>
    {
        var release = await Task.Run(() => _updater.CheckAsync(null, CancellationToken.None))
                      ?? throw new InvalidOperationException("Не удалось найти релиз Flowseal.");
        await InstallCoreAsync(release);
    });

    private async Task InstallCoreAsync(ReleaseInfo release)
    {
        var previousVersion = _engine?.Version;
        var wasRunning = _runner.IsRunning;
        UpdateResult result;
        try
        {
            result = await Task.Run(() => _updater.InstallAsync(
                release,
                switchTo: async installed =>
                {
                    _engine = installed;
                    // Restart on the new files so the old version is no longer locked and can be cleaned up.
                    if (wasRunning) await StartCoreAsync();
                },
                rollback: async previous =>
                {
                    _engine = previous;
                    if (wasRunning) await StartCoreAsync();
                },
                CancellationToken.None));
        }
        catch (UpdateException ex)
        {
            Log.Error("Update failed", ex);
            if (ex.ReleaseDefect && ex.Version is not null)
            {
                // Do not announce a release that is known to be broken on every launch.
                // Network hiccups are not remembered: the release is offered again next time.
                Settings.SkippedUpdateVersion = ex.Version;
                _settingsStore.Save(Settings);
                _availableUpdate = null;
            }
            Notify?.Invoke("Обновление не удалось",
                _engine is null ? ex.Message : $"{ex.Message}\nОставлена версия {_engine.Version}.", ToolTipIcon.Warning);
            return;
        }

        _availableUpdate = null;
        if (result.Outcome != UpdateOutcome.Installed) return; // already on this version (e.g. confirmed twice)
        if (Settings.SkippedUpdateVersion is not null)
        {
            Settings.SkippedUpdateVersion = null;
            _settingsStore.Save(Settings);
        }

        Log.Info($"Installed Flowseal {result.LatestVersion} (was {previousVersion ?? "none"}). Problems: {string.Join("; ", _engine!.Problems)}");
        foreach (var file in Directory.EnumerateFiles(_engine.BinDir))
        {
            Log.Info($"  {Path.GetFileName(file)} sha256 {FileSha256(file)}");
        }
        Notify?.Invoke("Flowseal обновлён", $"Версия {result.LatestVersion}. Стратегий: {_engine.Strategies.Count}.", ToolTipIcon.Info);
    }

    public Task EnableAsync() => Serialized("Запуск…", silentErrors: false, async () =>
    {
        if (!await ResolveConflictsAsync()) return;
        await StartCoreAsync();
        _bypassWantedAfterLearning = true;
        Settings.EnableOnStart = true;
        _settingsStore.Save(Settings);
    });

    public Task DisableAsync() => Serialized("Остановка…", silentErrors: false, async () =>
    {
        await Task.Run(_runner.Stop);
        _bypassWantedAfterLearning = false;
        Settings.EnableOnStart = false;
        _settingsStore.Save(Settings);
    });

    public Task SelectStrategyAsync(string name) => Serialized("Смена стратегии…", silentErrors: false, () =>
        ApplySettingWithRollback(
            () => Settings.SelectedStrategy,
            v => Settings.SelectedStrategy = v,
            name));

    public Task SetGameFilterAsync(GameFilterMode mode) => Serialized("Смена игрового фильтра…", silentErrors: false, () =>
        ApplySettingWithRollback(
            () => Settings.GameFilter,
            v => Settings.GameFilter = v,
            mode));

    public Task AutoSelectAsync(IProgress<SelectionProgress>? uiProgress, CancellationToken ct) => Serialized("Подбор стратегии…", silentErrors: false, async () =>
    {
        var engine = _engine ?? throw new InvalidOperationException("Движок Flowseal не установлен.");
        if (!await ResolveConflictsAsync()) return;

        SeedTargets();
        var targets = TargetsFile.Parse(File.ReadAllText(AppPaths.Targets));
        if (targets.Count == 0)
        {
            Notify?.Invoke("Нет целей для проверки", $"Добавьте адреса в {AppPaths.Targets}", ToolTipIcon.Warning);
            return;
        }

        var wasRunning = _runner.IsRunning;
        using var probeHttp = new HttpClient(HttpConnectivityProbe.CreateNonPoolingHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var gameRules = CompileGameRules();
        var selector = new StrategyAutoSelector(
            _runner,
            new HttpConnectivityProbe(probeHttp, TimeSpan.FromSeconds(6)),
            s => BuildArgs(s, gameRules),
            settleDelay: TimeSpan.FromSeconds(1));

        // Synchronous progress for the tray text: Progress<T> would post updates that can land after the
        // operation ends and leave the tray stuck in "busy". The window passes its own marshalled progress.
        var progress = new InlineProgress<SelectionProgress>(p =>
        {
            SetBusy($"Подбор стратегии: {p.StrategyIndex + 1}/{p.StrategyCount} ({p.Fraction:P0})");
            if (p.Completed is { } s)
            {
                Log.Info($"AutoSelect {s.Strategy.Name}: {s.Passed}/{s.Total}, latency {s.TotalLatency.TotalMilliseconds:F0} ms {s.Error}" +
                         (s.Failed is { Count: > 0 } failed ? $"; failed: {string.Join(", ", failed)}" : ""));
            }
            uiProgress?.Report(p);
        });

        SelectionResult result;
        _autoSelecting = true;
        try
        {
            result = await Task.Run(() => selector.SelectAsync(engine.Strategies, targets, progress, ct));
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException) Notify?.Invoke("Подбор отменён", "Оставлена прежняя стратегия.", ToolTipIcon.Info);
            if (wasRunning) await StartCoreAsync();
            if (ex is OperationCanceledException) return;
            throw;
        }
        finally
        {
            _autoSelecting = false;
        }

        if (result.Best is null)
        {
            Notify?.Invoke("Рабочая стратегия не найдена", "Ни одна стратегия не открыла цели. Подробности в логе.", ToolTipIcon.Warning);
            if (wasRunning) await StartCoreAsync();
            return;
        }

        var best = result.Scores.First(s => s.Strategy == result.Best);
        try
        {
            await ApplySettingWithRollback(() => Settings.SelectedStrategy, v => Settings.SelectedStrategy = v, best.Strategy.Name, startEvenIfStopped: true);
        }
        catch
        {
            // The winner passed probing but would not start for real: go back to what was running before.
            if (wasRunning)
            {
                try { await StartCoreAsync(); }
                catch (Exception restoreEx) { Log.Error("Restoring previous strategy failed", restoreEx); }
            }
            throw;
        }
        Settings.EnableOnStart = true;
        _bypassWantedAfterLearning = true;
        _settingsStore.Save(Settings);
        Notify?.Invoke("Стратегия выбрана", $"{best.Strategy.Name}: открыто {best.Passed} из {best.Total}.", ToolTipIcon.Info);
    });

    // Persist a changed setting only once winws accepted it; otherwise restore the previous value.
    private async Task ApplySettingWithRollback<T>(Func<T> get, Action<T> set, T value, bool startEvenIfStopped = false)
    {
        var previous = get();
        var wasRunning = _runner.IsRunning;
        set(value);
        if (wasRunning || startEvenIfStopped)
        {
            try
            {
                await StartCoreAsync();
            }
            catch
            {
                set(previous);
                try
                {
                    if (wasRunning) await StartCoreAsync();
                }
                catch (Exception restoreEx)
                {
                    Log.Error("Restoring previous setting failed", restoreEx);
                }
                throw;
            }
        }
        _settingsStore.Save(Settings);
    }

    private async Task StartCoreAsync()
    {
        var engine = _engine ?? throw new InvalidOperationException("Движок Flowseal не установлен.");
        var strategy = StrategyPicker.Pick(engine.Strategies, Settings.SelectedStrategy);
        UserLists.EnsureReferenced(engine.Strategies, AppPaths.UserLists);
        var args = BuildArgs(strategy, CompileGameRules());

        if (EngineFiles.Missing(engine.BinDir) is { Count: > 0 } missing)
        {
            Log.Error($"Engine files missing: {string.Join(", ", missing)}", null);
            throw new InvalidOperationException(EngineFiles.MissingMessage(missing, engine.BinDir));
        }
        try
        {
            await _runner.StartAsync(args, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (EngineFiles.ExplainStartFailure(ex.Message, engine.BinDir) is { } hint)
        {
            Log.Error("winws failed to start", ex);
            throw new InvalidOperationException(hint, ex);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // The exe itself was removed or blocked between the check and the start.
            Log.Error("winws could not be started", ex);
            throw new InvalidOperationException(EngineFiles.MissingMessage(new[] { "winws.exe" }, engine.BinDir), ex);
        }
        Log.Info($"winws started: {strategy.Name}, game filter {Settings.GameFilter}, Flowseal {engine.Version}, " +
                 $"sha256 {FileSha256(engine.WinwsPath)}, args: {string.Join(' ', args)}");
    }

    private IReadOnlyList<string> BuildArgs(StrategyDefinition strategy, IReadOnlyList<GameRule> gameRules)
    {
        var engine = _engine ?? throw new InvalidOperationException("Движок Flowseal не установлен.");
        return StrategyArgsBuilder.Build(
            strategy,
            new EnginePaths(engine.BinDir, engine.ListsDir, AppPaths.UserLists),
            new GameFilterOptions(Settings.GameFilter, Settings.GameTcpRange, Settings.GameUdpRange),
            gameRules);
    }

    private async Task<bool> ResolveConflictsAsync()
    {
        var report = await Task.Run(() => ConflictDetector.Find(
            ConflictDetector.SnapshotCandidates().ToList(), RunningServiceNames(), _runner.ProcessId));
        if (!report.Any) return true;

        var description = string.Join(Environment.NewLine,
            report.Services.Select(s => $"служба {s}")
                .Concat(report.Processes.Select(p => $"{p.Name}.exe (PID {p.Id})")));
        if (!ConfirmStopConflicts(description))
        {
            Notify?.Invoke("Обход не включён", "Сначала остановите другой обход блокировок.", ToolTipIcon.Warning);
            return false;
        }

        await Task.Run(() => StopConflicts(report));
        return true;
    }

    private void StopConflicts(ConflictReport report)
    {
        // Services first: the Flowseal "zapret" service would otherwise respawn winws.
        foreach (var name in report.Services)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status != ServiceControllerStatus.Stopped)
                {
                    sc.Stop();
                    sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(10));
                }
                Log.Info($"Stopped conflicting service {name}");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ServiceProcess.TimeoutException)
            {
                throw new InvalidOperationException($"Не удалось остановить службу {name}: {ex.Message}", ex);
            }
        }

        foreach (var info in ConflictDetector.Find(ConflictDetector.SnapshotCandidates().ToList(), Array.Empty<string>(), _runner.ProcessId).Processes)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(info.Id);
                p.Kill(entireProcessTree: true);
                if (!p.WaitForExit(5000)) throw new InvalidOperationException("процесс не завершился за 5 секунд");
                Log.Info($"Killed conflicting process {info.Name} (PID {info.Id})");
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                throw new InvalidOperationException($"Не удалось остановить {info.Name}.exe (PID {info.Id}): {ex.Message}", ex);
            }
        }
    }

    private static IReadOnlyList<string> RunningServiceNames()
    {
        var running = new List<string>();
        foreach (var name in ConflictDetector.ServiceNames)
        {
            try
            {
                using var sc = new ServiceController(name);
                if (sc.Status != ServiceControllerStatus.Stopped) running.Add(name);
            }
            catch (InvalidOperationException)
            {
                // Service is not installed.
            }
        }
        return running;
    }

    private void SeedTargets()
    {
        if (File.Exists(AppPaths.Targets)) return;
        var fromEngine = _engine is null ? null : Path.Combine(_engine.RootDir, "utils", "targets.txt");
        if (fromEngine is not null && File.Exists(fromEngine))
        {
            File.Copy(fromEngine, AppPaths.Targets);
        }
        else
        {
            File.WriteAllText(AppPaths.Targets,
                "DiscordMain = \"https://discord.com\"\nYouTubeWeb = \"https://www.youtube.com\"\nGoogleVideo = \"https://redirector.googlevideo.com\"\n");
        }
    }

    private static string FileSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (IOException)
        {
            return "unreadable";
        }
    }

    private void OnCrashed(string output)
    {
        Log.Error("winws exited unexpectedly:\n" + output);
        // During auto-select a crashing candidate is just a failed trial, already scored and logged.
        if (_autoSelecting) return;
        Notify?.Invoke("winws остановился", "Обход выключен. Подробности в логе.", ToolTipIcon.Error);
        Changed();
    }

    private async Task Serialized(string busyText, bool silentErrors, Func<Task> action)
    {
        await _gate.WaitAsync();
        try
        {
            SetBusy(busyText);
            await action();
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user (e.g. a window closed): not an error worth a balloon.
            Log.Info(busyText + " cancelled");
        }
        catch (Exception ex)
        {
            Log.Error(busyText, ex);
            if (!silentErrors) Notify?.Invoke("Ошибка", ex.Message, ToolTipIcon.Error);
        }
        finally
        {
            _busyText = null;
            _gate.Release();
            Changed();
        }
    }

    private void AnnounceUpdate(string product, string version)
    {
        UpdateAnnounced?.Invoke(product);
        Notify?.Invoke($"Доступно обновление {product}",
            $"Версия {version}. Нажмите на это уведомление или откройте «Настройки», чтобы установить.", ToolTipIcon.Info);
    }

    private void SetBusy(string text)
    {
        _busyText = text;
        Changed();
    }

    private void Changed() => StateChanged?.Invoke();

    public void Dispose()
    {
        try
        {
            // The proxy lives outside winws' kill-on-close job; the app that started it stops it — always,
            // even if a start was still in flight.
            TgProxyRunner.Stop();
        }
        catch (Exception ex)
        {
            Log.Error("Stopping TG WS Proxy on exit failed", ex);
        }
        DisposeVpn();
        _learningSession?.Dispose();
        _runner.Dispose();
        _http.Dispose();
    }

    private sealed class InlineProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;
        public InlineProgress(Action<T> report) => _report = report;
        public void Report(T value) => _report(value);
    }
}

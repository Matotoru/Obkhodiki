using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obkhodiki.Core.Vpn;

namespace Obkhodiki.App.Ui.ViewModels;

public sealed record QualityTarget(string Title, IReadOnlyList<IPEndPoint> Endpoints)
{
    public override string ToString() => Title;
}

public sealed partial class PathStatsViewModel : ObservableObject
{
    [ObservableProperty] private string _ping = "—";
    [ObservableProperty] private string _jitter = "—";
    [ObservableProperty] private string _loss = "—";
    [ObservableProperty] private bool _isWinner;

    public void Set(PathStats s, bool winner)
    {
        Ping = s.MedianMs is { } m ? $"{m:F0} мс" : "нет ответа";
        Jitter = s.MedianMs is null ? "—" : $"{s.JitterMs:F1} мс";
        Loss = $"{s.LossPercent:F0}%";
        IsWinner = winner;
    }
}

/// <summary>One server of the source, with its last ping.</summary>
public sealed partial class ServerRowViewModel : ObservableObject
{
    public required string Tag { get; init; }
    public required string Name { get; init; }
    public required string Details { get; init; }
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private string _pingText = "—";
    [ObservableProperty] private bool _isDown;
}

/// <summary>A routing category checkbox.</summary>
public sealed partial class CategoryOption : ObservableObject
{
    private readonly Action _changed;

    public CategoryOption(RuleCategory category, Action changed)
    {
        Id = category.Id;
        Title = category.Title;
        Description = category.Description;
        _changed = changed;
    }

    public string Id { get; }
    public string Title { get; }
    public string? Description { get; }
    internal bool Syncing { get; set; }
    [ObservableProperty] private bool _isChecked;

    partial void OnIsCheckedChanged(bool value)
    {
        if (!Syncing) _changed();
    }
}

/// <summary>VPS: servers, what goes through them, and the path quality check.</summary>
public sealed partial class VpnViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private CancellationTokenSource? _measureCts;

    public ObservableCollection<string> Programs { get; } = new();
    public ObservableCollection<string> RunningPrograms { get; } = new();
    public ObservableCollection<QualityTarget> Targets { get; } = new();

    public ObservableCollection<ServerRowViewModel> Servers { get; } = new();
    public IReadOnlyList<CategoryOption> ProxyCategories { get; }
    public IReadOnlyList<CategoryOption> DirectCategories { get; }

    [ObservableProperty] private bool _vpnEnabled = true;
    [ObservableProperty] private bool _fullTunnel;
    [ObservableProperty] private bool _autoBest = true;
    [ObservableProperty] private bool _isSubscription;
    [ObservableProperty] private string? _sourceDetails;
    [ObservableProperty] private bool _hasInsecure;
    [ObservableProperty] private string? _pingStatus;
    [ObservableProperty] private bool _isPinging;
    [ObservableProperty] private bool _isBusy;
    private bool _syncing;

    public bool ManyServers => Servers.Count > 1;

    public PathStatsViewModel Direct { get; } = new();
    public PathStatsViewModel Tunnel { get; } = new();

    [ObservableProperty] private string? _serverName;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _singBoxVersion = "не установлен";
    [ObservableProperty] private bool _isEditingServer;
    [ObservableProperty] private string _serverLink = "";
    [ObservableProperty] private string? _serverError;
    [ObservableProperty] private string _newProgram = "";
    [ObservableProperty] private string? _selectedProgram;
    [ObservableProperty] private string _domainsText = "";
    [ObservableProperty] private string? _listsMessage;
    [ObservableProperty] private bool _listsDirty;
    [ObservableProperty] private QualityTarget? _selectedTarget;
    [ObservableProperty] private string _customTarget = "";
    [ObservableProperty] private bool _isMeasuring;
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string? _verdict;

    public bool HasServer => ServerName is not null;
    public bool ShowServerForm => !HasServer || IsEditingServer;
    public string MeasureButtonText => IsMeasuring ? "Остановить" : "Измерить";

    public VpnViewModel(ShellViewModel shell)
    {
        _shell = shell;
        ProxyCategories = RuleCatalog.Proxy.Select(c => new CategoryOption(c, ScheduleCategoriesApply)).ToList();
        DirectCategories = RuleCatalog.Direct.Select(c => new CategoryOption(c, ScheduleCategoriesApply)).ToList();
        // Several ticks in a row become one restart of the tunnel.
        _categoriesTimer.Tick += (_, _) =>
        {
            _categoriesTimer.Stop();
            _categoriesPending = false;
            var proxy = ProxyCategories.Where(o => o.IsChecked).Select(o => o.Id).ToList();
            var direct = DirectCategories.Where(o => o.IsChecked).Select(o => o.Id).ToList();
            _ = _shell.RunAsync(c => c.SetVpnCategoriesAsync(proxy, direct));
        };
        Servers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ManyServers));
    }

    partial void OnVpnEnabledChanged(bool value)
    {
        if (_syncing) return;
        _ = _shell.RunAsync(c => c.SetVpnEnabledAsync(value));
    }

    private readonly System.Windows.Threading.DispatcherTimer _categoriesTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };
    private bool _categoriesPending;

    private void ScheduleCategoriesApply()
    {
        _categoriesPending = true;
        _categoriesTimer.Stop();
        _categoriesTimer.Start();
    }

    partial void OnFullTunnelChanged(bool value)
    {
        if (_syncing) return;
        _ = _shell.RunAsync(c => c.SetVpnFullTunnelAsync(value));
    }

    partial void OnAutoBestChanged(bool value)
    {
        if (_syncing) return;
        _ = _shell.RunAsync(c => c.SetVpnAutoBestAsync(value));
    }

    private static string Size(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):F1} ГБ" : $"{bytes / (double)(1L << 20):F0} МБ";

    private void SyncSource(AppController c)
    {
        var info = c.VpnSourceInfo;
        IsSubscription = info?.IsSubscription == true;
        HasInsecure = info?.HasInsecure == true;
        if (info is null || !info.IsSubscription)
        {
            SourceDetails = null;
        }
        else
        {
            var parts = new List<string>();
            if (info.Used is { } used) parts.Add(info.Total is { } total ? $"трафик {Size(used)} из {Size(total)}" : $"трафик {Size(used)}");
            if (info.Expire is { } exp) parts.Add($"до {exp.ToLocalTime():dd.MM.yyyy}");
            if (info.FetchedAt is { } at) parts.Add($"обновлена {at.ToLocalTime():dd.MM HH:mm}");
            if (info.Skipped > 0) parts.Add($"пропущено {info.Skipped}");
            SourceDetails = string.Join(" · ", parts);
        }

        var servers = c.VpnServers;
        if (!servers.Select(s => s.Tag).SequenceEqual(Servers.Select(s => s.Tag)))
        {
            Servers.Clear();
            foreach (var s in servers)
            {
                Servers.Add(new ServerRowViewModel
                {
                    Tag = s.Tag,
                    Name = s.Server.Name ?? s.Server.Host,
                    Details = $"{ProtocolTitle(s.Server)} · {s.Server.Host}:{s.Server.Port}",
                });
            }
        }
        var active = c.ActiveServerTag;
        foreach (var row in Servers)
        {
            row.IsActive = row.Tag == active;
            if (c.ServerPings.TryGetValue(row.Tag, out var p))
            {
                row.IsDown = p.MedianMs is null;
                row.PingText = p.MedianMs is { } ms ? $"{ms} мс" : "нет ответа";
            }
            else
            {
                row.IsDown = false;
                row.PingText = "—";
            }
        }

        IsPinging = c.IsPingingServers;
        PingStatus = c.IsPingingServers ? "Проверяю серверы…"
            : c.Settings.VpnAutoBest && c.GameRunning ? "Автовыбор на паузе: запущена игра"
            : c.LastPingTime is { } t ? $"Проверено в {t:HH:mm}"
            : null;
    }

    private static string ProtocolTitle(IProxyServer s) => s switch
    {
        VlessLink { Tls.Security: LinkSecurity.Reality } => "VLESS Reality",
        VlessLink v => v.Transport.Type == LinkTransportType.Tcp ? "VLESS" : $"VLESS {v.Transport.Type}",
        Hysteria2Link => "Hysteria2",
        TrojanLink => "Trojan",
        ShadowsocksLink => "Shadowsocks",
        _ => s.Protocol,
    };

    partial void OnServerNameChanged(string? value) => NotifyServer();
    partial void OnIsEditingServerChanged(bool value) => NotifyServer();
    partial void OnIsMeasuringChanged(bool value) => OnPropertyChanged(nameof(MeasureButtonText));
    partial void OnDomainsTextChanged(string value) => ListsDirty = true;

    private void NotifyServer()
    {
        OnPropertyChanged(nameof(HasServer));
        OnPropertyChanged(nameof(ShowServerForm));
    }

    internal void Refresh(AppController c)
    {
        ServerName = c.VpnServerName;
        IsRunning = c.IsVpnRunning;
        IsBusy = c.BusyText is not null;
        SingBoxVersion = c.SingBoxVersion ?? "не установлен";
        SyncSource(c);
        _syncing = true;
        // While an operation runs the switch shows what the user asked for; the result arrives with the next refresh.
        if (!IsBusy)
        {
            VpnEnabled = c.Settings.VpnEnabled;
            FullTunnel = c.Settings.VpnFullTunnel;
            AutoBest = c.Settings.VpnAutoBest;
        }
        _syncing = false;
        if (!ListsDirty)
        {
            foreach (var (options, chosen) in _categoriesPending
                         ? Array.Empty<(IReadOnlyList<CategoryOption>, List<string>)>()
                         : new[] { (ProxyCategories, c.Settings.VpnProxyCategories), (DirectCategories, c.Settings.VpnDirectCategories) })
            {
                foreach (var o in options)
                {
                    o.Syncing = true;
                    o.IsChecked = chosen.Contains(o.Id);
                    o.Syncing = false;
                }
            }
            if (!c.Settings.VpnProcesses.SequenceEqual(Programs))
            {
                Programs.Clear();
                foreach (var p in c.Settings.VpnProcesses) Programs.Add(p);
            }
            DomainsText = string.Join(Environment.NewLine, c.Settings.VpnDomains);
            ListsDirty = false;
        }

        var games = c.Settings.GameProfiles
            .Select(p => new QualityTarget($"Игра: {p.Name}",
                p.ProbeEndpoints.Select(IPEndPoint.Parse).Where(e => TlsPing.IsTlsPort(e.Port)).Take(3).ToList()))
            .ToList();
        var measurable = games.Where(t => t.Endpoints.Count > 0).ToList();
        var missing = games.Where(t => t.Endpoints.Count == 0).Select(t => t.Title[6..]).ToList();
        TargetsHint = missing.Count == 0 ? null
            : $"Для {string.Join(", ", missing)} нет адресов для замера: на странице «Игры» откройте «…» → «Дообучить», запустите запись и сыграйте матч.";
        // Always something to measure: the general path to big networks near the VPS.
        var targets = measurable.Concat(GeneralTargets).ToList();
        if (!targets.Select(t => t.Title).SequenceEqual(Targets.Select(t => t.Title)))
        {
            var selected = SelectedTarget?.Title;
            Targets.Clear();
            foreach (var t in targets) Targets.Add(t);
            SelectedTarget = Targets.FirstOrDefault(t => t.Title == selected) ?? Targets.FirstOrDefault();
        }
    }

    private static readonly QualityTarget[] GeneralTargets =
    {
        new("Общий канал: Cloudflare (1.1.1.1)", new[] { IPEndPoint.Parse("1.1.1.1:443"), IPEndPoint.Parse("1.0.0.1:443") }),
        new("Общий канал: Google (8.8.8.8)", new[] { IPEndPoint.Parse("8.8.8.8:443"), IPEndPoint.Parse("8.8.4.4:443") }),
    };

    [ObservableProperty] private string? _targetsHint;

    // ---------- server ----------

    [RelayCommand]
    private void EditServer()
    {
        ServerLink = "";
        ServerError = null;
        IsEditingServer = true;
    }

    [RelayCommand]
    private void CancelEditServer()
    {
        ServerLink = "";
        IsEditingServer = false;
    }

    /// <summary>The link carries the server password: drop it as soon as the form is out of sight.</summary>
    internal void ForgetServerLink()
    {
        if (ServerLink.Length == 0) return;
        ServerLink = "";
        ServerError = null;
        if (HasServer) IsEditingServer = false;
    }

    [RelayCommand]
    private async Task SaveServerAsync()
    {
        if (_shell.Controller is not { } c) return;
        ServerError = null;
        var error = await c.SetVpnSourceAsync(ServerLink);
        if (error is not null)
        {
            ServerError = error;
            return;
        }
        ServerLink = "";
        IsEditingServer = false;
        _shell.AddEvent("VPS", c.VpnSourceInfo?.IsSubscription == true ? "Подписка подключена." : "Сервер сохранён.", EventKind.Success);
        _shell.Refresh();
    }

    [RelayCommand]
    private async Task RemoveServerAsync()
    {
        if (!await UiDialogs.ConfirmAsync("Удалить сервер", "Удалить сервер VPS? Все маршруты через VPS перестанут работать.", "Удалить")) return;
        await _shell.RunAsync(c => c.RemoveVpnServerAsync());
    }

    // ---------- programs and sites ----------

    [RelayCommand]
    private void RefreshRunningPrograms()
    {
        RunningPrograms.Clear();
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    if (p.MainWindowHandle != IntPtr.Zero && p.Id != Environment.ProcessId) names.Add(p.ProcessName + ".exe");
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        foreach (var n in names) RunningPrograms.Add(n);
    }

    [RelayCommand]
    private void AddProgram()
    {
        var name = NewProgram.Trim();
        if (name.Length == 0) return;
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        if (!SingBoxConfig.IsValidProcessName(name))
        {
            ListsMessage = "Укажите имя программы, например chrome.exe.";
            return;
        }
        if (!Programs.Contains(name, StringComparer.OrdinalIgnoreCase)) Programs.Add(name);
        NewProgram = "";
        ListsMessage = null;
        ListsDirty = true;
    }

    [RelayCommand]
    private void RemoveProgram(string? name)
    {
        if (name is null) return;
        Programs.Remove(name);
        ListsDirty = true;
    }

    [RelayCommand]
    private async Task SaveListsAsync()
    {
        if (_shell.Controller is not { } c) return;
        var lines = DomainsText.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        var bad = lines.Where(l => SingBoxConfig.NormalizeDomain(l) is null).ToList();
        if (bad.Count > 0)
        {
            ListsMessage = "Не похоже на домен: " + string.Join(", ", bad.Take(3));
            return;
        }
        ListsMessage = null;
        if (await c.SetVpnListsAsync(Programs.ToList(), lines, c.Settings.VpnProxyCategories, c.Settings.VpnDirectCategories))
        {
            ListsDirty = false;
            ListsMessage = "Сохранено.";
        }
        else
        {
            ListsMessage = "Не удалось применить — подробности в уведомлении.";
        }
        _shell.Refresh();
    }

    // ---------- servers ----------

    [RelayCommand]
    private Task RefreshSubscription() => _shell.RunAsync(c => c.RefreshSubscriptionAsync(interactive: true));

    [RelayCommand]
    private Task PingServers() => _shell.RunAsync(c => c.PingServersManuallyAsync());

    [RelayCommand]
    private Task SelectServer(ServerRowViewModel? row) =>
        row is null || row.IsActive ? Task.CompletedTask : _shell.RunAsync(c => c.SelectVpnServerAsync(row.Tag));

    // ---------- quality ----------

    // Concurrent: the same button stops a running measurement.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task MeasureAsync()
    {
        if (IsMeasuring)
        {
            _measureCts?.Cancel();
            return;
        }
        if (_shell.Controller is not { } c) return;

        IReadOnlyList<IPEndPoint> endpoints;
        if (CustomTarget.Trim().Length > 0)
        {
            if (!IPEndPoint.TryParse(CustomTarget.Trim(), out var ep) || !TlsPing.IsTlsPort(ep.Port))
            {
                Verdict = "Введите адрес TLS-сервера в виде IP:443, например 1.1.1.1:443.";
                return;
            }
            endpoints = new[] { ep };
        }
        else if (SelectedTarget is { } t)
        {
            endpoints = t.Endpoints;
        }
        else
        {
            Verdict = "Нет адресов для замера: дообучите игру или введите адрес вида IP:443.";
            return;
        }

        IsMeasuring = true;
        HasResult = false;
        Verdict = "Идёт замер, около 10–30 секунд…";
        using var cts = new CancellationTokenSource();
        _measureCts = cts;
        try
        {
            var result = await c.MeasureAsync(endpoints, interactive: true, cts.Token);
            if (result is null)
            {
                Verdict = cts.IsCancellationRequested ? "Замер остановлен." : "Замер не удался — подробности в событиях.";
                return;
            }
            var vpnWins = result.Decision.Choice == PathChoice.Vpn;
            Direct.Set(result.Direct, !vpnWins);
            Tunnel.Set(result.Tunnel, vpnWins);
            HasResult = true;
            Verdict = (vpnWins ? "Лучше через VPS: " : "Лучше напрямую: ") + result.Decision.Reason;
        }
        finally
        {
            _measureCts = null;
            IsMeasuring = false;
            _shell.Refresh();
        }
    }

    internal void LoadSample()
    {
        _syncing = true;
        FullTunnel = true;
        AutoBest = true;
        _syncing = false;
        IsSubscription = true;
        SourceDetails = "трафик 12.4 ГБ из 100.0 ГБ · до 01.12.2026 · обновлена 27.09 13:10";
        foreach (var (name, details, ping, active, down) in new[]
                 {
                     ("Нидерланды", "VLESS Reality · nl.example.com:443", "48 мс", true, false),
                     ("Германия", "Hysteria2 · de.example.com:29615", "56 мс", false, false),
                     ("Финляндия", "VLESS WebSocket · fi.example.com:443", "нет ответа", false, true),
                 })
        {
            Servers.Add(new ServerRowViewModel { Tag = name, Name = name, Details = details, PingText = ping, IsActive = active, IsDown = down });
        }
        PingStatus = "Проверено в 13:12";
        foreach (var o in DirectCategories) { o.Syncing = true; o.IsChecked = true; o.Syncing = false; }
        foreach (var o in ProxyCategories.Take(2)) { o.Syncing = true; o.IsChecked = true; o.Syncing = false; }
        ServerName = "Мой 3x-ui · 3 сервера";
        IsRunning = true;
        SingBoxVersion = "1.14.2";
        foreach (var p in new[] { "chrome.exe", "Spotify.exe" }) Programs.Add(p);
        DomainsText = "chatgpt.com\nopenai.com";
        ListsDirty = false;
        Targets.Add(new QualityTarget("Игра: War Dogs", new[] { IPEndPoint.Parse("3.120.1.1:443") }));
        SelectedTarget = Targets[0];
        Direct.Set(new PathStats(58, 9.4, 6, 18), false);
        Tunnel.Set(new PathStats(71, 2.1, 0, 18), true);
        HasResult = true;
        Verdict = "Лучше через VPS: меньше потерь: 0% против 6%";
    }
}

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZapretHub.Core.Vpn;

namespace ZapretHub.App.Ui.ViewModels;

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

/// <summary>VPS: server, what goes through it, and the path quality check.</summary>
public sealed partial class VpnViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private CancellationTokenSource? _measureCts;

    public ObservableCollection<string> Programs { get; } = new();
    public ObservableCollection<string> RunningPrograms { get; } = new();
    public ObservableCollection<QualityTarget> Targets { get; } = new();

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

    public VpnViewModel(ShellViewModel shell) => _shell = shell;

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
        SingBoxVersion = c.SingBoxVersion ?? "не установлен";
        if (!ListsDirty)
        {
            if (!c.Settings.VpnProcesses.SequenceEqual(Programs))
            {
                Programs.Clear();
                foreach (var p in c.Settings.VpnProcesses) Programs.Add(p);
            }
            DomainsText = string.Join(Environment.NewLine, c.Settings.VpnDomains);
            ListsDirty = false;
        }

        var targets = c.Settings.GameProfiles
            .Select(p => new QualityTarget($"Игра: {p.Name}",
                p.ProbeEndpoints.Select(IPEndPoint.Parse).Where(e => TlsPing.IsTlsPort(e.Port)).Take(3).ToList()))
            .Where(t => t.Endpoints.Count > 0)
            .ToList();
        if (!targets.Select(t => t.Title).SequenceEqual(Targets.Select(t => t.Title)))
        {
            var selected = SelectedTarget?.Title;
            Targets.Clear();
            foreach (var t in targets) Targets.Add(t);
            SelectedTarget = Targets.FirstOrDefault(t => t.Title == selected) ?? Targets.FirstOrDefault();
        }
    }

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
        var error = await c.SetVpnServerAsync(ServerLink);
        if (error is not null)
        {
            ServerError = error;
            return;
        }
        ServerLink = "";
        IsEditingServer = false;
        _shell.AddEvent("VPS", "Сервер сохранён.", EventKind.Success);
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
        if (await c.SetVpnListsAsync(Programs.ToList(), lines))
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
        ServerName = "hysteria2://***@fr2.example.com:29615 (HysteriaFR2)";
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

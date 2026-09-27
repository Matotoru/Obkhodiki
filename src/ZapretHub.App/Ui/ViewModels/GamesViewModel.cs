using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZapretHub.Core.Games;

namespace ZapretHub.App.Ui.ViewModels;

public sealed record RouteOption(GameRoute Route, string Title);

public sealed record ProcessChoice(string ExeName, string Title)
{
    public override string ToString() => Title == Path.GetFileNameWithoutExtension(ExeName) ? ExeName : $"{Title}  ·  {ExeName}";
}

public sealed partial class GameCardViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _syncing;

    public GameCardViewModel(ShellViewModel shell, IReadOnlyList<RouteOption> routes)
    {
        _shell = shell;
        RouteOptions = routes;
    }

    public required string Id { get; init; }
    public IReadOnlyList<RouteOption> RouteOptions { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private RouteOption? _route;
    [ObservableProperty] private bool _vpnAvailable;

    partial void OnEnabledChanged(bool value)
    {
        if (_syncing) return;
        _ = _shell.RunAsync(c => c.SetGameProfileEnabledAsync(Id, value));
    }

    partial void OnRouteChanged(RouteOption? value)
    {
        if (_syncing || value is null) return;
        _ = _shell.RunAsync(c => c.SetGameRouteAsync(Id, value.Route));
    }

    internal void Sync(GameProfile p, bool vpnAvailable, IReadOnlyList<RouteOption> routes)
    {
        _syncing = true;
        Name = p.Name;
        var ports = string.Join(", ", new[] { p.TcpPorts is { Length: > 0 } t ? "TCP " + t : null, p.UdpPorts is { Length: > 0 } u ? "UDP " + u : null }.Where(x => x is not null));
        Details = $"{p.ProcessName ?? "процесс не указан"} · {(ports.Length > 0 ? ports : "порты не записаны")}";
        Enabled = p.Enabled;
        Route = routes.First(r => r.Route == p.Route);
        VpnAvailable = vpnAvailable;
        _syncing = false;
    }

    [RelayCommand]
    private void Relearn() => _shell.Games.OpenLearning(this);

    [RelayCommand]
    private void OpenAddresses() => ShellActions.OpenInNotepad(AppController.GameIpsetPath(Id));

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!await UiDialogs.ConfirmAsync("Удалить игру", $"Удалить «{Name}» и её список адресов?", "Удалить")) return;
        await _shell.RunAsync(c => c.DeleteGameProfileAsync(Id));
    }
}

/// <summary>Game profiles and the learning panel (record a game's servers while playing).</summary>
public sealed partial class GamesViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private TrafficLearner _learned = new();
    private GameProfile? _relearnTarget;
    private readonly System.Windows.Threading.DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public ObservableCollection<GameCardViewModel> Profiles { get; } = new();
    public ObservableCollection<ProcessChoice> Processes { get; } = new();

    public IReadOnlyList<RouteOption> RouteOptions { get; } = new[]
    {
        new RouteOption(GameRoute.Direct, "Напрямую (zapret)"),
        new RouteOption(GameRoute.Vpn, "Через VPS"),
        new RouteOption(GameRoute.Auto, "Авто — выбрать при запуске"),
    };

    [ObservableProperty] private bool _isLearningOpen;
    [ObservableProperty] private string _learningTitle = "Добавить игру";
    [ObservableProperty] private string _gameName = "";
    [ObservableProperty] private ProcessChoice? _selectedProcess;
    [ObservableProperty] private string _processText = "";
    [ObservableProperty] private bool _bypassAll = true;
    [ObservableProperty] private bool _expandAws = true;
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private int _addressCount;
    [ObservableProperty] private string _tcpPorts = "—";
    [ObservableProperty] private string _udpPorts = "—";

    public bool HasProfiles => Profiles.Count > 0;
    public bool CanSave => !IsRecording && !IsWorking && AddressCount > 0;
    public string RecordButtonText => IsRecording ? "Остановить запись" : AddressCount > 0 ? "Продолжить запись" : "Начать запись";

    public GamesViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _statsTimer.Tick += (_, _) => UpdateStats();
        Profiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasProfiles));
    }

    partial void OnIsRecordingChanged(bool value) => NotifyButtons();
    partial void OnIsWorkingChanged(bool value) => NotifyButtons();
    partial void OnAddressCountChanged(int value) => NotifyButtons();

    private void NotifyButtons()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(RecordButtonText));
    }

    // Name filled in from the process; replaced when another process is picked, unless the user edited it.
    private string? _autoName;

    partial void OnSelectedProcessChanged(ProcessChoice? value)
    {
        if (value is null) return;
        if (GameName.Length == 0 || GameName == _autoName)
        {
            GameName = value.Title;
            _autoName = value.Title;
        }
    }

    internal void Refresh(AppController c)
    {
        var vpn = c.VpnServerName is not null;
        var profiles = c.Settings.GameProfiles;
        // Keep card instances (and their bindings) when possible; rebuild only when the set changes.
        if (!profiles.Select(p => p.Id).SequenceEqual(Profiles.Select(p => p.Id)))
        {
            Profiles.Clear();
            foreach (var p in profiles) Profiles.Add(new GameCardViewModel(_shell, RouteOptions) { Id = p.Id });
        }
        for (var i = 0; i < profiles.Count; i++) Profiles[i].Sync(profiles[i], vpn, RouteOptions);

        // Recording stopped elsewhere (tray, or the controller gave up): reflect it here.
        if (IsRecording && !IsWorking && !c.IsLearning)
        {
            _statsTimer.Stop();
            IsRecording = false;
            UpdateStats();
        }
    }

    /// <summary>Tray safety net: stops recording whatever state this page is in.</summary>
    internal async Task StopRecordingAsync()
    {
        if (IsRecording && !IsWorking) await ToggleRecordingAsync();
        else if (_shell.Controller is { IsLearning: true } c) await c.StopLearningAsync();
    }

    [RelayCommand]
    private void AddGame() => OpenLearning(null);

    internal void OpenLearning(GameCardViewModel? card)
    {
        if (IsLearningOpen && (IsRecording || IsWorking || AddressCount > 0))
        {
            // Never drop a running or unsaved recording because of a stray click elsewhere.
            _shell.AddEvent("Запись игры", "Сначала сохраните или отмените текущую запись.", EventKind.Warning);
            return;
        }
        _relearnTarget = card is null ? null : _shell.Controller?.Settings.GameProfiles.FirstOrDefault(p => p.Id == card.Id);
        _learned = new TrafficLearner();
        LearningTitle = _relearnTarget is null ? "Добавить игру" : $"Дообучить: {_relearnTarget.Name}";
        GameName = _relearnTarget?.Name ?? "";
        _autoName = null;
        RefreshProcesses();
        SelectedProcess = Processes.FirstOrDefault(p => string.Equals(p.ExeName, _relearnTarget?.ProcessName, StringComparison.OrdinalIgnoreCase));
        ProcessText = SelectedProcess?.ToString() ?? _relearnTarget?.ProcessName ?? "";
        UpdateStats();
        IsLearningOpen = true;
    }

    [RelayCommand]
    private void RefreshProcesses()
    {
        var current = SelectedProcess?.ExeName;
        Processes.Clear();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ProcessChoice>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    // Windowed programs only: games have a window, background services do not.
                    if (p.MainWindowHandle == IntPtr.Zero || p.Id == Environment.ProcessId) continue;
                    var exe = p.ProcessName + ".exe";
                    if (!seen.Add(exe)) continue;
                    list.Add(new ProcessChoice(exe, string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.MainWindowTitle));
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        foreach (var c in list.OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)) Processes.Add(c);
        if (current is not null) SelectedProcess = Processes.FirstOrDefault(p => p.ExeName == current);
    }

    private string SelectedExe()
    {
        if (SelectedProcess is { } p) return p.ExeName;
        var typed = ProcessText.Trim();
        return typed.Length == 0 || typed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? typed : typed + ".exe";
    }

    [RelayCommand]
    private async Task ToggleRecordingAsync()
    {
        if (_shell.Controller is not { } c || IsWorking) return;
        if (IsRecording)
        {
            _statsTimer.Stop();
            IsWorking = true;
            await c.StopLearningAsync();
            IsWorking = false;
            IsRecording = false;
            UpdateStats();
            return;
        }

        var exe = SelectedExe();
        if (exe.Length == 0)
        {
            await UiDialogs.InfoAsync("Процесс игры", "Выберите процесс игры из списка (игра должна быть запущена).");
            return;
        }
        IsWorking = true;
        var started = await c.StartLearningAsync(exe, BypassAll, _learned);
        IsWorking = false;
        if (!started) return;
        IsRecording = true;
        _statsTimer.Start();
    }

    private void UpdateStats()
    {
        AddressCount = _learned.Addresses.Count;
        TcpPorts = _learned.TcpPorts.ToString() is { Length: > 0 } t ? t : "—";
        UdpPorts = _learned.UdpPorts.ToString() is { Length: > 0 } u ? u : "—";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (_shell.Controller is not { } c || !CanSave) return;
        var name = GameName.Trim();
        if (name.Length == 0)
        {
            await UiDialogs.InfoAsync("Название игры", "Укажите название игры.");
            return;
        }

        IsWorking = true;
        try
        {
            AwsIpRanges? aws = null;
            if (ExpandAws)
            {
                try
                {
                    // The real file is a few MB; the cap only stops a runaway response.
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 32 * 1024 * 1024 };
                    aws = AwsIpRanges.Parse(await http.GetStringAsync(AwsIpRanges.Source));
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException)
                {
                    Log.Error("AWS ranges download failed", ex);
                    _shell.AddEvent("Диапазоны AWS", "Не удалось загрузить — адреса сохранены подсетями.", EventKind.Warning);
                }
            }

            var existing = _relearnTarget;
            var profile = new GameProfile
            {
                Id = existing?.Id ?? GameProfiles.MakeId(name, c.Settings.GameProfiles.Select(p => p.Id)),
                Name = name,
                Enabled = true,
                ProcessName = SelectedExe(),
                // Relearning adds to what the profile already knew rather than forgetting earlier sessions.
                TcpPorts = PortSet.Parse(existing?.TcpPorts ?? "").Union(_learned.TcpPorts).ToString(),
                UdpPorts = PortSet.Parse(existing?.UdpPorts ?? "").Union(_learned.UdpPorts).ToString(),
                Route = existing?.Route ?? GameRoute.Direct,
                ProbeEndpoints = GameProfiles.SanitizeEndpoints(
                    _learned.TcpEndpoints.Select(e => e.ToString()).Concat(existing?.ProbeEndpoints ?? new List<string>())),
            };
            if (await c.SaveGameProfileAsync(profile, IpsetBuilder.Build(_learned.Endpoints, aws)))
            {
                IsLearningOpen = false;
                _shell.AddEvent(profile.Name, "Профиль сохранён и включён.", EventKind.Success);
            }
        }
        finally
        {
            IsWorking = false;
            _shell.Refresh();
        }
    }

    [RelayCommand]
    private async Task CancelLearningAsync()
    {
        // A start in flight would come up after the panel is gone; the button is disabled meanwhile.
        if (IsWorking) return;
        if (IsRecording) await ToggleRecordingAsync();
        IsLearningOpen = false;
    }

    internal void LoadSample()
    {
        foreach (var (id, name, details, on, route) in new[]
                 {
                     ("war-dogs", "War Dogs", "WarDogs.exe · TCP 443 · UDP 7777-7800", true, GameRoute.Auto),
                     ("cs2", "Counter-Strike 2", "cs2.exe · UDP 27015-27050", false, GameRoute.Direct),
                 })
        {
            var card = new GameCardViewModel(_shell, RouteOptions) { Id = id };
            card.Sync(new GameProfile { Id = id, Name = name, Enabled = on, Route = route, ProcessName = details.Split(' ')[0] }, true, RouteOptions);
            card.Details = details;
            Profiles.Add(card);
        }
    }

    internal void LoadLearningSample()
    {
        LearningTitle = "Добавить игру";
        GameName = "War Dogs";
        Processes.Add(new ProcessChoice("WarDogs.exe", "War Dogs"));
        SelectedProcess = Processes[0];
        IsRecording = true;
        AddressCount = 14;
        TcpPorts = "443";
        UdpPorts = "7777-7790";
        IsLearningOpen = true;
    }
}

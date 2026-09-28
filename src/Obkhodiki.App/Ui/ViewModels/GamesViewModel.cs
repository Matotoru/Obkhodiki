using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obkhodiki.Core.Games;

namespace Obkhodiki.App.Ui.ViewModels;

public sealed record RouteOption(GameRoute Route, string Title);

public sealed record ProcessChoice(string ExeName, string Title)
{
    public override string ToString() => Title == Path.GetFileNameWithoutExtension(ExeName) ? ExeName : $"{Title}  ·  {ExeName}";
}

/// <summary>Card art: the cover picture when there is one, else a gradient in the game's colours.</summary>
internal static class CardArt
{
    public static Brush Gradient(GameCatalogEntry? entry)
    {
        var (from, to) = entry?.Colors ?? ("#2B2F3A", "#5B6275");
        var brush = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(from), (Color)ColorConverter.ConvertFromString(to), 25);
        brush.Freeze();
        return brush;
    }

    /// <summary>Loads a cached cover without locking the file (it may be replaced later).</summary>
    public static ImageSource? Load(string? path)
    {
        if (path is null || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or FileFormatException or UriFormatException)
        {
            return null;
        }
    }

    public static string Initials(string name) =>
        string.Concat(name.Split(new[] { ' ', ':', '-' }, StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
}

/// <summary>A game from the built-in catalog, shown as a card that adds it in one click.</summary>
public sealed partial class CatalogCardViewModel : ObservableObject
{
    private readonly GamesViewModel _owner;

    internal CatalogCardViewModel(GamesViewModel owner, GameCatalogEntry entry)
    {
        _owner = owner;
        Entry = entry;
        Gradient = CardArt.Gradient(entry);
    }

    public GameCatalogEntry Entry { get; }
    public string Name => Entry.Name;
    public string Initials => CardArt.Initials(Entry.Name);
    public string Subtitle => Entry.NeedsRecording ? $"{Entry.Publisher} · нужна запись одного матча" : $"{Entry.Publisher} · работает сразу";
    public Brush Gradient { get; }

    [ObservableProperty] private ImageSource? _cover;
    [ObservableProperty] private bool _isAdded;

    public string AddText => IsAdded ? "Добавлена" : "Добавить";

    partial void OnIsAddedChanged(bool value) => OnPropertyChanged(nameof(AddText));

    [RelayCommand]
    private Task AddAsync() => IsAdded ? Task.CompletedTask : _owner.AddFromCatalogAsync(this);
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
    [ObservableProperty] private ImageSource? _cover;
    [ObservableProperty] private Brush _gradient = CardArt.Gradient(null);
    [ObservableProperty] private string _initials = "";
    [ObservableProperty] private bool _hasCustomCover;
    private string? _coverPath;

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
        var entry = GameCatalog.For(p);
        Gradient = CardArt.Gradient(entry);
        Initials = CardArt.Initials(p.Name);
        HasCustomCover = File.Exists(AppController.CustomCoverPath(p.Id));
        var path = AppController.CoverFor(p.Id, entry);
        if (path != _coverPath)
        {
            _coverPath = path;
            Cover = CardArt.Load(path);
        }
        _syncing = false;
    }

    [RelayCommand]
    private async Task ChooseCoverAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Картинка для «{Name}»",
            Filter = "Картинки (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog() != true || _shell.Controller is not { } c) return;
        try
        {
            await c.SetCustomCoverAsync(Id, dialog.FileName);
            _coverPath = null;
            _shell.Refresh();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or FileFormatException or UnauthorizedAccessException or ArgumentException)
        {
            _shell.AddEvent("Картинка", "Не удалось открыть картинку: " + ex.Message, EventKind.Error);
        }
    }

    [RelayCommand]
    private void RemoveCover()
    {
        _shell.Controller?.RemoveCustomCover(Id);
        _coverPath = null;
        _shell.Refresh();
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
    public IReadOnlyList<CatalogCardViewModel> Catalog { get; }
    private readonly HashSet<string> _coversRequested = new();
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
        Catalog = GameCatalog.Entries.Select(e => new CatalogCardViewModel(this, e)).ToList();
    }

    internal async Task AddFromCatalogAsync(CatalogCardViewModel card)
    {
        if (_shell.Controller is not { } c) return;
        var entry = card.Entry;
        // The exe that is running now, if the game is open (some games ship several).
        var running = entry.ProcessNames.FirstOrDefault(exe =>
        {
            var procs = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
            foreach (var p in procs) p.Dispose();
            return procs.Length > 0;
        });
        var profile = await c.AddCatalogGameAsync(entry, running);
        _shell.Refresh();
        if (profile is null) return;
        _shell.AddEvent(entry.Name, entry.NeedsRecording
            ? "Добавлена. Запустите игру и запишите один матч — так программа узнает её серверы."
            : "Добавлена и включена: адреса серверов получены.", EventKind.Success);
        if (entry.NeedsRecording) OpenLearning(Profiles.FirstOrDefault(p => p.Id == profile.Id));
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

        var added = profiles.Select(GameCatalog.For).Where(e => e is not null).Select(e => e!.Id).ToHashSet();
        foreach (var card in Catalog)
        {
            card.IsAdded = added.Contains(card.Entry.Id);
            card.Cover ??= CardArt.Load(AppController.CoverFor(null, card.Entry));
            if (card.Cover is null && card.Entry.SteamAppId is not null && _coversRequested.Add(card.Entry.Id)) _ = FetchCoverAsync(c, card);
        }

        // Recording stopped elsewhere (tray, or the controller gave up): reflect it here.
        if (IsRecording && !IsWorking && !c.IsLearning)
        {
            _statsTimer.Stop();
            IsRecording = false;
            UpdateStats();
        }
    }

    private async Task FetchCoverAsync(AppController c, CatalogCardViewModel card)
    {
        try
        {
            if (await c.EnsureCoverAsync(card.Entry) is { } path)
            {
                card.Cover = CardArt.Load(path);
                _shell.Refresh();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Cover for {card.Entry.Id} failed", ex);
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
                    using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 32 * 1024 * 1024 };
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
                    _learned.TcpEndpoints.Select(e => e.ToString()).Concat(existing?.ProbeEndpoints ?? new List<string>()),
                    _learned.Endpoints.Where(e => e.SeenUdp).Select(e => e.Address)),
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

    internal void LoadCatalogSample(string coversDir)
    {
        foreach (var card in Catalog)
        {
            card.IsAdded = card.Entry.Id is "wardogs" or "cs2";
            if (card.Entry.SteamAppId is { } app) card.Cover = CardArt.Load(Path.Combine(coversDir, $"steam-{app}.png"));
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

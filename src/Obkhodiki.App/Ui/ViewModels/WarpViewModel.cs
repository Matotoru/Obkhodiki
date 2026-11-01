using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Obkhodiki.Core.Vpn;

namespace Obkhodiki.App.Ui.ViewModels;

/// <summary>Cloudflare WARP: everything or chosen programs, sites and categories, like the VPS page.</summary>
public sealed partial class WarpViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private bool _syncing;

    public ObservableCollection<string> Programs { get; } = new();
    public ObservableCollection<string> BypassPrograms { get; } = new();
    public IReadOnlyList<CategoryOption> Categories { get; }
    public IReadOnlyList<CategoryOption> DirectCategories { get; }

    /// <summary>The list behind the program pickers is shared with the VPS page.</summary>
    public ObservableCollection<string> RunningPrograms => _shell.Vpn.RunningPrograms;

    public bool Installed => WarpClient.IsInstalled;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private bool _fullTunnel;
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _domainsText = "";
    [ObservableProperty] private string _bypassText = "";
    [ObservableProperty] private string _newProgram = "";
    [ObservableProperty] private string _newBypassProgram = "";
    [ObservableProperty] private string? _listsMessage;
    [ObservableProperty] private bool _listsDirty;

    public WarpViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Categories = RuleCatalog.Proxy.Select(c => new CategoryOption(c, () => ListsDirty = true)).ToList();
        DirectCategories = RuleCatalog.Direct.Select(c => new CategoryOption(c, () => ListsDirty = true)).ToList();
    }

    partial void OnEnabledChanged(bool value)
    {
        if (_syncing) return;
        _ = _shell.RunAsync(c => c.SetWarpEnabledAsync(value));
    }

    partial void OnFullTunnelChanged(bool value)
    {
        if (_syncing) return;
        _ = _shell.RunAsync(c => c.SetWarpFullTunnelAsync(value));
    }

    partial void OnDomainsTextChanged(string value)
    {
        if (!_syncing) ListsDirty = true;
    }

    partial void OnBypassTextChanged(string value)
    {
        if (!_syncing) ListsDirty = true;
    }

    internal void Refresh(AppController c)
    {
        IsConnected = c.IsWarpActive;
        IsBusy = c.BusyText is not null;
        _syncing = true;
        // While an operation runs the switch shows what the user asked for; the result arrives with the next refresh.
        if (!IsBusy)
        {
            Enabled = c.Settings.WarpEnabled;
            FullTunnel = c.Settings.WarpFullTunnel;
            if (!ListsDirty)
            {
                Sync(Categories, c.Settings.WarpCategories);
                Sync(DirectCategories, c.Settings.WarpDirectCategories);
                Replace(Programs, c.Settings.WarpProcesses);
                Replace(BypassPrograms, c.Settings.WarpBypassProcesses);
                DomainsText = string.Join(Environment.NewLine, c.Settings.WarpDomains);
                BypassText = string.Join(Environment.NewLine, c.Settings.WarpBypassEntries);
            }
        }
        _syncing = false;
    }

    private static void Sync(IEnumerable<CategoryOption> options, ICollection<string> selected)
    {
        foreach (var o in options)
        {
            o.Syncing = true;
            o.IsChecked = selected.Contains(o.Id);
            o.Syncing = false;
        }
    }

    private static void Replace(ObservableCollection<string> target, IEnumerable<string> items)
    {
        var list = items.ToList();
        if (target.SequenceEqual(list)) return;
        target.Clear();
        foreach (var i in list) target.Add(i);
    }

    private static List<string> Lines(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

    private void Add(ObservableCollection<string> target, string text, string example)
    {
        var name = text.Trim();
        if (name.Length == 0) return;
        if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
        if (!SingBoxConfig.IsValidProcessName(name))
        {
            ListsMessage = $"Укажите имя программы, например {example}.";
            return;
        }
        if (!target.Contains(name, StringComparer.OrdinalIgnoreCase)) target.Add(name);
        ListsMessage = null;
        ListsDirty = true;
    }

    [RelayCommand]
    private void AddProgram()
    {
        Add(Programs, NewProgram, "chrome.exe");
        if (ListsMessage is null) NewProgram = "";
    }

    [RelayCommand]
    private void RemoveProgram(string? name)
    {
        if (name is null) return;
        Programs.Remove(name);
        ListsDirty = true;
    }

    [RelayCommand]
    private void AddBypassProgram()
    {
        Add(BypassPrograms, NewBypassProgram, "steam.exe");
        if (ListsMessage is null) NewBypassProgram = "";
    }

    [RelayCommand]
    private void RemoveBypassProgram(string? name)
    {
        if (name is null) return;
        BypassPrograms.Remove(name);
        ListsDirty = true;
    }

    [RelayCommand]
    private async Task SaveListsAsync()
    {
        var domains = Lines(DomainsText);
        if (domains.FirstOrDefault(d => SingBoxConfig.NormalizeDomain(d) is null) is { } badDomain)
        {
            ListsMessage = "Не похоже на адрес сайта: " + badDomain;
            return;
        }
        var bypass = Lines(BypassText);
        if (bypass.FirstOrDefault(e => SingBoxConfig.NormalizeBypassEntry(e) is null) is { } badEntry)
        {
            ListsMessage = "Не похоже на сайт или адрес: " + badEntry;
            return;
        }
        ListsMessage = null;
        var categories = Categories.Where(o => o.IsChecked).Select(o => o.Id).ToList();
        var direct = DirectCategories.Where(o => o.IsChecked).Select(o => o.Id).ToList();
        var (programs, bypassPrograms) = (Programs.ToList(), BypassPrograms.ToList());
        // Cleared first: the refresh that follows the save has to load the normalized lists.
        ListsDirty = false;
        await _shell.RunAsync(c => c.SetWarpListsAsync(programs, domains, categories, direct, bypassPrograms, bypass));
    }

    /// <summary>UI preview only: WARP on with sample lists.</summary>
    internal void Preview(bool full)
    {
        _syncing = true;
        Enabled = true;
        FullTunnel = full;
        IsConnected = true;
        foreach (var o in Categories) o.IsChecked = o.Id == "ai";
        foreach (var o in DirectCategories) o.IsChecked = true;
        Replace(Programs, new[] { "chrome.exe" });
        Replace(BypassPrograms, new[] { "steam.exe" });
        DomainsText = "gemini.google.com";
        BypassText = "sberbank.ru";
        _syncing = false;
        ListsDirty = false;
    }
}

using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Obkhodiki.App.Ui.ViewModels;

public enum EventKind { Info, Success, Warning, Error }

public sealed record EventItem(DateTime Time, string Title, string Text, EventKind Kind)
{
    public string TimeText => Time.ToString("HH:mm");
}

/// <summary>
/// Root of all page view models. Pages bind to <see cref="Current"/>'s children; the controller pushes state in
/// through <see cref="Refresh"/> on the UI thread. <see cref="Controller"/> is null in design previews.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public static ShellViewModel Current { get; set; } = new();

    internal AppController? Controller { get; }

    public HomeViewModel Home { get; }
    public BypassViewModel Bypass { get; }
    public GamesViewModel Games { get; }
    public VpnViewModel Vpn { get; }
    public TelegramViewModel Telegram { get; }
    public SettingsViewModel Settings { get; }

    public ObservableCollection<EventItem> Events { get; } = new();

    public event Action<EventItem>? EventAdded;

    [ObservableProperty] private string? _busyText;

    public bool IsBusy => BusyText is not null;

    partial void OnBusyTextChanged(string? value) => OnPropertyChanged(nameof(IsBusy));

    public ShellViewModel() : this(null)
    {
    }

    internal ShellViewModel(AppController? controller)
    {
        Controller = controller;
        Home = new HomeViewModel(this);
        Bypass = new BypassViewModel(this);
        Games = new GamesViewModel(this);
        Vpn = new VpnViewModel(this);
        Telegram = new TelegramViewModel(this);
        Settings = new SettingsViewModel(this);
    }

    public void AddEvent(string title, string text, EventKind kind)
    {
        var item = new EventItem(DateTime.Now, title, text, kind);
        Events.Insert(0, item);
        EventAdded?.Invoke(item);
        while (Events.Count > 50) Events.RemoveAt(Events.Count - 1);
    }

    /// <summary>Pulls the controller's current state into every page (UI thread only).</summary>
    public void Refresh()
    {
        if (Controller is not { } c) return;
        BusyText = c.BusyText;
        Home.Refresh(c);
        Bypass.Refresh(c);
        Games.Refresh(c);
        Vpn.Refresh(c);
        Telegram.Refresh(c);
        Settings.Refresh(c);
    }

    /// <summary>Runs a controller action; commands stay responsive and errors surface as events.</summary>
    internal async Task RunAsync(Func<AppController, Task> action)
    {
        if (Controller is null) return;
        try
        {
            await action(Controller);
        }
        catch (Exception ex)
        {
            Log.Error("UI action failed", ex);
            AddEvent("Ошибка", ex.Message, EventKind.Error);
        }
        finally
        {
            Refresh();
        }
    }

    [RelayCommand]
    private static void OpenLog() => ShellActions.OpenInNotepad(AppPaths.Log);

    [RelayCommand]
    private static void OpenUserFolder() => ShellActions.OpenFolder(AppPaths.UserData);

    internal static Window? MainWindow => Application.Current?.MainWindow;
}

/// <summary>Opening files and folders without ever handing them to elevated shell associations.</summary>
internal static class ShellActions
{
    // Explicit notepad instead of ShellExecute: file associations live in HKCU, which a non-admin process
    // could point at its own program to run it elevated.
    public static void OpenInNotepad(string path)
    {
        if (!File.Exists(path)) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.SystemTool("notepad.exe"))
        {
            ArgumentList = { path },
            UseShellExecute = false,
        });
    }

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.WindowsTool("explorer.exe"))
        {
            ArgumentList = { path },
            UseShellExecute = false,
        });
    }
}

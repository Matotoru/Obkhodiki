using System.ComponentModel;
using Obkhodiki.App.Ui.Pages;
using Obkhodiki.App.Ui.ViewModels;

namespace Obkhodiki.App.Ui;

public partial class MainWindow
{
    private bool _exiting;

    public MainWindow()
    {
        DataContext = ShellViewModel.Current;
        var settings = ShellViewModel.Current.Controller?.Settings;
        ThemeService.Apply(this, settings?.AppTheme, settings?.AppPalette);
        InitializeComponent();
        var version = SelfUpdate.CurrentVersion;
        Title = $"{Obkhodiki.Core.Updates.AppInfo.Name} {version}";
        AppTitleBar.Title = Title;
        VersionText.Text = "v" + version;
        Loaded += (_, _) => RootNavigation.Navigate(typeof(HomePage));
        HomeViewModel.NavigationRequested += Navigate;
        ShellViewModel.Current.EventAdded += ShowSnackbar;
    }

    private void ShowSnackbar(EventItem item)
    {
        if (!IsVisible) return;
        // The home page lists events itself; elsewhere only problems need to interrupt.
        var onHome = RootNavigation.SelectedItem?.TargetPageType == typeof(HomePage);
        if (onHome && item.Kind is not (EventKind.Warning or EventKind.Error)) return;
        _ = new Wpf.Ui.Controls.Snackbar(Snackbars)
        {
            Title = item.Title,
            Content = item.Text,
            Appearance = item.Kind switch
            {
                EventKind.Error => Wpf.Ui.Controls.ControlAppearance.Danger,
                EventKind.Warning => Wpf.Ui.Controls.ControlAppearance.Caution,
                EventKind.Success => Wpf.Ui.Controls.ControlAppearance.Success,
                _ => Wpf.Ui.Controls.ControlAppearance.Secondary,
            },
            Timeout = TimeSpan.FromSeconds(item.Kind == EventKind.Error ? 8 : 5),
        }.ShowAsync();
    }

    public void Navigate(string page)
    {
        var type = page switch
        {
            "Bypass" => typeof(BypassPage),
            "Games" => typeof(GamesPage),
            "Vpn" => typeof(VpnPage),
            "Telegram" => typeof(TelegramPage),
            "Settings" => typeof(SettingsPage),
            _ => typeof(HomePage),
        };
        RootNavigation.Navigate(type);
    }

    private void OnNavigated(Wpf.Ui.Controls.NavigationView sender, Wpf.Ui.Controls.NavigatedEventArgs args) =>
        PageTitle.Text = (args.Page as System.Windows.Controls.Page)?.Title;

    // Opened as the desktop user: a browser started from this elevated process would run as admin.
    private async void OnAuthorClick(object sender, System.Windows.RoutedEventArgs e)
    {
        try
        {
            if (!await Task.Run(() => TgProxyRunner.OpenAsUser(Obkhodiki.Core.Updates.AppInfo.AuthorUrl)))
            {
                ShellViewModel.Current.AddEvent("Ссылка", "Не найден рабочий стол Windows, ссылку открыть нельзя.", EventKind.Warning);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Author link could not be opened", ex);
            ShellViewModel.Current.AddEvent("Ссылка", ex.Message, EventKind.Warning);
        }
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == System.Windows.WindowState.Minimized) WindowState = System.Windows.WindowState.Normal;
        Activate();
    }

    /// <summary>Closes for real (app exit) instead of hiding to the tray.</summary>
    public void CloseForExit()
    {
        _exiting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The window is just a view: closing it keeps bypass running in the tray.
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }
}

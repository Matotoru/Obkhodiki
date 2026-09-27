using Obkhodiki.App.Ui.ViewModels;

namespace Obkhodiki.App.Ui.Pages;

// Pages are created by the NavigationView with their default constructor; all state lives in ShellViewModel.

public partial class HomePage
{
    public HomePage()
    {
        DataContext = ShellViewModel.Current;
        InitializeComponent();
    }
}

public partial class BypassPage
{
    public BypassPage()
    {
        DataContext = ShellViewModel.Current.Bypass;
        InitializeComponent();
    }
}

public partial class GamesPage
{
    public GamesPage()
    {
        DataContext = ShellViewModel.Current.Games;
        InitializeComponent();
    }
}

public partial class VpnPage
{
    public VpnPage()
    {
        DataContext = ShellViewModel.Current.Vpn;
        InitializeComponent();
        Unloaded += (_, _) => ShellViewModel.Current.Vpn.ForgetServerLink();
    }

    private void OnProgramsDropDown(object sender, EventArgs e) => ShellViewModel.Current.Vpn.RefreshRunningProgramsCommand.Execute(null);
}

public partial class TelegramPage
{
    public TelegramPage()
    {
        DataContext = ShellViewModel.Current.Telegram;
        InitializeComponent();
    }
}

public partial class SettingsPage
{
    public SettingsPage()
    {
        DataContext = ShellViewModel.Current.Settings;
        InitializeComponent();
    }
}

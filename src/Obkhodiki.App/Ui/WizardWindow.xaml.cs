using Obkhodiki.App.Ui.ViewModels;

namespace Obkhodiki.App.Ui;

public partial class WizardWindow
{
    public WizardWindow(ShellViewModel shell)
    {
        var vm = new WizardViewModel(shell);
        DataContext = vm;
        InitializeComponent();
        vm.CloseRequested += Close;
    }

    /// <summary>Shows the guide over the main window (modal: the pages behind update as the steps are done).</summary>
    public static void ShowFor(System.Windows.Window owner, ShellViewModel shell)
    {
        var wizard = new WizardWindow(shell) { Owner = owner };
        wizard.ShowDialog();
        shell.Refresh();
    }
}

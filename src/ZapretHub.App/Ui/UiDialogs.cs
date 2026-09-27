using System.Windows;
using WpfUiMessageBox = Wpf.Ui.Controls.MessageBox;
using WpfUiMessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace ZapretHub.App.Ui;

/// <summary>Fluent-styled dialogs inside the app window; fall back to the topmost tray dialog when it is hidden.</summary>
internal static class UiDialogs
{
    // Only when our window is in front: a dialog owned by a window behind a fullscreen game would go unnoticed
    // while the controller waits on it. Otherwise the topmost fallback is used.
    private static bool WindowVisible => Application.Current?.MainWindow is { IsActive: true, WindowState: not WindowState.Minimized };

    public static async Task<bool> ConfirmAsync(string title, string text, string primary = "Да", string close = "Отмена")
    {
        if (!WindowVisible) return Dialogs.Confirm(text, "ZapretHub — " + title, System.Windows.Forms.MessageBoxIcon.Question);
        var box = new WpfUiMessageBox
        {
            Title = title,
            Content = text,
            PrimaryButtonText = primary,
            CloseButtonText = close,
            MaxWidth = 520,
        };
        return await box.ShowDialogAsync() == WpfUiMessageBoxResult.Primary;
    }

    public static async Task InfoAsync(string title, string text)
    {
        if (!WindowVisible)
        {
            System.Windows.Forms.MessageBox.Show(text, "ZapretHub — " + title);
            return;
        }
        var box = new WpfUiMessageBox { Title = title, Content = text, CloseButtonText = "Понятно", MaxWidth = 520 };
        await box.ShowDialogAsync();
    }

    /// <summary>Synchronous confirmation for controller callbacks (they expect a bool right away).</summary>
    public static bool Confirm(string title, string text) =>
        WindowVisible
            ? MessageBox.Show(Application.Current.MainWindow!, text, "ZapretHub — " + title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes
            : Dialogs.Confirm(text, "ZapretHub — " + title, System.Windows.Forms.MessageBoxIcon.Question);
}

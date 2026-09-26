namespace ZapretHub.App;

internal static class Dialogs
{
    /// <summary>
    /// A tray app has no window of its own, so an ownerless MessageBox can open behind the game or browser.
    /// A hidden topmost owner keeps confirmations in front.
    /// </summary>
    public static bool Confirm(string text, string caption, MessageBoxIcon icon)
    {
        using var owner = new Form
        {
            TopMost = true,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1),
        };
        owner.Show();
        return MessageBox.Show(owner, text, caption, MessageBoxButtons.YesNo, icon) == DialogResult.Yes;
    }
}

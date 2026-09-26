namespace ZapretHub.App;

static class Program
{
    [STAThread]
    static void Main()
    {
        // Local (per-session) name: a Global name could be pre-created by any user to block startup.
        Mutex mutex;
        bool isFirst;
        try
        {
            mutex = new Mutex(initiallyOwned: true, @"Local\ZapretHub.SingleInstance", out isFirst);
        }
        catch (UnauthorizedAccessException)
        {
            MessageBox.Show("Не удалось проверить, запущен ли уже ZapretHub.", "ZapretHub", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        using var _ = mutex;
        if (!isFirst)
        {
            MessageBox.Show("ZapretHub уже запущен — значок в трее.", "ZapretHub", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        ApplicationConfiguration.Initialize();

        try
        {
            SecureStorage.Prepare();
        }
        catch (Exception ex)
        {
            // Running from an unprotected data folder would let other programs plant code we execute as admin.
            MessageBox.Show("Не удалось подготовить защищённую папку данных:\n" + ex.Message, "ZapretHub",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error("Unobserved task exception", e.Exception);
        Log.Info("ZapretHub started");

        try
        {
            Application.Run(new TrayContext());
        }
        catch (Exception ex)
        {
            Log.Error("Fatal error", ex);
            MessageBox.Show(ex.Message, "ZapretHub", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

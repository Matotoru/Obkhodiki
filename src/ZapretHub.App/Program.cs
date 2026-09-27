using System.Windows.Forms;

namespace ZapretHub.App;

static class Program
{
    // Posted by a second launch so the running instance brings its window forward instead of a dead-end message.
    private static readonly uint ActivateMessage = RegisterWindowMessage("ZapretHub.Activate");
    private const string ActivationWindowTitle = "ZapretHub.Activation";

    [STAThread]
    static void Main(string[] args)
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
            var target = FindWindow(null, ActivationWindowTitle);
            if (target == IntPtr.Zero)
            {
                // A running version without the activation window (older build, or still starting up).
                MessageBox.Show("ZapretHub уже запущен — значок в трее.", "ZapretHub", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            // Let the running instance take the foreground; Windows refuses it to background processes otherwise.
            AllowSetForegroundWindow(AsfwAny);
            PostMessage(target, ActivateMessage, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        // The tray menu is WinForms: its visual styles and DPI mode must be set before any of it is created.
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

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error("Unobserved task exception", e.Exception);
        Application.ThreadException += (_, e) => Log.Error("Unhandled tray exception", e.Exception);
        Log.Info("ZapretHub started");

        try
        {
            var app = new Ui.App();
            app.InitializeComponent();
            // A failing click handler must not take down the process that holds winws, the tunnel and the proxy.
            app.DispatcherUnhandledException += (_, e) =>
            {
                Log.Error("Unhandled UI exception", e.Exception);
                e.Handled = true;
            };

            using var host = new TrayHost(startHidden: args.Contains(Autostart.TrayArgument, StringComparer.OrdinalIgnoreCase));
            host.ExitRequested += () =>
            {
                host.Dispose();
                app.Shutdown();
            };
            // A hidden window of its own: its WndProc runs even inside modal loops (message boxes, the tray menu),
            // and a window exists to find even when the main one was never shown (--tray).
            using var activation = new System.Windows.Interop.HwndSource(new System.Windows.Interop.HwndSourceParameters(ActivationWindowTitle)
            {
                WindowStyle = 0,
                Width = 0,
                Height = 0,
            });
            activation.AddHook((IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg != (int)ActivateMessage) return IntPtr.Zero;
                host.ShowWindow();
                handled = true;
                return IntPtr.Zero;
            });
            app.Run();
        }
        catch (Exception ex)
        {
            Log.Error("Fatal error", ex);
            MessageBox.Show(ex.Message, "ZapretHub", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string windowName);
    private const int AsfwAny = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}

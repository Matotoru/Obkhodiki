using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Obkhodiki.Core.Updates;
using Obkhodiki.App.Ui;
using Obkhodiki.App.Ui.ViewModels;
using Forms = System.Windows.Forms;

namespace Obkhodiki.App;

/// <summary>
/// Owns the controller, the main window and a minimal tray icon. Everything beyond on/off lives in the window;
/// the tray only reflects state and brings the window back.
/// </summary>
internal sealed class TrayHost : IDisposable
{
    private readonly AppController _controller = new();
    private readonly Dispatcher _ui = Dispatcher.CurrentDispatcher;
    private readonly ShellViewModel _shell;
    private readonly MainWindow _window;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ContextMenuStrip _menu = new();
    private readonly Font _boldFont;
    private readonly Icon _iconOn = MakeIcon(Color.FromArgb(46, 160, 67));
    private readonly Icon _iconOff = MakeIcon(Color.FromArgb(128, 128, 128));
    private readonly Icon _iconBusy = MakeIcon(Color.FromArgb(210, 153, 34));
    private string? _updateBalloonProduct;
    private string? _pendingUpdateProduct;
    private bool _disposed;

    public event Action? ExitRequested;

    private readonly bool _upgraded;

    public TrayHost(bool startHidden, bool upgraded)
    {
        // Pages bind to ShellViewModel.Current when the window creates them, so it must be set first.
        _upgraded = upgraded;
        _shell = new ShellViewModel(_controller);
        ShellViewModel.Current = _shell;
        _window = new MainWindow();
        System.Windows.Application.Current.MainWindow = _window;

        _boldFont = new Font(_menu.Font, FontStyle.Bold);
        _tray = new Forms.NotifyIcon { Icon = _iconOff, Visible = true, Text = "Obkhodiki", ContextMenuStrip = _menu };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _window.ShowAndActivate();
        };
        _menu.Opening += (_, _) =>
        {
            RebuildMenu();
            _ = _controller.RefreshTgStateAsync();
        };

        // Controller events can fire on worker threads (process exit, watchdogs): always hop to the UI thread.
        _controller.StateChanged += () => _ui.BeginInvoke(OnStateChanged);
        // Both handlers are queued in order, so the product is known before its notification shows.
        _controller.UpdateAnnounced += product => _ui.BeginInvoke(() => _pendingUpdateProduct = product);
        _controller.Notify += (title, text, icon) => _ui.BeginInvoke(() => OnNotify(title, text, icon));
        _tray.BalloonTipClicked += async (_, _) =>
        {
            var product = _updateBalloonProduct;
            _updateBalloonProduct = null;
            if (product is null)
            {
                _window.ShowAndActivate();
                return;
            }
            await InstallUpdateAsync(product);
        };
        _tray.BalloonTipClosed += (_, _) => _updateBalloonProduct = null;

        _controller.ConfirmVpnRisk = text => Confirm("VPS", text);
        _controller.ExitForUpdateRequested += () => _ui.BeginInvoke(() => ExitRequested?.Invoke());
        _controller.ConfirmStopForeignTg = () => Confirm("Telegram",
            "Уже запущен отдельно установленный TG WS Proxy. Он занимает тот же порт, что и прокси Obkhodiki.\n\n" +
            "Закрыть его и запустить прокси из Obkhodiki?");
        _controller.ConfirmUpdate = (product, version, current) => Confirm($"обновление {product}",
            $"Доступна новая версия {product}: {version}" +
            (current is null ? "" : $" (установлена {current})") +
            ".\n\nСкачать и установить? Файлы проверяются по контрольной сумме с GitHub." +
            "\nЕсли новая версия не запустится, вернётся прежняя.");
        _controller.ConfirmStopConflicts = description => Confirm("другой обход",
            "Уже запущен другой обход блокировок (он использует тот же драйвер WinDivert):\n\n" + description +
            "\n\nОстановить его и продолжить?");

        // The proxy has its own tray icon and can be quit there: re-check whenever the user looks at the window.
        _window.Activated += (_, _) => _ = _controller.RefreshTgStateAsync();
        _window.IsVisibleChanged += (_, e) =>
        {
            if (!(bool)e.NewValue) _shell.Vpn.ForgetServerLink();
        };

        OnStateChanged();
        if (!startHidden) _window.Show();
        _ = SelfUpdate.CleanupLaterAsync();
        ScheduleWhatsNew();
        _ = RefreshAutostartAsync();
        _ = _controller.InitializeAsync();
    }

    public void ShowWindow() => _window.ShowAndActivate();

    /// <summary>
    /// After an update, lists what changed — once, when the window is first on screen (a --tray start waits
    /// until the user opens it). A fresh install shows nothing.
    /// </summary>
    private void ScheduleWhatsNew()
    {
        var settings = _controller.Settings;
        var current = SelfUpdate.CurrentVersion;
        if (settings.LastSeenVersion == current) return;
        // Versions before 0.4 did not record this; an existing settings file means an update from one of them.
        var usedBefore = settings.LastSeenVersion is not null || _upgraded;
        var entries = usedBefore ? Changelog.Since(settings.LastSeenVersion ?? "0.3.1", current) : Array.Empty<Changelog.Entry>();

        async void Show()
        {
            // Recorded first: closing the app with the dialog open must not show it again next time.
            await _controller.MarkVersionSeenAsync();
            try
            {
                // A fresh install gets the guide instead of a list of changes it never saw before.
                if (!usedBefore) WizardWindow.ShowFor(_window, _shell);
                else await WhatsNewDialog.ShowAsync(entries);
            }
            catch (Exception ex)
            {
                Log.Error("What's new dialog failed", ex);
            }
        }

        if (_window.IsVisible)
        {
            _window.Dispatcher.BeginInvoke(Show, DispatcherPriority.ApplicationIdle);
            return;
        }
        System.Windows.DependencyPropertyChangedEventHandler? handler = null;
        handler = (_, e) =>
        {
            if (!(bool)e.NewValue) return;
            _window.IsVisibleChanged -= handler;
            _window.Dispatcher.BeginInvoke(Show, DispatcherPriority.ApplicationIdle);
        };
        _window.IsVisibleChanged += handler;
    }

    private async Task RefreshAutostartAsync()
    {
        try
        {
            // The old ZapretHub task would start the old copy at logon next to this app.
            if (await Task.Run(Autostart.MigrateLegacy))
            {
                _shell.AddEvent("Автозапуск", "Автозапуск перенесён со старого ZapretHub на Obkhodiki.", EventKind.Info);
            }
            // An older copy in Program Files would keep starting at logon instead of this version.
            else if (await Task.Run(Autostart.RefreshIfOutdated))
            {
                _shell.AddEvent("Автозапуск", "Копия для автозапуска обновлена до этой версии.", EventKind.Info);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Autostart refresh failed", ex);
            _shell.AddEvent("Автозапуск", "Не удалось обновить копию для автозапуска: " + ex.Message, EventKind.Warning);
        }
        await _shell.Settings.RefreshAutostartAsync();
    }

    private bool Confirm(string title, string text) =>
        _ui.CheckAccess() ? UiDialogs.Confirm(title, text) : _ui.Invoke(() => UiDialogs.Confirm(title, text));

    private void OnStateChanged()
    {
        if (_disposed) return;
        var busy = _controller.BusyText;
        _tray.Icon = busy is not null ? _iconBusy : _controller.IsRunning ? _iconOn : _iconOff;
        var status = busy ?? (_controller.IsRunning ? $"Включён: {_controller.ActiveStrategyName}" : "Выключен");
        // NotifyIcon.Text is limited to 127 characters.
        var text = "Obkhodiki — " + status;
        _tray.Text = text.Length > 127 ? text[..127] : text;
        _shell.Refresh();
    }

    private void OnNotify(string title, string text, Forms.ToolTipIcon icon)
    {
        if (_disposed) return;
        var updateProduct = _pendingUpdateProduct;
        _pendingUpdateProduct = null;

        _shell.AddEvent(title, text, icon switch
        {
            Forms.ToolTipIcon.Error => EventKind.Error,
            Forms.ToolTipIcon.Warning => EventKind.Warning,
            _ when updateProduct is not null => EventKind.Warning,
            _ => EventKind.Info,
        });

        // With the window in front the event list is enough; a balloon on top would just be noise.
        if (_window.IsActive) return;
        // Remember which balloon is on screen: only an "update available" one installs on click.
        _updateBalloonProduct = updateProduct;
        _tray.ShowBalloonTip(5000, title, text, icon);
    }

    private Task InstallUpdateAsync(string product) => product switch
    {
        "Flowseal" => _controller.InstallAvailableUpdateAsync(),
        AppController.TgProductName => _controller.InstallAvailableTgUpdateAsync(),
        AppController.SingBoxProductName => _controller.InstallAvailableSbUpdateAsync(),
        AppController.AppProductName => _controller.InstallAvailableAppUpdateAsync(),
        _ => Task.CompletedTask,
    };

    private void RebuildMenu()
    {
        foreach (var item in _menu.Items.Cast<Forms.ToolStripItem>().ToList()) item.Dispose();
        _menu.Items.Clear();

        var busy = _controller.BusyText is not null;
        var ready = !busy && _controller.Engine is not null;

        _menu.Items.Add(new Forms.ToolStripMenuItem(_controller.BusyText
            ?? (_controller.IsRunning ? $"● Включён — {_controller.ActiveStrategyName}" : "○ Выключен")) { Enabled = false });
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(new Forms.ToolStripMenuItem("Открыть Obkhodiki", null, (_, _) => _window.ShowAndActivate()) { Font = _boldFont });
        _menu.Items.Add(new Forms.ToolStripMenuItem(_controller.IsRunning ? "Выключить обход" : "Включить обход", null, async (_, _) =>
        {
            if (_controller.IsRunning) await _controller.DisableAsync();
            else await _controller.EnableAsync();
        })
        {
            Enabled = ready,
        });
        if (_controller.VpnServerName is not null)
        {
            var enabled = _controller.Settings.VpnEnabled;
            var full = _controller.Settings.VpnFullTunnel;
            _menu.Items.Add(new Forms.ToolStripMenuItem(enabled ? "Выключить VPS" : "Включить VPS", null, async (_, _) => await _controller.SetVpnEnabledAsync(!enabled))
            {
                Enabled = _controller.BusyText is null,
            });
            _menu.Items.Add(new Forms.ToolStripMenuItem("VPS: весь трафик", null, async (_, _) => await _controller.SetVpnFullTunnelAsync(!full))
            {
                Checked = enabled && full,
                Enabled = _controller.BusyText is null,
            });
        }
        if (_shell.Bypass.IsSelecting)
        {
            _menu.Items.Add(new Forms.ToolStripMenuItem("■ Остановить подбор стратегии", null, (_, _) => _shell.Bypass.CancelAutoSelect()));
        }
        if (_controller.IsLearning)
        {
            // Safety net: recording (and its temporary "all addresses" rule) must always be stoppable.
            _menu.Items.Add(new Forms.ToolStripMenuItem("■ Остановить запись игры", null, async (_, _) =>
            {
                await _shell.Games.StopRecordingAsync();
            }));
        }
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Выход", null, (_, _) => ExitRequested?.Invoke());
    }

    // The mascot with a status dot in the corner.
    private static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);
            using (var stream = typeof(TrayHost).Assembly.GetManifestResourceStream("Obkhodiki.cat.png"))
            {
                if (stream is not null)
                {
                    using var cat = Image.FromStream(stream);
                    g.DrawImage(cat, 0, 0, 32, 32);
                }
            }
            using var ring = new SolidBrush(Color.White);
            g.FillEllipse(ring, 17, 17, 15, 15);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 19, 19, 11, 11);
        }
        var handle = bmp.GetHicon();
        try
        {
            // Icon.FromHandle does not own the handle: clone, then free the original.
            using var borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _tray.Visible = false;
        try
        {
            _window.CloseForExit();
        }
        catch (Exception ex)
        {
            Log.Error("Closing the window failed", ex);
        }
        try
        {
            _controller.Dispose();
        }
        catch (Exception ex)
        {
            // Exit must always complete, otherwise an invisible process keeps the single-instance mutex.
            Log.Error("Shutdown cleanup failed", ex);
        }
        _tray.Dispose();
        _menu.Dispose();
        _boldFont.Dispose();
        _iconOn.Dispose();
        _iconOff.Dispose();
        _iconBusy.Dispose();
    }
}

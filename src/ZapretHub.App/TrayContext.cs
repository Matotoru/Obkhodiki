using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ZapretHub.Core.Games;
using ZapretHub.Core.Strategies;

namespace ZapretHub.App;

internal sealed class TrayContext : ApplicationContext
{
    private readonly AppController _controller = new();
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly Font _boldFont;
    private readonly Icon _iconOn = MakeIcon(Color.FromArgb(46, 160, 67));
    private readonly Icon _iconOff = MakeIcon(Color.FromArgb(128, 128, 128));
    private readonly Icon _iconBusy = MakeIcon(Color.FromArgb(210, 153, 34));
    private readonly SynchronizationContext _ui;
    private AutoSelectForm? _autoSelectForm;
    private LearnGameForm? _learnForm;
    private bool _updateBalloonShown;
    private bool _autostartEnabled;

    public TrayContext()
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _boldFont = new Font(_menu.Font, FontStyle.Bold);
        _tray = new NotifyIcon { Icon = _iconOff, Visible = true, Text = "ZapretHub", ContextMenuStrip = _menu };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ToggleAsync();
        };
        _menu.Opening += (_, _) => RebuildMenu();

        _controller.StateChanged += () => _ui.Post(_ => RefreshIcon(), null);
        _controller.Notify += (title, text, icon) => _ui.Post(_ =>
        {
            // Remember which balloon is on screen: only the "update available" one installs on click.
            _updateBalloonShown = _controller.AvailableUpdate is not null && title.StartsWith("Доступно обновление", StringComparison.Ordinal);
            _tray.ShowBalloonTip(5000, title, text, icon);
        }, null);
        _tray.BalloonTipClicked += async (_, _) =>
        {
            if (!_updateBalloonShown) return;
            _updateBalloonShown = false;
            await _controller.InstallAvailableUpdateAsync();
        };
        _tray.BalloonTipClosed += (_, _) => _updateBalloonShown = false;
        _controller.ConfirmUpdate = (version, current) => Dialogs.Confirm(
            $"Доступна новая версия стратегий Flowseal: {version}" +
            (current is null ? "" : $" (установлена {current})") +
            ".\n\nСкачать и установить? Файлы проверяются по контрольной сумме с GitHub." +
            "\nЕсли новая версия не запустится, вернётся прежняя.",
            "ZapretHub — обновление", MessageBoxIcon.Question);
        _controller.ConfirmStopConflicts = description => Dialogs.Confirm(
            "Уже запущен другой обход блокировок (он использует тот же драйвер WinDivert):\n\n" + description +
            "\n\nОстановить его и продолжить?",
            "ZapretHub", MessageBoxIcon.Warning);

        RefreshIcon();
        _ = RefreshAutostartStateAsync();
        _ = _controller.InitializeAsync();
    }

    private void RefreshIcon()
    {
        var busy = _controller.BusyText;
        _tray.Icon = busy is not null ? _iconBusy : _controller.IsRunning ? _iconOn : _iconOff;
        var status = busy ?? (_controller.IsRunning ? $"Включён: {_controller.ActiveStrategyName}" : "Выключен");
        // NotifyIcon.Text is limited to 127 characters.
        var text = "ZapretHub — " + status;
        _tray.Text = text.Length > 127 ? text[..127] : text;
    }

    private void RebuildMenu()
    {
        foreach (var item in _menu.Items.Cast<ToolStripItem>().ToList()) item.Dispose();
        _menu.Items.Clear();

        var busy = _controller.BusyText is not null;
        var engine = _controller.Engine;
        var ready = !busy && engine is not null;

        _menu.Items.Add(new ToolStripMenuItem(_controller.BusyText
            ?? (_controller.IsRunning ? $"● Включён — {_controller.ActiveStrategyName}" : "○ Выключен")) { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        if (_controller.AvailableUpdate is { } update)
        {
            _menu.Items.Add(new ToolStripMenuItem($"⬆ Установить Flowseal {update.Version}…", null,
                async (_, _) => await _controller.InstallAvailableUpdateAsync())
            {
                Enabled = !busy,
                Font = _boldFont,
            });
            _menu.Items.Add(new ToolStripSeparator());
        }

        _menu.Items.Add(new ToolStripMenuItem(_controller.IsRunning ? "Выключить" : "Включить", null, (_, _) => ToggleAsync())
        {
            Enabled = ready,
            Font = _boldFont,
        });

        var strategies = new ToolStripMenuItem("Стратегия") { Enabled = ready };
        foreach (var s in engine?.Strategies ?? Array.Empty<StrategyDefinition>())
        {
            var name = s.Name;
            strategies.DropDownItems.Add(new ToolStripMenuItem(name, null, async (_, _) => await _controller.SelectStrategyAsync(name))
            {
                Checked = name == _controller.ActiveStrategyName,
            });
        }
        _menu.Items.Add(strategies);

        _menu.Items.Add(new ToolStripMenuItem("Подобрать стратегию автоматически…", null, (_, _) => OpenAutoSelect())
        {
            Enabled = ready || _autoSelectForm is not null,
        });

        _menu.Items.Add(BuildGamesMenu(ready));

        var game = new ToolStripMenuItem("Игровой фильтр") { Enabled = ready };
        foreach (var (mode, title) in new[]
                 {
                     (GameFilterMode.Disabled, "Выключен"),
                     (GameFilterMode.Tcp, "TCP"),
                     (GameFilterMode.Udp, "UDP"),
                     (GameFilterMode.All, "TCP и UDP"),
                 })
        {
            game.DropDownItems.Add(new ToolStripMenuItem(title, null, async (_, _) => await _controller.SetGameFilterAsync(mode))
            {
                Checked = _controller.Settings.GameFilter == mode,
            });
        }
        _menu.Items.Add(game);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem(
            $"Проверить обновления Flowseal (сейчас {engine?.Version ?? "нет"})", null,
            async (_, _) => await _controller.CheckForUpdateAsync(userInitiated: true))
        {
            Enabled = !busy,
        });

        _menu.Items.Add(new ToolStripMenuItem("Запускать вместе с Windows", null, (_, _) => ToggleAutostartAsync())
        {
            Checked = _autostartEnabled,
        });

        _menu.Items.Add("Открыть мои списки и цели", null, (_, _) => OpenFolder(AppPaths.UserData));
        _menu.Items.Add("Открыть лог", null, (_, _) => OpenLog());

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Выход", null, (_, _) => ExitThread());
    }

    private async void ToggleAsync()
    {
        if (_controller.BusyText is not null || _controller.Engine is null) return;
        if (_controller.IsRunning) await _controller.DisableAsync();
        else await _controller.EnableAsync();
    }

    private void OpenAutoSelect()
    {
        if (_autoSelectForm is not null)
        {
            _autoSelectForm.Activate();
            return;
        }
        _autoSelectForm = new AutoSelectForm(_controller);
        _autoSelectForm.FormClosed += (_, _) =>
        {
            _autoSelectForm.Dispose();
            _autoSelectForm = null;
        };
        _autoSelectForm.Show();
    }

    private ToolStripMenuItem BuildGamesMenu(bool ready)
    {
        var games = new ToolStripMenuItem("Игры") { Enabled = ready || _learnForm is not null };
        foreach (var profile in _controller.Settings.GameProfiles)
        {
            var id = profile.Id;
            var item = new ToolStripMenuItem(profile.Name) { Checked = profile.Enabled };
            item.DropDownItems.Add(new ToolStripMenuItem(profile.Enabled ? "Выключить" : "Включить", null,
                async (_, _) => await _controller.SetGameProfileEnabledAsync(id, !profile.Enabled)) { Font = _boldFont });
            item.DropDownItems.Add("Дообучить (записать ещё)…", null, (_, _) => OpenLearn(profile));
            item.DropDownItems.Add("Открыть список адресов", null, (_, _) => OpenInNotepad(AppController.GameIpsetPath(id)));
            item.DropDownItems.Add(new ToolStripSeparator());
            item.DropDownItems.Add("Удалить", null, async (_, _) =>
            {
                if (MessageBox.Show($"Удалить профиль «{profile.Name}» и его список адресов?", "ZapretHub",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    await _controller.DeleteGameProfileAsync(id);
                }
            });
            games.DropDownItems.Add(item);
        }
        if (games.DropDownItems.Count > 0) games.DropDownItems.Add(new ToolStripSeparator());
        games.DropDownItems.Add("Добавить игру…", null, (_, _) => OpenLearn(null));
        if (_controller.IsLearning && _learnForm is null)
        {
            // Safety net: recording (and its temporary "all addresses" rule) must always be stoppable.
            games.DropDownItems.Add(new ToolStripMenuItem("■ Остановить запись игры", null,
                async (_, _) => await _controller.StopLearningAsync()) { Font = _boldFont });
        }
        return games;
    }

    private void OpenLearn(GameProfile? profile)
    {
        if (_learnForm is not null)
        {
            _learnForm.Activate();
            return;
        }
        _learnForm = new LearnGameForm(_controller, profile);
        _learnForm.FormClosed += (_, _) =>
        {
            _learnForm.Dispose();
            _learnForm = null;
        };
        _learnForm.Show();
    }

    private async Task RefreshAutostartStateAsync()
    {
        try
        {
            _autostartEnabled = await Task.Run(Autostart.IsEnabled);
        }
        catch (Exception ex)
        {
            Log.Error("Autostart query failed", ex);
        }
    }

    private async void ToggleAutostartAsync()
    {
        var enable = !_autostartEnabled;
        try
        {
            var copied = await Task.Run(() =>
            {
                if (enable) return Autostart.Enable();
                Autostart.Disable();
                return false;
            });
            _autostartEnabled = enable;
            if (copied)
            {
                _tray.ShowBalloonTip(5000, "Автозапуск включён",
                    $"Приложение скопировано в {AppPaths.InstallDir}; при входе в Windows будет запускаться эта копия.", ToolTipIcon.Info);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Autostart change failed", ex);
            _tray.ShowBalloonTip(5000, "Автозапуск", ex.Message, ToolTipIcon.Error);
            await RefreshAutostartStateAsync();
        }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(AppPaths.WindowsTool("explorer.exe")) { ArgumentList = { path }, UseShellExecute = false });
    }

    private static void OpenLog() => OpenInNotepad(AppPaths.Log);

    // Explicit notepad instead of ShellExecute: file associations live in HKCU,
    // which a non-admin process could point at its own program to run it elevated.
    private static void OpenInNotepad(string path)
    {
        if (!File.Exists(path)) return;
        Process.Start(new ProcessStartInfo(AppPaths.SystemTool("notepad.exe")) { ArgumentList = { path }, UseShellExecute = false });
    }

    private static Icon MakeIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 2, 28, 28);
            using var pen = new Pen(Color.White, 3);
            g.DrawArc(pen, 9, 9, 14, 14, 300, 300);
            g.DrawLine(pen, 16, 6, 16, 16);
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

    protected override void ExitThreadCore()
    {
        _autoSelectForm?.Dispose();
        _learnForm?.Dispose();
        _tray.Visible = false;
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
        base.ExitThreadCore();
    }
}

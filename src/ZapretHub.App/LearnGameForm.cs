using System.Diagnostics;
using ZapretHub.Core.Games;

namespace ZapretHub.App;

/// <summary>
/// Creates or relearns a game profile: pick the game's process, record its connections while playing,
/// then save the addresses (optionally widened to Amazon's regional ranges) and ports.
/// </summary>
internal sealed class LearnGameForm : Form
{
    private readonly AppController _controller;
    private readonly GameProfile? _existing;
    private readonly TextBox _name = new() { Width = 300 };
    private readonly ComboBox _process = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _refresh = new() { Text = "Обновить", AutoSize = true };
    private readonly CheckBox _bypassAll = new()
    {
        Text = "Во время записи обходить для всех адресов (как ipset «any»),\nчтобы игра смогла подключиться",
        AutoSize = true,
        Checked = true,
    };
    private readonly CheckBox _expandAws = new()
    {
        Text = "Расширять адреса Amazon до диапазонов AWS\n(серверы меняются от матча к матчу)",
        AutoSize = true,
        Checked = true,
    };
    private readonly Button _record = new() { Text = "● Начать запись", AutoSize = true };
    private readonly Label _stats = new() { AutoSize = true, Text = "Запись не идёт." };
    private readonly Button _save = new() { Text = "Сохранить", AutoSize = true, Enabled = false };
    private readonly Button _cancel = new() { Text = "Отмена", AutoSize = true };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    // One learner for the whole dialog: "continue recording" adds to what was already captured.
    private readonly TrafficLearner _learned = new();
    private bool _recording;
    private bool _busy;

    public LearnGameForm(AppController controller, GameProfile? existing)
    {
        _controller = controller;
        _existing = existing;
        Text = existing is null ? "ZapretHub — добавить игру" : $"ZapretHub — обучение: {existing.Name}";
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        ShowIcon = false;
        Padding = new Padding(12);

        var steps = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(460, 0),
            Text = "1. Запустите игру (например, через Steam) и выберите её процесс.\n" +
                   "2. Нажмите «Начать запись» и сыграйте матч или зайдите в лобби.\n" +
                   "3. Остановите запись и сохраните. Профиль можно включать и выключать в меню «Игры».",
        };

        var processRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        processRow.Controls.Add(_process);
        processRow.Controls.Add(_refresh);

        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.Add(_cancel);
        buttons.Controls.Add(_save);

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(steps);
        layout.Controls.Add(new Label { Text = "Название игры:", AutoSize = true, Padding = new Padding(0, 10, 0, 0) });
        layout.Controls.Add(_name);
        layout.Controls.Add(new Label { Text = "Процесс игры:", AutoSize = true, Padding = new Padding(0, 10, 0, 0) });
        layout.Controls.Add(processRow);
        layout.Controls.Add(_bypassAll);
        layout.Controls.Add(_expandAws);
        layout.Controls.Add(_record);
        layout.Controls.Add(_stats);
        layout.Controls.Add(buttons);
        Controls.Add(layout);

        _name.Text = existing?.Name ?? "";
        LoadProcesses(existing?.ProcessName);

        _refresh.Click += (_, _) => LoadProcesses(_process.Text);
        _process.SelectedIndexChanged += (_, _) =>
        {
            if (_name.Text.Length == 0 && _process.SelectedItem is ProcessChoice pc) _name.Text = pc.Title;
        };
        _record.Click += async (_, _) => await ToggleRecordingAsync();
        _save.Click += async (_, _) => await SaveAsync();
        _cancel.Click += (_, _) => Close();
        _timer.Tick += (_, _) => UpdateStats();
        FormClosing += async (_, e) =>
        {
            // Closing while a start/stop/save is in flight would orphan the session (and its "any" rule).
            if (_busy && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                return;
            }
            if (_recording) await StopAsync();
        };
    }

    private sealed record ProcessChoice(string ExeName, string Title)
    {
        public override string ToString() => Title == ExeName ? ExeName : $"{ExeName} — {Title}";
    }

    private void LoadProcesses(string? select)
    {
        _process.Items.Clear();
        var choices = new List<ProcessChoice>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    // Windowed processes only: games have a window, background services do not.
                    if (p.MainWindowHandle == IntPtr.Zero || p.Id == Environment.ProcessId) continue;
                    choices.Add(new ProcessChoice(p.ProcessName + ".exe", string.IsNullOrWhiteSpace(p.MainWindowTitle) ? p.ProcessName : p.MainWindowTitle));
                }
                catch (InvalidOperationException)
                {
                    // Exited while enumerating.
                }
            }
        }
        foreach (var c in choices.DistinctBy(c => c.ExeName.ToLowerInvariant()).OrderBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            _process.Items.Add(c);
        }

        var match = _process.Items.Cast<ProcessChoice>().FirstOrDefault(c => string.Equals(c.ExeName, select, StringComparison.OrdinalIgnoreCase));
        if (match is not null) _process.SelectedItem = match;
        else _process.Text = select ?? "";
    }

    private string SelectedExe() =>
        _process.SelectedItem is ProcessChoice pc ? pc.ExeName : _process.Text.Trim();

    private async Task ToggleRecordingAsync()
    {
        if (_busy) return;
        if (_recording)
        {
            await StopAsync();
            return;
        }

        var exe = SelectedExe();
        if (exe.Length == 0)
        {
            MessageBox.Show(this, "Выберите процесс игры.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true);
        var started = await _controller.StartLearningAsync(exe, _bypassAll.Checked, _learned);
        SetBusy(false);
        if (!started || IsDisposed) return; // the controller already reported the error

        _recording = true;
        _record.Text = "■ Остановить запись";
        _process.Enabled = _refresh.Enabled = _bypassAll.Enabled = _save.Enabled = false;
        _timer.Start();
        UpdateStats();
    }

    private async Task StopAsync()
    {
        _timer.Stop();
        _recording = false;
        SetBusy(true);
        await _controller.StopLearningAsync();
        SetBusy(false);
        if (IsDisposed) return;
        _record.Text = "● Продолжить запись";
        _process.Enabled = _refresh.Enabled = _bypassAll.Enabled = true;
        UpdateStats();
        _save.Enabled = _learned.Addresses.Count > 0;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        if (IsDisposed) return;
        _record.Enabled = _cancel.Enabled = !busy;
        UseWaitCursor = busy;
    }

    private void UpdateStats()
    {
        var tcp = _learned.TcpPorts.ToString();
        var udp = _learned.UdpPorts.ToString();
        _stats.Text = $"{(_recording ? "Идёт запись…" : "Запись остановлена.")} Адресов: {_learned.Addresses.Count}" +
                      $"{Environment.NewLine}TCP: {(tcp.Length > 0 ? tcp : "—")}{Environment.NewLine}UDP: {(udp.Length > 0 ? udp : "—")}";
    }

    private async Task SaveAsync()
    {
        if (_busy || _recording || _learned.Addresses.Count == 0) return;
        var name = _name.Text.Trim();
        if (name.Length == 0)
        {
            MessageBox.Show(this, "Укажите название игры.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetBusy(true);
        _save.Enabled = false;
        AwsIpRanges? aws = null;
        if (_expandAws.Checked)
        {
            try
            {
                // The real file is a few MB; the cap only stops a runaway response from exhausting memory.
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = 32 * 1024 * 1024 };
                aws = AwsIpRanges.Parse(await http.GetStringAsync(AwsIpRanges.Source));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or FormatException)
            {
                Log.Error("AWS ranges download failed", ex);
                MessageBox.Show(this, "Не удалось загрузить диапазоны AWS, адреса будут сохранены подсетями /24.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        var lines = IpsetBuilder.Build(_learned.Endpoints, aws);
        var profile = new GameProfile
        {
            Id = _existing?.Id ?? GameProfiles.MakeId(name, _controller.Settings.GameProfiles.Select(p => p.Id)),
            Name = name,
            Enabled = true,
            ProcessName = SelectedExe(),
            // Relearning adds to what the profile already knew rather than forgetting earlier sessions.
            TcpPorts = PortSet.Parse(_existing?.TcpPorts ?? "").Union(_learned.TcpPorts).ToString(),
            UdpPorts = PortSet.Parse(_existing?.UdpPorts ?? "").Union(_learned.UdpPorts).ToString(),
        };
        var saved = await _controller.SaveGameProfileAsync(profile, lines);
        SetBusy(false);
        if (!saved)
        {
            // Keep the dialog (and what was recorded) so the user can retry.
            _save.Enabled = true;
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}

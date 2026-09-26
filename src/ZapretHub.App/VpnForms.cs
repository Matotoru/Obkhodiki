using System.Diagnostics;
using System.Net;
using ZapretHub.Core.Vpn;

namespace ZapretHub.App;

internal abstract class VpnFormBase : Form
{
    protected VpnFormBase(string title)
    {
        Text = "ZapretHub — " + title;
        StartPosition = FormStartPosition.CenterScreen;
        ShowIcon = false;
        Padding = new Padding(12);
        Font = SystemFonts.MessageBoxFont;
    }

    protected static Label Hint(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(520, 0), Padding = new Padding(0, 4, 0, 8) };
}

/// <summary>Paste or replace the hysteria2:// link. The link is never shown back in full.</summary>
internal sealed class VpnServerForm : VpnFormBase
{
    public VpnServerForm(AppController controller) : base("сервер VPS")
    {
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var current = controller.VpnServerName;
        var link = new TextBox { Width = 520, UseSystemPasswordChar = true, PlaceholderText = "hysteria2://…" };
        var show = new CheckBox { Text = "Показать ссылку", AutoSize = true };
        show.CheckedChanged += (_, _) => link.UseSystemPasswordChar = !show.Checked;
        var status = new Label { AutoSize = true, ForeColor = Color.Firebrick, MaximumSize = new Size(520, 0) };
        var save = new Button { Text = "Сохранить", AutoSize = true };
        var remove = new Button { Text = "Удалить сервер", AutoSize = true, Enabled = current is not null };
        var close = new Button { Text = "Закрыть", AutoSize = true };

        save.Click += async (_, _) =>
        {
            save.Enabled = false;
            UseWaitCursor = true;
            var error = await controller.SetVpnServerAsync(link.Text);
            UseWaitCursor = false;
            save.Enabled = true;
            if (error is not null)
            {
                status.Text = error;
                return;
            }
            Close();
        };
        remove.Click += async (_, _) =>
        {
            if (!Dialogs.Confirm("Удалить сервер VPS? Все маршруты через VPS перестанут работать.", Text, MessageBoxIcon.Question)) return;
            await controller.RemoveVpnServerAsync();
            Close();
        };
        close.Click += (_, _) => Close();

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange(new Control[] { close, save, remove });

        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Dock = DockStyle.Fill };
        layout.Controls.Add(Hint(current is null
            ? "Вставьте ссылку на ваш сервер Hysteria2 (hysteria2://… или hy2://…). Она хранится зашифрованной и в чат/логи не попадает."
            : $"Сейчас: {current}. Чтобы заменить сервер, вставьте новую ссылку."));
        layout.Controls.Add(link);
        layout.Controls.Add(show);
        layout.Controls.Add(status);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
    }
}

/// <summary>Programs and sites that always go through the VPS.</summary>
internal sealed class VpnListsForm : VpnFormBase
{
    public VpnListsForm(AppController controller) : base("программы и сайты через VPS")
    {
        ClientSize = new Size(560, 520);
        MinimumSize = new Size(480, 420);

        var processes = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        processes.Items.AddRange(controller.Settings.VpnProcesses.Cast<object>().ToArray());
        var running = new ComboBox { Width = 330, DropDownStyle = ComboBoxStyle.DropDown };
        var add = new Button { Text = "Добавить", AutoSize = true };
        var removeProcess = new Button { Text = "Убрать выбранную", AutoSize = true };

        void LoadRunning()
        {
            running.Items.Clear();
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    try
                    {
                        if (p.MainWindowHandle != IntPtr.Zero) names.Add(p.ProcessName + ".exe");
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }
            }
            running.Items.AddRange(names.Cast<object>().ToArray());
        }
        LoadRunning();
        running.DropDown += (_, _) => LoadRunning();

        add.Click += (_, _) =>
        {
            var name = running.Text.Trim();
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name += ".exe";
            if (!SingBoxConfig.IsValidProcessName(name))
            {
                MessageBox.Show(this, "Укажите имя программы, например chrome.exe.", Text);
                return;
            }
            if (!processes.Items.Cast<string>().Contains(name, StringComparer.OrdinalIgnoreCase)) processes.Items.Add(name);
            running.Text = "";
        };
        removeProcess.Click += (_, _) =>
        {
            if (processes.SelectedItem is not null) processes.Items.Remove(processes.SelectedItem);
        };

        var domains = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            Text = string.Join(Environment.NewLine, controller.Settings.VpnDomains),
        };

        var save = new Button { Text = "Сохранить", AutoSize = true };
        var cancel = new Button { Text = "Отмена", AutoSize = true };
        var error = new Label { AutoSize = true, ForeColor = Color.Firebrick };
        save.Click += async (_, _) =>
        {
            var lines = domains.Lines.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
            var bad = lines.Where(l => SingBoxConfig.NormalizeDomain(l) is null).ToList();
            if (bad.Count > 0)
            {
                error.Text = "Не похоже на домен: " + string.Join(", ", bad.Take(3));
                return;
            }
            save.Enabled = false;
            if (await controller.SetVpnListsAsync(processes.Items.Cast<string>().ToList(), lines))
            {
                Close();
                return;
            }
            save.Enabled = true;
            error.Text = "Не удалось применить, подробности в уведомлении.";
        };
        cancel.Click += (_, _) => Close();

        var processRow = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        processRow.Controls.AddRange(new Control[] { running, add, removeProcess });
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.AddRange(new Control[] { cancel, save, error });

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(Hint("Программы: весь их трафик идёт через VPS. Лучше выбирать из списка запущенных — имя должно совпадать точно (например, chrome.exe, Spotify.exe)."));
        layout.Controls.Add(processes);
        layout.Controls.Add(processRow);
        layout.Controls.Add(Hint("Сайты: по одному на строку, поддомены включаются автоматически (например, chatgpt.com)."));
        layout.Controls.Add(domains);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
    }
}

/// <summary>Side-by-side ping / jitter / loss for the direct path and the VPS path.</summary>
internal sealed class QualityForm : VpnFormBase
{
    private readonly AppController _controller;
    private readonly ComboBox _target = new() { Width = 360, DropDownStyle = ComboBoxStyle.DropDown };
    private readonly Button _run = new() { Text = "Измерить", AutoSize = true };
    private readonly ListView _table = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable };
    private readonly Label _verdict = new() { AutoSize = true, MaximumSize = new Size(560, 0), Padding = new Padding(0, 8, 0, 0) };
    private readonly CancellationTokenSource _cts = new();

    private sealed record Choice(string Title, IReadOnlyList<IPEndPoint> Endpoints)
    {
        public override string ToString() => Title;
    }

    public QualityForm(AppController controller) : base("качество канала")
    {
        _controller = controller;
        ClientSize = new Size(600, 360);
        MinimumSize = new Size(520, 320);

        foreach (var p in controller.Settings.GameProfiles)
        {
            var tls = p.ProbeEndpoints.Select(IPEndPoint.Parse).Where(e => TlsPing.IsTlsPort(e.Port)).Take(3).ToList();
            if (tls.Count > 0) _target.Items.Add(new Choice($"Игра: {p.Name}", tls));
        }
        if (_target.Items.Count > 0) _target.SelectedIndex = 0;
        else _target.Text = "1.1.1.1:443";

        _table.Columns.Add("Путь", 140);
        _table.Columns.Add("Пинг (медиана)", 130);
        _table.Columns.Add("Джиттер", 100);
        _table.Columns.Add("Потери", 100);

        _run.Click += async (_, _) => await RunAsync();
        FormClosed += (_, _) => _cts.Cancel();

        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        row.Controls.AddRange(new Control[] { _target, _run });
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(Hint("Замер — время ответа TLS-серверов игры (её бэкенд, обычно в том же дата-центре, что и игровые " +
                                 "серверы): напрямую и через VPS, на уже открытом соединении. Выберите игру или введите адрес вида IP:443."));
        layout.Controls.Add(row);
        layout.Controls.Add(_table);
        layout.Controls.Add(_verdict);
        Controls.Add(layout);
    }

    private async Task RunAsync()
    {
        IReadOnlyList<IPEndPoint> endpoints;
        if (_target.SelectedItem is Choice c && _target.Text == c.Title) endpoints = c.Endpoints;
        else if (IPEndPoint.TryParse(_target.Text.Trim(), out var ep) && TlsPing.IsTlsPort(ep.Port)) endpoints = new[] { ep };
        else
        {
            MessageBox.Show(this, "Введите адрес TLS-сервера в виде IP:443, например 1.1.1.1:443.", Text);
            return;
        }
        if (endpoints.Count == 0)
        {
            MessageBox.Show(this, "У этой игры нет TLS-адресов для замера. Выполните «Дообучить».", Text);
            return;
        }

        _run.Enabled = false;
        _table.Items.Clear();
        _verdict.Text = "Идёт замер (около 10–30 секунд)…";
        try
        {
            var result = await _controller.MeasureAsync(endpoints, interactive: true, _cts.Token);
            if (IsDisposed) return;
            if (result is null)
            {
                _verdict.Text = "Замер не удался, подробности в уведомлении и логе.";
                return;
            }
            AddRow("Напрямую", result.Direct);
            AddRow("Через VPS", result.Tunnel);
            _verdict.Text = (result.Decision.Choice == PathChoice.Vpn ? "Лучше через VPS: " : "Лучше напрямую: ") + result.Decision.Reason;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!IsDisposed) _run.Enabled = true;
        }
    }

    private void AddRow(string title, PathStats s)
    {
        var item = new ListViewItem(title);
        item.SubItems.Add(s.MedianMs is { } m ? $"{m:F0} мс" : "нет ответа");
        item.SubItems.Add(s.MedianMs is null ? "—" : $"{s.JitterMs:F1} мс");
        item.SubItems.Add($"{s.LossPercent:F0}%");
        _table.Items.Add(item);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}

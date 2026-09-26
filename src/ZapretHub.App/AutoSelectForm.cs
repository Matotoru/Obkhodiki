using ZapretHub.Core.Testing;

namespace ZapretHub.App;

/// <summary>Shows strategy auto-selection progress with a per-strategy result table and a cancel button.</summary>
internal sealed class AutoSelectForm : Form
{
    private const int BarMax = 1000;

    private readonly AppController _controller;
    private readonly ProgressBar _bar = new() { Dock = DockStyle.Top, Height = 22, Maximum = BarMax, Style = ProgressBarStyle.Continuous };
    private readonly Label _status = new() { Dock = DockStyle.Top, Height = 40, Padding = new Padding(0, 6, 0, 0), Text = "Подготовка…" };
    private readonly ListView _results = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Button _action = new() { Text = "Отмена", AutoSize = true, Anchor = AnchorStyles.Right };
    private readonly CancellationTokenSource _cts = new();
    private bool _finished;

    public AutoSelectForm(AppController controller)
    {
        _controller = controller;
        Text = "ZapretHub — подбор стратегии";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(560, 460);
        MinimumSize = new Size(420, 320);
        Padding = new Padding(12);
        ShowIcon = false;

        _results.Columns.Add("Стратегия", 250);
        _results.Columns.Add("Открыто", 80);
        _results.Columns.Add("Время", 80);
        _results.Columns.Add("Ошибка", 120);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        bottom.Controls.Add(_action);
        _action.Click += (_, _) =>
        {
            if (_finished) Close();
            else
            {
                _action.Enabled = false;
                _status.Text = "Отмена… дожидаемся текущей проверки.";
                _cts.Cancel();
            }
        };

        Controls.Add(_results);
        Controls.Add(_status);
        Controls.Add(_bar);
        Controls.Add(bottom);

        Shown += async (_, _) => await RunAsync();
        FormClosing += (_, e) =>
        {
            // Closing mid-run cancels instead of leaving winws half-tested in the background.
            // Never block Windows shutdown or logoff, though.
            if (!_finished && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                _cts.Cancel();
                _status.Text = "Отмена… окно закроется после текущей проверки.";
                _closeWhenDone = true;
            }
        };
    }

    private bool _closeWhenDone;

    private async Task RunAsync()
    {
        // Progress<T> captures this form's UI context, so Report calls land on the UI thread.
        var progress = new Progress<SelectionProgress>(ShowProgress);
        await _controller.AutoSelectAsync(progress, _cts.Token);

        _finished = true;
        _bar.Value = BarMax;
        _action.Text = "Закрыть";
        _action.Enabled = true;
        var anyPassed = _results.Items.Cast<ListViewItem>().Any(i => i.Tag is StrategyScore { Passed: > 0 });
        if (_cts.IsCancellationRequested)
        {
            _status.Text = "Подбор отменён, оставлена прежняя стратегия.";
        }
        else if (_results.Items.Count == 0)
        {
            _status.Text = "Подбор не выполнен. Подробности — в уведомлении или в логе.";
        }
        else if (!anyPassed)
        {
            _status.Text = "Ни одна стратегия не открыла цели. Проверьте интернет и список целей.";
        }
        else
        {
            _status.Text = $"Готово. Выбрана стратегия: {_controller.ActiveStrategyName}";
            HighlightBest();
        }
        if (_closeWhenDone) Close();
    }

    private void ShowProgress(SelectionProgress p)
    {
        if (IsDisposed) return;
        _bar.Value = Math.Clamp((int)(p.Fraction * BarMax), 0, BarMax);
        _status.Text = $"Стратегия {p.StrategyIndex + 1} из {p.StrategyCount}: {p.Strategy.Name}" +
                       $"{Environment.NewLine}Проверено целей: {p.TargetsDone} из {p.TargetCount}";

        if (p.Completed is { } s)
        {
            var item = new ListViewItem(s.Strategy.Name)
            {
                Tag = s,
                ForeColor = s.Passed == s.Total && s.Total > 0 ? Color.ForestGreen : s.Passed == 0 ? Color.Firebrick : SystemColors.ControlText,
            };
            item.SubItems.Add($"{s.Passed} из {s.Total}");
            item.SubItems.Add(s.Passed > 0 ? $"{s.TotalLatency.TotalMilliseconds / s.Passed:F0} мс" : "—");
            item.SubItems.Add(s.Error ?? "");
            _results.Items.Add(item);
            item.EnsureVisible();
        }
    }

    private void HighlightBest()
    {
        foreach (ListViewItem item in _results.Items)
        {
            if (item.Text == _controller.ActiveStrategyName)
            {
                item.Font = new Font(_results.Font, FontStyle.Bold);
                item.Text = "★ " + item.Text;
                item.EnsureVisible();
            }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cts.Dispose();
        base.Dispose(disposing);
    }
}

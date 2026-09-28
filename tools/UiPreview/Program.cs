using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Obkhodiki.App.Ui;
using Obkhodiki.App.Ui.ViewModels;

namespace UiPreview;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var outDir = args.Length > 0 ? args[0] : "ui-preview";
        Directory.CreateDirectory(outDir);

        var app = new App();
        app.InitializeComponent();

        var shell = ShellViewModel.Current;
        shell.Home.SetState(bypassOn: true, engineReady: true);
        shell.Home.LoadSample();
        shell.Bypass.LoadSample();
        shell.Games.LoadSample();
        shell.Vpn.LoadSample();
        shell.Telegram.LoadSample();
        shell.Settings.LoadSample();
        shell.AddEvent("Обход включён", "Стратегия general (ALT2)", EventKind.Success);
        shell.AddEvent("War Dogs: через VPS", "Потери напрямую 6%, через VPS 0%", EventKind.Info);
        shell.AddEvent("Доступно обновление Flowseal", "1.9.7 → 1.9.8", EventKind.Warning);

        var window = new MainWindow
        {
            WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -4000,
            Top = 0,
            ShowActivated = false,
            ShowInTaskbar = false,
        };
        window.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");

        ThemeService.Backdrop = Wpf.Ui.Controls.WindowBackdropType.None;
        var shots = new (string Name, int Height, Action Setup)[]
        {
            ("home", 740, () => window.Navigate("Home")),
            ("bypass", 900, () => window.Navigate("Bypass")),
            ("games", 740, () => window.Navigate("Games")),
            ("games-catalog", 1000, () => { shell.Games.LoadCatalogSample(Path.Combine(outDir, "covers")); window.Navigate("Games"); }),
            ("games-top", 1000, () => window.Navigate("Games")),
            ("games-learning", 1000, () => { shell.Games.LoadLearningSample(); window.Navigate("Games"); }),
            ("vpn", 1900, () => window.Navigate("Vpn")),
            ("vpn-small", 600, () => window.Navigate("Vpn")),
            ("vpn-selective", 1500, () => { shell.Vpn.FullTunnel = false; window.Navigate("Vpn"); }),
            ("vpn-bottom", 1000, () => window.Navigate("Vpn")),
            ("telegram", 740, () => window.Navigate("Telegram")),
            ("settings", 1400, () => window.Navigate("Settings")),
            ("home-light", 740, () => { ThemeService.Apply(window, ThemeService.Light, "cat"); window.Navigate("Home"); }),
            ("vpn-light", 1100, () => { ThemeService.Apply(window, ThemeService.Light, "ocean"); window.Navigate("Vpn"); }),
            ("settings-light", 1400, () => { ThemeService.Apply(window, ThemeService.Light, "mint"); window.Navigate("Settings"); }),
            ("home-dark-lavender", 740, () => { ThemeService.Apply(window, ThemeService.Dark, "lavender"); window.Navigate("Home"); }),
        };

        window.Loaded += async (_, _) =>
        {
            try
            {
                foreach (var (name, height, setup) in shots)
                {
                    window.Height = height;
                    setup();
                    await Settle(window);
                    if (name.StartsWith("settings", StringComparison.Ordinal) || name == "games-top")
                    {
                        ScrollToEnd(window, top: true);
                        await Settle(window);
                    }
                    if (name.EndsWith("-bottom", StringComparison.Ordinal) || name == "games-catalog")
                    {
                        ScrollToEnd(window);
                        await Settle(window);
                    }
                    Save(window, Path.Combine(outDir, name + ".png"));
                    if (Environment.GetEnvironmentVariable("UIPREVIEW_ACCENT") == "1")
                    {
                        foreach (var key in new[] { "SystemAccentColor", "SystemAccentColorPrimary", "AccentFillColorDefaultBrush", "ToggleSwitchFillOn", "AccentTextFillColorPrimaryBrush" })
                        {
                            var v = Application.Current.TryFindResource(key);
                            File.AppendAllText(Path.Combine(outDir, "accent.txt"), $"{name} {key} = {(v is SolidColorBrush b ? b.Color.ToString() : v?.ToString())}" + Environment.NewLine);
                        }
                    }
                    if (Environment.GetEnvironmentVariable("UIPREVIEW_SCROLL") == "1") DumpScroll(window, name, outDir);
                }

                // The first-run guide, one picture per step.
                ThemeService.Apply(window, ThemeService.Dark, "cat");
                var wizard = new WizardWindow(shell) { ShowActivated = false, ShowInTaskbar = false, Left = -4000, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual };
                wizard.WindowBackdropType = Wpf.Ui.Controls.WindowBackdropType.None;
                wizard.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
                wizard.Show();
                var wvm = (Obkhodiki.App.Ui.ViewModels.WizardViewModel)wizard.DataContext;
                for (var step = 0; step < Obkhodiki.App.Ui.ViewModels.WizardViewModel.StepCount; step++)
                {
                    wvm.Step = step;
                    await Settle(wizard);
                    Save(wizard, Path.Combine(outDir, $"wizard-{step + 1}.png"));
                }
                wizard.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(outDir, "error.txt"), ex.ToString());
            }
            try
            {
                // The dialog itself is modal; render its content the way it appears inside it.
                var host = new Wpf.Ui.Controls.Card
                {
                    Width = 520,
                    Padding = new Thickness(24),
                    Content = new System.Windows.Controls.StackPanel
                    {
                        Children =
                        {
                            new System.Windows.Controls.TextBlock { Text = Obkhodiki.App.Ui.WhatsNewDialog.Title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 14) },
                            Obkhodiki.App.Ui.WhatsNewDialog.BuildContent(Obkhodiki.Core.Updates.Changelog.Since("0.3.0", "0.4.0")),
                        },
                    },
                };
                var popup = new Window { Content = host, SizeToContent = SizeToContent.WidthAndHeight, WindowStyle = WindowStyle.None, Left = -4000, ShowInTaskbar = false, ShowActivated = false };
                popup.SetResourceReference(Window.BackgroundProperty, "ApplicationBackgroundBrush");
                popup.SetResourceReference(Window.ForegroundProperty, "TextFillColorPrimaryBrush");
                popup.Show();
                await Settle(popup);
                Save(popup, Path.Combine(outDir, "whatsnew.png"));
                popup.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(outDir, "error-whatsnew.txt"), ex.ToString());
            }
            finally
            {
                window.CloseForExit();
                app.Shutdown();
            }
        };
        window.Show();
        app.Run();
    }

    private static async Task Settle(Window w)
    {
        // Navigation transitions and bindings finish over several dispatcher passes.
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(150);
            await w.Dispatcher.InvokeAsync(() => w.UpdateLayout(), DispatcherPriority.ApplicationIdle);
        }
    }

    // Diagnostics: every ScrollViewer on screen with its sizes, to prove long pages can scroll.
    private static void ScrollToEnd(DependencyObject d, bool top = false)
    {
        if (d is System.Windows.Controls.ScrollViewer { ScrollableHeight: > 0 } sv)
        {
            if (top) sv.ScrollToHome();
            else sv.ScrollToEnd();
            return;
        }
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) ScrollToEnd(VisualTreeHelper.GetChild(d, i), top);
    }

    private static void DumpScroll(Window w, string name, string outDir)
    {
        var lines = new List<string>();
        void Walk(DependencyObject d, int depth)
        {
            if (d is System.Windows.Controls.ScrollViewer sv)
                lines.Add($"{name} depth={depth} viewport={sv.ViewportHeight:F0} extent={sv.ExtentHeight:F0} scrollable={sv.ScrollableHeight:F0} bar={sv.ComputedVerticalScrollBarVisibility} type={sv.GetType().Name} parentOf={(sv.Content?.GetType().Name ?? "-")}");
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Walk(VisualTreeHelper.GetChild(d, i), depth + 1);
        }
        Walk(w, 0);

        // Wheel over a card deep inside the page must scroll the page.
        System.Windows.Controls.TextBlock? deep = null;
        System.Windows.Controls.ScrollViewer? pageScroller = null;
        void Find(DependencyObject d)
        {
            if (d is System.Windows.Controls.ScrollViewer { ScrollableHeight: > 0 } sv && pageScroller is null) pageScroller = sv;
            if (pageScroller is not null && d is System.Windows.Controls.TextBlock tb && deep is null && tb.IsVisible) deep = tb;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) Find(VisualTreeHelper.GetChild(d, i));
        }
        Find(w);
        if (pageScroller is not null && deep is not null)
        {
            var before = pageScroller.VerticalOffset;
            var args = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
            };
            deep.RaiseEvent(args);
            pageScroller.UpdateLayout();
            lines.Add($"{name} wheel: offset {before:F0} -> {pageScroller.VerticalOffset:F0}");
        }
        File.AppendAllLines(Path.Combine(outDir, "scroll.txt"), lines);
    }

    private static void Save(Window w, string path)
    {
        var root = (FrameworkElement)w.Content;
        var width = (int)root.ActualWidth;
        var height = (int)root.ActualHeight;
        var bmp = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(w.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }
        bmp.Render(visual);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ZapretHub.App.Ui;
using ZapretHub.App.Ui.ViewModels;

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

        var shots = new (string Name, int Height, Action Setup)[]
        {
            ("home", 740, () => window.Navigate("Home")),
            ("bypass", 900, () => window.Navigate("Bypass")),
            ("games", 740, () => window.Navigate("Games")),
            ("games-learning", 1000, () => { shell.Games.LoadLearningSample(); window.Navigate("Games"); }),
            ("vpn", 1250, () => window.Navigate("Vpn")),
            ("telegram", 740, () => window.Navigate("Telegram")),
            ("settings", 900, () => window.Navigate("Settings")),
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
                    Save(window, Path.Combine(outDir, name + ".png"));
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(outDir, "error.txt"), ex.ToString());
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

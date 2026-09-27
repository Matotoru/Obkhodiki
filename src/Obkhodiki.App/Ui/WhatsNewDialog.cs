using System.Windows;
using System.Windows.Controls;
using Obkhodiki.Core.Updates;
using WpfUiMessageBox = Wpf.Ui.Controls.MessageBox;

namespace Obkhodiki.App.Ui;

/// <summary>"Что нового": the changelog entries as a closable dialog over the main window.</summary>
internal static class WhatsNewDialog
{
    public static async Task ShowAsync(IReadOnlyList<Changelog.Entry> entries)
    {
        if (entries.Count == 0) return;
        var box = new WpfUiMessageBox
        {
            Title = Title,
            Content = new ScrollViewer { Content = BuildContent(entries), MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            CloseButtonText = "Понятно",
            MinWidth = 460,
            MaxWidth = 560,
        };
        await box.ShowDialogAsync();
    }

    public static string Title => $"Что нового в {AppInfo.Name} {SelfUpdate.CurrentVersion}";

    public static FrameworkElement BuildContent(IReadOnlyList<Changelog.Entry> entries)
    {
        var panel = new StackPanel();
        foreach (var entry in entries)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Версия {entry.Version}",
                FontWeight = FontWeights.SemiBold,
                FontSize = 15,
                Margin = new Thickness(0, panel.Children.Count == 0 ? 0 : 14, 0, 6),
            });
            foreach (var item in entry.Items)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.Children.Add(new TextBlock { Text = "•" });
                var text = new TextBlock { Text = item, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                panel.Children.Add(row);
            }
        }

        return panel;
    }
}

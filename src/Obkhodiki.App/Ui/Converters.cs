using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Obkhodiki.App.Ui.ViewModels;

namespace Obkhodiki.App.Ui;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null or "" ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the number is above zero; with parameter "zero", visible when it is zero.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var positive = value is int n && n > 0;
        var wantZero = parameter is "zero";
        return positive != wantZero ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EventKindToSymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        EventKind.Success => SymbolRegular.CheckmarkCircle24,
        EventKind.Warning => SymbolRegular.Warning24,
        EventKind.Error => SymbolRegular.ErrorCircle24,
        _ => SymbolRegular.Info24,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EventKindToBrushConverter : IValueConverter
{
    private static readonly Brush Success = Frozen(Color.FromRgb(0x6C, 0xCB, 0x5F));
    private static readonly Brush Warning = Frozen(Color.FromRgb(0xFC, 0xE1, 0x00));
    private static readonly Brush Error = Frozen(Color.FromRgb(0xFF, 0x99, 0xA4));
    private static readonly Brush Info = Frozen(Color.FromRgb(0x60, 0xCD, 0xFF));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        EventKind.Success => Success,
        EventKind.Warning => Warning,
        EventKind.Error => Error,
        _ => Info,
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

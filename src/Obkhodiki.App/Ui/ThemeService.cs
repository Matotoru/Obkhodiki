using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Obkhodiki.App.Ui;

/// <param name="Dark">Accent on a dark background: light enough to read on it.</param>
/// <param name="Light">Accent on a light background: dark enough for white text on accent buttons.</param>
public sealed record AccentPalette(string Id, string Title, Color? Dark, Color? Light)
{
    public Brush Swatch { get; } = new SolidColorBrush(Dark ?? Color.FromRgb(0x80, 0x80, 0x80));
    public bool IsSystem => Dark is null;
}

public sealed record ThemeOption(string Id, string Title);

/// <summary>Light/dark theme and the accent palette, applied to the whole app at once.</summary>
public static class ThemeService
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    public static IReadOnlyList<ThemeOption> Themes { get; } = new[]
    {
        new ThemeOption(System, "Как в Windows"),
        new ThemeOption(Dark, "Тёмная"),
        new ThemeOption(Light, "Светлая"),
    };

    public static IReadOnlyList<AccentPalette> Palettes { get; } = new[]
    {
        new AccentPalette("cat", "Котик", Rgb(0xE0, 0xA2, 0x6A), Rgb(0x9A, 0x5B, 0x2E)),
        new AccentPalette("coral", "Коралл", Rgb(0xFF, 0x8A, 0x7A), Rgb(0xC0, 0x42, 0x36)),
        new AccentPalette("ocean", "Океан", Rgb(0x6C, 0xB8, 0xFF), Rgb(0x0B, 0x63, 0xC1)),
        new AccentPalette("mint", "Мята", Rgb(0x5F, 0xD3, 0xA5), Rgb(0x0E, 0x77, 0x52)),
        new AccentPalette("lavender", "Лаванда", Rgb(0xB7, 0x9C, 0xFF), Rgb(0x66, 0x44, 0xC4)),
        new AccentPalette("amber", "Янтарь", Rgb(0xFF, 0xC2, 0x4B), Rgb(0x9C, 0x62, 0x00)),
        new AccentPalette("rose", "Роза", Rgb(0xFF, 0x86, 0xB5), Rgb(0xB0, 0x2A, 0x68)),
        new AccentPalette(System, "Как в Windows", null, null),
    };

    public const string DefaultPalette = "cat";

    private static Window? _window;
    private static string _theme = System;
    private static string _palette = DefaultPalette;

    public static AccentPalette Palette(string? id) => Palettes.FirstOrDefault(p => p.Id == id) ?? Palettes[0];

    private static bool _applying;

    /// <summary>Window material; previews render off-screen, where Mica cannot show.</summary>
    public static WindowBackdropType Backdrop { get; set; } = WindowBackdropType.Mica;

    /// <summary>Applies the choice to <paramref name="window"/> and every other window of the app.</summary>
    public static void Apply(Window window, string? theme, string? palette)
    {
        _window = window;
        _theme = Themes.Any(t => t.Id == theme) ? theme! : System;
        _palette = Palette(palette).Id;
        var systemAccent = Palette(_palette).IsSystem;

        ApplicationThemeManager.Changed -= OnThemeChanged;
        _applying = true;
        try
        {
            // Accent first: theme dictionaries copy some accent colours (e.g. switches) when they load.
            ApplyAccent(CurrentTheme());
            if (_theme == System)
            {
                SystemThemeWatcher.Watch(window, Backdrop, updateAccents: systemAccent);
                ApplicationThemeManager.ApplySystemTheme(updateAccent: systemAccent);
            }
            else
            {
                SystemThemeWatcher.UnWatch(window);
                ApplicationThemeManager.Apply(CurrentTheme(), Backdrop, updateAccent: false);
            }
        }
        finally
        {
            _applying = false;
        }
        // Windows switching between light and dark re-applies its own accent; put the chosen palette back.
        ApplicationThemeManager.Changed += OnThemeChanged;
    }

    private static void OnThemeChanged(ApplicationTheme theme, Color accent)
    {
        if (_applying || Palette(_palette).IsSystem) return;
        _applying = true;
        try
        {
            ApplyAccent(theme);
            ApplicationThemeManager.Apply(theme, Backdrop, updateAccent: false);
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>
    /// Sets every accent shade from the palette. WPF-UI's own derivation expects Windows' mid-tone accents and
    /// turns an already light colour nearly white on dark backgrounds, so the shades are set here explicitly.
    /// Dark theme: fills use the secondary shade, text the tertiary; light theme: fills the primary, text the tertiary.
    /// </summary>
    private static void ApplyAccent(ApplicationTheme theme)
    {
        var palette = Palette(_palette);
        if (palette.IsSystem)
        {
            ApplicationAccentColorManager.ApplySystemAccent();
            return;
        }
        if (theme != ApplicationTheme.Light)
        {
            var c = palette.Dark!.Value;
            ApplicationAccentColorManager.Apply(Mix(c, Colors.Black, 0.25), Mix(c, Colors.Black, 0.1), c, Mix(c, Colors.White, 0.35));
        }
        else
        {
            var c = palette.Light!.Value;
            ApplicationAccentColorManager.Apply(Mix(c, Colors.White, 0.15), c, Mix(c, Colors.Black, 0.12), Mix(c, Colors.Black, 0.25));
        }
    }

    private static ApplicationTheme CurrentTheme() => _theme switch
    {
        Light => ApplicationTheme.Light,
        Dark => ApplicationTheme.Dark,
        _ => ApplicationThemeManager.GetSystemTheme() == SystemTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark,
    };

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);
}

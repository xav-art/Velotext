// Velotext: applies the Fluent theme (dark, light or system) with Velotext's warm palette and accent check.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Velotext.Core.Settings;

namespace Velotext.App.Services;

/// <summary>
/// Applies the theme. The Windows Fluent theme supplies the controls and the user's accent colour;
/// Velotext's warm palette replaces the cool neutrals. When the accent colour is too dark or too light
/// for the background (below 3:1), a teal accent is used instead, so focus and selection stay visible.
/// </summary>
public sealed class ThemeService : IDisposable
{
    private static readonly Color s_fallbackDark = Color.FromRgb(0x4C, 0xC2, 0xB8);
    private static readonly Color s_fallbackLight = Color.FromRgb(0x00, 0x78, 0x6F);

    private readonly Application _application;
    private ResourceDictionary? _palette;
    private ResourceDictionary? _accent;
    private ResourceDictionary? _styles;
    private AppTheme _theme;

    /// <summary>Creates the service for the running application.</summary>
    /// <param name="application">The WPF application.</param>
    public ThemeService(Application application)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>True when the dark palette is active.</summary>
    public bool IsDark { get; private set; }

    /// <summary>Applies a theme.</summary>
    /// <param name="theme">The theme from settings.</param>
    public void Apply(AppTheme theme)
    {
        _theme = theme;
        IsDark = theme switch
        {
            AppTheme.Light => false,
            AppTheme.System => !SystemUsesLightTheme(),
            _ => true,
        };

#pragma warning disable WPF0001 // Fluent ThemeMode is marked experimental in .NET 10 but is the supported way to get Fluent controls.
        _application.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        var dictionaries = _application.Resources.MergedDictionaries;
        if (_palette is not null)
        {
            dictionaries.Remove(_palette);
        }

        if (_accent is not null)
        {
            dictionaries.Remove(_accent);
        }

        _palette = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Velotext;component/Themes/Warm.{(IsDark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        };
        dictionaries.Add(_palette);
        _accent = BuildAccentFallback();
        if (_accent is not null)
        {
            dictionaries.Add(_accent);
        }

        // Shared styles build on the Fluent styles, so they load once the Fluent theme is in place.
        if (_styles is null)
        {
            _styles = new ResourceDictionary { Source = new Uri("pack://application:,,,/Velotext;component/Themes/Styles.xaml", UriKind.Absolute) };
            dictionaries.Add(_styles);
        }
    }

    /// <summary>Stops listening for Windows theme changes.</summary>
    public void Dispose() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    /// <summary>Contrast ratio between two colours (WCAG relative luminance).</summary>
    /// <param name="a">First colour.</param>
    /// <param name="b">Second colour.</param>
    public static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    private static bool SystemUsesLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }

    private ResourceDictionary? BuildAccentFallback()
    {
        var surface = (Color)_palette!["Velotext.SurfaceColor"];
        var accent = IsDark ? SystemColors.AccentColorLight2 : SystemColors.AccentColorDark1;
        if (Contrast(accent, surface) >= 3.0)
        {
            return null;
        }

        var fallback = IsDark ? s_fallbackDark : s_fallbackLight;
        var brush = new SolidColorBrush(fallback);
        brush.Freeze();
        var dictionary = new ResourceDictionary();
        foreach (var key in new[]
        {
            "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
            "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush",
            "AccentFillColorSelectedTextBackgroundBrush",
        })
        {
            dictionary[key] = brush;
        }

        return dictionary;
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (_theme == AppTheme.System && e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
        {
            _application.Dispatcher.BeginInvoke(() => Apply(_theme));
        }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Helpers;

/// <summary>
/// Colors the shared "ShelfCardBrush" behind the cards in Home's rows (Settings > Appearance > Card style).
/// </summary>
public static class ShelfCardColors
{
    private static SolidColorBrush? _brush, _stroke;

    public static void Initialize()
    {
        var res = Application.Current.Resources;
        _brush = res.TryGetValue("ShelfCardBrush", out var b) ? b as SolidColorBrush : null;
        _stroke = res.TryGetValue("ShelfCardStrokeBrush", out var s) ? s as SolidColorBrush : null;
        Svc.Settings.Changed += name => { if (name is nameof(AppSettings.ShelfCardColor) or nameof(AppSettings.Theme)) Refresh(); };
        Svc.Theme.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ThemeService.Accent)) Refresh(); };
        Refresh();
    }

    public static void Refresh()
    {
        if (_brush == null) return;
        var (fill, stroke) = For(Svc.Settings.Current.ShelfCardColor, Svc.Theme.IsDark);
        // Blend into the new style instead of snapping.
        ColorTween.To(_brush, fill, 380);
        if (_stroke != null) ColorTween.To(_stroke, stroke, 380);
    }

    /// <summary>Fill and edge of a card style (also used for the previews in Home's appearance panel).</summary>
    public static (Color fill, Color stroke) For(ShelfCardColor style, bool dark) => style switch
        {
            // Frosted and see-through.
            ShelfCardColor.Glass => dark ? (Color.FromArgb(28, 255, 255, 255), Color.FromArgb(34, 255, 255, 255)) : (Color.FromArgb(150, 255, 255, 255), Color.FromArgb(30, 0, 0, 0)),
            // Deep, half-transparent black.
            ShelfCardColor.Smoke => dark ? (Color.FromArgb(150, 0, 0, 0), Color.FromArgb(20, 255, 255, 255)) : (Color.FromArgb(40, 0, 0, 0), Color.FromArgb(20, 0, 0, 0)),
            // Cool blue-gray.
            ShelfCardColor.Slate => dark ? (Color.FromArgb(235, 34, 39, 50), Color.FromArgb(30, 160, 180, 220)) : (Color.FromArgb(240, 236, 240, 247), Color.FromArgb(26, 40, 60, 100)),
            // Lighter than the page, with a brighter edge, so cards seem to float.
            ShelfCardColor.Elevated => dark ? (Color.FromArgb(240, 56, 56, 64), Color.FromArgb(46, 255, 255, 255)) : (Color.FromArgb(255, 255, 255, 255), Color.FromArgb(36, 0, 0, 0)),
            // Nothing at all: the artwork and text sit straight on the page.
            ShelfCardColor.None => (Color.FromArgb(0, 0, 0, 0), Color.FromArgb(0, 0, 0, 0)),
            // No fill, just an outline.
            ShelfCardColor.Outline => dark ? (Color.FromArgb(0, 0, 0, 0), Color.FromArgb(110, 255, 255, 255)) : (Color.FromArgb(0, 255, 255, 255), Color.FromArgb(90, 0, 0, 0)),
            // Neutral gray (also stands in for the retired Accent and Opposite options).
            _ => dark ? (Color.FromArgb(235, 40, 40, 46), Color.FromArgb(26, 255, 255, 255)) : (Color.FromArgb(240, 246, 246, 248), Color.FromArgb(22, 0, 0, 0)),
        };
}

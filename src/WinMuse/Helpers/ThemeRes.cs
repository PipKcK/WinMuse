using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinMuse.Helpers;

/// <summary>
/// Resolves theme resources for an element's *actual* theme (the app can force light/dark independently of Windows,
/// so Application.Current.Resources[key] alone may return the wrong variant).
/// </summary>
public static class ThemeRes
{
    public static Brush Brush(string key, FrameworkElement? scope = null) => (Get(key, scope) as Brush) ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);

    /// <summary>
    /// WinUI's status and subtle fill colors (its own theme dictionaries can't be read from code, so a lookup would
    /// fall back to the Windows theme's variant: dark colors in a light window). Values from WinUI's theme resources.
    /// </summary>
    private static readonly Dictionary<string, (uint light, uint dark)> WinUiColors = new()
    {
        ["SystemFillColorSuccessBrush"] = (0xFF0F7B0F, 0xFF6CCB5F),
        ["SystemFillColorCautionBrush"] = (0xFF9D5D00, 0xFFFCE100),
        ["SystemFillColorCriticalBrush"] = (0xFFC42B1C, 0xFFFF99A4),
        ["SystemFillColorSuccessBackgroundBrush"] = (0xFFDFF6DD, 0xFF393D1B),
        ["SystemFillColorCautionBackgroundBrush"] = (0xFFFFF4CE, 0xFF433519),
        ["SystemFillColorCriticalBackgroundBrush"] = (0xFFFDE7E9, 0xFF442726),
        ["SubtleFillColorSecondaryBrush"] = (0x09000000, 0x0FFFFFFF),
        ["SubtleFillColorTertiaryBrush"] = (0x06000000, 0x0AFFFFFF),
    };

    public static object? Get(string key, FrameworkElement? scope = null)
    {
        scope ??= App.MainWindow?.Content as FrameworkElement;
        var themeKey = scope?.ActualTheme == ElementTheme.Light ? "Light" : scope?.ActualTheme == ElementTheme.Dark ? "Dark" : null;
        if (themeKey != null && WinUiColors.TryGetValue(key, out var pair))
        {
            var argb = themeKey == "Light" ? pair.light : pair.dark;
            return new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
        }
        if (themeKey != null)
        {
            // WinUI's own dictionaries key the dark theme as "Default".
            var keys = themeKey == "Dark" ? new[] { "Dark", "Default" } : new[] { "Light" };
            foreach (var dict in Walk(Application.Current.Resources))
                foreach (var k in keys)
                    if (dict.ThemeDictionaries.TryGetValue(k, out var td) && td is ResourceDictionary rd && rd.TryGetValue(key, out var v))
                        return v;
        }
        return Application.Current.Resources.TryGetValue(key, out var x) ? x : null;
    }

    // In XAML's lookup order: a dictionary, then its merged dictionaries from the last one merged to the first. The app's
    // own brushes (Styles.xaml, recolored with the accent) therefore win over WinUI's defaults (XamlControlsResources).
    private static IEnumerable<ResourceDictionary> Walk(ResourceDictionary root)
    {
        yield return root;
        for (int i = root.MergedDictionaries.Count - 1; i >= 0; i--)
            foreach (var d in Walk(root.MergedDictionaries[i])) yield return d;
    }
}

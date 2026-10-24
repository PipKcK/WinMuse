using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;
using WinMuse.Helpers;
using WinMuse.Models;

namespace WinMuse.Services;

/// <summary>Theme (including OLED true black), the Mica Alt backdrop, accent color and the album-derived ambient palette.</summary>
public sealed partial class ThemeService : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly UISettings _ui = new();
    private Window? _window;
    private FrameworkElement? _root;
    private int _paletteVersion;

    /// <summary>Four colors used by ambient backgrounds (album art or the custom gradient).</summary>
    [ObservableProperty] public partial Color[] Palette { get; private set; }
    [ObservableProperty] public partial Color Accent { get; private set; }

    public ThemeService(SettingsService settings)
    {
        _settings = settings;
        Palette = GradientPalette();
        _ui.ColorValuesChanged += (_, _) => App.UI.TryEnqueue(() =>
        {
            if (_settings.Current.AccentSource == AccentSource.Windows) ApplyAccent();
            if (_settings.Current.Theme == AppTheme.System) ApplyTitleBarColors();
        });
    }

    public bool IsDark => _root?.ActualTheme == ElementTheme.Dark;
    public bool IsOled => _settings.Current.Theme == AppTheme.Oled;

    public void Attach(Window window, FrameworkElement root)
    {
        _window = window;
        _root = root;
        _root.ActualThemeChanged += (_, _) => { ApplyTitleBarColors(); ApplyAccent(); };
        InstallControlAccentAliases();
        // No theme "flip" is needed to refresh accents: the app owns the accent brushes and recolors them in place.
        ApplyTheme();
        ApplyBackdrop();
        ApplyAccent();
    }

    public void ApplyTheme()
    {
        if (_root == null) return;
        _root.RequestedTheme = _settings.Current.Theme switch
        {
            AppTheme.Light => ElementTheme.Light,
            AppTheme.Dark or AppTheme.Oled => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ApplyTitleBarColors();
        ApplyBackdrop();
        ApplyAccent();
    }

    /// <summary>Mica Alt always; OLED replaces it with true black.</summary>
    public void ApplyBackdrop()
    {
        if (_window == null) return;
        if (IsOled) _window.SystemBackdrop = null;
        else if (_window.SystemBackdrop is not MicaBackdrop) _window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
        if (_root is Microsoft.UI.Xaml.Controls.Panel p)
            p.Background = new SolidColorBrush(IsOled ? Colors.Black : Colors.Transparent);
    }

    private void ApplyTitleBarColors()
    {
        if (_window?.AppWindow?.TitleBar is not { } tb) return;
        var dark = IsDark;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        tb.ButtonInactiveForegroundColor = dark ? Color.FromArgb(255, 150, 150, 150) : Color.FromArgb(255, 120, 120, 120);
        tb.ButtonHoverBackgroundColor = dark ? Color.FromArgb(24, 255, 255, 255) : Color.FromArgb(20, 0, 0, 0);
        tb.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        tb.ButtonPressedBackgroundColor = dark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(12, 0, 0, 0);
        tb.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
    }

    // ---------------- Accent ----------------

    private Color? _albumAccent;

    public void ApplyAccent()
    {
        var s = _settings.Current;
        Color baseColor = s.AccentSource switch
        {
            AccentSource.Custom => ColorKit.Parse(s.CustomAccent, Color.FromArgb(255, 139, 92, 246)),
            AccentSource.AlbumArt when _albumAccent is { } a => a,
            _ => _ui.GetColorValue(UIColorType.Accent),
        };
        SetAccentResources(baseColor);
    }

    // ---------------- Control accent aliases ----------------

    /// <summary>
    /// WinUI's control brushes (toggle switch, slider, check box, accent button, selection pills, progress...) are
    /// aliases created once from the Windows accent. Re-pointing them at the app-owned accent brushes makes the app
    /// accent apply everywhere. "Alias=Target" where Target is AccentFillColor{Default|Secondary|Tertiary}Brush,
    /// AccentTextFillColor{Primary|Secondary|Tertiary}Brush or AccentFillColorSelectedTextBackgroundBrush.
    /// </summary>
    private static readonly string[] ControlAccentAliases =
    {
        "SliderThumbBackground=Default SliderThumbBackgroundPointerOver=Secondary SliderThumbBackgroundPressed=Tertiary",
        "SliderTrackValueFill=Default SliderTrackValueFillPointerOver=Secondary SliderTrackValueFillPressed=Tertiary",
        "RadioButtonOuterEllipseCheckedStroke=Default RadioButtonOuterEllipseCheckedStrokePointerOver=Secondary RadioButtonOuterEllipseCheckedStrokePressed=Tertiary",
        "RadioButtonOuterEllipseCheckedFill=Default RadioButtonOuterEllipseCheckedFillPointerOver=Secondary RadioButtonOuterEllipseCheckedFillPressed=Tertiary",
        "CheckBoxCheckBackgroundStrokeChecked=Default CheckBoxCheckBackgroundStrokeCheckedPointerOver=Secondary CheckBoxCheckBackgroundStrokeCheckedPressed=Tertiary",
        "CheckBoxCheckBackgroundStrokeIndeterminate=Default CheckBoxCheckBackgroundStrokeIndeterminatePointerOver=Secondary CheckBoxCheckBackgroundStrokeIndeterminatePressed=Tertiary",
        "CheckBoxCheckBackgroundFillChecked=Default CheckBoxCheckBackgroundFillCheckedPointerOver=Secondary CheckBoxCheckBackgroundFillCheckedPressed=Tertiary",
        "CheckBoxCheckBackgroundFillIndeterminate=Default CheckBoxCheckBackgroundFillIndeterminatePointerOver=Secondary CheckBoxCheckBackgroundFillIndeterminatePressed=Tertiary",
        "HyperlinkButtonForeground=TextPrimary HyperlinkButtonForegroundPointerOver=TextSecondary HyperlinkButtonForegroundPressed=TextTertiary",
        "ToggleSwitchFillOn=Default ToggleSwitchFillOnPointerOver=Secondary ToggleSwitchFillOnPressed=Tertiary",
        "ToggleSwitchStrokeOn=Default ToggleSwitchStrokeOnPointerOver=Secondary ToggleSwitchStrokeOnPressed=Tertiary",
        "ToggleButtonBackgroundChecked=Default ToggleButtonBackgroundCheckedPointerOver=Secondary ToggleButtonBackgroundCheckedPressed=Tertiary",
        "AccentButtonBackground=Default AccentButtonBackgroundPointerOver=Secondary AccentButtonBackgroundPressed=Tertiary",
        "CalendarViewFocusBorderBrush=Secondary CalendarViewSelectedHoverBorderBrush=Secondary CalendarViewSelectedBorderBrush=Default CalendarViewSelectedForeground=TextPrimary",
        "TextControlSelectionHighlightColor=SelectedText PivotHeaderItemSelectedPipeFill=Default",
        "HyperlinkForeground=TextPrimary HyperlinkForegroundPointerOver=TextSecondary HyperlinkForegroundPressed=TextTertiary",
        "RatingControlSelectedForeground=Default RatingControlPointerOverSelectedForeground=Default NavigationViewSelectionIndicatorForeground=Default",
        "PersonPictureEllipseBadgeFillThemeBrush=Default",
        "AppBarToggleButtonBackgroundChecked=Default AppBarToggleButtonBackgroundCheckedPointerOver=Secondary AppBarToggleButtonBackgroundCheckedPressed=Tertiary",
        "SwipeItemPostThresholdExecuteBackground=Default",
        "SplitButtonBackgroundChecked=Default SplitButtonBackgroundCheckedPointerOver=Secondary SplitButtonBackgroundCheckedPressed=Tertiary",
        "GridViewItemSelectedBorderBrush=Default GridViewItemSelectedPointerOverBorderBrush=Secondary GridViewItemSelectedPressedBorderBrush=Tertiary",
        "GridViewItemCheckBoxSelectedBrush=Default GridViewItemCheckBoxSelectedPointerOverBrush=Secondary GridViewItemCheckBoxSelectedPressedBrush=Tertiary",
        "ListViewItemMultiArrangeOverlayTextBackground=Default",
        "ListViewItemCheckBoxSelectedBrush=Default ListViewItemCheckBoxSelectedPointerOverBrush=Secondary ListViewItemCheckBoxSelectedPressedBrush=Tertiary",
        "ListViewItemSelectionIndicatorBrush=Default ListViewItemSelectionIndicatorPointerOverBrush=Default ListViewItemSelectionIndicatorPressedBrush=Default",
        "CalendarViewSelectedHoverForeground=TextPrimary CalendarViewSelectedPressedForeground=TextTertiary",
        "CalendarViewTodayBackground=Default CalendarViewTodayBlackoutBackground=Tertiary CalendarViewTodayHoverBackground=Secondary CalendarViewTodayPressedBackground=Tertiary",
        "InfoBadgeBackground=Default",
        "CommandBarFlyoutAppBarButtonBackgroundChecked=Default CommandBarFlyoutAppBarButtonBackgroundCheckedPointerOver=Secondary CommandBarFlyoutAppBarButtonBackgroundCheckedPressed=Tertiary",
        "TreeViewItemSelectionIndicatorForeground=Default TreeViewItemSelectionIndicatorForegroundPointerOver=Default TreeViewItemSelectionIndicatorForegroundPressed=Default",
        "ComboBoxItemPillFillBrush=Default ProgressBarForeground=Default ProgressRingForegroundThemeBrush=Default PagerControlSelectionIndicatorForeground=Default",
        "InfoBarHyperlinkButtonForeground=TextPrimary",
        "ItemContainerSelectionVisualBackground=Default ItemContainerSelectionVisualPointerOverBackground=Default ItemContainerSelectionVisualPressedBackground=Default",
        "VerticalThumbBrush=Default SelectorBarItemPillFill=Default",
        "SystemControlHighlightAccentBrush=Default SystemControlForegroundAccentBrush=Default SystemControlBackgroundAccentBrush=Default SystemControlHyperlinkTextBrush=TextPrimary",
    };

    private readonly Dictionary<string, LinearGradientBrush> _focusBorders = new();

    private void InstallControlAccentAliases()
    {
        var owner = Walk(Application.Current.Resources).FirstOrDefault(d => d is not Microsoft.UI.Xaml.Controls.XamlControlsResources
            && d.ThemeDictionaries.TryGetValue("Dark", out var td) && td is ResourceDictionary rd && rd.ContainsKey("AccentFillColorDefaultBrush"));
        if (owner == null) return;
        foreach (var theme in new[] { "Light", "Dark" })
        {
            if (!owner.ThemeDictionaries.TryGetValue(theme, out var td) || td is not ResourceDictionary rd) continue;
            foreach (var pair in ControlAccentAliases.SelectMany(l => l.Split(' ')))
            {
                var (alias, target) = (pair[..pair.IndexOf('=')], pair[(pair.IndexOf('=') + 1)..]);
                var key = target switch
                {
                    "SelectedText" => "AccentFillColorSelectedTextBackgroundBrush",
                    _ when target.StartsWith("Text") => $"AccentTextFillColor{target[4..]}Brush",
                    _ => $"AccentFillColor{target}Brush",
                };
                if (rd.TryGetValue(key, out var brush)) rd[alias] = brush;
            }
            // Text box focus underline: accent at the bottom edge, like WinUI's own brush.
            var focus = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute, StartPoint = new(0, 0), EndPoint = new(0, 2),
                RelativeTransform = new ScaleTransform { ScaleY = -1, CenterY = 0.5 },
                GradientStops =
                {
                    new GradientStop { Offset = 1, Color = theme == "Dark" ? Color.FromArgb(255, 96, 205, 255) : Color.FromArgb(255, 0, 95, 184) },
                    new GradientStop { Offset = 1, Color = theme == "Dark" ? Color.FromArgb(0x12, 255, 255, 255) : Color.FromArgb(0x0F, 0, 0, 0) },
                },
            };
            rd["TextControlElevationBorderFocusedBrush"] = focus;
            rd["TextControlBorderBrushFocused"] = focus;
            _focusBorders[theme] = focus;
        }
    }

    private void SetAccentResources(Color c)
    {
        var res = Application.Current.Resources;
        var dark = IsDark;
        var (h, sat, l) = ColorKit.ToHsl(c);
        sat = Math.Max(sat, 0.3);
        var tuned = s_adjust(c, dark);

        // A full light/dark ramp like Windows generates for the system accent.
        Color Ramp(double light) => ColorKit.FromHsl(h, sat, light);
        var ramp = new Dictionary<string, Color>
        {
            ["SystemAccentColor"] = Ramp(0.47),
            ["SystemAccentColorLight1"] = Ramp(0.58), ["SystemAccentColorLight2"] = Ramp(0.68), ["SystemAccentColorLight3"] = Ramp(0.80),
            ["SystemAccentColorDark1"] = Ramp(0.38), ["SystemAccentColorDark2"] = Ramp(0.28), ["SystemAccentColorDark3"] = Ramp(0.18),
        };
        foreach (var (k, v) in ramp) res[k] = v;

        // Recolor the app-owned accent brushes in both theme dictionaries; every control using them updates live.
        var perTheme = new Dictionary<string, Dictionary<string, Color>>
        {
            ["Light"] = new()
            {
                ["AccentFillColorDefaultBrush"] = ramp["SystemAccentColorDark1"],
                ["AccentFillColorSecondaryBrush"] = ramp["SystemAccentColorDark1"],
                ["AccentFillColorTertiaryBrush"] = ramp["SystemAccentColorDark1"],
                ["AccentTextFillColorPrimaryBrush"] = ramp["SystemAccentColorDark2"],
                ["AccentTextFillColorSecondaryBrush"] = ramp["SystemAccentColorDark3"],
                ["AccentTextFillColorTertiaryBrush"] = ramp["SystemAccentColorDark1"],
                ["AccentFillColorSelectedTextBackgroundBrush"] = ramp["SystemAccentColor"],
            },
            ["Dark"] = new()
            {
                ["AccentFillColorDefaultBrush"] = ramp["SystemAccentColorLight2"],
                ["AccentFillColorSecondaryBrush"] = ramp["SystemAccentColorLight2"],
                ["AccentFillColorTertiaryBrush"] = ramp["SystemAccentColorLight2"],
                ["AccentTextFillColorPrimaryBrush"] = ramp["SystemAccentColorLight3"],
                ["AccentTextFillColorSecondaryBrush"] = ramp["SystemAccentColorLight3"],
                ["AccentTextFillColorTertiaryBrush"] = ramp["SystemAccentColorLight2"],
                ["AccentFillColorSelectedTextBackgroundBrush"] = ramp["SystemAccentColor"],
            },
        };
        foreach (var dict in Walk(res))
        {
            foreach (var (theme, brushes) in perTheme)
            {
                if (!dict.ThemeDictionaries.TryGetValue(theme, out var td) || td is not ResourceDictionary rd) continue;
                foreach (var (key, color) in brushes)
                    if (rd.TryGetValue(key, out var b) && b is SolidColorBrush scb) scb.Color = color;
            }
        }
        foreach (var (theme, brush) in _focusBorders) brush.GradientStops[0].Color = perTheme[theme]["AccentFillColorDefaultBrush"];

        // App-specific gradient brushes are mutated in place too.
        if (res.TryGetValue("AccentGradientBrush", out var gb) && gb is LinearGradientBrush g && g.GradientStops.Count >= 2)
        {
            var (th, ts, tl) = ColorKit.ToHsl(tuned);
            // Blend into the new colors (the accent follows the album art, so it changes with every song).
            ColorTween.To(g.GradientStops[0], ColorKit.FromHsl(th - 18, Math.Min(1, ts + 0.1), tl));
            ColorTween.To(g.GradientStops[1], ColorKit.FromHsl(th + 22, Math.Min(1, ts + 0.05), Math.Max(0.3, tl - 0.06)));
        }
        if (res.TryGetValue("AccentGlowBrush", out var glow) && glow is SolidColorBrush gs) ColorTween.To(gs, ColorKit.WithAlpha(tuned, 90));

        Accent = tuned;
    }

    private static IEnumerable<ResourceDictionary> Walk(ResourceDictionary root)
    {
        yield return root;
        foreach (var m in root.MergedDictionaries)
            foreach (var d in Walk(m)) yield return d;
    }

    private static Color s_adjust(Color c, bool dark) => ColorKit.AdjustForTheme(c, dark, 0.3);

    // ---------------- Palette ----------------

    public Color[] GradientPalette()
    {
        var s = _settings.Current;
        var c1 = ColorKit.Parse(s.GradientColor1, Color.FromArgb(255, 109, 93, 252));
        var c2 = ColorKit.Parse(s.GradientColor2, Color.FromArgb(255, 224, 86, 155));
        var c3 = ColorKit.Parse(s.GradientColor3, Color.FromArgb(255, 34, 195, 230));
        return new[] { c1, c2, c3, ColorKit.Lighten(c1, 0.1) };
    }

    public async Task UpdateFromTrackAsync(Track? track)
    {
        var version = ++_paletteVersion;
        var palette = await ColorKit.ExtractPaletteAsync(track?.ArtPath);
        if (version != _paletteVersion) return;

        if (palette.Length == 0)
        {
            var (a, b) = ColorKit.GradientFor(track?.AlbumKey ?? "music");
            palette = new[] { a, b, ColorKit.Lighten(a, 0.15), ColorKit.Lighten(b, -0.1) };
        }

        _albumAccent = ColorKit.PickAccent(palette);
        Palette = _settings.Current.BackgroundMode == BackgroundMode.Gradient ? GradientPalette() : palette;
        AlbumPalette = palette;
        if (_settings.Current.AccentSource == AccentSource.AlbumArt) ApplyAccent();
    }

    /// <summary>Palette of the current album regardless of the background mode (used by Now Playing).</summary>
    public Color[] AlbumPalette { get; private set; } = Array.Empty<Color>();

    public void RefreshPaletteForMode()
    {
        Palette = _settings.Current.BackgroundMode == BackgroundMode.Gradient || AlbumPalette.Length == 0 ? GradientPalette() : AlbumPalette;
    }
}

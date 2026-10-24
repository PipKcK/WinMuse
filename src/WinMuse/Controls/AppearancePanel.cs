using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Controls;

/// <summary>
/// Quick appearance controls shown from Home: Theme, Card style and Greeting, as tiles with small previews.
/// The selected tile is highlighted with the accent and the change applies (and blends in) right away.
/// </summary>
public sealed class AppearancePanel : UserControl
{
    private sealed record Choice(int Value, string Label, FrameworkElement Preview);

    private readonly List<(Border tile, int value, Func<int> current)> _tiles = new();
    private static readonly List<(Border card, ShelfCardColor style)> CardPreviews = new();

    public AppearancePanel()
    {
        var root = new StackPanel { Spacing = 18, Width = 452, Padding = new Thickness(4, 2, 4, 4) };
        root.Children.Add(new TextBlock { Text = "Appearance", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] });

        root.Children.Add(Section("Theme",
            new[]
            {
                new Choice((int)AppTheme.System, "System", ThemeSwatch(Color.FromArgb(255, 243, 243, 243), Color.FromArgb(255, 32, 32, 36), split: true)),
                new Choice((int)AppTheme.Light, "Light", ThemeSwatch(Color.FromArgb(255, 245, 245, 247), Color.FromArgb(255, 245, 245, 247))),
                new Choice((int)AppTheme.Dark, "Dark", ThemeSwatch(Color.FromArgb(255, 36, 36, 40), Color.FromArgb(255, 36, 36, 40))),
                new Choice((int)AppTheme.Oled, "OLED", ThemeSwatch(Colors.Black, Colors.Black)),
            },
            () => (int)Svc.Settings.Current.Theme,
            v =>
            {
                // Picking a theme here also returns accent and background to the album art (a quick "reset to the
                // album look"); Settings keeps finer control.
                Svc.Settings.Set(nameof(AppSettings.Theme), s => s.Theme = (AppTheme)v);
                if (Svc.Settings.Current.AccentSource != AccentSource.AlbumArt)
                    Svc.Settings.Set(nameof(AppSettings.AccentSource), s => s.AccentSource = AccentSource.AlbumArt);
                if (Svc.Settings.Current.BackgroundMode != BackgroundMode.AlbumArt)
                    Svc.Settings.Set(nameof(AppSettings.BackgroundMode), s => s.BackgroundMode = BackgroundMode.AlbumArt);
            }));

        var styles = new[] { ShelfCardColor.Neutral, ShelfCardColor.Glass, ShelfCardColor.Smoke, ShelfCardColor.Slate, ShelfCardColor.Elevated, ShelfCardColor.Outline, ShelfCardColor.None };
        string[] names = { "Neutral", "Glass", "Smoke", "Slate", "Elevated", "Outline", "None" };
        root.Children.Add(Section("Card style",
            styles.Select((st, i) => new Choice((int)st, names[i], CardSwatch(st))).ToArray(),
            () => (int)Svc.Settings.Current.ShelfCardColor,
            v => Svc.Settings.Set(nameof(AppSettings.ShelfCardColor), s => s.ShelfCardColor = (ShelfCardColor)v),
            columns: 4));

        root.Children.Add(Section("Greeting",
            new[]
            {
                new Choice((int)GreetingStyle.Banner, "Banner", GreetingSwatch(banner: true)),
                new Choice((int)GreetingStyle.Gradient, "Gradient", GreetingSwatch(banner: false)),
                new Choice((int)GreetingStyle.None, "None", GreetingSwatch(banner: false, plain: true)),
            },
            () => (int)Svc.Settings.Current.GreetingStyle,
            v => Svc.Settings.Set(nameof(AppSettings.GreetingStyle), s => s.GreetingStyle = (GreetingStyle)v),
            columns: 3));

        Content = root;
        Loaded += (_, _) => Refresh();
        // Previews and highlight follow the theme and accent.
        Svc.Theme.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ThemeService.Accent)) DispatcherQueue.TryEnqueue(Refresh); };
        ActualThemeChanged += (_, _) => Refresh();
    }

    private FrameworkElement Section(string title, Choice[] choices, Func<int> current, Action<int> set, int columns = 4)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Opacity = 0.75 });
        var grid = new Grid { ColumnSpacing = 8, RowSpacing = 8 };
        for (int i = 0; i < columns; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < (choices.Length + columns - 1) / columns; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int i = 0; i < choices.Length; i++)
        {
            var c = choices[i];
            var tile = Tile(c, () => { set(c.Value); Refresh(); });
            Grid.SetColumn(tile, i % columns);
            Grid.SetRow(tile, i / columns);
            grid.Children.Add(tile);
            _tiles.Add((tile, c.Value, current));
        }
        panel.Children.Add(grid);
        return panel;
    }

    private static Border Tile(Choice c, Action pick)
    {
        var content = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(c.Preview);
        content.Children.Add(new TextBlock { Text = c.Label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center });
        var tile = new Border
        {
            Child = content, Padding = new Thickness(6, 10, 6, 8), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5),
            Background = new SolidColorBrush(Colors.Transparent), BorderBrush = new SolidColorBrush(Colors.Transparent),
            // Selection and hover ease in.
            BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(220) },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(tile, c.Label);
        tile.Tapped += (_, _) => pick();
        tile.PointerEntered += (_, _) => { tile.CenterPoint = new Vector3((float)tile.ActualWidth / 2, (float)tile.ActualHeight / 2, 0); tile.Scale = new Vector3(1.04f, 1.04f, 1); };
        tile.PointerExited += (_, _) => tile.Scale = Vector3.One;
        tile.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
        return tile;
    }

    private void Refresh()
    {
        var accent = Svc.Theme.Accent;
        foreach (var (card, style) in CardPreviews)
        {
            var (fill, stroke) = ShelfCardColors.For(style, Svc.Theme.IsDark);
            ((SolidColorBrush)card.Background).Color = fill;
            ((SolidColorBrush)card.BorderBrush).Color = stroke;
        }
        foreach (var (tile, value, current) in _tiles)
        {
            bool on = current() == value;
            ((SolidColorBrush)tile.Background).Color = on ? ColorKit.WithAlpha(accent, 46) : (Svc.Theme.IsDark ? Color.FromArgb(14, 255, 255, 255) : Color.FromArgb(10, 0, 0, 0));
            ((SolidColorBrush)tile.BorderBrush).Color = on ? accent : Colors.Transparent;
        }
    }

    // ---------------- Previews ----------------

    /// <summary>A tiny window: title bar strip and two content lines.</summary>
    private static FrameworkElement ThemeSwatch(Color a, Color b, bool split = false)
    {
        bool darkA = a.R < 128;
        var g = new Grid { Width = 72, Height = 46, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128)) };
        g.Background = split
            ? new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1), GradientStops = { new GradientStop { Color = a, Offset = 0.49 }, new GradientStop { Color = b, Offset = 0.51 } } } // a hair of blend: a hard stop renders jagged
            : new SolidColorBrush(a);
        var line = new SolidColorBrush(darkA && !split ? Color.FromArgb(70, 255, 255, 255) : Color.FromArgb(60, 0, 0, 0));
        var lines = new StackPanel { Spacing = 4, Margin = new Thickness(8, 9, 8, 0) };
        lines.Children.Add(new Border { Height = 5, Width = 34, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, Background = Accentish() });
        lines.Children.Add(new Border { Height = 4, Width = 46, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Background = line });
        lines.Children.Add(new Border { Height = 4, Width = 28, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, Background = line });
        g.Children.Add(lines);
        return g;
    }

    /// <summary>A tiny card in the style itself, on a backdrop like Home's.</summary>
    private static FrameworkElement CardSwatch(ShelfCardColor style)
    {
        var (fill, stroke) = ShelfCardColors.For(style, Svc.Theme.IsDark);
        CardPreviews.RemoveAll(p => p.style == style);
        var backdrop = new Grid { Width = 50, Height = 56, CornerRadius = new CornerRadius(6), Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"] };
        var card = new Border
        {
            Width = 32, Height = 42, CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1.5),
            Background = new SolidColorBrush(fill), BorderBrush = new SolidColorBrush(stroke),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new Border { Height = 24, VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(4, 4, 0, 0), Background = Accentish() },
        };
        backdrop.Children.Add(card);
        CardPreviews.Add((card, style));
        return backdrop;
    }

    /// <summary>Banner: a colored card with text. Gradient: color fading down behind text.</summary>
    private static FrameworkElement GreetingSwatch(bool banner, bool plain = false)
    {
        var g = new Grid { Width = 120, Height = 52, CornerRadius = new CornerRadius(6), Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"] };
        var a = Svc.Theme.Accent;
        var (h, s, _) = ColorKit.ToHsl(a);
        var c1 = ColorKit.FromHsl(h - 30, Math.Max(0.55, s), 0.5);
        var c2 = ColorKit.FromHsl(h + 50, Math.Max(0.6, s), 0.58);
        if (banner)
            g.Children.Add(new Border
            {
                Margin = new Thickness(7, 7, 7, 17), CornerRadius = new CornerRadius(4),
                Background = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1), GradientStops = { new GradientStop { Color = c1, Offset = 0 }, new GradientStop { Color = c2, Offset = 1 } } },
            });
        else if (!plain)
            g.Children.Add(new Border
            {
                Background = new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(0, 1), GradientStops = { new GradientStop { Color = ColorKit.WithAlpha(c1, 200), Offset = 0 }, new GradientStop { Color = ColorKit.WithAlpha(c1, 0), Offset = 1 } } },
            });
        var text = new StackPanel { Spacing = 3, Margin = new Thickness(13, 13, 0, 0) };
        var ink = plain ? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"] : new SolidColorBrush(Colors.White);
        var ink2 = plain ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] : new SolidColorBrush(Color.FromArgb(170, 255, 255, 255));
        text.Children.Add(new Border { Height = 5, Width = 40, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, Background = ink });
        text.Children.Add(new Border { Height = 3, Width = 28, CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Background = ink2 });
        g.Children.Add(text);
        g.Children.Add(new Border { Height = 6, Margin = new Thickness(7, 0, 7, 5), VerticalAlignment = VerticalAlignment.Bottom, CornerRadius = new CornerRadius(2), Background = (Brush)Application.Current.Resources["SubtleFillColorTertiaryBrush"] });
        return g;
    }

    private static Brush Accentish() => (Brush)Application.Current.Resources["AccentGradientBrush"];
}

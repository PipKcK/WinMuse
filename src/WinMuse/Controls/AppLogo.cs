using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Controls;

/// <summary>
/// The app's logo, drawn as vectors so it takes on the accent color: every color of the design is re-hued to the
/// accent (lightness kept, so the layered rings stay light-to-dark). Two designs: Rings (concentric rings behind a
/// rounded play tile) and Simple (one ring, larger tile; reads better at small sizes).
/// </summary>
public sealed class AppLogo : UserControl
{
    // The designs' own palette (indigo); BaseHue/BaseSat describe it, so recoloring keeps their relationships.
    private const double BaseHue = 229, BaseSat = 0.36;

    private readonly Canvas _canvas = new() { Width = 256, Height = 256 };
    private readonly List<(GradientStop stop, Color original)> _stops = new();
    private readonly List<(SolidColorBrush brush, Color original)> _solids = new();
    private AppLogoStyle _style = AppLogoStyle.Rings;

    public AppLogo()
    {
        Content = new Viewbox { Child = _canvas, Stretch = Stretch.Uniform };
        IsTabStop = false;
        Build();
        void OnAccent(object? s, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(ThemeService.Accent)) DispatcherQueue.TryEnqueue(Recolor); }
        Loaded += (_, _) => { Svc.Theme.PropertyChanged += OnAccent; Recolor(); };
        Unloaded += (_, _) => Svc.Theme.PropertyChanged -= OnAccent;
    }

    public AppLogoStyle LogoStyle
    {
        get => _style;
        set { if (_style == value) return; _style = value; Build(); Recolor(); }
    }

    private LinearGradientBrush Gradient(string from, string to)
    {
        var a = new GradientStop { Offset = 0, Color = Hex(from) };
        var b = new GradientStop { Offset = 1, Color = Hex(to) };
        _stops.Add((a, a.Color)); _stops.Add((b, b.Color));
        return new LinearGradientBrush { StartPoint = new(0, 0), EndPoint = new(1, 1), GradientStops = { a, b } };
    }

    private SolidColorBrush Solid(string color)
    {
        var b = new SolidColorBrush(Hex(color));
        _solids.Add((b, b.Color));
        return b;
    }

    private static Color Hex(string s) => Color.FromArgb(255, Convert.ToByte(s[1..3], 16), Convert.ToByte(s[3..5], 16), Convert.ToByte(s[5..7], 16));

    private static Ellipse Circle(double cx, double cy, double r, Brush fill)
    {
        var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = fill };
        Canvas.SetLeft(e, cx - r); Canvas.SetTop(e, cy - r);
        return e;
    }

    private static Rectangle Tile(double x, double y, double size, double radius, Brush fill)
    {
        var r = new Rectangle { Width = size, Height = size, RadiusX = radius, RadiusY = radius, Fill = fill };
        Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
        return r;
    }

    private static Microsoft.UI.Xaml.Shapes.Path Play(double x1, double y1, double y2, double tipX, double stroke)
    {
        var fig = new PathFigure { StartPoint = new(x1, y1), IsClosed = true, IsFilled = true };
        fig.Segments.Add(new LineSegment { Point = new(x1, y2) });
        fig.Segments.Add(new LineSegment { Point = new(tipX, (y1 + y2) / 2) });
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = new PathGeometry { Figures = { fig } },
            Fill = white, Stroke = white, StrokeThickness = stroke, StrokeLineJoin = PenLineJoin.Round,
        };
    }

    /// <summary>The two SVG designs, shape for shape (the soft drop shadow is left out: invisible at title-bar size).</summary>
    private void Build()
    {
        _canvas.Children.Clear();
        _stops.Clear(); _solids.Clear();
        if (_style == AppLogoStyle.Image)
        {
            // The app's own icon file, unchanged (e.g. a custom icon from Build-App).
            var img = new Image { Width = 256, Height = 256, Stretch = Stretch.Uniform,
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(System.IO.Path.Combine(AppPaths.AssetsDir, "AppIcon.png"))) { DecodePixelWidth = 64 } };
            _canvas.Children.Add(img);
            _canvas.RenderTransform = null;
            return;
        }
        if (_style == AppLogoStyle.Simple)
        {
            _canvas.Children.Add(Circle(168, 128, 88, Gradient("#5565af", "#45539c")));
            _canvas.Children.Add(Circle(168, 128, 38, Solid("#140e67")));
            _canvas.Children.Add(Tile(0, 48, 160, 28, Gradient("#272e89", "#060037")));
            _canvas.Children.Add(Play(52, 84, 172, 124, 14));
            _canvas.RenderTransform = null;
            return;
        }
        var group = new Canvas { RenderTransform = new MatrixTransform { Matrix = new Matrix(1.148, 0, 0, 1.148, -23.55, -18.94) } };
        group.Children.Add(Circle(148, 128, 92, Gradient("#7f8ec9", "#6e7cb5")));
        group.Children.Add(Circle(148, 128, 70, Gradient("#5565af", "#45539c")));
        group.Children.Add(Circle(148, 128, 48, Gradient("#333f92", "#262e7f")));
        group.Children.Add(Circle(148, 128, 26, Gradient("#1f2379", "#140e67")));
        group.Children.Add(Tile(24, 72, 112, 14, Gradient("#272e89", "#060037")));
        group.Children.Add(Play(61, 100, 156, 109, 10));
        _canvas.Children.Add(group);
    }

    /// <summary>Re-hues every color to the accent, keeping each one's lightness (and its saturation relative to the design's).</summary>
    private void Recolor()
    {
        var (h, s, _) = ColorKit.ToHsl(Svc.Theme.Accent);
        double satScale = Math.Clamp(s / BaseSat, 0.25, 2.2);
        Color Map(Color c)
        {
            var (ch, cs, cl) = ColorKit.ToHsl(c);
            return ColorKit.FromHsl(h + (ch - BaseHue), Math.Clamp(cs * satScale, 0, 1), cl);
        }
        foreach (var (stop, original) in _stops) stop.Color = Map(original);
        foreach (var (brush, original) in _solids) brush.Color = Map(original);
    }
}

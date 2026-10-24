using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Controls;

/// <summary>Real-time spectrum visualizer drawn with composition shapes, fed by the playback pipeline's FFT.</summary>
public sealed class Visualizer : Grid
{
    private Compositor? _c;
    private ShapeVisual? _shapes;
    private readonly List<(CompositionSpriteShape shape, CompositionRoundedRectangleGeometry geo)> _bars = new();
    private CompositionLinearGradientBrush? _fill;
    private float[] _levels = new float[48];
    private bool _rendering;

    public int BarCount { get; set; } = 48;
    public VisualizerStyle Style { get; set; } = VisualizerStyle.Bars;
    public double BarOpacity { get; set; } = 0.9;
    /// <summary>Level multiplier (behind the player bar the bars need more height to show through the blur).</summary>
    public double Gain { get; set; } = 1;
    /// <summary>Bars fade out toward the top instead of using a diagonal gradient (peaks dissolve as they rise).</summary>
    public bool FadeTop { get; set; }
    /// <summary>The per-frame loop only runs while active (owners turn it on when the visualizer is on screen).</summary>
    public bool IsActive
    {
        get => _active;
        set
        {
            _active = value;
            if (value && IsLoaded) Start(); else Stop();
        }
    }
    private bool _active;
    /// <summary>Average of the lowest bands from the last frame (0..1); drives the artwork "pulse".</summary>
    public float BassLevel { get; private set; }

    public Visualizer()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => { Build(); if (_active) Start(); };
        Unloaded += (_, _) => { Stop(); Teardown(); };
        // Now Playing starts hidden (zero size), so the bars are created on the first real size.
        SizeChanged += (_, e) => { if (e.NewSize.Width > 0 && e.NewSize.Height > 0) Rebuild(); };
    }

    public void SetColors(Color a, Color b)
    {
        if (_fill == null || _fill.ColorStops.Count < 2) return;
        if (FadeTop && _fill.ColorStops.Count == 3)
        {
            _fill.ColorStops[0].Color = a;
            _fill.ColorStops[1].Color = Helpers.ColorKit.WithAlpha(b, 170);
            _fill.ColorStops[2].Color = Helpers.ColorKit.WithAlpha(b, 0);
            return;
        }
        _fill.ColorStops[0].Color = a;
        _fill.ColorStops[1].Color = b;
    }

    public void Rebuild()
    {
        if (!IsLoaded) return;
        Teardown();
        Build();
    }

    private void Build()
    {
        if (_shapes != null || ActualWidth <= 0) return;
        _c = CompositionTarget.GetCompositorForCurrentThread();
        _shapes = _c.CreateShapeVisual();
        _shapes.Size = new Vector2((float)ActualWidth, (float)ActualHeight);
        _shapes.Opacity = (float)BarOpacity;

        _fill = _c.CreateLinearGradientBrush();
        _fill.MappingMode = CompositionMappingMode.Absolute;
        _fill.StartPoint = new Vector2(0, (float)ActualHeight);
        _fill.EndPoint = new Vector2(FadeTop ? 0 : (float)ActualWidth, 0);
        var accent = Svc.Theme.Accent;
        _fill.ColorStops.Add(_c.CreateColorGradientStop(0, accent));
        if (FadeTop) _fill.ColorStops.Add(_c.CreateColorGradientStop(0.65f, accent));
        _fill.ColorStops.Add(_c.CreateColorGradientStop(1, Helpers.ColorKit.Lighten(accent, 0.2)));
        SetColors(accent, Helpers.ColorKit.Lighten(accent, 0.2));

        int n = Math.Max(4, BarCount);
        _levels = new float[n];
        for (int i = 0; i < n; i++)
        {
            var geo = _c.CreateRoundedRectangleGeometry();
            var shape = _c.CreateSpriteShape(geo);
            shape.FillBrush = _fill;
            _shapes.Shapes.Add(shape);
            _bars.Add((shape, geo));
        }
        ElementCompositionPreview.SetElementChildVisual(this, _shapes);
    }

    private void Teardown()
    {
        ElementCompositionPreview.SetElementChildVisual(this, null);
        _bars.Clear();
        _shapes?.Dispose();
        _shapes = null;
    }

    private void Start()
    {
        if (_rendering) return;
        _rendering = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Stop()
    {
        if (!_rendering) return;
        _rendering = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, object e)
    {
        if (_shapes == null || _bars.Count == 0 || Visibility != Visibility.Visible || !IsActive) return;
        var player = Svc.Player;
        player.Spectrum.GetBands(_levels, player.IsPlaying);
        BassLevel = (_levels[0] + _levels[1] + _levels[2] + _levels[3]) / 4f;

        float w = (float)ActualWidth, h = (float)ActualHeight;
        int n = _bars.Count;
        float slot = w / n;
        float barW = Style == VisualizerStyle.Line ? MathF.Max(2, slot * 0.22f) : MathF.Max(2, slot * 0.58f);
        float radius = barW / 2;

        for (int i = 0; i < n; i++)
        {
            // mirror the spectrum around the centre for a symmetric look
            int src = Style == VisualizerStyle.Mirror ? Math.Min(n - 1, Math.Abs(i - n / 2) * 2) : i;
            float lv = MathF.Min(1, _levels[Math.Clamp(src, 0, n - 1)] * (float)Gain);
            float bh = MathF.Max(barW, lv * h);
            var (_, geo) = _bars[i];
            geo.Size = new Vector2(barW, bh);
            geo.CornerRadius = new Vector2(radius, radius);
            float x = i * slot + (slot - barW) / 2;
            float y = Style == VisualizerStyle.Mirror ? (h - bh) / 2 : h - bh;
            geo.Offset = new Vector2(x, y);
        }
    }
}

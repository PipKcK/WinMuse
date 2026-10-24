using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WinMuse.Controls;

/// <summary>
/// "Aurora" background: large soft radial-gradient blobs that drift slowly on the compositor thread and
/// cross-fade to new colors whenever the palette changes.
/// </summary>
public sealed class AmbientBackground : Grid
{
    private Compositor? _c;
    private ContainerVisual? _root;
    private SpriteVisual? _base;
    private CompositionColorBrush? _baseBrush;
    private readonly List<(SpriteVisual visual, CompositionColorGradientStop inner, CompositionColorGradientStop outer)> _blobs = new();
    private Color[] _palette = { Color.FromArgb(255, 109, 93, 252), Color.FromArgb(255, 224, 86, 155), Color.FromArgb(255, 34, 195, 230), Color.FromArgb(255, 120, 80, 220) };
    private bool _animate = true;

    // Relative anchor points the blobs drift between.
    private static readonly Vector2[][] Paths =
    {
        new[] { new Vector2(-0.15f, -0.2f), new Vector2(0.1f, 0.05f), new Vector2(-0.05f, 0.15f) },
        new[] { new Vector2(0.55f, -0.25f), new Vector2(0.35f, 0.0f), new Vector2(0.6f, 0.1f) },
        new[] { new Vector2(0.45f, 0.45f), new Vector2(0.6f, 0.3f), new Vector2(0.3f, 0.55f) },
        new[] { new Vector2(-0.2f, 0.5f), new Vector2(0.05f, 0.35f), new Vector2(0.1f, 0.65f) },
    };

    public double BaseDarkness { get; set; } = 0.55;

    /// <summary>In light theme the base layer is a pale tint instead of a dark one.</summary>
    public bool LightMode
    {
        get => _light;
        set { if (_light == value) return; _light = value; SetPalette(_palette, false); }
    }
    private bool _light;

    public AmbientBackground()
    {
        IsHitTestVisible = false;
        Loaded += (_, _) => Build();
        Unloaded += (_, _) => Teardown();
        SizeChanged += (_, _) => Layout();
    }

    public bool Animate
    {
        get => _animate;
        set { _animate = value; Layout(); }
    }

    private void Build()
    {
        if (_root != null) return;
        _c = CompositionTarget.GetCompositorForCurrentThread();
        _root = _c.CreateContainerVisual();
        _baseBrush = _c.CreateColorBrush(Darken(_palette[0]));
        _base = _c.CreateSpriteVisual();
        _base.Brush = _baseBrush;
        _root.Children.InsertAtBottom(_base);

        for (int i = 0; i < 4; i++)
        {
            var brush = _c.CreateRadialGradientBrush();
            brush.EllipseCenter = new Vector2(0.5f, 0.5f);
            brush.EllipseRadius = new Vector2(0.5f, 0.5f);
            var inner = _c.CreateColorGradientStop(0f, WithA(_palette[i], 235));
            var outer = _c.CreateColorGradientStop(1f, WithA(_palette[i], 0));
            brush.ColorStops.Add(inner);
            brush.ColorStops.Add(_c.CreateColorGradientStop(0.55f, WithA(_palette[i], 110)));
            brush.ColorStops.Add(outer);
            var v = _c.CreateSpriteVisual();
            v.Brush = brush;
            _root.Children.InsertAtTop(v);
            _blobs.Add((v, inner, outer));
        }
        ElementCompositionPreview.SetElementChildVisual(this, _root);
        Layout();
    }

    private void Teardown()
    {
        ElementCompositionPreview.SetElementChildVisual(this, null);
        _root?.Dispose();
        _root = null;
        _blobs.Clear();
    }

    private void Layout()
    {
        if (_c == null || _root == null || ActualWidth <= 0) return;
        var w = (float)ActualWidth; var h = (float)ActualHeight;
        _root.Size = new Vector2(w, h);
        _base!.Size = new Vector2(w, h);
        var size = MathF.Max(w, h) * 0.95f;

        for (int i = 0; i < _blobs.Count; i++)
        {
            var v = _blobs[i].visual;
            v.Size = new Vector2(size, size);
            var path = Paths[i];
            v.Offset = new Vector3(path[0].X * w, path[0].Y * h, 0);
            v.StopAnimation("Offset");
            v.StopAnimation("Scale");
            if (!_animate) continue;

            var anim = _c.CreateVector3KeyFrameAnimation();
            var ease = _c.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f));
            for (int k = 0; k < path.Length; k++)
                anim.InsertKeyFrame(k / (float)path.Length, new Vector3(path[k].X * w, path[k].Y * h, 0), ease);
            anim.InsertKeyFrame(1f, new Vector3(path[0].X * w, path[0].Y * h, 0), ease);
            anim.Duration = TimeSpan.FromSeconds(26 + i * 7);
            anim.IterationBehavior = AnimationIterationBehavior.Forever;
            v.StartAnimation("Offset", anim);

            v.CenterPoint = new Vector3(size / 2, size / 2, 0);
            var scale = _c.CreateVector3KeyFrameAnimation();
            scale.InsertKeyFrame(0f, Vector3.One);
            scale.InsertKeyFrame(0.5f, new Vector3(1.18f, 1.12f, 1), ease);
            scale.InsertKeyFrame(1f, Vector3.One, ease);
            scale.Duration = TimeSpan.FromSeconds(18 + i * 5);
            scale.IterationBehavior = AnimationIterationBehavior.Forever;
            v.StartAnimation("Scale", scale);
        }
    }

    public void SetPalette(Color[] palette, bool animate = true)
    {
        if (palette.Length == 0) return;
        _palette = Enumerable.Range(0, 4).Select(i => palette[i % palette.Length]).ToArray();
        if (_c == null || _baseBrush == null) return;

        var dur = TimeSpan.FromMilliseconds(animate ? 1400 : 1);
        AnimateColor(_baseBrush, "Color", Darken(_palette[0]), dur);
        for (int i = 0; i < _blobs.Count; i++)
        {
            var (_, inner, outer) = _blobs[i];
            AnimateColor(inner, "Color", WithA(_palette[i], 235), dur);
            AnimateColor(outer, "Color", WithA(_palette[i], 0), dur);
            if (_blobs[i].visual.Brush is CompositionRadialGradientBrush b && b.ColorStops.Count > 2)
                AnimateColor(b.ColorStops[1], "Color", WithA(_palette[i], 110), dur);
        }
    }

    private void AnimateColor(CompositionObject target, string prop, Color to, TimeSpan duration)
    {
        var a = _c!.CreateColorKeyFrameAnimation();
        a.InsertKeyFrame(1f, to, _c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.2f, 1f)));
        a.Duration = duration;
        a.InterpolationColorSpace = CompositionColorSpace.Rgb;
        target.StartAnimation(prop, a);
    }

    private Color Darken(Color c)
    {
        if (_light)
            return Color.FromArgb(255, (byte)(c.R + (255 - c.R) * 0.8), (byte)(c.G + (255 - c.G) * 0.8), (byte)(c.B + (255 - c.B) * 0.8));
        var f = 1 - BaseDarkness;
        return Color.FromArgb(255, (byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
    }

    private static Color WithA(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);
}

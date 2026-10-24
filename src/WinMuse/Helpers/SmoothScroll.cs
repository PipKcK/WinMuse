using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinMuse.Helpers;

/// <summary>
/// Eased scrolling for a ScrollViewer, driven by wall-clock time so it is equally smooth at any frame rate.
/// <list type="bullet">
/// <item><see cref="By"/> (mouse wheel): the view chases a target that each notch moves, covering most of the
/// remaining distance quickly and then settling softly; quick notches flow into one continuous motion.</item>
/// <item><see cref="Glide"/> (arrow buttons): a timed move that starts fast and slows to a gentle stop.</item>
/// </list>
/// The user grabbing the content (touch, touchpad, scrollbar) takes over at once.
/// </summary>
public sealed class SmoothScroll
{
    /// <summary>Pixels per wheel notch (120 delta units).</summary>
    public const double NotchPixels = 84;
    private const double FollowSeconds = 0.075; // wheel: time constant of the chase (~95% of the way in under 0.25 s)
    private const double GlideSeconds = 0.7;   // arrows: length of the glide

    private static readonly DependencyProperty VerticalProperty =
        DependencyProperty.RegisterAttached("SmoothScrollVertical", typeof(SmoothScroll), typeof(SmoothScroll), new PropertyMetadata(null));
    private static readonly DependencyProperty HorizontalProperty =
        DependencyProperty.RegisterAttached("SmoothScrollHorizontal", typeof(SmoothScroll), typeof(SmoothScroll), new PropertyMetadata(null));

    private readonly ScrollViewer _sv;
    private readonly bool _horizontal;
    private bool _running;
    private double _pos;       // where this scroller has put the view (sub-pixel)
    private double _target;
    private DateTime _last;
    // Glide state: a fixed-length ease-out from _glideFrom to _target.
    private bool _gliding;
    private double _glideFrom;
    private DateTime _glideStart;

    private double _lastSet = double.NaN; // the offset this scroller last asked for

    private SmoothScroll(ScrollViewer sv, bool horizontal)
    {
        _sv = sv; _horizontal = horizontal;
        // Something else moved the view mid-glide (content above changed size, a row was rebuilt, the scrollbar,
        // the end of the content moved): carry on from there. Snapping back to the stale position caused jitter.
        sv.ViewChanged += (_, _) =>
        {
            if (!_running || double.IsNaN(_lastSet)) return;
            var drift = Offset - _lastSet;
            if (Math.Abs(drift) <= 1) return;
            _pos += drift; _glideFrom += drift;
            _target = Math.Clamp(_target + drift, 0, Max);
            _lastSet = Offset;
        };
    }

    public static SmoothScroll For(ScrollViewer sv, bool horizontal = false)
    {
        var key = horizontal ? HorizontalProperty : VerticalProperty;
        if (sv.GetValue(key) is SmoothScroll s) return s;
        s = new SmoothScroll(sv, horizontal);
        sv.SetValue(key, s);
        sv.DirectManipulationStarted += (_, _) => s.Stop();
        return s;
    }

    private double Offset => _horizontal ? _sv.HorizontalOffset : _sv.VerticalOffset;
    private double Max => _horizontal ? _sv.ScrollableWidth : _sv.ScrollableHeight;

    /// <summary>Wheel: moves the target by <paramref name="pixels"/> (positive = down / right) and follows it.</summary>
    public void By(double pixels)
    {
        var from = _running ? _target : Offset;
        if (!_running) _pos = Offset;
        _gliding = false;
        _target = Math.Clamp(from + pixels, 0, Max);
        Run();
    }

    /// <summary>Arrows: glides <paramref name="pixels"/> from the current target, starting fast and easing out.</summary>
    public void Glide(double pixels)
    {
        var from = _running ? _target : Offset;
        _pos = _running ? _pos : Offset;
        _glideFrom = _pos;
        _target = Math.Clamp(from + pixels, 0, Max);
        _glideStart = DateTime.UtcNow;
        _gliding = true;
        Run();
    }

    private void Run()
    {
        _last = DateTime.UtcNow;
        if (_running) return;
        _running = true;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_running) return;
        _running = false;
        _gliding = false;
        _lastSet = double.NaN;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, object e)
    {
        if (!_sv.IsLoaded) { Stop(); return; }
        var now = DateTime.UtcNow;
        if (_gliding)
        {
            var t = Math.Clamp((now - _glideStart).TotalSeconds / GlideSeconds, 0, 1);
            var k = 1 - Math.Pow(1 - t, 4); // ease-out quart: quick start, long soft landing
            _pos = _glideFrom + (_target - _glideFrom) * k;
            if (t >= 1) _pos = _target;
        }
        else
        {
            var dt = Math.Min(0.05, (now - _last).TotalSeconds);
            _pos += (_target - _pos) * (1 - Math.Exp(-dt / FollowSeconds));
        }
        _last = now;

        _target = Math.Min(_target, Max); // the content may have become shorter
        if (Math.Abs(_target - _pos) < 0.3) _pos = _target;
        _lastSet = Math.Clamp(_pos, 0, Max);
        if (_horizontal) _sv.ChangeView(_pos, null, null, true);
        else _sv.ChangeView(null, _pos, null, true);
        if (_pos == _target) Stop();
    }
}

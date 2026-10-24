using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace WinMuse.Helpers;

/// <summary>
/// Animates color changes on gradient stops and solid brushes (for example the accent following the album art), so
/// colors blend into each other instead of jumping. A new change to the same target continues from its current color.
/// </summary>
public static class ColorTween
{
    private sealed class Tween { public Color From, To; public DateTime Start; public double Ms; public Action<Color> Set = _ => { }; }
    private static readonly ConditionalWeakTable<object, Tween> Running = new();
    private static readonly List<WeakReference<object>> Active = new();
    private static bool _hooked;

    public static void To(GradientStop stop, Color to, double ms = 650) => Start(stop, stop.Color, to, ms, c => stop.Color = c);
    public static void To(SolidColorBrush brush, Color to, double ms = 650) => Start(brush, brush.Color, to, ms, c => brush.Color = c);

    private static void Start(object target, Color from, Color to, double ms, Action<Color> set)
    {
        if (from == to) return;
        Running.AddOrUpdate(target, new Tween { From = from, To = to, Start = DateTime.UtcNow, Ms = ms, Set = set });
        if (!Active.Any(w => w.TryGetTarget(out var t) && ReferenceEquals(t, target))) Active.Add(new WeakReference<object>(target));
        if (!_hooked) { _hooked = true; Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnFrame; }
    }

    private static void OnFrame(object? sender, object e)
    {
        var now = DateTime.UtcNow;
        for (int i = Active.Count - 1; i >= 0; i--)
        {
            if (!Active[i].TryGetTarget(out var target) || !Running.TryGetValue(target, out var tw)) { Active.RemoveAt(i); continue; }
            var t = Math.Clamp((now - tw.Start).TotalMilliseconds / tw.Ms, 0, 1);
            var k = 1 - Math.Pow(1 - t, 3); // ease out
            tw.Set(Lerp(tw.From, tw.To, k));
            if (t >= 1) { Running.Remove(target); Active.RemoveAt(i); }
        }
        if (Active.Count == 0) { _hooked = false; Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnFrame; }
    }

    private static Color Lerp(Color a, Color b, double k) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * k), (byte)(a.R + (b.R - a.R) * k), (byte)(a.G + (b.G - a.G) * k), (byte)(a.B + (b.B - a.B) * k));
}

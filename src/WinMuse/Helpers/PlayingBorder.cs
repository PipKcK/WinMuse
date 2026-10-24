using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI;
using Windows.UI;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Helpers;

/// <summary>
/// Drives the shared "NowPlayingBorderBrush": the animated border around the playing song in every song list.
/// The gradient spans two periods of colors and slides by one period, so the colors flow along the row seamlessly.
/// One brush and one per-frame update serve every list; nothing runs while playback is paused or the style is Off.
/// </summary>
public static class PlayingBorder
{
    private const double PeriodSeconds = 3.5;
    private static LinearGradientBrush? _brush;
    private static bool _running;
    private static readonly DateTime Start = DateTime.UtcNow;

    public static void Initialize()
    {
        if (Application.Current.Resources.TryGetValue("NowPlayingBorderBrush", out var b)) _brush = b as LinearGradientBrush;
        if (_brush == null) return;
        Svc.Settings.Changed += name => { if (name == nameof(AppSettings.PlayingHighlight)) Refresh(); };
        Svc.Theme.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ThemeService.Accent)) Refresh(); };
        Svc.Player.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlaybackService.IsPlaying)) UpdateRunning(); };
        Refresh();
    }

    /// <summary>Colors per period; the stops are created once and only recolored (rows already showing the brush keep it).</summary>
    private const int PeriodColors = 6;

    private static void Refresh()
    {
        if (_brush == null) return;
        var style = Svc.Settings.Current.PlayingHighlight;
        Color[] period = style switch
        {
            PlayingHighlight.Rainbow => new[] { Hex(0xFF4D6D), Hex(0xFF9F1C), Hex(0xFFE14D), Hex(0x3DDC84), Hex(0x2EC4F6), Hex(0x8B5CF6) },
            PlayingHighlight.Accent => AccentPeriod(Svc.Theme.Accent),
            _ => Enumerable.Repeat(Colors.Transparent, PeriodColors).ToArray(),
        };
        // Two periods across the brush, closing on the first color so the slide wraps without a seam.
        if (_brush.GradientStops.Count != 2 * PeriodColors + 1)
        {
            _brush.GradientStops.Clear();
            for (int i = 0; i <= 2 * PeriodColors; i++) _brush.GradientStops.Add(new GradientStop { Offset = (double)i / (2 * PeriodColors) });
        }
        for (int i = 0; i <= 2 * PeriodColors; i++) _brush.GradientStops[i].Color = period[i % PeriodColors];
        UpdateRunning();
    }

    private static Color[] AccentPeriod(Color a)
    {
        var (h, s, l) = ColorKit.ToHsl(a);
        return new[]
        {
            a, ColorKit.FromHsl(h + 20, Math.Min(1, s + 0.1), Math.Min(0.72, l + 0.08)), ColorKit.FromHsl(h + 40, s, Math.Min(0.78, l + 0.16)),
            ColorKit.Lighten(a, 0.3), ColorKit.FromHsl(h - 20, s, l), ColorKit.FromHsl(h - 40, Math.Min(1, s + 0.1), Math.Max(0.35, l - 0.05)),
        };
    }

    private static void UpdateRunning()
    {
        bool run = _brush != null && Svc.Settings.Current.PlayingHighlight != PlayingHighlight.Off && Svc.Player.IsPlaying;
        if (run == _running) return;
        _running = run;
        if (run) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private static void OnRendering(object? sender, object e)
    {
        var phase = (DateTime.UtcNow - Start).TotalSeconds % PeriodSeconds / PeriodSeconds;
        _brush!.StartPoint = new Windows.Foundation.Point(-phase, 0.5);
        _brush.EndPoint = new Windows.Foundation.Point(2 - phase, 0.5);
    }

    private static Color Hex(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}

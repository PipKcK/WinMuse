using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using WinMuse.Models;

namespace WinMuse.Helpers;

/// <summary>Static functions for x:Bind function bindings.</summary>
public static class Conv
{
    public static string PlayGlyph(bool playing) => playing ? "" : "";
    public static string PlayLabel(bool playing) => playing ? "Pause" : "Play";
    public static string RepeatGlyph(RepeatMode m) => m == RepeatMode.One ? "" : "";
    public static bool RepeatOn(RepeatMode m) => m != RepeatMode.Off;
    public static string RepeatTip(RepeatMode m) => m switch { RepeatMode.All => "Repeat all", RepeatMode.One => "Repeat one", _ => "Repeat off" };
    public static string VolumeGlyph(double v, bool muted) => muted || v <= 0 ? "" : v < 0.34 ? "" : v < 0.67 ? "" : "";
    public static string Time(double seconds) => Format.Time(seconds);
    public static string Remaining(double pos, double dur) => "-" + Format.Time(Math.Max(0, dur - pos));
    public static Visibility Show(bool v) => v ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Hide(bool v) => v ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility ShowIf(object? o) => o == null ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility ShowText(string? s) => string.IsNullOrWhiteSpace(s) ? Visibility.Collapsed : Visibility.Visible;
    public static bool Not(bool v) => !v;
    public static double Max1(double d) => d <= 0 ? 1 : d;
    // The current song's details. x:Bind never calls a function again when its argument becomes null, so the player
    // would keep showing a song after it was cleared. Bindings also pass Player.HasTrack, which changes exactly then.
    public static string TitleOf(Track? t, bool hasTrack = true) => t?.DisplayTitle ?? "Not playing";
    public static string ArtistOf(Track? t, bool hasTrack = true) => t?.DisplayArtist ?? "Select a song to start playback";
    public static string? ArtOf(Track? t, bool hasTrack = true) => t?.ArtPath;
    public static string? SeedOf(Track? t, bool hasTrack = true) => t?.AlbumKey;
    public static string FormatOf(Track? t, bool hasTrack = true) => t?.FormatText ?? "";
    public static Visibility HiRes(Track? t, bool hasTrack = true) => t?.IsHiRes == true ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Player bar, when the song's file has gone missing: the countdown to skipping it, on the Skip button.</summary>
    public static string SkipText(int seconds) => seconds > 0 ? $"Skip ({seconds})" : "Skip";
}

/// <summary>Slider tooltip showing m:ss.</summary>
public sealed class TimeTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => Format.Time(value is double d ? d : 0);
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class PercentTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => $"{(value is double d ? d : 0) * 100:0}%";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class DbTooltipConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => $"{(value is double d ? d : 0):+0.0;-0.0;0} dB";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

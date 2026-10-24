using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinMuse.Controls;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;
using WinMuse.Views;

namespace WinMuse.Dialogs;

/// <summary>
/// Read-only details of one song, on four tabs so nothing needs scrolling: Song (tags), Audio (format), File
/// (location, size, dates, plays) and Sources (where each item came from).
/// </summary>
public sealed class PropertiesDialog : ContentDialog
{
    private static readonly HashSet<string> Lossless = new(StringComparer.OrdinalIgnoreCase)
    {
        "FLAC", "ALAC", "WAV", "AIFF", "APE", "WavPack", "TTA", "TAK", "DSD", "DSF", "DFF", "MLP", "TrueHD", "W64", "PCM",
    };

    private PropertiesDialog(Track t)
    {
        XamlRoot = App.MainWindow.Content.XamlRoot;
        RequestedTheme = ((FrameworkElement)App.MainWindow.Content).ActualTheme;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 720.0;
        Title = "Properties";
        CloseButtonText = "Close";
        SecondaryButtonText = "Show in File Explorer";
        DefaultButton = ContentDialogButton.Close;
        SecondaryButtonClick += (_, _) => TrackActions.ShowInExplorer(t.Path);

        string? Num(int n) => n > 0 ? n.ToString() : null;
        string? Date(long unix) => unix > 0 ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("g") : null;

        var song = Rows(
            ("Title", t.Title),
            ("Artist", t.Artist),
            ("Album", t.Album),
            ("Album artist", t.AlbumArtist),
            ("Year", Num(t.Year)),
            ("Track", t.TrackNumber > 0 ? (t.TrackCount > 0 ? $"{t.TrackNumber} of {t.TrackCount}" : t.TrackNumber.ToString()) : null),
            ("Disc", Num(t.DiscNumber)),
            ("Genre", t.Genre),
            ("Composer", t.Composer),
            ("BPM", Num(t.Bpm)),
            ("Rating", t.Rating > 0 ? new string('★', t.Rating) + new string('☆', Math.Max(0, 5 - t.Rating)) : null),
            ("Comment", t.Comment));

        var codec = string.IsNullOrEmpty(t.Codec) ? t.Extension : t.Codec!;
        var quality = Lossless.Contains(codec) ? (t.IsHiRes ? "Lossless, Hi-Res" : "Lossless") : "Lossy";
        var audio = Rows(
            ("Length", Format.Time(t.Duration)),
            ("Format", (string.Equals(codec, t.Extension, StringComparison.OrdinalIgnoreCase) ? codec : $"{codec} in a {t.Extension} file") + $" · {quality}"),
            ("Bitrate", t.Bitrate > 0 ? $"{t.Bitrate:N0} kbps" : null),
            ("Sample rate", t.SampleRate > 0 ? $"{t.SampleRate / 1000.0:0.###} kHz" : null),
            ("Bit depth", t.BitsPerSample > 0 ? $"{t.BitsPerSample}-bit" : null),
            ("Channels", t.Channels switch { 0 => null, 1 => "Mono", 2 => "Stereo", 6 => "5.1 surround", 8 => "7.1 surround", var n => $"{n} channels" }));

        var file = Rows(
            ("Name", t.FileName),
            ("Folder", Path.GetDirectoryName(t.Path)),
            ("Size", t.FileSize > 0 ? $"{t.FileSize / 1048576.0:0.0} MB" : null),
            ("Modified", Date(t.ModifiedUtc)),
            ("Added", Date(t.AddedUtc)),
            ("Plays", t.PlayCount.ToString("N0")),
            ("Last played", Date(t.LastPlayedUtc)));

        var lyrics = Svc.Lyrics.GetLocal(t);
        var prefs = Svc.Library.PrefsFor(t);
        string State(int s, string item) => SourceStatus.From(s, item, t.IsCheckable).Label;
        var sources = Rows(
            ("Metadata", State(t.MetaState, "Metadata")),
            ("Album art", State(t.ArtState, "Album art")),
            ("Artist photo", State(Svc.Library.ArtistImageStateOf(t), "Artist image")),
            ("Lyrics", lyrics == null ? State(t.LyricsState, "Lyrics") : (lyrics.IsSynced ? "Synced" : "Plain text") + (lyrics.Source != null ? $" · {lyrics.Source}" : "")),
            ("Lyrics timing", prefs.LyricsOffsetMs != 0 ? $"{prefs.LyricsOffsetMs / 1000.0:+0.00;-0.00} s" : null),
            ("Equalizer", prefs.EqBands != null ? "Saved for this song" : null),
            ("Tags", t.EditedLocally ? "Edits kept in the library" : "Read from the file"));

        // Tabs: the card area keeps the height of the tallest tab so the dialog doesn't jump.
        var pages = new[] { ("Song", song), ("Audio", audio), ("File", file), ("Sources", sources) };
        var host = new Grid
        {
            Padding = new Thickness(16, 12, 16, 12), CornerRadius = new CornerRadius(8), MinHeight = 24 + 26 * pages.Max(p => p.Item2.RowDefinitions.Count),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        };
        var bar = new SelectorBar();
        foreach (var (name, _) in pages) bar.Items.Add(new SelectorBarItem { Text = name });
        bar.SelectionChanged += (_, _) =>
        {
            host.Children.Clear();
            host.Children.Add(pages[bar.Items.IndexOf(bar.SelectedItem)].Item2);
        };
        bar.SelectedItem = bar.Items[0];

        var root = new StackPanel { Spacing = 10, Width = 600 };
        root.Children.Add(Header(t));
        root.Children.Add(bar);
        root.Children.Add(host);
        Content = root;
        DialogChrome.AddCloseButton(this);
    }

    public static async Task ShowAsync(Track t) => await new PropertiesDialog(t).ShowAsync();

    private static Grid Header(Track t)
    {
        var g = new Grid { ColumnSpacing = 14 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.Children.Add(new ArtImage { Width = 64, Height = 64, CornerRadius = new CornerRadius(8), DecodeSize = 72, Source = t.ArtPath, Seed = t.AlbumKey });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        text.Children.Add(new TextBlock { Text = t.DisplayTitle, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = $"{t.DisplayArtist} · {t.AlbumText}", Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock { Text = t.FormatText, Opacity = 0.55, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] });
        Grid.SetColumn(text, 1);
        g.Children.Add(text);
        return g;
    }

    /// <summary>Label/value rows; empty values are left out. Long values are trimmed with the full text in a tooltip.</summary>
    private static Grid Rows(params (string label, string? value)[] rows)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int r = 0;
        foreach (var (label, value) in rows)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Opacity = 0.65 };
            var v = new TextBlock { Text = value, TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
            ToolTipService.SetToolTip(v, value);
            Grid.SetRow(l, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
            grid.Children.Add(l); grid.Children.Add(v);
            r++;
        }
        return grid;
    }
}

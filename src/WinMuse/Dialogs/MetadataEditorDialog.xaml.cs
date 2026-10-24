using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Storage.Streams;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Dialogs;

/// <summary>Edits tags for one or many songs. With several songs, only the fields you change are applied.</summary>
public sealed partial class MetadataEditorDialog : ContentDialog
{
    private readonly IList<Track> _tracks;
    private readonly List<(Control box, string initial, Action<TagEdit, string> apply)> _fields = new();
    private string _lyricsInitial = "";
    private byte[]? _newArt;
    private bool _openFix;

    private MetadataEditorDialog(IList<Track> tracks)
    {
        _tracks = tracks;
        InitializeComponent();
        var first = tracks[0];
        var multi = tracks.Count > 1;
        Title = multi ? $"Edit {tracks.Count} songs" : "Edit";
        DialogChrome.AddCloseButton(this);
        HeaderTitle.Text = multi ? Common(t => t.Album) ?? $"{tracks.Count} songs" : first.DisplayTitle;
        HeaderSub.Text = multi ? Common(t => t.AlbumArtist ?? t.Artist) ?? "Various artists" : $"{first.DisplayArtist} · {first.DisplayAlbum}";
        HeaderArt.Source = Common(t => t.ArtPath) ?? first.ArtPath;
        HeaderArt.Seed = first.AlbumKey;
        ArtPreview.Source = HeaderArt.Source;
        ArtPreview.Seed = first.AlbumKey;
        ArtInfo.Text = multi ? "New album art applies to all selected songs." : first.ArtPath != null ? "Album art from the file or its folder." : "No album art.";

        Bind(TitleBox, t => t.Title, (e, v) => e.Title = v, allowMulti: !multi);
        Bind(ArtistBox, t => t.Artist, (e, v) => e.Artist = v);
        Bind(AlbumArtistBox, t => t.AlbumArtist, (e, v) => e.AlbumArtist = v);
        Bind(AlbumBox, t => t.Album, (e, v) => e.Album = v);
        Bind(GenreBox, t => t.Genre, (e, v) => e.Genre = v);
        BindInt(YearBox, t => t.Year, (e, v) => e.Year = v);
        BindInt(TrackBox, t => t.TrackNumber, (e, v) => e.TrackNumber = v);
        BindInt(TrackCountBox, t => t.TrackCount, (e, v) => e.TrackCount = v);
        BindInt(DiscBox, t => t.DiscNumber, (e, v) => e.DiscNumber = v);
        Bind(ComposerBox, t => t.Composer, (e, v) => e.Composer = v);
        BindInt(BpmBox, t => t.Bpm, (e, v) => e.Bpm = v);
        Bind(CommentBox, t => t.Comment, (e, v) => e.Comment = v);

        if (!multi)
        {
            _lyricsInitial = Svc.Lyrics.GetLocal(first) is { } lr && lr.Source != "Embedded" ? ReadSidecar(first) ?? first.Lyrics ?? "" : first.Lyrics ?? "";
            LyricsBox.Text = _lyricsInitial;
        }
        else
        {
            TabLyrics.Visibility = Visibility.Collapsed;
        }

        BuildFileInfo();
        WriteToggle.IsOn = Svc.Metadata.WritesToFiles;
        WriteToggle.Toggled += (_, _) => Svc.Settings.Set(nameof(AppSettings.WriteMode), s => s.WriteMode = WriteToggle.IsOn ? MetadataWriteMode.WriteToFiles : MetadataWriteMode.LibraryOnly);

        PrimaryButtonClick += OnSave;
        // The body takes the room the window has, so the footer and buttons always show (scrolls inside instead).
        Opened += (_, _) => FitToWindow();
    }

    private void FitToWindow()
    {
        if (XamlRoot == null) return;
        // Title, header, tabs, footer, buttons and the dialog's margins take about 400 px.
        BodyRow.Height = new GridLength(Math.Clamp(XamlRoot.Size.Height - 400, 240, 520));
    }

    public static async Task ShowAsync(IList<Track> tracks, bool startOnLyrics = false)
    {
        if (tracks.Count == 0) return;
        var dlg = new MetadataEditorDialog(tracks)
        {
            XamlRoot = App.MainWindow.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)App.MainWindow.Content).ActualTheme,
        };
        if (startOnLyrics) dlg.Tabs.SelectedItem = dlg.TabLyrics;
        await dlg.ShowAsync();
        if (dlg._openFix) await FetchDialog.ShowAsync(tracks);
    }

    private string? Common(Func<Track, string?> get)
    {
        var v = get(_tracks[0]);
        return _tracks.All(t => get(t) == v) ? v : null;
    }

    private void Bind(Control box, Func<Track, string?> get, Action<TagEdit, string> apply, bool allowMulti = true)
    {
        var first = get(_tracks[0]) ?? "";
        var same = _tracks.All(t => (get(t) ?? "") == first);
        var text = same ? first : "";
        SetText(box, text);
        if (!same) SetPlaceholder(box, "Multiple values");
        if (!allowMulti && _tracks.Count > 1) { box.IsEnabled = false; SetPlaceholder(box, "Not available for multiple songs"); }
        _fields.Add((box, text, apply));
    }

    private void BindInt(TextBox box, Func<Track, int> get, Action<TagEdit, int> apply)
    {
        Bind(box, t => get(t) > 0 ? get(t).ToString() : "", (e, v) => apply(e, int.TryParse(v, out var n) ? n : 0));
    }

    private static void SetText(Control c, string text)
    {
        if (c is TextBox tb) tb.Text = text;
        else if (c is AutoSuggestBox asb) asb.Text = text;
        else if (c is Controls.TagPicker tp) tp.SetCredit(text);
    }

    private static string GetText(Control c) => c switch { TextBox tb => tb.Text, AutoSuggestBox asb => asb.Text, Controls.TagPicker tp => tp.Credit, _ => "" };

    private static void SetPlaceholder(Control c, string text)
    {
        if (c is TextBox tb) tb.PlaceholderText = text;
        else if (c is AutoSuggestBox asb) asb.PlaceholderText = text;
        else if (c is Controls.TagPicker tp) tp.PlaceholderText = text == "Multiple values" ? "Different on each song: add to replace on all" : text;
    }

    private static string? ReadSidecar(Track t)
    {
        var side = Path.ChangeExtension(t.Path, ".lrc");
        try { return File.Exists(side) ? File.ReadAllText(side) : null; } catch { return null; }
    }

    private void BuildFileInfo()
    {
        var rows = new List<(string, string)>();
        if (_tracks.Count == 1)
        {
            var t = _tracks[0];
            rows.AddRange(new[]
            {
                ("File name", t.FileName), ("Folder", Path.GetDirectoryName(t.Path) ?? ""), ("Format", t.FormatText),
                ("Channels", t.Channels.ToString()), ("Duration", t.DurationText), ("Size", Format.Size(t.FileSize)),
                ("Modified", DateTimeOffset.FromUnixTimeSeconds(t.ModifiedUtc).LocalDateTime.ToString("g")),
                ("Added to library", DateTimeOffset.FromUnixTimeSeconds(t.AddedUtc).LocalDateTime.ToString("g")),
                ("Plays", t.PlayCount.ToString()), ("Edits stored", t.EditedLocally ? "In library only" : "In file tags"),
            });
        }
        else
        {
            rows.Add(("Songs", _tracks.Count.ToString()));
            rows.Add(("Total size", Format.Size(_tracks.Sum(t => t.FileSize))));
            rows.Add(("Total length", Format.LongTime(TimeSpan.FromMilliseconds(_tracks.Sum(t => t.DurationMs)))));
            rows.Add(("Formats", string.Join(", ", _tracks.Select(t => t.Codec ?? t.Extension).Distinct())));
        }
        FileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        FileGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Count; i++)
        {
            FileGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = rows[i].Item1, Opacity = 0.7 };
            var v = new TextBlock { Text = rows[i].Item2, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            FileGrid.Children.Add(k); FileGrid.Children.Add(v);
        }
    }

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = sender.SelectedItem?.Tag as string;
        DetailsPanel.Visibility = tag == "details" ? Visibility.Visible : Visibility.Collapsed;
        ArtPanel.Visibility = tag == "art" ? Visibility.Visible : Visibility.Collapsed;
        LyricsPanel.Visibility = tag == "lyrics" ? Visibility.Visible : Visibility.Collapsed;
        FilePanel.Visibility = tag == "file" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- Artwork ----------------

    private async void OnChooseArt(object sender, RoutedEventArgs e)
    {
        var path = await Pickers.PickImageAsync();
        if (path == null) return;
        await SetNewArt(await File.ReadAllBytesAsync(path));
    }

    private async void OnSearchArt(object sender, RoutedEventArgs e)
    {
        ArtProgress.IsActive = true;
        try
        {
            var (bytes, fromYouTube) = await Svc.Online.FetchCoverAsync(null, NullIfEmpty(GetText(AlbumArtistBox)) ?? NullIfEmpty(GetText(ArtistBox)) ?? _tracks[0].Artist,
                NullIfEmpty(GetText(AlbumBox)) ?? _tracks[0].Album);
            if (bytes != null && fromYouTube) bytes = await ArtworkService.CropToSquareAsync(bytes);
            if (bytes != null) { await SetNewArt(bytes); ArtInfo.Text = "Album art found. Select Save to apply it."; }
            else ArtInfo.Text = "No album art was found. Verify the album and artist names, or run Fetch.";
        }
        catch (Exception ex) { ArtInfo.Text = "Unable to search online: " + ex.Message; }
        ArtProgress.IsActive = false;
    }

    private void OnRemoveArt(object sender, RoutedEventArgs e)
    {
        _newArt = Array.Empty<byte>();
        NewArtPreview.Source = null;
        ArtPreview.Source = null;
        ArtInfo.Text = "Album art will be removed when you save.";
    }

    private async Task SetNewArt(byte[] bytes)
    {
        _newArt = bytes;
        var bmp = new BitmapImage { DecodePixelWidth = 320 };
        using var ms = new InMemoryRandomAccessStream();
        await ms.WriteAsync(bytes.AsBuffer());
        ms.Seek(0);
        await bmp.SetSourceAsync(ms);
        NewArtPreview.Source = bmp;
    }

    // ---------------- Lyrics ----------------

    private async void OnDownloadLyrics(object sender, RoutedEventArgs e)
    {
        var t = _tracks[0];
        LyricsProgress.IsActive = true;
        try
        {
            var text = await Svc.Online.FetchLyricsAsync(NullIfEmpty(GetText(ArtistBox)) ?? t.Artist, NullIfEmpty(GetText(TitleBox)) ?? t.DisplayTitle,
                NullIfEmpty(GetText(AlbumBox)) ?? t.Album, (int)t.Duration.TotalSeconds);
            if (text != null) LyricsBox.Text = text;
            else Notifier.Show("Lyrics unavailable", "No lyrics were found for this song.");
        }
        finally { LyricsProgress.IsActive = false; }
    }

    // ---------------- Save ----------------

    private void OnFix(object sender, RoutedEventArgs e)
    {
        _openFix = true;
        Hide();
    }

    private async void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var edit = new TagEdit();
            bool any = false;
            foreach (var (box, initial, apply) in _fields)
            {
                var now = GetText(box).Trim();
                if (now == initial.Trim() || !box.IsEnabled) continue;
                if (_tracks.Count > 1 && now.Length == 0) continue; // "multiple values" left empty = keep
                apply(edit, now);
                any = true;
            }
            if (_newArt != null) { edit.Artwork = _newArt; any = true; }

            var lyricsNow = LyricsBox.Text;
            bool lyricsChanged = _tracks.Count == 1 && lyricsNow != _lyricsInitial;
            if (lyricsChanged)
            {
                edit.Lyrics = lyricsNow;
                any = true;
            }
            if (!any) return;

            var errors = await Svc.Metadata.ApplyAsync(_tracks, edit, byUser: true);
            if (lyricsChanged && LrcParser.LooksSynced(lyricsNow)) Svc.Lyrics.Save(_tracks[0], lyricsNow);

            if (errors.Count > 0) Notifier.Error("Some files could not be updated", string.Join("\n", errors.Take(3)));
            else Notifier.Success(_tracks.Count == 1 ? "Metadata saved" : $"Metadata saved for {_tracks.Count} songs",
                Svc.Metadata.WritesToFiles ? "Saved to file tags." : "Saved to the library.");
        }
        catch (Exception ex)
        {
            Notifier.Error("Unable to save metadata", ex.Message);
        }
        finally { deferral.Complete(); }
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

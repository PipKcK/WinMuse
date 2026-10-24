using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Dialogs;

/// <summary>
/// Sync: identifies songs as soon as it opens (fingerprint, then the catalogs), preselects the match the automatic
/// rules would choose, shows what would change, and applies it together with album art, artist photo, lyrics and
/// genre. The user can pick another match or search manually. Works through several songs one at a time.
/// </summary>
public sealed partial class FetchDialog : ContentDialog
{
    private readonly IList<Track> _tracks;
    private int _index;
    private CancellationTokenSource? _cts;
    private int _fixed;

    private Track Current => _tracks[_index];

    private FetchDialog(IList<Track> tracks)
    {
        _tracks = tracks;
        InitializeComponent();
        DialogChrome.AddCloseButton(this);
        if (tracks.Count > 1) SecondaryButtonText = "Skip";
        KeyHint.IsOpen = string.IsNullOrWhiteSpace(Svc.Settings.Current.AcoustIdApiKey);
        PrimaryButtonClick += OnApply;
        SecondaryButtonClick += OnSkip;
        Closed += (_, _) => _cts?.Cancel();
        Opened += (_, _) => LoadCurrent();
    }

    public static async Task ShowAsync(IList<Track> tracks)
    {
        if (tracks.Count == 0) return;
        var dlg = new FetchDialog(tracks.ToList())
        {
            XamlRoot = App.MainWindow.Content.XamlRoot,
            RequestedTheme = ((FrameworkElement)App.MainWindow.Content).ActualTheme,
        };
        await dlg.ShowAsync();
        if (dlg._fixed > 0) Notifier.Success(dlg._fixed == 1 ? "Fetched: 1 song" : $"Fetched: {dlg._fixed} songs");
    }

    private void LoadCurrent()
    {
        var t = Current;
        CurrentArt.Source = t.ArtPath; CurrentArt.Seed = t.AlbumKey;
        CurrentTitle.Text = t.DisplayTitle;
        CurrentSub.Text = $"{t.DisplayArtist} · {t.AlbumText}" + (t.Year > 0 ? $" · {t.Year}" : "");
        CurrentFile.Text = t.FileName;
        StepText.Text = _tracks.Count > 1 ? $"Song {_index + 1} of {_tracks.Count}" : "";
        var (artist, title) = MetadataEditService.GuessQuery(t);
        QueryBox.Text = string.IsNullOrWhiteSpace(artist) ? title ?? "" : $"{artist} - {title}";
        _ = IdentifyAsync(null);
    }

    private async Task IdentifyAsync(string? manual)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Candidates.ItemsSource = null;
        DiffGrid.Children.Clear();
        Busy.Visibility = Visibility.Visible;
        BusyRing.IsActive = true;
        IsPrimaryButtonEnabled = false;
        var progress = new Progress<string>(s => StatusText.Text = s);

        try
        {
            string? fpError = null, busy = null;
            var list = await Svc.Metadata.IdentifyAsync(Current, manual, progress, cts.Token, fingerprintError: e => fpError = e, serviceBusy: svc => busy ??= svc);
            if (cts.IsCancellationRequested) return;
            if (fpError != null)
            {
                KeyHint.Severity = InfoBarSeverity.Error;
                KeyHint.Title = "AcoustID";
                KeyHint.Message = fpError;
                KeyHint.IsOpen = true;
            }
            Candidates.ItemsSource = list;
            IsPrimaryButtonEnabled = true; // without a match, Apply still downloads the selected extras
            if (list.Count > 0)
            {
                Busy.Visibility = Visibility.Collapsed;
                // The match the automatic rules would apply; for a manual search, the best result.
                Candidates.SelectedItem = (manual == null ? MetadataEditService.ChooseAutomatic(Current, list) : null) ?? list[0];
            }
            else
            {
                BusyRing.IsActive = false;
                StatusText.Text = busy != null
                    ? $"{busy} is busy right now (too many requests). Wait a minute, then select Identify again."
                    : "No matches found. Search manually using the format Artist - Title, or just the title. Apply still downloads the items selected below.";
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            BusyRing.IsActive = false;
            StatusText.Text = "Unable to reach the metadata service: " + ex.Message;
        }
    }

    private void OnSearch(object sender, RoutedEventArgs e) => _ = IdentifyAsync(QueryBox.Text);
    private void OnAuto(object sender, RoutedEventArgs e) => _ = IdentifyAsync(null);

    private void OnQueryKey(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        _ = IdentifyAsync(QueryBox.Text);
    }

    private void OnCandidateChanged(object sender, SelectionChangedEventArgs e) => BuildDiff(Candidates.SelectedItem as MetadataCandidate);

    /// <summary>Field-by-field comparison; changed values are highlighted with the accent color.</summary>
    private void BuildDiff(MetadataCandidate? c)
    {
        DiffGrid.Children.Clear();
        DiffGrid.RowDefinitions.Clear();
        DiffGrid.ColumnDefinitions.Clear();
        if (c == null) return;
        DiffGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        DiffGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        DiffGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        DiffGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var t = Current;
        var rows = new (string field, string? now, string? next)[]
        {
            ("Title", t.Title, c.Title),
            ("Artist", t.Artist, c.Artist),
            ("Album", t.Album, c.Album),
            ("Album artist", t.AlbumArtist, c.AlbumArtist),
            ("Year", t.Year > 0 ? t.Year.ToString() : null, c.Year > 0 ? c.Year.ToString() : null),
            ("Track", t.TrackNumber > 0 ? t.TrackNumber.ToString() : null, c.TrackNumber > 0 ? c.TrackNumber.ToString() : null),
            ("Genre", t.Genre, c.Genre),
        };
        var accent = ThemeRes.Brush("AccentTextFillColorPrimaryBrush");
        int r = 0;
        foreach (var (field, now, next) in rows)
        {
            if (string.IsNullOrEmpty(next)) continue;
            bool changed = !string.Equals(now ?? "", next, StringComparison.Ordinal);
            DiffGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Add(new TextBlock { Text = field, Opacity = 0.65 }, r, 0);
            Add(new TextBlock { Text = string.IsNullOrEmpty(now) ? "—" : now, Opacity = changed ? 0.6 : 0.9, TextTrimming = TextTrimming.CharacterEllipsis }, r, 1);
            Add(new FontIcon { Glyph = changed ? "" : "", FontSize = 12, Opacity = changed ? 1 : 0.4 }, r, 2);
            var nextBlock = new TextBlock { Text = next, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = changed ? FontWeights.SemiBold : FontWeights.Normal };
            if (changed) nextBlock.Foreground = accent;
            Add(nextBlock, r, 3);
            r++;
        }
    }

    private void Add(FrameworkElement el, int row, int col)
    {
        Grid.SetRow(el, row);
        Grid.SetColumn(el, col);
        el.VerticalAlignment = VerticalAlignment.Center;
        DiffGrid.Children.Add(el);
    }

    private async void OnApply(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var c = Candidates.SelectedItem as MetadataCandidate;
        var deferral = args.GetDeferral();
        var t = Current;
        bool art = ArtCheck.IsChecked == true, artist = ArtistCheck.IsChecked == true, lyrics = LyricsCheck.IsChecked == true, genre = GenreCheck.IsChecked == true;
        try
        {
            StatusText.Text = "Saving…";
            if (c != null)
            {
                var albumBefore = t.AlbumKey;
                var errors = await Svc.Metadata.ApplyAsync(new[] { t }, TagEdit.FromCandidate(c));
                Svc.Metadata.DropStaleArt(t, albumBefore);
                t.MetaState = ItemState.Downloaded;
                Svc.Library.SaveStates(new[] { t });
                if (errors.Count > 0) Notifier.Error("Unable to write tags", errors[0]);
            }
            _fixed++;
            // Extras download in the background so the dialog closes immediately.
            if (art || artist || lyrics || genre) _ = DownloadExtrasAsync(t, c, art, artist, lyrics, genre);
        }
        catch (Exception ex) { Notifier.Error("Unable to apply metadata", ex.Message); }
        finally
        {
            if (Advance()) args.Cancel = true;
            deferral.Complete();
        }
    }

    private static async Task DownloadExtrasAsync(Track t, MetadataCandidate? c, bool art, bool artist, bool lyrics, bool genre)
    {
        try
        {
            if (genre && await Svc.Metadata.ResolveGenreAsync(t, c) is { } g && g != t.Genre)
                await Svc.Metadata.ApplyAsync(new[] { t }, new TagEdit { Genre = g });
            // One photo per artist: only looked up when the artist has none yet.
            if (artist)
                foreach (var (name, i) in t.ArtistNames.Select((n, i) => (n, i)))
                    if (Svc.Library.GetArtistImage(name) == null)
                        await Svc.Metadata.FetchArtistImageAsync(name, i == 0 ? c : null, force: true, sample: i == 0 ? t : null);
        }
        catch (Exception ex) { AppPaths.Log("Sync genre/artist image: " + ex.Message); }
        if (art)
        {
            // A cover another song of the album already has is reused instead of looked up again.
            try { if (!Svc.Metadata.ReuseAlbumArt(new[] { t })) await Svc.Metadata.FetchAndApplyArtAsync(new[] { t }, c); }
            catch (Exception ex) { AppPaths.Log("Sync art: " + ex.Message); }
        }
        if (lyrics)
        {
            try { await Svc.Lyrics.DownloadAsync(t); } catch (Exception ex) { AppPaths.Log("Sync lyrics: " + ex.Message); }
        }
    }

    private void OnSkip(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (Advance()) args.Cancel = true;
    }

    /// <summary>Moves to the next song; returns true if the dialog should stay open.</summary>
    private bool Advance()
    {
        if (_index + 1 >= _tracks.Count) return false;
        _index++;
        LoadCurrent();
        return true;
    }
}

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinMuse.Controls;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Views;

/// <summary>
/// One status cell on the Fetch page. The icon carries the state's color (status fill colors are made for icons);
/// the label uses a theme text color so it stays readable in light and dark mode.
/// </summary>
public sealed record SourceStatus(int State, string Label, string Glyph, Brush IconBrush, Brush TextBrush, string Tooltip)
{
    public static SourceStatus From(int state, string item, bool checkable = true)
    {
        Brush Text(string key = "TextFillColorSecondaryBrush") => ThemeRes.Brush(key);
        return state switch
        {
            ItemState.Missing when !checkable => new(state, "Skipped", "", Text("TextFillColorTertiaryBrush"), Text("TextFillColorTertiaryBrush"), "Songs shorter than 30 seconds or longer than 15 minutes aren't checked online"),
            ItemState.Downloaded => new(state, "Downloaded", "", ThemeRes.Brush("SystemFillColorSuccessBrush"), Text("TextFillColorPrimaryBrush"), $"{item} was downloaded from the online sources"),
            ItemState.FromFile => new(state, "From file", "", Text(), Text(), $"{item} was already in the file or its folder"),
            ItemState.EditedByUser => new(state, "Edited", "", ThemeRes.Brush("AccentTextFillColorPrimaryBrush"), Text("TextFillColorPrimaryBrush"), $"{item} was changed by hand"),
            ItemState.Instrumental => new(state, "Instrumental", "", Text(), Text(), "Instrumental: this song has no lyrics"),
            ItemState.NotFound => new(state, "Not found", "", ThemeRes.Brush("SystemFillColorCautionBrush"), Text(), $"{item} was looked up, but the online sources have nothing for this song. Fetch tries again."),
            _ => new(state, "Pending", "", Text("TextFillColorTertiaryBrush"), Text("TextFillColorTertiaryBrush"), $"{item} has not been looked up yet"),
        };
    }
}

/// <summary>Overall fetch status of a song (the Fetch page's filter and summary).</summary>
public enum FetchStatus { Complete, Pending, NotFound, Edited, Skipped }

/// <summary>A song with the state of each downloadable item and its overall fetch status.</summary>
public sealed class SourceRow
{
    public Track Track { get; }
    public SourceStatus Metadata { get; }
    public SourceStatus AlbumArt { get; }
    public SourceStatus ArtistImage { get; }
    public SourceStatus Lyrics { get; }
    public FetchStatus Status { get; }
    /// <summary>"Complete", "Pending: lyrics", "Not found: lyrics, artist image", "Edited by hand" or "Skipped".</summary>
    public string StatusText { get; }
    /// <summary>Every item with its exact state, for checking why a song is where it is.</summary>
    public string StatusTooltip { get; }
    /// <summary>What screen readers announce for the row (WinUI names list items from ToString).</summary>
    public override string ToString() => $"{Track}, {StatusText}";
    public Brush StatusBackground { get; }
    public Brush StatusForeground { get; }
    public bool CanSync => Track.IsCheckable;
    public string SyncTooltip => Track.IsCheckable ? "Fetch this song" : "Songs shorter than 30 seconds or longer than 15 minutes aren't checked online";

    public SourceRow(Track t)
    {
        Track = t;
        bool checkable = t.IsCheckable;
        Metadata = SourceStatus.From(t.MetaState, "Metadata", checkable);
        AlbumArt = SourceStatus.From(t.ArtState, "Album art", checkable);
        int artistState = Svc.Library.ArtistImageStateOf(t);
        ArtistImage = SourceStatus.From(artistState, "Artist image", checkable);
        Lyrics = SourceStatus.From(t.LyricsState, "Lyrics", checkable);

        // The same rules as LibraryService.SyncState, item by item, so the page says what is left and why.
        var gaps = new List<(string name, int state)>();
        if (t.MetaState != ItemState.Downloaded) gaps.Add(("metadata", t.MetaState));
        if (!ItemState.IsSatisfied(t.ArtState)) gaps.Add(("album art", t.ArtState));
        if (artistState != ItemState.Downloaded) gaps.Add(("artist image", artistState));
        if (!ItemState.IsSatisfied(t.LyricsState)) gaps.Add(("lyrics", t.LyricsState));
        string Names(IEnumerable<(string name, int state)> g) => string.Join(", ", g.Select(x => x.name));

        var flag = Svc.Library.SyncState(t);
        if (flag == -1) { Status = FetchStatus.Edited; StatusText = "Edited by hand"; }
        else if (flag == 1) { Status = FetchStatus.Complete; StatusText = "Complete"; }
        else if (!checkable) { Status = FetchStatus.Skipped; StatusText = "Skipped"; }
        else if (gaps.Any(g => g.state == ItemState.Missing)) { Status = FetchStatus.Pending; StatusText = "Pending: " + Names(gaps.Where(g => g.state == ItemState.Missing)); }
        else { Status = FetchStatus.NotFound; StatusText = "Not found: " + Names(gaps); }

        StatusTooltip = string.Join("\n",
            $"Metadata: {Metadata.Label} ({t.MetaState})",
            $"Album art: {AlbumArt.Label} ({t.ArtState})",
            $"Artist image: {ArtistImage.Label} ({artistState}) · {string.Join(", ", t.ArtistNames)}",
            $"Lyrics: {Lyrics.Label} ({t.LyricsState})",
            $"Length: {Format.Time(t.DurationMs / 1000.0)}{(checkable ? "" : " (not checked online)")} · Processed: {(t.AutoFixed ? "yes" : "no")}",
            t.Path);

        (StatusBackground, StatusForeground) = Status switch
        {
            FetchStatus.Complete => (ThemeRes.Brush("SystemFillColorSuccessBackgroundBrush"), ThemeRes.Brush("TextFillColorPrimaryBrush")),
            FetchStatus.Edited => (ThemeRes.Brush("SubtleFillColorSecondaryBrush"), ThemeRes.Brush("AccentTextFillColorPrimaryBrush")),
            FetchStatus.NotFound => (ThemeRes.Brush("SystemFillColorCautionBackgroundBrush"), ThemeRes.Brush("TextFillColorPrimaryBrush")),
            _ => (ThemeRes.Brush("SubtleFillColorSecondaryBrush"), ThemeRes.Brush("TextFillColorSecondaryBrush")),
        };
    }

    public bool SameAs(SourceRow o) =>
        o.Metadata.State == Metadata.State && o.AlbumArt.State == AlbumArt.State && o.ArtistImage.State == ArtistImage.State
        && o.Lyrics.State == Lyrics.State && o.StatusText == StatusText && o.Track.ArtPath == Track.ArtPath
        && o.Track.DisplayTitle == Track.DisplayTitle && o.Track.DisplayArtist == Track.DisplayArtist;
}

/// <summary>Lists every song and where its metadata, album art, artist image and lyrics came from.</summary>
public sealed partial class FetchPage : Page
{
    private ObservableCollection<SourceRow>? _rows;
    private readonly Debouncer _refresh = new(TimeSpan.FromMilliseconds(400));

    public FetchPage()
    {
        InitializeComponent();
        FilterBox.SelectedIndex = 0;
        Action changed = () => _refresh.Run(() => App.UI.TryEnqueue(Load));
        Action<int, int, string?> progress = OnProgress;
        this.WhileLoaded(() => { Svc.Library.Changed += changed; Svc.AutoFix.Progress += progress; },
                         () => { Svc.Library.Changed -= changed; Svc.AutoFix.Progress -= progress; });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Load();
        OnProgress(0, Svc.AutoFix.IsRunning ? 1 : 0, null);
        Effects.EntranceSlide(Header, 16);
    }

    private void Load()
    {
        var all = Svc.Library.Tracks
            .OrderBy(t => t.DisplayArtist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
            .Select(t => new SourceRow(t)).ToList();

        int Count(FetchStatus s) => all.Count(r => r.Status == s);
        var parts = new List<string> { $"{all.Count:N0} songs", $"{Count(FetchStatus.Complete):N0} complete" };
        if (Count(FetchStatus.Pending) is > 0 and var pending) parts.Add($"{pending:N0} pending");
        if (Count(FetchStatus.NotFound) is > 0 and var notFound) parts.Add($"{notFound:N0} with items not found online");
        if (Count(FetchStatus.Edited) is > 0 and var edited) parts.Add($"{edited:N0} edited by hand");
        if (Count(FetchStatus.Skipped) is > 0 and var skipped) parts.Add($"{skipped:N0} skipped");
        SummaryText.Text = string.Join(" · ", parts);

        // Filter order matches FilterBox: All, Complete, Pending, Not found online, Edited by hand, Skipped.
        var shown = FilterBox.SelectedIndex switch
        {
            1 => all.Where(r => r.Status == FetchStatus.Complete).ToList(),
            2 => all.Where(r => r.Status == FetchStatus.Pending).ToList(),
            3 => all.Where(r => r.Status == FetchStatus.NotFound).ToList(),
            4 => all.Where(r => r.Status == FetchStatus.Edited).ToList(),
            5 => all.Where(r => r.Status == FetchStatus.Skipped).ToList(),
            _ => all,
        };
        var bound = CollectionSync.SyncOrReplace(_rows, shown, r => r.Track, (a, b) => a.SameAs(b));
        if (!ReferenceEquals(bound, _rows)) { _rows = bound; List.ItemsSource = _rows; }
        Empty.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = all.Count == 0 ? "No songs in the library" : "No songs match this filter";
    }

    private void OnProgress(int done, int total, string? current)
    {
        SyncText.Text = total > 0 ? $"Stop · {Math.Min(done + 1, total)} of {total}" : "Fetch";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(SyncButton,
            total > 0 ? $"Stop fetching, song {Math.Min(done + 1, total)} of {total}" : "Fetch");
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Load(); }

    private async void OnSync(object sender, RoutedEventArgs e)
    {
        if (Svc.AutoFix.IsRunning) { Svc.AutoFix.Stop(); return; }
        var rows = Svc.Library.Tracks.Where(t => t.IsCheckable).Select(t => new SourceRow(t)).Where(r => r.Status != FetchStatus.Complete).ToList();
        if (rows.Count == 0) { Notifier.Show("All songs are complete"); return; }
        int notFound = rows.Count(r => r.Status == FetchStatus.NotFound), edited = rows.Count(r => r.Status == FetchStatus.Edited);
        var detail = $"{rows.Count:N0} songs aren't complete and will be looked up again for metadata, album art, artist images and lyrics.";
        if (notFound > 0) detail += $" {notFound:N0} of them were looked up before and had items the online sources didn't have.";
        if (edited > 0) detail += $" This includes {edited:N0} songs edited by hand; those changes will be replaced.";
        if (!await TrackActions.Confirm("Fetch for the whole library?", detail, "Fetch")) return;
        await Svc.AutoFix.SyncAsync();
    }

    private static SourceRow? RowFrom(object source)
    {
        var el = source as DependencyObject;
        while (el != null && el is not FrameworkElement { DataContext: SourceRow }) el = VisualTreeHelper.GetParent(el);
        return (el as FrameworkElement)?.DataContext as SourceRow;
    }

    private async void OnSyncRow(object sender, RoutedEventArgs e)
    {
        if (RowFrom(sender) is not { } row) return;
        Notifier.Show(Svc.AutoFix.IsRunning ? $"“{row.Track.DisplayTitle}” will be fetched next" : $"Fetching “{row.Track.DisplayTitle}”");
        await Svc.AutoFix.SyncTracksAsync(new[] { row.Track });
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (RowFrom(e.OriginalSource) is not { } row) return;
        TrackActions.BuildMenu(new[] { row.Track }).ShowAt((FrameworkElement)e.OriginalSource, new FlyoutShowOptions { Position = e.GetPosition((UIElement)e.OriginalSource) });
        e.Handled = true;
    }
}

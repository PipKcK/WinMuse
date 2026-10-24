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

/// <summary>One status cell on the Fetch page.</summary>
public sealed record SourceStatus(int State, string Label, string Glyph, Brush Brush, string Tooltip)
{
    public static SourceStatus From(int state, string item, bool checkable = true) => state switch
    {
        ItemState.Missing when !checkable => new(state, "Skipped", "", ThemeRes.Brush("TextFillColorTertiaryBrush"), "Songs shorter than 30 seconds or longer than 15 minutes aren't checked online"),
        ItemState.Downloaded => new(state, "Downloaded", "", ThemeRes.Brush("SystemFillColorSuccessBrush"), $"{item} downloaded by Music"),
        ItemState.FromFile => new(state, "From file", "", ThemeRes.Brush("TextFillColorSecondaryBrush"), $"{item} was already in the file or folder"),
        ItemState.EditedByUser => new(state, "Edited", "", ThemeRes.Brush("AccentTextFillColorPrimaryBrush"), $"{item} was changed by you"),
        ItemState.NotFound => new(state, "Not found", "", ThemeRes.Brush("SystemFillColorCautionBrush"), $"{item} was looked up but not found online"),
        _ => new(state, "Pending", "", ThemeRes.Brush("TextFillColorTertiaryBrush"), $"{item} has not been processed yet"),
    };
}

/// <summary>A song with the state of each downloadable item and its overall fetch status.</summary>
public sealed class SourceRow
{
    public Track Track { get; }
    public SourceStatus Metadata { get; }
    public SourceStatus AlbumArt { get; }
    public SourceStatus ArtistImage { get; }
    public SourceStatus Lyrics { get; }
    public int Flag { get; }
    public string FlagText => Flag switch { 1 => "Complete (1)", -1 => "Edited (-1)", _ => "Incomplete (0)" };
    public Brush FlagBackground { get; }
    public Brush FlagForeground { get; }
    public bool CanSync => Track.IsCheckable;
    public string SyncTooltip => Track.IsCheckable ? "Fetch this song" : "Songs shorter than 30 seconds or longer than 15 minutes aren't checked online";

    public SourceRow(Track t)
    {
        Track = t;
        bool checkable = t.IsCheckable;
        Metadata = SourceStatus.From(t.MetaState, "Metadata", checkable);
        AlbumArt = SourceStatus.From(t.ArtState, "Album art", checkable);
        ArtistImage = SourceStatus.From(Svc.Library.ArtistImageStateOf(t), "Artist image", checkable);
        Lyrics = SourceStatus.From(t.LyricsState, "Lyrics", checkable);
        Flag = Svc.Library.SyncState(t);
        (FlagBackground, FlagForeground) = Flag switch
        {
            1 => (ThemeRes.Brush("SystemFillColorSuccessBackgroundBrush"), ThemeRes.Brush("SystemFillColorSuccessBrush")),
            -1 => (ThemeRes.Brush("SubtleFillColorSecondaryBrush"), ThemeRes.Brush("AccentTextFillColorPrimaryBrush")),
            _ => (ThemeRes.Brush("SystemFillColorCautionBackgroundBrush"), ThemeRes.Brush("SystemFillColorCautionBrush")),
        };
    }

    public bool SameAs(SourceRow o) =>
        o.Metadata.State == Metadata.State && o.AlbumArt.State == AlbumArt.State && o.ArtistImage.State == ArtistImage.State
        && o.Lyrics.State == Lyrics.State && o.Track.ArtPath == Track.ArtPath && o.Track.DisplayTitle == Track.DisplayTitle && o.Track.DisplayArtist == Track.DisplayArtist;
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

        int complete = all.Count(r => r.Flag == 1), edited = all.Count(r => r.Flag == -1);
        SummaryText.Text = $"{all.Count:N0} songs · {complete:N0} complete · {all.Count - complete - edited:N0} incomplete · {edited:N0} edited by you";

        var shown = FilterBox.SelectedIndex switch
        {
            1 => all.Where(r => r.Flag == 0).ToList(),
            2 => all.Where(r => r.Flag == -1).ToList(),
            3 => all.Where(r => r.Flag == 1).ToList(),
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
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Load(); }

    private async void OnSync(object sender, RoutedEventArgs e)
    {
        if (Svc.AutoFix.IsRunning) { Svc.AutoFix.Stop(); return; }
        int pending = Svc.Library.Tracks.Count(t => t.IsCheckable && Svc.Library.SyncState(t) != 1);
        if (pending == 0) { Notifier.Show("All songs are complete"); return; }
        int edited = Svc.Library.Tracks.Count(t => t.IsCheckable && Svc.Library.SyncState(t) == -1);
        if (!await TrackActions.Confirm("Fetch for the whole library?",
                $"{pending:N0} songs will be updated with online metadata, album art, artist images and lyrics." +
                (edited > 0 ? $" This includes {edited:N0} songs you edited; your changes to them will be replaced." : ""), "Fetch")) return;
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

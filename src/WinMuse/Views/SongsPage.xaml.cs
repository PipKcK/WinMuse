using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinMuse.Controls;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Views;

/// <summary>All songs, or any filtered song list (genre, "all songs by artist", …) via <see cref="SongListRequest"/>.</summary>
public sealed partial class SongsPage : Page
{
    private SongListRequest? _request;
    private List<Track> _all = new();
    private readonly Debouncer _filter = new(TimeSpan.FromMilliseconds(200));

    public SongsPage()
    {
        InitializeComponent();
        Action changed = Load;
        this.WhileLoaded(() => Svc.Library.Changed += changed, () => Svc.Library.Changed -= changed);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _request = e.Parameter as SongListRequest;
        TitleText.Text = _request?.Title ?? "Songs";
        HeaderArt.Visibility = _request?.ArtPath != null ? Visibility.Visible : Visibility.Collapsed;
        HeaderArt.Source = _request?.ArtPath;
        HeaderArt.Seed = _request?.Title;
        Load();
        Effects.EntranceSlide(Header, 16);
        // Open at the song that is playing (rows exist once the list has loaded).
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Tracks.ScrollToPlaying);
    }

    private void Load()
    {
        _all = (_request?.Source() ?? Svc.Library.Tracks.OrderBy(t => t.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)).ToList();
        Tracks.Context = _request is { Subtitle: "Artist" } r && Svc.Library.FindArtist(r.Title) is { } artist ? PlayContext.Of(artist) : null;
        SubtitleText.Text = (_request?.Subtitle != null ? _request.Subtitle + " · " : "") +
                            $"{_all.Count:N0} songs · {Format.LongTime(TimeSpan.FromMilliseconds(_all.Sum(t => t.DurationMs)))}";
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = FilterBox.Text.Trim();
        Tracks.SetItems(q.Length == 0 ? _all : _all.Where(t =>
            t.DisplayTitle.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
            t.DisplayArtist.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
            t.DisplayAlbum.Contains(q, StringComparison.CurrentCultureIgnoreCase)));
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => _filter.Run(() => App.UI.TryEnqueue(ApplyFilter));
    private async void OnPlayAll(object sender, RoutedEventArgs e) => await Svc.Player.PlayAsync(Tracks.Items.ToList(), 0, shuffle: false);
    private async void OnShuffle(object sender, RoutedEventArgs e) => await Svc.Player.PlayAsync(Tracks.Items.ToList(), 0, shuffle: true);
}

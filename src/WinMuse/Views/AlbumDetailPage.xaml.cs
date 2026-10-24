using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using WinMuse.Controls;
using WinMuse.Dialogs;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Views;

public sealed partial class AlbumDetailPage : Page
{
    private string? _key;
    private Album? _album;

    public AlbumDetailPage()
    {
        InitializeComponent();
        Tracks.IndexMode = TrackIndexMode.TrackNumber;
        Tracks.ShowAlbum = false;
        Tracks.Sortable = false;
        Action changed = Reload;
        this.WhileLoaded(() => Svc.Library.Changed += changed, () => Svc.Library.Changed -= changed);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _key = e.Parameter as string;
        Reload();
        var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("AlbumArt");
        if (anim != null) anim.TryStart(Cover, new UIElement[] { Info });
        else { Effects.EntranceSlide(Cover, 20); Effects.EntranceSlide(Info, 24, 80); }
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        if (e.NavigationMode == NavigationMode.Back)
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("AlbumArtBack", Cover);
    }

    private void Reload()
    {
        _album = _key != null ? Svc.Library.FindAlbum(_key) : null;
        if (_album == null)
        {
            // Album was renamed by an edit: follow the first track to its new album.
            if (_album == null && Tracks.Items.FirstOrDefault() is { } t) { _key = t.AlbumKey; _album = Svc.Library.FindAlbum(_key); }
            if (_album == null) return;
        }
        TitleText.Text = _album.Title;
        ArtistLink.Content = _album.Artist;
        var parts = new List<string>();
        if (_album.Year > 0) parts.Add(_album.Year.ToString());
        if (!string.IsNullOrEmpty(_album.Genre)) parts.Add(_album.Genre!);
        parts.Add(_album.Summary);
        MetaText.Text = "· " + string.Join(" · ", parts);
        Cover.Source = _album.ArtPath; Cover.Seed = _album.Key;
        Backdrop.Source = _album.ArtPath; Backdrop.Seed = _album.Key;
        Tracks.Context = PlayContext.Of(_album);
        Tracks.SetItems(_album.Tracks);
    }

    private async void OnPlay(object sender, RoutedEventArgs e) { if (_album != null) await Svc.Player.PlayAsync(_album.Tracks, 0, shuffle: false, context: PlayContext.Of(_album)); }
    private async void OnShuffle(object sender, RoutedEventArgs e) { if (_album != null) await Svc.Player.PlayAsync(_album.Tracks, 0, shuffle: true, context: PlayContext.Of(_album)); }
    private void OnQueue(object sender, RoutedEventArgs e) { if (_album != null) { Svc.Player.AddToQueue(_album.Tracks); Notifier.Show($"Added to queue: {_album.Title}"); } }
    private void OnArtist(object sender, RoutedEventArgs e)
    {
        if (_album == null) return;
        var names = ArtistCredit.Split(_album.Artist);
        TrackActions.GoToArtist(names.Count > 0 ? names : new List<string> { _album.Artist }, (FrameworkElement)sender);
    }
    // Name, album artist, cover and year for every song on the album (Edit on a song edits that song's details).
    private async void OnEdit(object sender, RoutedEventArgs e) { if (_album != null) { await EditAlbumDialog.ShowAsync(_album); Reload(); } }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (_album == null) return;
        var tracks = _album.Tracks;
        var menu = new MenuFlyout();
        menu.Items.Add(TrackActions.Item("Play next", "", () => Svc.Player.PlayNext(tracks)));
        menu.Items.Add(TrackActions.AddToPlaylistMenu(tracks));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(TrackActions.Item("Edit album…", "", async () => { await EditAlbumDialog.ShowAsync(_album); Reload(); }));
        menu.Items.Add(TrackActions.Item("Fetch album…", "\uEBD3", async () => await FetchDialog.ShowAsync(tracks)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(TrackActions.Item("Show in File Explorer", "", () => TrackActions.ShowInExplorer(tracks[0].Path)));
        menu.ShowAt((FrameworkElement)sender);
    }
}

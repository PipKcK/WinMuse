using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinMuse.Controls;
using WinMuse.Dialogs;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Views;

public sealed partial class ArtistDetailPage : Page
{
    private Artist? _artist;
    private string? _name;
    private ObservableCollection<Album>? _albums;

    public ArtistDetailPage()
    {
        InitializeComponent();
        Popular.ShowAlbum = true;
        Popular.ShowHeader = false;
        Popular.Sortable = false;
        Action changed = Load;
        Action favorites = UpdateFavoriteButtons;
        this.WhileLoaded(() => { Svc.Library.Changed += changed; Svc.Favorites.Changed += favorites; },
                         () => { Svc.Library.Changed -= changed; Svc.Favorites.Changed -= favorites; });
        ActualThemeChanged += (_, _) => UpdateFavoriteButtons(); // colors picked in code follow the theme
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (_name != e.Parameter as string) _albums = null;
        _name = e.Parameter as string;
        Load();
        Effects.EntranceSlide(Portrait, 20);
        Effects.EntranceSlide(Info, 24, 80);
    }

    private void Load()
    {
        _artist = _name != null ? Svc.Library.FindArtist(_name) : null;
        if (_artist == null) return;
        NameText.Text = _artist.Name;
        SummaryText.Text = _artist.Summary;
        Portrait.Source = _artist.ArtPath; Portrait.Seed = _artist.Name;
        Backdrop.Source = _artist.ArtPath; Backdrop.Seed = _artist.Name;
        var popular = _artist.Tracks.OrderByDescending(t => t.PlayCount).ThenByDescending(t => t.Rating).Take(5).ToList();
        Popular.Context = PlayContext.Of(_artist);
        Popular.SetItems(popular);
        var bound = CollectionSync.SyncOrReplace(_albums, _artist.Albums, a => a.Key, (a, b) => a.Title == b.Title && a.Artist == b.Artist && a.Year == b.Year && a.ArtPath == b.ArtPath && a.TrackCount == b.TrackCount);
        if (!ReferenceEquals(bound, _albums)) { _albums = bound; Albums.ItemsSource = _albums; }
        UpdateFavoriteButtons();
    }

    private void UpdateFavoriteButtons()
    {
        if (_artist == null) return;
        bool fav = Svc.Favorites.IsFavoriteArtist(_artist.Name);
        FavoriteButton.Content = fav ? "" : "";
        FavoriteButton.Foreground = fav ? ThemeRes.Brush("AccentTextFillColorPrimaryBrush", this) : ThemeRes.Brush("TextFillColorPrimaryBrush", this);
        ToolTipService.SetToolTip(FavoriteButton, fav ? "Remove from Favorite" : "Add to Favorite (listed first on Artists)");
        bool pinned = Svc.Favorites.IsPinned(FavoritesService.Key(_artist));
        PinButton.Content = pinned ? "\uE77A" : "\uE718";
        ToolTipService.SetToolTip(PinButton, pinned ? "Unpin from Quick access" : "Pin to Quick access");
    }

    private void OnFavorite(object sender, RoutedEventArgs e)
    {
        if (_artist != null) Svc.Favorites.SetFavoriteArtist(_artist.Name, !Svc.Favorites.IsFavoriteArtist(_artist.Name));
    }

    private void OnPin(object sender, RoutedEventArgs e)
    {
        if (_artist == null) return;
        var key = FavoritesService.Key(_artist);
        Svc.Favorites.SetPinned(key, !Svc.Favorites.IsPinned(key));
    }

    private async void OnPlay(object sender, RoutedEventArgs e) { if (_artist != null) await Svc.Player.PlayAsync(_artist.Tracks, 0, shuffle: false, context: PlayContext.Of(_artist)); }
    private async void OnShuffle(object sender, RoutedEventArgs e) { if (_artist != null) await Svc.Player.PlayAsync(_artist.Tracks, 0, shuffle: true, context: PlayContext.Of(_artist)); }
    private void OnAlbumTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: Album a } el) return;
        for (var p = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(el); p != null; p = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(p))
            if (p is Button) return; // the card's play button handles itself
        App.MainWindow.Navigate(typeof(AlbumDetailPage), a.Key);
    }

    /// <summary>Name (on every song), photo and songs to add, in one dialog.</summary>
    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        if (_artist == null) return;
        _name = await EditArtistDialog.ShowAsync(_artist);
        Load();
    }

    private void OnAllSongs(object sender, RoutedEventArgs e)
    {
        if (_artist == null) return;
        var name = _artist.Name;
        App.MainWindow.Navigate(typeof(SongsPage), new SongListRequest(name, "Artist", () => Svc.Library.FindArtist(name)?.Tracks ?? new List<Track>(), _artist.ArtPath));
    }
}

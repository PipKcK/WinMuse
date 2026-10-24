using System.Collections.ObjectModel;
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

public sealed partial class AlbumsPage : Page
{
    private Album? _clicked;
    private bool _dirty = true;
    private ObservableCollection<Album>? _items;

    public AlbumsPage()
    {
        InitializeComponent();
        SortBox.SelectedIndex = 0;
        Svc.Library.Changed += () => { _dirty = true; if (IsLoaded) Load(); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (_dirty) Load();
        Effects.EntranceSlide(Header, 16);
        if (e.NavigationMode == NavigationMode.Back && _clicked != null)
        {
            var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("AlbumArtBack");
            if (anim != null) _ = Grid.TryStartConnectedAnimationAsync(anim, _clicked, "Art");
        }
    }

    private void Load()
    {
        _dirty = false;
        var q = FilterBox.Text.Trim();
        var albums = Svc.Library.Albums.Where(a => q.Length == 0 || a.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                                   || a.Artist.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        albums = SortBox.SelectedIndex switch
        {
            1 => albums.OrderBy(a => a.Artist, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Year),
            2 => albums.OrderByDescending(a => a.Year),
            3 => albums.OrderByDescending(a => a.AddedUtc),
            4 => albums.OrderByDescending(a => a.Tracks.Sum(t => t.PlayCount)),
            _ => albums,
        };
        var list = albums.ToList();
        var bound = CollectionSync.SyncOrReplace(_items, list, a => a.Key, (a, b) => a.Title == b.Title && a.Artist == b.Artist && a.Year == b.Year && a.ArtPath == b.ArtPath && a.TrackCount == b.TrackCount);
        if (!ReferenceEquals(bound, _items)) { _items = bound; Grid.ItemsSource = _items; }
        CountText.Text = $"{Svc.Library.Albums.Count:N0} albums";
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Load(); }
    private void OnFilter(object sender, TextChangedEventArgs e) => Load();

    private void OnRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: Album a } el) return;
        TrackActions.BuildAlbumMenu(a).ShowAt(el, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(el) });
        e.Handled = true;
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        _clicked = (Album)e.ClickedItem;
        Grid.PrepareConnectedAnimation("AlbumArt", _clicked, "Art");
        App.MainWindow.Navigate(typeof(AlbumDetailPage), _clicked.Key);
    }

    private async void OnNewAlbum(object sender, RoutedEventArgs e) => await CreateAlbumDialog.ShowAsync(null);
}

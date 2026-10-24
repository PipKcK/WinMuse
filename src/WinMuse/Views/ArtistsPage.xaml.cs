using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinMuse.Controls;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Views;

public sealed partial class ArtistsPage : Page
{
    private bool _dirty = true;
    private ObservableCollection<Artist>? _items;

    public ArtistsPage()
    {
        InitializeComponent();
        SortBox.SelectedIndex = 0;
        Svc.Library.Changed += () => { _dirty = true; if (IsLoaded) Load(); };
        Svc.Favorites.Changed += () => { _dirty = true; if (IsLoaded) Load(); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (_dirty) Load();
        Effects.EntranceSlide(Header, 16);
    }

    private void Load()
    {
        _dirty = false;
        var q = FilterBox.Text.Trim();
        // Favorite artists first; each group in the chosen order (the library's list is already A–Z).
        var matches = Svc.Library.Artists.Where(a => q.Length == 0 || a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        var list = (SortBox.SelectedIndex switch
        {
            1 => matches.OrderByDescending(a => a.IsFavorite).ThenByDescending(a => a.ChangedUtc), // a song added to them most recently
            2 => matches.OrderByDescending(a => a.IsFavorite).ThenByDescending(a => a.Tracks.Count),
            _ => matches.OrderByDescending(a => a.IsFavorite),
        }).ToList();
        var bound = CollectionSync.SyncOrReplace(_items, list, a => a.Name, (a, b) => a.ArtPath == b.ArtPath && a.Summary == b.Summary && a.IsFavorite == b.IsFavorite);
        if (!ReferenceEquals(bound, _items)) { _items = bound; Grid.ItemsSource = _items; }
        CountText.Text = $"{Svc.Library.Artists.Count:N0} artists";
    }

    private void OnFilter(object sender, TextChangedEventArgs e) => Load();
    private void OnSortChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Load(); }
    private async void OnNewArtist(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await Dialogs.EditArtistDialog.ShowCreateAsync();
    private void OnItemClick(object sender, ItemClickEventArgs e) => App.MainWindow.Navigate(typeof(ArtistDetailPage), ((Artist)e.ClickedItem).Name);

    private void OnRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not Microsoft.UI.Xaml.FrameworkElement { DataContext: Artist a } el) return;
        TrackActions.BuildArtistMenu(a).ShowAt(el, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(el) });
        e.Handled = true;
    }
}

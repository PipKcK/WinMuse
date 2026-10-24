using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinMuse.Controls;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Views;

public sealed partial class GenresPage : Page
{
    private ObservableCollection<Genre>? _items;

    public GenresPage()
    {
        InitializeComponent();
        SortBox.SelectedIndex = 0;
        Svc.Library.Changed += () => { if (IsLoaded) Load(); };
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Load();
        Effects.EntranceSlide(Header, 16);
    }

    private void Load()
    {
        var q = FilterBox.Text.Trim();
        var genres = Svc.Library.Genres.Where(g => q.Length == 0 || g.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        var list = (SortBox.SelectedIndex == 1 ? genres.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase) : genres).ToList(); // the library's list is by song count
        var bound = CollectionSync.SyncOrReplace(_items, list, g => g.Name, (a, b) => a.Summary == b.Summary && a.Art1 == b.Art1);
        if (!ReferenceEquals(bound, _items)) { _items = bound; Grid.ItemsSource = _items; }
        CountText.Text = $"{Svc.Library.Genres.Count:N0} genres";
    }

    private void OnFilter(object sender, TextChangedEventArgs e) => Load();
    private void OnSortChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) Load(); }
    private async void OnNewGenre(object sender, Microsoft.UI.Xaml.RoutedEventArgs e) => await Dialogs.EditGenreDialog.ShowAsync(null);

    private void OnRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not Microsoft.UI.Xaml.FrameworkElement { DataContext: Genre g } el) return;
        TrackActions.BuildGenreMenu(g).ShowAt(el, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(el) });
        e.Handled = true;
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        var g = (Genre)e.ClickedItem;
        var name = g.Name;
        App.MainWindow.Navigate(typeof(SongsPage), new SongListRequest(name, "Genre",
            () => Svc.Library.Genres.FirstOrDefault(x => x.Name == name)?.Tracks.OrderBy(t => t.DisplayArtist).ThenBy(t => t.DisplayAlbum).ThenBy(t => t.TrackNumber).ToList() ?? new List<Track>(),
            g.Art1));
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinMuse.Controls;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Views;

public sealed record SearchSuggestion(string Title, string Subtitle, string? ArtPath, string Glyph, object Payload);

public sealed partial class SearchPage : Page
{
    public SearchPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var q = e.Parameter as string ?? "";
        TitleText.Text = $"Results for “{q}”";
        var (songs, albums, artists) = Svc.Library.Search(q, 500);
        SummaryText.Text = $"{songs.Count} songs · {albums.Count} albums · {artists.Count} artists";
        ArtistsList.ItemsSource = artists.Take(20).ToList();
        AlbumsList.ItemsSource = albums.Take(30).ToList();
        ArtistsSection.Visibility = artists.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AlbumsSection.Visibility = albums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Songs.SetItems(songs);
        Effects.EntranceSlide(Header, 16);
    }

    private void OnArtistClick(object sender, ItemClickEventArgs e) => App.MainWindow.Navigate(typeof(ArtistDetailPage), ((Artist)e.ClickedItem).Name);
    private void OnAlbumClick(object sender, ItemClickEventArgs e) => App.MainWindow.Navigate(typeof(AlbumDetailPage), ((Album)e.ClickedItem).Key);
}

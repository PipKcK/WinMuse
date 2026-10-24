using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinMuse.Controls;
using WinMuse.Dialogs;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Views;

public sealed partial class PlaylistsPage : Page
{
    public PlaylistsPage()
    {
        InitializeComponent();
        Action changed = Load;
        this.WhileLoaded(() => Svc.Library.PlaylistsChanged += changed, () => Svc.Library.PlaylistsChanged -= changed);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Load();
        Effects.EntranceSlide(Header, 16);
    }

    private System.Collections.ObjectModel.ObservableCollection<Playlist>? _items;
    private readonly Dictionary<long, string> _shown = new();

    /// <summary>
    /// Updated in place: a fetch saves songs one by one, and replacing the whole grid each time made the page flash.
    /// Only cards whose name, songs or cover changed are refreshed.
    /// </summary>
    private void Load()
    {
        var lists = Svc.Library.Playlists.ToList();
        static string Look(Playlist p) => string.Join("|", p.Name, p.Summary, p.Art1, p.Art2, p.Art3, p.Art4, p.IsMosaic, p.IsSmart);
        foreach (var p in lists)
            if (_shown.TryGetValue(p.Id, out var was) && was != Look(p)) p.Refresh();
        _shown.Clear();
        foreach (var p in lists) _shown[p.Id] = Look(p);
        var bound = CollectionSync.SyncOrReplace(_items, lists, p => p.Id);
        if (!ReferenceEquals(bound, _items)) { _items = bound; Grid.ItemsSource = _items; }
        CountText.Text = $"{lists.Count} playlists";
        Empty.Visibility = lists.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnItemClick(object sender, ItemClickEventArgs e) => App.MainWindow.Navigate(typeof(PlaylistDetailPage), ((Playlist)e.ClickedItem).Id);

    private async void OnNew(object sender, RoutedEventArgs e)
    {
        NewFlyout.Hide();
        var p = await PlaylistDialog.ShowCreateAsync(null);
        if (p != null) App.MainWindow.Navigate(typeof(PlaylistDetailPage), p.Id);
    }

    private async void OnNewSmart(object sender, RoutedEventArgs e)
    {
        NewFlyout.Hide();
        var p = await PlaylistDialog.ShowCreateSmartAsync();
        if (p != null) App.MainWindow.Navigate(typeof(PlaylistDetailPage), p.Id);
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        var path = await Pickers.PickFileAsync(".m3u", ".m3u8");
        if (path == null) return;
        var dir = Path.GetDirectoryName(path)!;
        var tracks = new List<Track>();
        int missing = 0;
        foreach (var raw in await File.ReadAllLinesAsync(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var full = Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(dir, line));
            var t = Svc.Library.GetByPath(full);
            if (t != null) tracks.Add(t); else missing++;
        }
        var p = Svc.Library.CreatePlaylist(Path.GetFileNameWithoutExtension(path), "Imported playlist", null, tracks);
        Notifier.Success($"Playlist imported: {p.Name}", missing > 0 ? $"{missing} songs are not in the library and were skipped." : null);
        Load();
    }
}

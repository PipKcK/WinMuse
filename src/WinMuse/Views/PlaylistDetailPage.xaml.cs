using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinMuse.Controls;
using WinMuse.Dialogs;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Views;

public sealed partial class PlaylistDetailPage : Page
{
    private Playlist? _playlist;

    public PlaylistDetailPage()
    {
        InitializeComponent();
        Tracks.Reordered += order => { if (Editable) Svc.Library.SetPlaylistTracks(_playlist!, order); };
        Tracks.DeleteRequested += RemoveIndices;
        Tracks.ExtraMenuItems = (_, idx) => Editable
            ? new[] { new MenuAction("Remove from playlist", "", () => RemoveIndices(idx)) }
            : Array.Empty<MenuAction>();
        Action changed = Load;
        // Automatic playlists follow the library and play history.
        Action libraryChanged = () => { if (_playlist is { IsSystem: true }) Load(); };
        this.WhileLoaded(() => { Svc.Library.PlaylistsChanged += changed; Svc.Library.Changed += libraryChanged; },
                         () => { Svc.Library.PlaylistsChanged -= changed; Svc.Library.Changed -= libraryChanged; });
    }

    /// <summary>
    /// Opens a playlist and plays it from <paramref name="start"/>: the page slides in first, then playback starts
    /// once the transition has settled and the list scrolls to the playing song.
    /// </summary>
    public static async Task OpenAndPlayAsync(Playlist p, Track start)
    {
        Svc.Library.TouchPlaylist(p);
        App.MainWindow.Navigate(typeof(PlaylistDetailPage), p.Id);
        await Task.Delay(260);
        await Svc.Player.PlayAsync(p.Tracks, Math.Max(0, p.Tracks.IndexOf(start)), context: PlayContext.Of(p));
        if (App.MainWindow.CurrentPage is PlaylistDetailPage page) page.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, page.Tracks.ScrollToPlaying);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        var id = e.Parameter is long l ? l : 0;
        _playlist = id < 0 ? Svc.Library.SystemPlaylist(id) : Svc.Library.Playlists.FirstOrDefault(p => p.Id == id);
        Load();
        Effects.EntranceSlide(CoverHost, 20);
        Effects.EntranceSlide(Info, 24, 80);
    }

    private void Load()
    {
        if (_playlist is { IsSystem: true }) _playlist = Svc.Library.SystemPlaylist(_playlist.Id); // current songs
        if (_playlist == null || (!_playlist.IsSystem && !Svc.Library.Playlists.Contains(_playlist))) { Tracks.SetItems(Array.Empty<Track>()); return; }
        var p = _playlist;
        KindText.Text = p.IsSystem ? "AUTOMATIC PLAYLIST" : p.IsSmart ? "SMART PLAYLIST" : "PLAYLIST";
        EditButton.Visibility = p.IsSystem ? Visibility.Collapsed : Visibility.Visible;
        Tracks.EmptyMessage = p.IsSystem ? "Nothing here yet. This playlist fills itself as you add and play music." : "This playlist is empty. Right-click a song and select Add to playlist.";
        TitleText.Text = p.Name;
        DescText.Text = p.Description ?? "";
        DescText.Visibility = string.IsNullOrWhiteSpace(p.Description) ? Visibility.Collapsed : Visibility.Visible;
        SummaryText.Text = p.Summary;
        Cover.Source = p.Art1; Cover.Seed = p.Name;
        Mosaic.Visibility = p.IsMosaic ? Visibility.Visible : Visibility.Collapsed;
        M1.Source = p.Art1; M2.Source = p.Art2; M3.Source = p.Art3; M4.Source = p.Art4;
        var (a, _) = ColorKit.GradientFor(p.Name);
        Stop1.Color = Svc.Theme.Accent;
        Tracks.Reorderable = Editable;
        Tracks.Sortable = !Editable;
        Tracks.Context = PlayContext.Of(p);
        Tracks.SetItems(p.Tracks);
    }

    /// <summary>Your own playlists: songs can be reordered and removed (smart and automatic ones fill themselves).</summary>
    private bool Editable => _playlist is { IsSmart: false, IsSystem: false };

    private void RemoveIndices(List<int> idx)
    {
        if (!Editable) return;
        var p = _playlist!;
        var keep = Tracks.Items.Where((_, i) => !idx.Contains(i)).ToList();
        Svc.Library.SetPlaylistTracks(p, keep);
    }

    private async void OnPlay(object sender, RoutedEventArgs e) { if (_playlist?.Tracks.Count > 0) { Svc.Library.TouchPlaylist(_playlist); await Svc.Player.PlayAsync(Tracks.Items.ToList(), 0, shuffle: false, context: PlayContext.Of(_playlist)); } }
    private async void OnShuffle(object sender, RoutedEventArgs e) { if (_playlist?.Tracks.Count > 0) { Svc.Library.TouchPlaylist(_playlist); await Svc.Player.PlayAsync(Tracks.Items.ToList(), 0, shuffle: true, context: PlayContext.Of(_playlist)); } }
    private async void OnEdit(object sender, RoutedEventArgs e) { if (_playlist != null) { await PlaylistDialog.ShowEditAsync(_playlist); Load(); } }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (_playlist == null) return;
        var p = _playlist;
        var menu = new MenuFlyout();
        menu.Items.Add(TrackActions.Item("Add to queue", "", () => Svc.Player.AddToQueue(p.Tracks)));
        if (p.IsSystem)
        {
            // Automatic: Music decides its songs, name and cover.
            menu.Items.Add(TrackActions.Item("Export…", "", async () => await ExportAsync(p)));
            menu.ShowAt((FrameworkElement)sender);
            return;
        }
        menu.Items.Add(TrackActions.Item("Change cover…", "", async () =>
        {
            var img = await Pickers.PickImageAsync();
            if (img == null) return;
            p.CoverPath = Svc.Art.Save(await File.ReadAllBytesAsync(img));
            Svc.Library.UpdatePlaylist(p);
        }));
        if (p.CoverPath != null)
            menu.Items.Add(TrackActions.Item("Use album art mosaic", "", () => { p.CoverPath = null; Svc.Library.UpdatePlaylist(p); }));
        menu.Items.Add(TrackActions.Item("Export…", "", async () => await ExportAsync(p)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(TrackActions.Item("Delete playlist", "", async () =>
        {
            if (!await TrackActions.Confirm($"Delete \"{p.Name}\"?", "The songs remain in your library.", "Delete")) return;
            Svc.Library.DeletePlaylist(p);
            App.MainWindow.GoBack();
        }));
        menu.ShowAt((FrameworkElement)sender);
    }

    private static async Task ExportAsync(Playlist p)
    {
        var path = await Pickers.PickSaveAsync(p.Name, "M3U playlist", ".m3u8");
        if (path == null) return;
        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var t in p.Tracks)
        {
            sb.Append($"#EXTINF:{(int)t.Duration.TotalSeconds},{t.DisplayArtist} - {t.DisplayTitle}\n");
            sb.Append(t.Path).Append('\n');
        }
        await File.WriteAllTextAsync(path, sb.ToString(), new UTF8Encoding(false));
        Notifier.Success("Playlist exported", path);
    }
}

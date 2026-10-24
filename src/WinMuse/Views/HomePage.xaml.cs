using System.Collections.ObjectModel;
using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinMuse.Controls;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Views;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
        // Home is cached between visits: changes made while away only mark it dirty (see OnNavigatedTo).
        Svc.Library.Changed += () => { if (IsLoaded) Refresh(); else _dirty = true; };
        Svc.Library.ScanCompleted += () => { if (IsLoaded) Refresh(); else _dirty = true; };
        Svc.Theme.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ThemeService.Accent)) TintHero(); };
        Svc.Player.TrackChanged += _ => UpdateResume();
        Svc.Favorites.Changed += () => { if (IsLoaded) Refresh(); else _dirty = true; };
        Svc.Continue.Changed += () => { if (IsLoaded) RefreshContinue(); else _dirty = true; };
        Svc.Library.PlaylistsChanged += () => { if (IsLoaded) RefreshPlaylists(); else _dirty = true; };
        _loadingSort = true;
        PlaylistSortBox.SelectedIndex = (int)Svc.Settings.Current.HomePlaylistSort;
        _loadingSort = false;
        // The fanned covers need room; hide them rather than letting them crowd the buttons.
        Hero.SizeChanged += (_, e) => ArtFan.Opacity = e.NewSize.Width >= 760 ? 1 : 0;
        // Home scrolls smoothly with the wheel (rows hand their wheel to the page too, see ShelfRow).
        // On the page's content, so it runs before the page's scroll viewer handles the wheel itself.
        ((UIElement)Scroller.Content).AddHandler(PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPageWheel), false);
        Loaded += (_, _) => { SpinGreetingIcon(); ApplyGreetingStyle(); };
        // Input outside the appearance panel reaches the page (instead of an invisible dismiss layer), so the
        // wheel there both closes the panel and scrolls (see OnPageWheel).
        AppearanceFlyout.OverlayInputPassThroughElement = Scroller;
        ((UIElement)Scroller.Content).AddHandler(PointerWheelChangedEvent,
            new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => { if (AppearanceFlyout.IsOpen) AppearanceFlyout.Hide(); }), true);
        Svc.Settings.Changed += name =>
        {
            if (name == nameof(AppSettings.GreetingStyle) && IsLoaded) ApplyGreetingStyle();
            if (name == nameof(AppSettings.ShelfCardColor) && IsLoaded) UpdateAppearanceButton(); // the circle's color changed
        };
        // Appearance button hover: the icon lifts and the palette tilts.
        AppearanceDisc.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) };
        AppearanceIcon.RotationTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(260) };
        AppearanceButton.AddHandler(PointerEnteredEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => { _appearanceHover = true; UpdateAppearanceButton(); }), true);
        AppearanceButton.AddHandler(PointerExitedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) => { _appearanceHover = false; UpdateAppearanceButton(); }), true);
        // Again once the theme switch has settled: the greeting's text colors must never keep the previous theme's.
        // The greeting is always white: if anything sets another color, it goes straight back.
        void KeepLight(DependencyObject el, DependencyProperty prop, byte alpha) =>
            el.RegisterPropertyChangedCallback(prop, (d, p) =>
            {
                if (PlainGreeting) return; // None: the theme's colors
                var want = Windows.UI.Color.FromArgb(alpha, 255, 255, 255);
                if ((d.GetValue(p) as SolidColorBrush)?.Color != want) d.SetValue(p, new SolidColorBrush(want));
            });
        KeepLight(Greeting, TextBlock.ForegroundProperty, 0xFF);
        KeepLight(GreetingIcon, IconElement.ForegroundProperty, 0xFF);
        KeepLight(Stats, TextBlock.ForegroundProperty, 0xE6);
        KeepLight(ResumeButton, Control.ForegroundProperty, 0xFF);
        ActualThemeChanged += (_, _) => { ApplyGreetingStyle(); DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ApplyGreetingColors); };
        // The glow ends halfway down the Quick access tiles, wherever they are.
        QuickPicks.SizeChanged += (_, _) => SizeGreetingGlow();
        Hero.SizeChanged += (_, _) => SizeGreetingGlow();
    }

    private bool _dirty = true, _shownOnce;

    // Where each of the lower rows starts on the page, measured as Home is left (while it is still on screen).
    private readonly Dictionary<FrameworkElement, double> _rowTops = new();
    private FrameworkElement[] DeferrableRows => new FrameworkElement[] { ContinueSection, MostPlayedSection, RediscoverSection, PlaylistsSection };

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        base.OnNavigatingFrom(e);
        if (!IsLoaded) return;
        foreach (var section in DeferrableRows)
            if (section.Visibility == Visibility.Visible)
                _rowTops[section] = section.TransformToVisual(Scroller).TransformPoint(default).Y + Scroller.VerticalOffset;
        DetachRowsBelowView();
    }

    // Rows taken out of the page while Home is away, with their positions in the page.
    private readonly List<(int index, FrameworkElement row)> _detached = new();

    /// <summary>
    /// Opening Home was slower than other tabs because a cached page re-entering the window walks every element in it,
    /// hidden or not, and Home's card rows hold thousands. Rows below the visible area are therefore taken out of the
    /// page when Home is left, and put back on the first frame after it reappears (out of sight, so nothing shifts).
    /// </summary>
    private void DetachRowsBelowView()
    {
        if (Scroller.VerticalOffset >= 200) return; // scrolled into the rows: keep everything, so the position holds
        double viewBottom = Scroller.ViewportHeight;
        foreach (var row in DeferrableRows)
        {
            if (row.Visibility != Visibility.Visible || !_rowTops.TryGetValue(row, out var top) || top - Scroller.VerticalOffset < viewBottom) continue;
            _detached.Add((ContentStack.Children.IndexOf(row), row));
        }
        // Remove from the end, so the recorded indices stay valid when they are put back in order.
        foreach (var (_, row) in _detached.OrderByDescending(d => d.index)) ContentStack.Children.Remove(row);
    }

    private void ReattachRowsAfterFirstFrame()
    {
        EventHandler<object>? show = null;
        show = (_, _) =>
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= show;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                foreach (var (index, row) in _detached.OrderBy(d => d.index))
                    if (!ContentStack.Children.Contains(row)) ContentStack.Children.Insert(Math.Min(index, ContentStack.Children.Count), row);
                _detached.Clear();
            });
        };
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += show;
    }

    /// <summary>
    /// Opening Home must feel as quick as any other tab. The first visit builds the page; later visits show the cached
    /// page at once and only rebuild (just after it appears) if something changed while away.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Greeting.Text = Format.Greeting(); // cheap things that may have changed with time
        UpdateResume();
        ApplyGreetingColors(); // the theme or greeting style may have changed while Home was away
        RefreshContinue(); // songs and progress move on while Home is away
        // Rows that were taken out when Home was left (see OnNavigatingFrom) go back right after it appears.
        if (_detached.Count > 0) ReattachRowsAfterFirstFrame();
        if (!_shownOnce) { _shownOnce = true; _dirty = false; Refresh(); }
        else if (_dirty)
        {
            _dirty = false;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Refresh);
        }
        Effects.EntranceSlide(Hero, 24);
    }

    private void Refresh()
    {
        var lib = Svc.Library;
        var tracks = lib.Tracks;
        var hasMusic = tracks.Count > 0;
        var hasFolders = Svc.Settings.Current.MusicFolders.Count > 0;

        Greeting.Text = Format.Greeting();
        GreetingIcon.Glyph = DateTime.Now.Hour is >= 6 and < 18 ? "\uE706" : "\uE708"; // sun / moon
        Stats.Text = hasMusic
            ? $"{tracks.Count:N0} songs · {lib.Albums.Count:N0} albums · {lib.Artists.Count:N0} artists · {Format.LongTime(TimeSpan.FromMilliseconds(tracks.Sum(t => t.DurationMs)))} of music"
            : hasFolders && lib.IsScanning ? "Scanning library…" : "Your library is empty";
        ShuffleAllButton.IsEnabled = hasMusic;
        Welcome.Visibility = !hasMusic && !lib.IsScanning ? Visibility.Visible : Visibility.Collapsed;
        var musicFolder = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        bool musicAdded = Svc.Settings.Current.MusicFolders.Contains(musicFolder, StringComparer.OrdinalIgnoreCase);
        WelcomeTitle.Text = hasFolders ? "No Songs Found" : "Set Up Your Library";
        WelcomeText.Text = hasFolders
            ? $"Copy music into {musicFolder} or add another folder. New files are detected automatically and their metadata, album art and lyrics are updated."
            : "Add the folders that contain your music. Files remain on this PC. Supported formats include MP3, FLAC, ALAC, AAC, Opus, Vorbis, WAV, AIFF, APE, WavPack and DSD.";
        MusicFolderButtonText.Text = musicAdded ? "Open Music Folder" : "Add Music Folder";

        Bind(QuickPicks, ref _quick, BuildQuickAccess(), q => q.Key, (a, b) => a.SameAs(b));
        QuickSection.Visibility = hasMusic ? Visibility.Visible : Visibility.Collapsed;

        // Automatic playlists, updated in place (cards that stay keep their covers).
        ShowSystemPlaylist(RecentAdded, RecentAddedSection, ref _recentAdded, LibraryService.RecentlyAddedId);
        RefreshContinue();
        ShowSystemPlaylist(MostPlayed, MostPlayedSection, ref _most, LibraryService.MostPlayedId);
        ShowSystemPlaylist(Rediscover, RediscoverSection, ref _rediscover, LibraryService.NotPlayedRecentlyId);

        RefreshPlaylists();

        var fan = lib.Albums.Where(a => a.ArtPath != null).OrderByDescending(a => a.AddedUtc).Take(3).ToList();
        var fanKey = string.Join("|", fan.Select(a => a.ArtPath));
        if (fanKey != _fanKey) { _fanKey = fanKey; BuildArtFan(fan); }
        UpdateResume();
        TintHero();
    }

    private ObservableCollection<QuickEntry> _quick = new();
    private bool _loadingSort;

    // ---------------- Playlists row ----------------

    private void RefreshPlaylists()
    {
        var s = Svc.Settings.Current;
        var all = Svc.Library.Playlists.Where(p => p.Tracks.Count > 0).ToList();
        IEnumerable<Playlist> sorted = s.HomePlaylistSort switch
        {
            PlaylistSort.Alphabetical => all.OrderBy(p => p.Name, NaturalOrder.Instance), // "Mix 2" before "Mix 10", like File Explorer
            PlaylistSort.DateCreated => all.OrderByDescending(p => p.CreatedUtc).ThenByDescending(p => p.Id),
            PlaylistSort.Custom => all.OrderBy(p => s.PlaylistOrder.IndexOf(p.Id) is var i && i >= 0 ? i : int.MaxValue).ThenByDescending(p => p.Id),
            _ => all.OrderByDescending(Svc.Library.LastActivity).ThenByDescending(p => p.Id),
        };
        var list = sorted.ToList();
        // Updated in place: only playlists whose name or songs changed are rebuilt, a new order is applied by moving
        // rows. Rebuilding every row on the way back to Home made it visibly refresh and reload all its covers.
        string Sig(Playlist p) => p.Name + "\u001f" + string.Join(",", p.Tracks.Select(t => t.Id));
        foreach (var p in _playlists.ToList())
            if (!_playlistSigs.TryGetValue(p.Id, out var sig) || sig != Sig(p)) _playlists.Remove(p); // re-added fresh below
        if (!CollectionSync.Sync(_playlists, list, p => p.Id)) { _playlists = new ObservableCollection<Playlist>(list); HomePlaylists.ItemsSource = _playlists; }
        if (HomePlaylists.ItemsSource == null) HomePlaylists.ItemsSource = _playlists;
        _playlistSigs.Clear();
        foreach (var p in list) _playlistSigs[p.Id] = Sig(p);
        foreach (var p in list) p.CanMove = s.HomePlaylistSort == PlaylistSort.Custom;
        PlaylistCountText.Text = $"{list.Count} {(list.Count == 1 ? "playlist" : "playlists")}";
        PlaylistsSection.Visibility = list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private ObservableCollection<Playlist> _playlists = new();
    private readonly Dictionary<long, string> _playlistSigs = new();

    private void OnPlaylistSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSort) return;
        var sort = (PlaylistSort)PlaylistSortBox.SelectedIndex;
        var shown = _playlists.Select(p => p.Id).ToList();
        Svc.Settings.Set(nameof(AppSettings.HomePlaylistSort), s =>
        {
            // Custom starts from the order currently shown.
            if (sort == PlaylistSort.Custom && s.PlaylistOrder.Count == 0) s.PlaylistOrder = shown;
            s.HomePlaylistSort = sort;
        });
        RefreshPlaylists();
    }

    private static Playlist? PlaylistOf(object sender) => (sender as FrameworkElement)?.DataContext as Playlist;

    private void OnOpenPlaylist(object sender, RoutedEventArgs e)
    {
        if (PlaylistOf(sender) is not { } p) return;
        Svc.Library.TouchPlaylist(p);
        App.MainWindow.Navigate(typeof(PlaylistDetailPage), p.Id);
    }

    /// <summary>A song card in a playlist row opens the playlist and plays it from that song.</summary>
    private async void OnPlaylistSongClick(object sender, ItemClickEventArgs e)
    {
        if (PlaylistOf(sender) is { } p && e.ClickedItem is Track t) await PlaylistDetailPage.OpenAndPlayAsync(p, t);
    }

    private void OnMovePlaylistUp(object sender, RoutedEventArgs e) => MovePlaylist(sender, -1);
    private void OnMovePlaylistDown(object sender, RoutedEventArgs e) => MovePlaylist(sender, 1);

    private void MovePlaylist(object sender, int by)
    {
        if (PlaylistOf(sender) is not { } p) return;
        var shown = _playlists.ToList();
        var order = shown.Select(x => x.Id).ToList();
        int i = order.IndexOf(p.Id), j = i + by;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        Svc.Settings.Set(nameof(AppSettings.PlaylistOrder), s => s.PlaylistOrder = order);
        RefreshPlaylists();
    }

    private void OnSeePlaylists(object sender, RoutedEventArgs e) => App.MainWindow.Navigate(typeof(PlaylistsPage));
    private ObservableCollection<Track> _recentAdded = new(), _most = new(), _rediscover = new();
    private ObservableCollection<ContinueItem> _continue = new();

    /// <summary>
    /// Continue Listening cards (entries whose song is no longer in the library are skipped). Cards that stay are
    /// updated in place; one whose song moved on (next song of its playlist, album or artist) eases its new song in.
    /// </summary>
    private void RefreshContinue()
    {
        var current = _continue.ToDictionary(c => c.Key);
        var items = new List<ContinueItem>();
        var changed = new List<ContinueItem>();
        foreach (var e in Svc.Continue.Entries)
        {
            if (Svc.Continue.TrackOf(e) is not { } t) continue;
            var key = $"{e.Kind}:{e.Key}";
            if (current.TryGetValue(key, out var item)) { if (item.Update(e, t)) changed.Add(item); }
            else item = new ContinueItem(e, t);
            items.Add(item);
        }
        Bind(ContinueRow, ref _continue, items, c => c.Key);
        ContinueSection.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in changed)
            if (ContinueRow.ContainerFromItem(item) is ListViewItem { ContentTemplateRoot: FrameworkElement root } && FindNamed(root, "CardContent") is { } content)
                Effects.SwapIn(content);
    }

    private static FrameworkElement? FindNamed(DependencyObject d, string name)
    {
        if (d is FrameworkElement { Name: var n } fe && n == name) return fe;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindNamed(VisualTreeHelper.GetChild(d, i), name) is { } found) return found;
        return null;
    }

    private async void OnContinueClick(object sender, ItemClickEventArgs e) => await Svc.Continue.ResumeAsync(((ContinueItem)e.ClickedItem).Entry);

    private void OnContinueRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: ContinueItem c } el) return;
        var menu = new MenuFlyout();
        menu.Items.Add(TrackActions.Item("Resume", "\uE768", async () => await Svc.Continue.ResumeAsync(c.Entry)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(TrackActions.Item("Remove from Continue Listening", "\uE711", () => Svc.Continue.Remove(c.Entry)));
        menu.ShowAt(el, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(el) });
        e.Handled = true;
    }

    private static void ShowSystemPlaylist(ListView list, FrameworkElement section, ref ObservableCollection<Track> bound, long id)
    {
        var tracks = Svc.Library.SystemPlaylist(id)?.Tracks ?? new List<Track>();
        Bind(list, ref bound, tracks, t => t);
        section.Visibility = tracks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static long SystemPlaylistIdOf(object sender) => long.TryParse((sender as FrameworkElement)?.Tag as string, out var id) ? id : 0;

    private void OnOpenSystemPlaylist(object sender, RoutedEventArgs e) => App.MainWindow.Navigate(typeof(PlaylistDetailPage), SystemPlaylistIdOf(sender));

    /// <summary>Same as a playlist row: opens the automatic playlist and plays it from that song.</summary>
    private async void OnSystemSongClick(object sender, ItemClickEventArgs e)
    {
        if (Svc.Library.SystemPlaylist(SystemPlaylistIdOf(sender)) is { } p && e.ClickedItem is Track t) await PlaylistDetailPage.OpenAndPlayAsync(p, t);
    }

    /// <summary>
    /// Quick access, at most 10 tiles, in this order: pinned (up to 10), then favorites (any number, as many as fit),
    /// then random tiles only for the places left (up to 2 artists and 3 songs, preferring ones with a photo or album
    /// art). Within each group, artists come before songs. Random and favorite picks are kept between refreshes so tiles don't reshuffle on every update.
    /// </summary>
    private List<QuickEntry> BuildQuickAccess()
    {
        var fav = Svc.Favorites;
        var lib = Svc.Library;
        var list = new List<QuickEntry>();
        var keys = new HashSet<string>();
        void Add(QuickEntry e) { if (list.Count < FavoritesService.MaxQuickAccess && keys.Add(e.Key)) list.Add(e); }
        void AddGroup(IEnumerable<QuickEntry> group) { foreach (var e in group.OrderBy(e => e.IsArtist ? 0 : 1)) Add(e); } // stable: keeps order inside
        QuickEntry Song(Track t) => new() { Track = t, IsPinned = fav.IsPinned(FavoritesService.Key(t)), IsFavorite = t.IsFavorite };
        QuickEntry Artist(Artist a) => new() { Artist = a, IsPinned = fav.IsPinned(FavoritesService.Key(a)), IsFavorite = fav.IsFavoriteArtist(a.Name) };

        // Pinned, in pin order.
        AddGroup(fav.PinnedItems().Select(item => item is Track t ? Song(t) : Artist((Artist)item)));

        // Favorites: ones already shown stay, the rest in random order.
        var shown = _quick.Select(q => q.Key).ToHashSet();
        var favorites = fav.FavoriteArtists.Select(Artist).Concat(fav.FavoriteSongs.Select(Song)).ToList();
        AddGroup(favorites.Where(e => shown.Contains(e.Key)).Concat(favorites.Where(e => !shown.Contains(e.Key)).OrderBy(_ => Random.Shared.Next())));

        // Random: 2 artists and 3 songs; earlier picks first, then new ones with artwork before ones without.
        var artists = lib.Artists.Where(a => a.Name is not ("Unknown Artist" or "Various Artists")).ToList();
        var randomArtists = _quick.Where(q => q is { IsPinned: false, IsFavorite: false, Artist: not null } && !keys.Contains(q.Key))
            .Select(q => lib.FindArtist(q.Artist!.Name)).OfType<Artist>().Take(2).ToList();
        randomArtists.AddRange(artists.Where(a => !keys.Contains(FavoritesService.Key(a)) && !randomArtists.Any(r => r.Name == a.Name))
            .OrderBy(a => a.ImagePath != null ? 0 : a.ArtPath != null ? 1 : 2).ThenBy(_ => Random.Shared.Next()).Take(2 - randomArtists.Count));
        var randomSongs = _quick.Where(q => q is { IsPinned: false, IsFavorite: false, Track: not null } && lib.Tracks.Contains(q.Track!) && !keys.Contains(q.Key))
            .Select(q => q.Track!).Take(3).ToList();
        randomSongs.AddRange(lib.Tracks.Where(t => !keys.Contains(FavoritesService.Key(t)) && !randomSongs.Contains(t))
            .OrderBy(t => t.ArtPath != null ? 0 : 1).ThenBy(_ => Random.Shared.Next()).Take(3 - randomSongs.Count));
        AddGroup(randomArtists.Select(Artist).Concat(randomSongs.Select(Song)));
        return list;
    }
    private string? _fanKey;


    private static void Bind<T>(ItemsControl control, ref ObservableCollection<T> current, IList<T> items, Func<T, object> key, Func<T, T, bool>? same = null)
    {
        var bound = CollectionSync.SyncOrReplace(current, items, key, same);
        if (!ReferenceEquals(bound, current) || control.ItemsSource == null) { current = bound; control.ItemsSource = current; }
    }

    private void UpdateResume()
    {
        var t = Svc.Player.CurrentTrack;
        ResumeButton.Visibility = t != null ? Visibility.Visible : Visibility.Collapsed;
        if (t != null) ResumeText.Text = $"{t.DisplayTitle} · {t.DisplayArtist}";
    }

    private void OnPageWheel(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(Scroller).Properties;
        if (props.IsHorizontalMouseWheel || e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift)) return;
        SmoothScroll.For(Scroller).By(-props.MouseWheelDelta / 120.0 * SmoothScroll.NotchPixels);
        e.Handled = true;
    }

    /// <summary>A very slow turn of the sun (or moon) in the greeting.</summary>
    private void SpinGreetingIcon()
    {
        var v = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(GreetingIcon);
        var c = v.Compositor;
        v.CenterPoint = new Vector3((float)GreetingIcon.ActualWidth / 2, (float)GreetingIcon.ActualHeight / 2, 0);
        var spin = c.CreateScalarKeyFrameAnimation();
        spin.InsertKeyFrame(0, 0);
        spin.InsertKeyFrame(1, 360, c.CreateLinearEasingFunction());
        spin.Duration = TimeSpan.FromSeconds(40);
        spin.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
        v.StartAnimation("RotationAngleInDegrees", spin);
    }

    // ---------------- Greeting style ----------------

    private bool GradientGreeting => Svc.Settings.Current.GreetingStyle == GreetingStyle.Gradient;
    /// <summary>Gradient and None: no card, the greeting sits on the page (None just has no glow behind it).</summary>
    private bool NoCard => Svc.Settings.Current.GreetingStyle != GreetingStyle.Banner;
    private bool PlainGreeting => Svc.Settings.Current.GreetingStyle == GreetingStyle.None;

    /// <summary>Banner: the gradient card. Gradient: no card; the glow behind the page carries the color.</summary>
    private (GreetingStyle style, ElementTheme theme)? _greetingApplied;
    private bool _appearanceHover;

    private readonly SolidColorBrush _appearanceIconBrush = new(Microsoft.UI.Colors.White);

    /// <summary>
    /// Just the palette icon, no circle: black over a light color, white over a dark one. The color is measured from
    /// what is actually drawn behind the button (the banner, or the glow over the page), once colors settle.
    /// </summary>
    private void UpdateAppearanceButton()
    {
        AppearanceIcon.Foreground = _appearanceIconBrush;
        bool hover = _appearanceHover;
        AppearanceDisc.CenterPoint = new Vector3(18, 18, 0);
        AppearanceDisc.Scale = hover ? new Vector3(1.15f, 1.15f, 1) : Vector3.One;
        AppearanceIcon.CenterPoint = new Vector3(9, 9, 0);
        AppearanceIcon.Rotation = hover ? -20 : 0;
        if (!hover) _ = MeasureBehindAppearanceButtonAsync();
    }

    private int _inkVersion;

    private async Task MeasureBehindAppearanceButtonAsync()
    {
        var version = ++_inkVersion;
        await Task.Delay(800); // let accent and greeting color blends finish
        if (version != _inkVersion || !IsLoaded) return;
        try
        {
            FrameworkElement source = GradientGreeting ? GreetingGlow : Hero;
            if (source.ActualWidth < 1 || source.ActualHeight < 1) return;
            const int scale = 4; // a quarter-size render is plenty to read a color
            var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await rtb.RenderAsync(source, (int)(source.ActualWidth / scale), (int)(source.ActualHeight / scale));
            var pixels = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(await rtb.GetPixelsAsync()); // BGRA, premultiplied
            int w = rtb.PixelWidth, h = rtb.PixelHeight;
            var center = AppearanceButton.TransformToVisual(source).TransformPoint(new Windows.Foundation.Point(20, 20));
            // Sample a ring around the icon (not the icon itself).
            double r = 0, g = 0, bl = 0, a = 0; int n = 0;
            foreach (var (dx, dy) in new[] { (-22, -22), (22, -22), (-22, 22), (22, 22), (-26, 0), (26, 0), (0, -26), (0, 26) })
            {
                int x = (int)((center.X + dx) / scale), y = (int)((center.Y + dy) / scale);
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                int i = (y * w + x) * 4;
                bl += pixels[i]; g += pixels[i + 1]; r += pixels[i + 2]; a += pixels[i + 3]; n++;
            }
            if (n == 0) return;
            r /= n; g /= n; bl /= n; a /= n;
            // Over what is behind Home itself (approximate page tone for the theme).
            bool dark = ActualTheme == ElementTheme.Dark;
            double baseC = dark ? 30 : 228, k = 1 - a / 255.0;
            var behind = Windows.UI.Color.FromArgb(255, (byte)Math.Clamp(r + baseC * k, 0, 255), (byte)Math.Clamp(g + baseC * k, 0, 255), (byte)Math.Clamp(bl + baseC * k, 0, 255));
            if (version != _inkVersion) return;
            ApplyAppearanceInk(behind);
        }
        catch (Exception ex) { AppPaths.Log("Appearance icon color: " + ex.Message); }
    }

    private readonly SolidColorBrush _appearanceDiscBrush = new(Microsoft.UI.Colors.White);

    private void ApplyAppearanceInk(Windows.UI.Color behind)
    {
        // The icon sits on the circle: its card-style fill over what shows through it.
        var (fill, _) = ShelfCardColors.For(Svc.Settings.Current.ShelfCardColor, ActualTheme == ElementTheme.Dark);
        double fa = fill.A / 255.0;
        byte Mix(byte f, byte b) => (byte)Math.Clamp(f * fa + b * (1 - fa), 0, 255);
        behind = Windows.UI.Color.FromArgb(255, Mix(fill.R, behind.R), Mix(fill.G, behind.G), Mix(fill.B, behind.B));
        double L(byte c) { var v = c / 255.0; return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        double lum = 0.2126 * L(behind.R) + 0.7152 * L(behind.G) + 0.0722 * L(behind.B);
        // Black from 0.24: just above the WCAG crossover (0.179), as white reads better on saturated blues and purples there.
        var ink = lum > 0.24 ? Windows.UI.Color.FromArgb(235, 0, 0, 0) : Windows.UI.Color.FromArgb(255, 255, 255, 255);
        ColorTween.To(_appearanceIconBrush, ink, 400);

        // Card style None has no card color to borrow: the circle is the icon's opposite (white behind a black icon,
        // black behind a white one), so the button still stands out. Other styles use the cards' own brushes.
        if (Svc.Settings.Current.ShelfCardColor == ShelfCardColor.None)
        {
            var disc = ink.R < 128 ? Windows.UI.Color.FromArgb(235, 255, 255, 255) : Windows.UI.Color.FromArgb(220, 20, 20, 24);
            if (AppearanceDisc.Background != _appearanceDiscBrush) { _appearanceDiscBrush.Color = disc; AppearanceDisc.Background = _appearanceDiscBrush; }
            else ColorTween.To(_appearanceDiscBrush, disc, 400);
            AppearanceDisc.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        else if (AppearanceDisc.Background == _appearanceDiscBrush)
        {
            AppearanceDisc.Background = (Brush)Application.Current.Resources["ShelfCardBrush"];
            AppearanceDisc.BorderBrush = (Brush)Application.Current.Resources["ShelfCardStrokeBrush"];
        }
    }

    private void ApplyGreetingStyle()
    {
        bool glow = NoCard;
        ApplyGreetingColors(); // cheap, so always (a stale color after a theme switch is what this guards against)
        // Nothing else to redo when Home reappears with the same style and theme (keeps opening Home quick).
        var style = Svc.Settings.Current.GreetingStyle;
        if (_greetingApplied == (style, ActualTheme)) { SizeGreetingGlow(); UpdateAppearanceButton(); return; }
        _greetingApplied = (style, ActualTheme);
        GreetingGlow.Visibility = GradientGreeting ? Visibility.Visible : Visibility.Collapsed;
        HeroBrush.Opacity = glow ? 0 : 0.9;
        // Aligned with the page's other content when there is no card around it.
        Hero.Padding = glow ? new Thickness(0, 26, 0, 6) : new Thickness(32, 30, 32, 30);
        // Banner: tucked into the card's corner. Gradient: no card, so it sits at the page's edge.
        AppearanceButton.Margin = glow ? new Thickness(0, 4, 0, 0) : new Thickness(0, -14, -14, 0);
        UpdateAppearanceButton();
        SizeGreetingGlow();
    }

    /// <summary>
    /// Text and button colors of the greeting. Banner: white on the colored card in every theme. Gradient: the
    /// theme's own text colors (it sits on the page).
    /// </summary>
    private void ApplyGreetingColors()
    {
        if (PlainGreeting)
        {
            // No color behind it: the theme's own text colors, by clearing the local values so they inherit from the
            // page (that follows theme switches by itself, unlike colors looked up here).
            Greeting.ClearValue(TextBlock.ForegroundProperty);
            GreetingIcon.ClearValue(IconElement.ForegroundProperty);
            Stats.ClearValue(TextBlock.ForegroundProperty);
            ResumeButton.ClearValue(Control.BackgroundProperty);
            ResumeButton.ClearValue(Control.ForegroundProperty);
            return;
        }
        // Always white, in every theme for Banner and Gradient: the greeting sits on the accent color either way.
        // (Theme text colors looked up here could be the previous theme's right after a switch, and stayed dark.)
        var white = new SolidColorBrush(Microsoft.UI.Colors.White);
        Greeting.Foreground = white;
        GreetingIcon.Foreground = white;
        Stats.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 255, 255, 255));
        ResumeButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x33, 255, 255, 255));
        ResumeButton.Foreground = white;
    }

    private void SizeGreetingGlow()
    {
        if (!GradientGreeting || !IsLoaded) return;
        double bottom;
        // Ends a little below the tiles, so the color is still clearly there halfway down them
        // (one row: around the middle of the row; two rows: further down).
        if (QuickSection.Visibility == Visibility.Visible && QuickPicks.ActualHeight > 0)
            bottom = QuickPicks.TransformToVisual(PageRoot).TransformPoint(default).Y + QuickPicks.ActualHeight + 56;
        else
            bottom = Hero.TransformToVisual(PageRoot).TransformPoint(default).Y + Hero.ActualHeight + 80;
        // Height starts as Auto (NaN), which never compares as "changed".
        if (double.IsNaN(GreetingGlow.Height) || Math.Abs(GreetingGlow.Height - bottom) > 0.5) GreetingGlow.Height = Math.Max(0, bottom);
    }

    private void TintHero()
    {
        UpdateAppearanceButton();
        var a = Svc.Theme.Accent;
        ColorTween.To(PlaylistLineStop, ColorKit.Lighten(a, 0.1));
        var (h, s, l) = ColorKit.ToHsl(a);
        // Glow: the banner's colors, see-through and fading to nothing, so it melts into the theme.
        var wash = ColorKit.FromHsl(h + 10, Math.Max(0.55, s), 0.5);
        ColorTween.To(GlowWashTop, ColorKit.WithAlpha(wash, 120));
        ColorTween.To(GlowWashMid, ColorKit.WithAlpha(wash, 40)); // by the Quick access tiles only a soft tint remains
        ColorTween.To(GlowWashEnd, ColorKit.WithAlpha(wash, 0));
        var left = ColorKit.FromHsl(h - 30, Math.Max(0.55, s), 0.52);
        ColorTween.To(GlowLeft, ColorKit.WithAlpha(left, 170));
        ColorTween.To(GlowLeftEnd, ColorKit.WithAlpha(left, 0));
        var right = ColorKit.FromHsl(h + 50, Math.Max(0.6, s), 0.58);
        ColorTween.To(GlowRight, ColorKit.WithAlpha(right, 150));
        ColorTween.To(GlowRightEnd, ColorKit.WithAlpha(right, 0));
        ColorTween.To(HeroStop1, ColorKit.FromHsl(h - 30, Math.Max(0.55, s), 0.5));
        ColorTween.To(HeroStop2, ColorKit.FromHsl(h + 10, Math.Max(0.55, s), 0.48));
        ColorTween.To(HeroStop3, ColorKit.FromHsl(h + 50, Math.Max(0.6, s), 0.58));
    }

    /// <summary>Three overlapping album covers fanned out on the hero card.</summary>
    private void BuildArtFan(List<Album> albums)
    {
        ArtFan.Children.Clear();
        ArtFan.Visibility = albums.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        float[] angles = { -12, 0, 12 };
        double[] offsets = { -60, 0, 60 };
        for (int i = 0; i < albums.Count; i++)
        {
            var art = new ArtImage
            {
                Width = 120, Height = 120, CornerRadius = new CornerRadius(12), DecodeSize = 144,
                Source = albums[i].ArtPath, Seed = albums[i].Key,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 1),
                RenderTransform = new CompositeTransform { Rotation = angles[i], TranslateX = offsets[i] },
                Shadow = new ThemeShadow(),
                Translation = new Vector3(0, 0, 16 + i * 8),
            };
            // Wrapper takes the composition entrance animation; the art itself uses facade properties for its shadow depth.
            var wrapper = new Grid { Children = { art } };
            ArtFan.Children.Add(wrapper);
            Effects.EntranceSlide(wrapper, 30, 120 + i * 90, 600);
        }
    }

    private async void OnShuffleAll(object sender, RoutedEventArgs e) => await Svc.Player.PlayAsync(Svc.Library.Tracks, 0, shuffle: true);
    private void OnResume(object sender, RoutedEventArgs e) { if (!Svc.Player.IsPlaying) Svc.Player.Play(); App.MainWindow.ShowNowPlaying(true); }
    private void OnSeeAlbums(object sender, RoutedEventArgs e) => App.MainWindow.Navigate(typeof(AlbumsPage));

    private async void OnQuickPickTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: QuickEntry q }) return;
        if (q.Artist != null) App.MainWindow.Navigate(typeof(ArtistDetailPage), q.Artist.Name);
        else await Svc.Player.PlayTrackAsync(q.Track!);
    }

    private void OnQuickPickRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: QuickEntry q } el) return;
        Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase menu = q.Artist != null ? TrackActions.BuildArtistMenu(q.Artist) : TrackActions.BuildMenu(new[] { q.Track! });
        menu.ShowAt(el, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(el) });
        e.Handled = true;
    }

    private async void OnUseMusicFolder(object sender, RoutedEventArgs e)
    {
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (Svc.Settings.Current.MusicFolders.Contains(music, StringComparer.OrdinalIgnoreCase))
        {
            try { System.Diagnostics.Process.Start("explorer.exe", $"\"{music}\""); } catch { }
            return;
        }
        if (Directory.Exists(music)) await Svc.Library.AddFolderAsync(music);
        Refresh();
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var folder = await Pickers.PickFolderAsync();
        if (folder != null) await Svc.Library.AddFolderAsync(folder);
        Refresh();
    }

    /// <summary>File Explorer's name order: numbers inside names compare by value.</summary>
    private sealed partial class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();
        [System.Runtime.InteropServices.LibraryImport("shlwapi.dll", StringMarshalling = System.Runtime.InteropServices.StringMarshalling.Utf16)]
        private static partial int StrCmpLogicalW(string a, string b);
        public int Compare(string? x, string? y) => StrCmpLogicalW(x ?? "", y ?? "");
    }
}

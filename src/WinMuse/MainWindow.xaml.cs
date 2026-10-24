using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.Graphics;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;
using WinMuse.Views;

namespace WinMuse;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["home"] = typeof(HomePage),
        ["songs"] = typeof(SongsPage),
        ["albums"] = typeof(AlbumsPage),
        ["artists"] = typeof(ArtistsPage),
        ["genres"] = typeof(GenresPage),
        ["playlists"] = typeof(PlaylistsPage),
        ["queue"] = typeof(QueuePage),
        ["download"] = typeof(DownloadPage),
        ["fetch"] = typeof(FetchPage),
        ["settings"] = typeof(SettingsPage),
    };

    private MiniPlayerWindow? _mini;
    private readonly Debouncer _searchDebounce = new(TimeSpan.FromMilliseconds(180));

    public IntPtr Hwnd { get; }

    public MainWindow()
    {
        InitializeComponent();
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        Title = Branding.Name;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Title = Branding.Name;
        AppNameText.Text = Branding.Name;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        var icon = Path.Combine(AppPaths.AssetsDir, "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        var s = Svc.Settings.Current;
        AppWindow.Resize(new SizeInt32((int)Math.Max(900, s.WindowWidth), (int)Math.Max(640, s.WindowHeight)));
        if (AppWindow.Presenter is OverlappedPresenter op)
        {
            op.PreferredMinimumWidth = 760;
            op.PreferredMinimumHeight = 560;
        }
        CenterOnScreen();

        Svc.Theme.Attach(this, RootGrid);
        Helpers.PlayingBorder.Initialize();
        Helpers.ShelfCardColors.Initialize();
        Svc.Theme.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThemeService.Palette)) Ambient.SetPalette(Svc.Theme.Palette);
            if (e.PropertyName == nameof(ThemeService.Accent)) BarViz.SetColors(Svc.Theme.Accent, Helpers.ColorKit.Lighten(Svc.Theme.Accent, 0.2));
        };
        NowPlaying.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => UpdateBarVisualizer());
        Svc.Settings.Changed += OnSettingChanged;
        SelectionService.ModeChanged += UpdateSelectionBar;
        SelectionService.SelectionChanged += UpdateSelectionBar;
        RootGrid.ActualThemeChanged += (_, _) => ApplyBackground();

        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        AppTitleBar.Loaded += (_, _) => UpdateTitleBarRegions();

        Notifier.Raised += ShowToast;
        AppWindow.Closing += OnClosing;
    }

    private void CenterOnScreen()
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(area.X + (area.Width - size.Width) / 2, area.Y + (area.Height - size.Height) / 2));
    }

    public async Task InitializeAsync(List<string> files)
    {
        await Svc.Library.LoadAsync();
        Svc.Player.Initialize();
        Svc.Player.PlaybackError += msg => Notifier.Error("Playback error", msg);
        Svc.Player.TrackChanged += OnTrackChanged;
        Svc.Player.Queue.CollectionChanged += (_, _) => UpdateQueueBadge();
        Svc.Smtc.Initialize();
        Svc.Taskbar.Initialize(Hwnd);
        Svc.Library.ScanProgressChanged += p =>
        {
            ScanIndicator.Visibility = Svc.Library.IsScanning || Svc.AutoFix.IsRunning ? Visibility.Visible : Visibility.Collapsed;
            ScanText.Text = p.Total > 0 ? $"Scanning ({p.Done:N0}/{p.Total:N0})" : "Scanning library…";
        };
        // Only songs that newly appear are processed automatically (on the very first launch that is every song).
        Svc.Library.TracksAdded += tracks => Svc.AutoFix.AddNew(tracks);
        SpaceKey.Attach(RootGrid); // Space = play/pause anywhere except text fields
        TitleLogo.LogoStyle = Svc.Settings.Current.AppLogoStyle ?? Branding.DefaultLogo;
        Svc.Settings.Changed += name => { if (name == nameof(AppSettings.AppLogoStyle)) TitleLogo.LogoStyle = Svc.Settings.Current.AppLogoStyle ?? Branding.DefaultLogo; };
        // Tag edits to the song that was playing are written once it has closed (a moment after the next one starts).
        Svc.Player.TrackChanged += track => _ = Task.Run(async () => { await Task.Delay(1500); Svc.Metadata.WriteDeferred(); });
        Svc.AutoFix.Progress += (done, total, _) =>
        {
            ScanIndicator.Visibility = total > 0 || Svc.Library.IsScanning ? Visibility.Visible : Visibility.Collapsed;
            if (total > 0) ScanText.Text = $"Updating ({done + 1:N0}/{total:N0})";
        };

        ApplyBackground();
        Ambient.SetPalette(Svc.Theme.Palette, false);
        UpdateQueueBadge();
        UpdateQueuePanel(animate: false);
        RootGrid.SizeChanged += (_, e) => OnWindowWidth(e.NewSize.Width);
        Svc.Player.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlaybackService.HasTrack)) { UpdateQueuePanel(); UpdateBarVisualizer(); } };
        UpdateBarVisualizer();
        Navigate(typeof(HomePage));
        PlayerBar.FocusPlayButton();
        if (Svc.Player.CurrentTrack != null) OnTrackChanged(Svc.Player.CurrentTrack);

        if (files.Count > 0) await OpenExternalFilesAsync(files);

        // First launch without API keys: ask for them before the first sync; scanning continues meanwhile.
        if (Dialogs.ApiKeysDialog.ShouldShow(Svc.Settings.Current))
        {
            Svc.AutoFix.IsHeld = true;
            _ = PromptApiKeysAsync();
        }

        if (Svc.Settings.Current.MusicFolders.Count > 0)
        {
            ScanIndicator.Visibility = Visibility.Visible;
            ScanText.Text = "Scanning library…";
            await Svc.Library.ScanAsync(); // new songs found here are processed automatically (TracksAdded)
        }
    }

    private async Task PromptApiKeysAsync()
    {
        try { await Dialogs.ApiKeysDialog.ShowOnceAsync(); }
        catch (Exception ex) { AppPaths.Log("API key dialog: " + ex.Message); }
        finally
        {
            Svc.AutoFix.IsHeld = false;
            _ = Svc.AutoFix.RunPendingAsync();
        }
    }

    private readonly HashSet<string> _artTried = new();

    private void OnTrackChanged(Track? t)
    {
        _ = Svc.Theme.UpdateFromTrackAsync(t);
        AppWindow.Title = t == null ? Branding.Name : $"{t.DisplayTitle} · {t.DisplayArtist} — {Branding.Name}";

        // Quietly download missing artwork for the album that just started (once per album per session).
        if (t is { ArtPath: null } && Svc.Settings.Current.AutoFetchArt && t.Album != null && _artTried.Add(t.AlbumKey))
        {
            var album = Svc.Library.FindAlbum(t.AlbumKey)?.Tracks ?? new List<Track> { t };
            _ = Task.Run(async () =>
            {
                try
                {
                    var ok = await Svc.Metadata.FetchAndApplyArtAsync(album, null);
                    if (ok) App.UI.TryEnqueue(() => { if (Svc.Player.CurrentTrack == t) _ = Svc.Theme.UpdateFromTrackAsync(t); });
                }
                catch (Exception ex) { AppPaths.Log("Auto art: " + ex.Message); }
            });
        }
    }

    private void UpdateQueueBadge()
    {
        var n = Svc.Player.Queue.Count;
        QueueBadge.Value = n;
        QueueBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- Title bar ----------------

    private void UpdateTitleBarRegions()
    {
        if (AppTitleBar.XamlRoot == null) return;
        var scale = AppTitleBar.XamlRoot.RasterizationScale;
        RightInset.Width = new GridLength(Math.Max(0, AppWindow.TitleBar.RightInset / scale));
        LeftInset.Width = new GridLength(Math.Max(0, AppWindow.TitleBar.LeftInset / scale));

        RectInt32 Rect(FrameworkElement el)
        {
            var b = el.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, el.ActualWidth, el.ActualHeight));
            return new RectInt32((int)Math.Round(b.X * scale), (int)Math.Round(b.Y * scale), (int)Math.Round(b.Width * scale), (int)Math.Round(b.Height * scale));
        }

        try
        {
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                .SetRegionRects(NonClientRegionKind.Passthrough, new[] { Rect(BackButton), Rect(SearchBox) });
        }
        catch (Exception ex) { AppPaths.Log("Title bar regions: " + ex.Message); }
    }

    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } op) op.Restore();
        Activate();
    }

    // ---------------- Navigation ----------------

    public Page? CurrentPage => ContentFrame.Content as Page;

    public void Navigate(Type page, object? parameter = null)
    {
        if (NowPlaying.Visibility == Visibility.Visible) ShowNowPlaying(false);
        if (ContentFrame.CurrentSourcePageType == page && parameter == null && page != typeof(SongsPage)) return;
        NavigationTransitionInfo transition = parameter is null ? new EntranceNavigationTransitionInfo() : new DrillInNavigationTransitionInfo();
        ContentFrame.Navigate(page, parameter, transition);
    }

    private void OnNavItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (args.InvokedItemContainer?.Tag is string tag && Pages.TryGetValue(tag, out var type))
            ContentFrame.Navigate(type, null, new EntranceNavigationTransitionInfo());
        if (NowPlaying.Visibility == Visibility.Visible) ShowNowPlaying(false);
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        BackButton.IsEnabled = ContentFrame.CanGoBack;
        var tag = Pages.FirstOrDefault(p => p.Value == e.SourcePageType).Key ?? e.SourcePageType.Name switch
        {
            nameof(AlbumDetailPage) => "albums",
            nameof(ArtistDetailPage) => "artists",
            nameof(PlaylistDetailPage) => "playlists",
            _ => null,
        };
        var item = Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>().FirstOrDefault(i => (string?)i.Tag == tag);
        Nav.SelectedItem = item;
    }

    private void OnBack(object sender, RoutedEventArgs e) => GoBack();

    public void GoBack()
    {
        if (NowPlaying.Visibility == Visibility.Visible) { ShowNowPlaying(false); return; }
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    // ---------------- Now Playing / mini player ----------------

    public void ShowNowPlaying(bool show, bool lyrics = false)
    {
        if (show)
        {
            if (Svc.Player.CurrentTrack == null) { Notifier.Show("No song is playing", "Start playback to open Now Playing."); return; }
            ConnectedAnimationService.GetForCurrentView().PrepareToAnimate("NowPlayingArt", PlayerBar.ArtElement);
            NowPlaying.Open(lyrics);
            BackButton.IsEnabled = true;
        }
        else
        {
            NowPlaying.Close();
            BackButton.IsEnabled = ContentFrame.CanGoBack;
        }
    }

    public bool IsNowPlayingOpen => NowPlaying.Visibility == Visibility.Visible;

    public void OpenMiniPlayer()
    {
        _mini ??= new MiniPlayerWindow();
        _mini.Closed += (_, _) => _mini = null;
        _mini.Activate();
        if (AppWindow.Presenter is OverlappedPresenter op) op.Minimize();
    }

    // ---------------- Selection mode ----------------

    private void UpdateSelectionBar()
    {
        var active = SelectionService.IsActive;
        var n = SelectionService.Checked.Count;
        SelectionCount.Text = n == 1 ? "1 selected" : $"{n} selected";
        if (active && SelectionBar.Visibility != Visibility.Visible)
        {
            SelectionBar.Visibility = Visibility.Visible;
            Controls.Effects.EntranceSlide(SelectionBar, 20, 0, 320);
        }
        else if (!active && SelectionBar.Visibility == Visibility.Visible)
        {
            Controls.Effects.ExitSlide(SelectionBar, () => SelectionBar.Visibility = Visibility.Collapsed);
        }
    }

    private List<Track>? CheckedOrNotify()
    {
        var list = SelectionService.Checked.ToList();
        if (list.Count == 0) { Notifier.Show("No songs selected", "Check one or more songs first."); return null; }
        return list;
    }

    private async void OnSelectionPlay(object sender, RoutedEventArgs e)
    {
        if (CheckedOrNotify() is not { } list) return;
        SelectionService.End();
        await Svc.Player.PlayAsync(list);
    }

    private void OnSelectionQueue(object sender, RoutedEventArgs e)
    {
        if (CheckedOrNotify() is not { } list) return;
        Svc.Player.AddToQueue(list);
        Notifier.Show("Added to queue: " + TrackActions.Count(list));
        SelectionService.End();
    }

    private void OnSelectionPlaylist(object sender, RoutedEventArgs e)
    {
        if (CheckedOrNotify() is not { } list) return;
        var menu = new MenuFlyout();
        TrackActions.FillPlaylistItems(menu.Items, list, SelectionService.End);
        menu.ShowAt(SelectionPlaylistButton);
    }

    private void OnSelectionAll(object sender, RoutedEventArgs e) => SelectionService.SelectAll();
    private void OnSelectionCancel(object sender, RoutedEventArgs e) => SelectionService.End();

    // ---------------- Narrow windows ----------------

    private bool _roomForQueue = true;
    private bool _queueAsked;   // turned on from the player bar while the window is narrow: show it anyway

    /// <summary>Below 1000 px the navigation shrinks to icons (the menu button still opens it over the page), and below
    /// 1200 px the floating Up next panel steps aside unless asked for; both come back when the window is wider again.</summary>
    private void OnWindowWidth(double width)
    {
        var mode = width < 1000 ? NavigationViewPaneDisplayMode.LeftCompact : NavigationViewPaneDisplayMode.Left;
        if (Nav.PaneDisplayMode != mode) Nav.PaneDisplayMode = mode;
        var room = width >= 1200;
        if (room != _roomForQueue) { _roomForQueue = room; _queueAsked = false; UpdateQueuePanel(); }
    }

    // ---------------- Queue panel ----------------

    private void UpdateQueuePanel(bool animate = true)
    {
        var show = Svc.Settings.Current.ShowQueuePanel && Svc.Player.HasTrack && (_roomForQueue || _queueAsked);
        var visible = QueueCard.Visibility == Visibility.Visible;
        if (show == visible) return;
        if (show)
        {
            QueueCard.Visibility = Visibility.Visible;
            if (animate) Controls.Effects.EntranceSlide(QueueCard, 24, 0, 380);
        }
        else if (animate) Controls.Effects.ExitSlide(QueueCard, () => QueueCard.Visibility = Visibility.Collapsed);
        else QueueCard.Visibility = Visibility.Collapsed;
    }

    // ---------------- Player bar visualizer ----------------

    /// <summary>Shown while a song is loaded; paused while Now Playing (which has its own visualizer) covers the window.</summary>
    private void UpdateBarVisualizer()
    {
        var s = Svc.Settings.Current;
        bool show = s.ShowVisualizer && s.ShowBarVisualizer && Svc.Player.HasTrack;
        BarViz.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BarViz.IsActive = show && NowPlaying.Visibility != Visibility.Visible;
    }

    // ---------------- Background ----------------

    private void OnSettingChanged(string name)
    {
        switch (name)
        {
            case nameof(AppSettings.Theme): Svc.Theme.ApplyTheme(); ApplyBackground(); break;
            case nameof(AppSettings.ShowQueuePanel): _queueAsked = Svc.Settings.Current.ShowQueuePanel; UpdateQueuePanel(); break;
            case nameof(AppSettings.ShowVisualizer):
            case nameof(AppSettings.ShowBarVisualizer): UpdateBarVisualizer(); break;
            case nameof(AppSettings.AccentSource):
            case nameof(AppSettings.CustomAccent): Svc.Theme.ApplyAccent(); break;
            case nameof(AppSettings.BackgroundMode):
            case nameof(AppSettings.GradientColor1):
            case nameof(AppSettings.GradientColor2):
            case nameof(AppSettings.GradientColor3):
                Svc.Theme.RefreshPaletteForMode();
                ApplyBackground();
                break;
            case nameof(AppSettings.WallpaperPath):
            case nameof(AppSettings.WallpaperBlur):
            case nameof(AppSettings.WallpaperDim):
            case nameof(AppSettings.BackgroundIntensity):
            case nameof(AppSettings.AnimateBackground):
                ApplyBackground();
                break;
        }
    }


    public void ApplyBackground()
    {
        var s = Svc.Settings.Current;
        var dark = RootGrid.ActualTheme == ElementTheme.Dark;
        BackgroundScrim.Fill = new SolidColorBrush(dark ? Windows.UI.Color.FromArgb(255, 0, 0, 0) : Windows.UI.Color.FromArgb(255, 255, 255, 255));
        Ambient.Animate = s.AnimateBackground;

        // OLED is always true black: no album art, gradient or image behind the app.
        switch (Svc.Theme.IsOled ? BackgroundMode.None : s.BackgroundMode)
        {
            case BackgroundMode.AlbumArt:
            case BackgroundMode.Gradient:
                Ambient.Opacity = s.BackgroundIntensity / 100.0 * (dark ? 0.9 : 0.65);
                WallpaperImage.Opacity = 0;
                BackgroundScrim.Opacity = dark ? 0.25 : 0.35;
                Ambient.SetPalette(Svc.Theme.Palette);
                break;

            case BackgroundMode.Wallpaper when !string.IsNullOrEmpty(s.WallpaperPath) && File.Exists(s.WallpaperPath):
                Ambient.Opacity = 0;
                // A real (Gaussian) blur, made in the background and cached (Helpers/WallpaperBlur).
                var key = $"{s.WallpaperPath}|{Math.Round(s.WallpaperBlur)}";
                if (_wallpaperKey != key) { _wallpaperKey = key; _ = LoadWallpaperAsync(s.WallpaperPath!, s.WallpaperBlur, key); }
                WallpaperImage.Opacity = 1;
                BackgroundScrim.Opacity = s.WallpaperDim / 100.0;
                break;

            default:
                Ambient.Opacity = 0;
                WallpaperImage.Opacity = 0;
                BackgroundScrim.Opacity = 0;
                break;
        }
    }

    // ---------------- Search ----------------

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        var q = sender.Text.Trim();
        _searchDebounce.Run(() => App.UI.TryEnqueue(() =>
        {
            if (q.Length < 2) { sender.ItemsSource = null; return; }
            var (songs, albums, artists) = Svc.Library.Search(q, 5);
            var list = new List<SearchSuggestion>();
            list.AddRange(artists.Take(2).Select(a => new SearchSuggestion(a.Name, "Artist", a.ArtPath, "", a)));
            list.AddRange(albums.Take(3).Select(a => new SearchSuggestion(a.Title, "Album · " + a.Artist, a.ArtPath, "", a)));
            list.AddRange(songs.Take(5).Select(t => new SearchSuggestion(t.DisplayTitle, "Song · " + t.DisplayArtist, t.ArtPath, "", t)));
            sender.ItemsSource = list;
        }));
    }

    private void OnSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is not SearchSuggestion s) return;
        sender.Text = "";
        sender.ItemsSource = null;
        switch (s.Payload)
        {
            case Artist a: Navigate(typeof(ArtistDetailPage), a.Name); break;
            case Album al: Navigate(typeof(AlbumDetailPage), al.Key); break;
            case Track t: _ = Svc.Player.PlayTrackAsync(t); break;
        }
    }

    private void OnSearchSubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion != null) return;
        var q = args.QueryText.Trim();
        if (q.Length == 0) return;
        sender.ItemsSource = null;
        Navigate(typeof(SearchPage), q);
    }

    // ---------------- External files ----------------

    public async Task OpenExternalFilesAsync(List<string> files)
    {
        var tracks = await Task.Run(() => files.Where(AppPaths.IsAudio).Select(f => Svc.Library.GetByPath(f) ?? Svc.Tags.Read(f)).ToList());
        if (tracks.Count > 0) await Svc.Player.PlayAsync(tracks);
    }

    // ---------------- Notifications ----------------

    private void ShowToast(string title, string? message, InfoBarSeverity severity)
    {
        var bar = new InfoBar
        {
            Title = title,
            Message = message ?? "",
            Severity = severity,
            IsOpen = true,
            IsClosable = true,
            Background = ThemeRes.Brush("GlassBrush"),
            CornerRadius = new CornerRadius(8),
        };
        // The wrapper animates (implicit transitions on the InfoBar itself would clash with its visual states).
        var wrap = new Border
        {
            Child = bar, Opacity = 0, Translation = new System.Numerics.Vector3(0, -12, 0),
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(200) },
            TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(260) },
        };
        void Close()
        {
            if (!Toasts.Children.Contains(wrap)) return;
            wrap.Opacity = 0;
            wrap.Translation = new System.Numerics.Vector3(0, -8, 0);
            After(TimeSpan.FromMilliseconds(220), () => Toasts.Children.Remove(wrap));
        }
        bar.CloseButtonClick += (_, _) => Close();
        Toasts.Children.Add(wrap);
        while (Toasts.Children.Count > 3) Toasts.Children.RemoveAt(0);
        After(TimeSpan.FromMilliseconds(16), () => { wrap.Opacity = 1; wrap.Translation = System.Numerics.Vector3.Zero; });
        After(TimeSpan.FromSeconds(severity == InfoBarSeverity.Error ? 8 : 4), Close);
    }

    // Timers are kept here until they fire: a timer held only in a local variable can be collected first, which is
    // why some notifications never closed.
    private readonly HashSet<Microsoft.UI.Dispatching.DispatcherQueueTimer> _timers = new();

    private void After(TimeSpan delay, Action action)
    {
        var timer = App.UI.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        timer.Tick += (t, _) => { _timers.Remove(t); action(); };
        _timers.Add(timer);
        timer.Start();
    }

    // ---------------- Keyboard ----------------

    private bool IsTyping()
    {
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot);
        return focused is TextBox or PasswordBox or AutoSuggestBox or RichEditBox or ComboBox;
    }

    private void OnAccelPlayPause(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        e.Handled = true;
        Svc.Player.TogglePlayPause();
    }

    private async void OnAccelNext(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; await Svc.Player.NextAsync(); }
    private async void OnAccelPrevious(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; await Svc.Player.PreviousAsync(); }
    private void OnAccelVolumeUp(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; Svc.Player.Volume = Math.Min(1, Svc.Player.Volume + 0.05); }
    private void OnAccelVolumeDown(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; Svc.Player.Volume = Math.Max(0, Svc.Player.Volume - 0.05); }
    private void OnAccelSearch(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; SearchBox.Focus(FocusState.Keyboard); }
    private void OnAccelMini(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; OpenMiniPlayer(); }
    private void OnAccelShuffle(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; Svc.Player.Shuffle = !Svc.Player.Shuffle; }
    private void OnAccelRepeat(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; Svc.Player.CycleRepeat(); }
    private void OnAccelNowPlaying(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e) { e.Handled = true; ShowNowPlaying(!IsNowPlayingOpen); }

    private void OnAccelEscape(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs e)
    {
        if (SelectionService.IsActive) { e.Handled = true; SelectionService.End(); return; }
        if (!IsNowPlayingOpen) return;
        e.Handled = true;
        ShowNowPlaying(false);
    }

    // ---------------- Shutdown ----------------

    private string? _wallpaperKey;

    private async Task LoadWallpaperAsync(string path, double blur, string key)
    {
        try
        {
            await Task.Delay(250); // the blur slider: only the value it settles on
            if (_wallpaperKey != key) return;
            var shown = await WallpaperBlur.GetAsync(path, blur);
            if (_wallpaperKey != key) return; // changed again meanwhile
            WallpaperImage.Source = new BitmapImage(new Uri(shown));
        }
        catch (Exception ex) { AppPaths.Log("Background image: " + ex.Message); }
    }

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            Svc.Settings.Current.WindowWidth = AppWindow.Size.Width;
            Svc.Settings.Current.WindowHeight = AppWindow.Size.Height;
        }
        _mini?.Close();
        Svc.AutoFix.Stop();
        Svc.Player.Shutdown();
        Svc.Metadata.WriteDeferred(all: true);
        Svc.Settings.Save();
    }
}

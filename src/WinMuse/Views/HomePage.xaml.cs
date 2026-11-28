using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinMuse.Controls;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;
using WinMuse.ViewModels;

namespace WinMuse.Views;

/// <summary>
/// Home. What it shows (the summary, Quick access, the rows and their order) comes from <see cref="HomeViewModel"/>;
/// this page adds the greeting's look (colors, glow, the fanned covers), smooth scrolling and navigation.
/// </summary>
public sealed partial class HomePage : Page
{
    public HomeViewModel Vm { get; } = new(Svc.Library, Svc.Settings, Svc.Favorites, Svc.Continue, Svc.Player);

    public HomePage()
    {
        InitializeComponent();
        // Home is cached between visits: changes made while away only mark it dirty (see OnNavigatedTo).
        Svc.Library.Changed += () => { if (IsLoaded) Refresh(); else _dirty = true; };
        Svc.Library.ScanCompleted += () => { if (IsLoaded) Refresh(); else _dirty = true; };
        Svc.Theme.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ThemeService.Accent)) TintHero(); };
        Svc.Player.TrackChanged += _ => Vm.RefreshResume();
        Svc.Favorites.Changed += () => { if (IsLoaded) Refresh(); else _dirty = true; };
        Svc.Continue.Changed += () => { if (IsLoaded) RefreshContinue(); else _dirty = true; };
        Svc.Library.PlaylistsChanged += () => { if (IsLoaded) Vm.RefreshPlaylists(); else _dirty = true; };
        // The fanned covers need room; hide them (and give back their column) rather than letting them crowd the greeting.
        Hero.SizeChanged += (_, e) =>
        {
            var room = e.NewSize.Width >= 760;
            ArtFan.Opacity = room ? 1 : 0;
            Hero.ColumnDefinitions[1].Width = room ? GridLength.Auto : new GridLength(0);
        };
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
            if (name == nameof(AppSettings.ShowAllHomeRows)) { if (IsLoaded) Refresh(); else _dirty = true; }
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
            CompositionTarget.Rendering -= show;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                foreach (var (index, row) in _detached.OrderBy(d => d.index))
                    if (!ContentStack.Children.Contains(row)) ContentStack.Children.Insert(Math.Min(index, ContentStack.Children.Count), row);
                _detached.Clear();
            });
        };
        CompositionTarget.Rendering += show;
    }

    /// <summary>
    /// Opening Home must feel as quick as any other tab. The first visit builds the page; later visits show the cached
    /// page at once and only rebuild (just after it appears) if something changed while away.
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Vm.RefreshGreeting(); // cheap things that may have changed with time
        Vm.RefreshResume();
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
        Vm.Refresh();
        var fanKey = string.Join("|", Vm.ArtFanAlbums.Select(a => a.ArtPath));
        if (fanKey != _fanKey) { _fanKey = fanKey; BuildArtFan(Vm.ArtFanAlbums); }
        TintHero();
    }

    private string? _fanKey;

    /// <summary>Continue Listening; a card whose song moved on eases its new song in.</summary>
    private void RefreshContinue()
    {
        foreach (var item in Vm.RefreshContinue())
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

    // ---------------- Playlists row ----------------

    private static Playlist? PlaylistOf(object sender) => (sender as FrameworkElement)?.DataContext as Playlist;

    private void OnOpenPlaylist(object sender, RoutedEventArgs e)
    {
        if (PlaylistOf(sender) is not { } p) return;
        Vm.OpenPlaylist(p);
        App.MainWindow.Navigate(typeof(PlaylistDetailPage), p.Id);
    }

    /// <summary>A song card in a playlist row opens the playlist and plays it from that song.</summary>
    private async void OnPlaylistSongClick(object sender, ItemClickEventArgs e)
    {
        if (PlaylistOf(sender) is { } p && e.ClickedItem is Track t) await PlaylistDetailPage.OpenAndPlayAsync(p, t);
    }

    private void OnMovePlaylistUp(object sender, RoutedEventArgs e) { if (PlaylistOf(sender) is { } p) Vm.MovePlaylist(p, -1); }
    private void OnMovePlaylistDown(object sender, RoutedEventArgs e) { if (PlaylistOf(sender) is { } p) Vm.MovePlaylist(p, 1); }

    private void OnSeePlaylists(object sender, RoutedEventArgs e) => App.MainWindow.Navigate(typeof(PlaylistsPage));

    // ---------------- Continue Listening and automatic rows ----------------

    private async void OnContinueClick(object sender, ItemClickEventArgs e) => await Vm.ResumeAsync((ContinueItem)e.ClickedItem);

    private void OnContinueRightTapped(object sender, Microsoft.UI.Xaml.Input.RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: ContinueItem c } el) return;
        var menu = new MenuFlyout();
        menu.Items.Add(TrackActions.Item("Resume", "\uE768", async () => await Vm.ResumeAsync(c)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(TrackActions.Item("Remove from Continue Listening", "\uE711", () => Vm.RemoveFromContinue(c)));
        menu.ShowAt(el, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = e.GetPosition(el) });
        e.Handled = true;
    }

    private static long SystemPlaylistIdOf(object sender) => long.TryParse((sender as FrameworkElement)?.Tag as string, out var id) ? id : 0;

    private void OnOpenSystemPlaylist(object sender, RoutedEventArgs e) => App.MainWindow.Navigate(typeof(PlaylistDetailPage), SystemPlaylistIdOf(sender));

    /// <summary>Same as a playlist row: opens the automatic playlist and plays it from that song.</summary>
    private async void OnSystemSongClick(object sender, ItemClickEventArgs e)
    {
        if (Svc.Library.SystemPlaylist(SystemPlaylistIdOf(sender)) is { } p && e.ClickedItem is Track t) await PlaylistDetailPage.OpenAndPlayAsync(p, t);
    }

    // ---------------- Quick access ----------------

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

    // ---------------- Greeting buttons and the empty library ----------------

    private async void OnShuffleAll(object sender, RoutedEventArgs e) => await Vm.ShuffleAllAsync();
    private void OnResume(object sender, RoutedEventArgs e) { Vm.Resume(); App.MainWindow.ShowNowPlaying(true); }

    private async void OnUseMusicFolder(object sender, RoutedEventArgs e)
    {
        if (!Vm.MusicFolderAdded) { await Vm.AddMusicFolderAsync(); return; }
        try { System.Diagnostics.Process.Start("explorer.exe", $"\"{AppPaths.MusicFolder}\""); } catch { }
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (await Pickers.PickFolderAsync() is { } folder) await Vm.AddFolderAsync(folder);
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
        var (h, s, _) = ColorKit.ToHsl(a);
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
    private void BuildArtFan(IReadOnlyList<Album> albums)
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
            // Decorative: the covers repeat albums listed below, so screen readers skip them.
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(art, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
            // Wrapper takes the composition entrance animation; the art itself uses facade properties for its shadow depth.
            var wrapper = new Grid { Children = { art } };
            ArtFan.Children.Add(wrapper);
            Effects.EntranceSlide(wrapper, 30, 120 + i * 90, 600);
        }
    }
}

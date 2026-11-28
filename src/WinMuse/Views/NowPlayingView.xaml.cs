using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinMuse.Controls;
using WinMuse.Dialogs;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;
using WinMuse.ViewModels;

namespace WinMuse.Views;

/// <summary>
/// The full-window Now Playing screen. Lyrics, their timing and the song's details come from
/// <see cref="NowPlayingViewModel"/>; this view lays them out and animates them.
/// </summary>
public sealed partial class NowPlayingView : UserControl
{
    private bool _dragging, _updating;
    private readonly List<TextBlock> _lineBlocks = new();
    private int _highlighted = -1;
    private DateTime _userScrolledAt = DateTime.MinValue;
    private string _tab = "lyrics";
    private bool _narrow;
    private SpriteVisual? _glow;
    private DropShadow? _glowShadow;
    private float _pulse;

    public PlaybackService Player => Svc.Player;
    public NowPlayingViewModel Vm { get; } = new(Svc.Lyrics, Svc.Library, Svc.Ui);

    public NowPlayingView()
    {
        InitializeComponent();
        // Lyrics timing box: always two decimals ("0.20", "-1.05").
        LyricsTimingBox.NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
        {
            IntegerDigits = 1, FractionDigits = 2,
            NumberRounder = new Windows.Globalization.NumberFormatting.IncrementNumberRounder
            {
                Increment = 0.01, RoundingAlgorithm = Windows.Globalization.NumberFormatting.RoundingAlgorithm.RoundHalfAwayFromZero,
            },
        };

        QueueList.BindTo(Player.Queue);
        QueueList.ShowHeader = false;
        QueueList.ShowAlbum = false;
        QueueList.Sortable = false;
        QueueList.Reorderable = true;
        QueueList.EmptyMessage = "The queue is empty.";
        QueueList.PlayRequested = i => _ = Player.PlayIndexAsync(i);
        QueueList.DeleteRequested += idx => Player.RemoveFromQueue(idx);

        foreach (var slider in new[] { Seek, MiniSeek })
        {
            var s = slider;
            s.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _dragging = true), true);
            s.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndDrag(s)), true);
            s.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndDrag(s)), true);
            s.ValueChanged += (_, e) => { if (!_updating && !_dragging && Math.Abs(e.NewValue - Player.Position) > 1) Player.Seek(e.NewValue); };
        }

        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaybackService.Position) && !_dragging)
            {
                _updating = true;
                Seek.Value = Player.Position;
                if (MiniBar.Visibility == Visibility.Visible) MiniSeek.Value = Player.Position;
                _updating = false;
            }
            else if (e.PropertyName == nameof(PlaybackService.IsPlaying) && Visibility == Visibility.Visible)
            {
                Effects.Pop(PlayGlyph, 0.6f);
            }
        };
        Player.TrackChanged += t => { if (Visibility == Visibility.Visible) LoadTrack(t, animate: true); };
        Player.Seeked += () => { Vm.ResetActiveLine(); _userScrolledAt = DateTime.MinValue; };

        Vm.LinesChanged += BuildLines;
        Vm.PropertyChanged += OnVmChanged;
        // Lyrics edited elsewhere (a song menu's Edit) show here once saved.
        Svc.Library.Changed += () => { if (Visibility == Visibility.Visible) _ = Vm.RefreshIfChangedAsync(); };

        Svc.Theme.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThemeService.Palette)) ApplyPalette();
            // The accent can also change on its own (custom color, Windows accent, light/dark tuning).
            else if (e.PropertyName == nameof(ThemeService.Accent)) ApplyVisualizerColors();
        };

        ActualThemeChanged += (_, _) => Aurora.LightMode = ActualTheme == ElementTheme.Light;
        Loaded += (_, _) => { Aurora.LightMode = ActualTheme == ElementTheme.Light; BuildGlow(); };
        SizeChanged += (_, e) => { _narrow = e.NewSize.Width < 1000; ApplyLayout(); };
        LyricsScroll.PointerWheelChanged += (_, _) => _userScrolledAt = DateTime.Now;
        LyricsScroll.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _userScrolledAt = DateTime.Now), true);

        Tabs.SelectedItem = TabLyrics;
    }

    private void EndDrag(Slider s)
    {
        if (!_dragging) return;
        _dragging = false;
        Player.Seek(s.Value);
    }

    private void OnMiniArtTapped(object sender, TappedRoutedEventArgs e) => Tabs.SelectedItem = TabArt;

    // ---------------- Open / close ----------------

    public void Open(bool lyrics)
    {
        Visibility = Visibility.Visible;
        Viz.IsActive = true;
        Viz.Mode = Svc.Settings.Current.VisualizerStyle;
        Viz.Visibility = Svc.Settings.Current.ShowVisualizer ? Visibility.Visible : Visibility.Collapsed;
        Aurora.Animate = Svc.Settings.Current.AnimateBackground;
        if (lyrics) Tabs.SelectedItem = TabLyrics;

        LoadTrack(Player.CurrentTrack, animate: false);
        ApplyPalette();

        var v = ElementCompositionPreview.GetElementVisual(Root);
        v.Opacity = 1;
        Effects.EntranceSlide(Root, 48, 0, 460);
        Effects.EntranceSlide(TitleBlock, 16, 120, 520);
        Effects.EntranceSlide(RightPanel, 24, 160, 560);

        var anim = ConnectedAnimationService.GetForCurrentView().GetAnimation("NowPlayingArt");
        if (anim != null)
        {
            anim.Configuration = new BasicConnectedAnimationConfiguration();
            anim.TryStart(BigArt);
        }

        CompositionTarget.Rendering -= OnRendering;
        CompositionTarget.Rendering += OnRendering;
        Focus(FocusState.Programmatic);
    }

    public void Close()
    {
        var v = ElementCompositionPreview.GetElementVisual(Root);
        var c = v.Compositor;
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 0);
        fade.Duration = TimeSpan.FromMilliseconds(220);
        var slide = c.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1, new Vector3(0, 40, 0), c.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0), new Vector2(0.84f, 0)));
        slide.Duration = TimeSpan.FromMilliseconds(220);
        ElementCompositionPreview.SetIsTranslationEnabled(Root, true);
        v.StartAnimation("Opacity", fade);
        v.StartAnimation("Translation", slide);
        batch.End();
        batch.Completed += (_, _) =>
        {
            Visibility = Visibility.Collapsed;
            Viz.IsActive = false;
            CompositionTarget.Rendering -= OnRendering;
            v.Opacity = 1;
            v.Properties.InsertVector3("Translation", Vector3.Zero);
        };
    }

    private void ApplyVisualizerColors()
    {
        var accent = Svc.Theme.Accent;
        Viz.SetColors(accent, ColorKit.Lighten(accent, 0.18));
    }

    private void ApplyPalette()
    {
        var p = Svc.Theme.AlbumPalette.Length > 0 ? Svc.Theme.AlbumPalette : Svc.Theme.Palette;
        Aurora.SetPalette(p);
        ApplyVisualizerColors();
        if (_glowShadow != null && p.Length > 0)
        {
            var glowColor = ColorKit.PickAccent(p) ?? Svc.Theme.Accent;
            var anim = _glowShadow.Compositor.CreateColorKeyFrameAnimation();
            anim.InsertKeyFrame(1, glowColor);
            anim.Duration = TimeSpan.FromMilliseconds(900);
            _glowShadow.StartAnimation("Color", anim);
        }
    }

    /// <summary>Colored glow behind the artwork, tinted with the album's accent color.</summary>
    private void BuildGlow()
    {
        if (_glow != null) return;
        var c = CompositionTarget.GetCompositorForCurrentThread();
        _glowShadow = c.CreateDropShadow();
        _glowShadow.BlurRadius = 80;
        _glowShadow.Opacity = 0.75f;
        _glowShadow.Offset = new Vector3(0, 24, 0);
        _glowShadow.Color = Svc.Theme.Accent;
        _glow = c.CreateSpriteVisual();
        _glow.Size = new Vector2(400, 400);
        _glow.Offset = new Vector3(20, 20, 0);
        _glow.Shadow = _glowShadow;
        ElementCompositionPreview.SetElementChildVisual(ArtShadowHost, _glow);
    }

    // ---------------- Track ----------------

    private void LoadTrack(Track? t, bool animate)
    {
        AlbumText.Text = t?.DisplayAlbum ?? "";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AlbumLink, AlbumText.Text);
        Rating.Value = t?.Rating > 0 ? t.Rating : -1;
        BuildDetails(t);
        if (animate) Effects.EntranceSlide(TitleBlock, 14, 0, 420);
        _ = Vm.ShowAsync(t);
    }

    private void BuildDetails(Track? t)
    {
        DetailsGrid.Children.Clear();
        DetailsGrid.RowDefinitions.Clear();
        DetailsGrid.ColumnDefinitions.Clear();
        var rows = NowPlayingViewModel.DetailsOf(t);
        if (rows.Count == 0) return;
        DetailsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        DetailsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (int i = 0; i < rows.Count; i++)
        {
            DetailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = rows[i].Label, Opacity = 0.7, Style = TextStyle };
            var v = new TextBlock { Text = rows[i].Value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, Style = TextStyle };
            Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            DetailsGrid.Children.Add(k); DetailsGrid.Children.Add(v);
        }
    }

    /// <summary>Text built in code takes its color from this style, so it follows light/dark switches.</summary>
    private Style TextStyle => (Style)Resources["NowPlayingTextStyle"];

    // ---------------- Lyrics ----------------

    /// <summary>One text block per line of the lyrics now in <see cref="NowPlayingViewModel.Lyrics"/>.</summary>
    private void BuildLines()
    {
        LyricsLines.Children.Clear();
        _lineBlocks.Clear();
        _highlighted = -1;
        UpdateTimingControls();
        if (Vm.Lyrics is not { } lyrics) return;

        bool synced = Vm.IsSynced;
        for (int i = 0; i < lyrics.Lines.Count; i++)
        {
            var line = lyrics.Lines[i];
            var tb = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text,
                FontSize = synced ? 28 : 20,
                FontWeight = synced ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Opacity = synced ? 0.32 : 0.92,
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(350) },
                ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(400) },
                Scale = synced ? new Vector3(0.94f, 0.94f, 1) : Vector3.One,
                Style = TextStyle,
            };
            if (synced)
            {
                int index = i;
                tb.Tapped += (_, _) => { Player.Seek(Vm.SeekSecondsFor(index)); _userScrolledAt = DateTime.MinValue; };
                tb.PointerEntered += (_, _) => { if (index != _highlighted) tb.Opacity = 0.6; };
                tb.PointerExited += (_, _) => { if (index != _highlighted) tb.Opacity = 0.32; };
            }
            LyricsLines.Children.Add(tb);
            _lineBlocks.Add(tb);
        }
        LyricsScroll.ChangeView(null, 0, null, true);
        Effects.EntranceSlide(LyricsLines, 30, 0, 600);
    }

    /// <summary>Moves the highlight to the line being sung, and scrolls it into view unless the user scrolled lately.</summary>
    private void UpdateLyricsHighlight()
    {
        if (_lineBlocks.Count == 0 || !Vm.UpdateActiveLine(Player.Engine.Pipeline.Position)) return;
        if (_highlighted >= 0 && _highlighted < _lineBlocks.Count)
        {
            var old = _lineBlocks[_highlighted];
            old.Opacity = 0.32;
            old.Scale = new Vector3(0.94f, 0.94f, 1);
        }
        _highlighted = Vm.ActiveLine;
        if (_highlighted < 0 || _highlighted >= _lineBlocks.Count) return;

        var tb = _lineBlocks[_highlighted];
        tb.CenterPoint = new Vector3(0, (float)tb.ActualHeight / 2, 0);
        tb.Opacity = 1;
        tb.Scale = Vector3.One;

        if ((DateTime.Now - _userScrolledAt).TotalSeconds > 3.5)
        {
            var y = tb.TransformToVisual(LyricsLines).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            LyricsScroll.ChangeView(null, Math.Max(0, y - LyricsScroll.ViewportHeight * 0.36), null, false);
        }
    }

    // ---------------- Lyrics timing ----------------

    private bool _offsetUpdating;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowPlayingViewModel.OffsetMs)) UpdateTimingControls();
    }

    /// <summary>The timing button sits with the tabs and only shows on the Lyrics tab, for synced lyrics.</summary>
    private void UpdateTimingControls()
    {
        LyricsTimingButton.Visibility = Vm.IsSynced && _tab == "lyrics" ? Visibility.Visible : Visibility.Collapsed;
        _offsetUpdating = true;
        LyricsTimingSlider.Value = Vm.OffsetMs / 1000.0;
        LyricsTimingBox.Value = Math.Round(Vm.OffsetMs / 1000.0, 2);
        _offsetUpdating = false;
    }

    private void OnLyricsTimingTyped(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_offsetUpdating) return;
        if (double.IsNaN(args.NewValue)) { UpdateTimingControls(); return; } // cleared: show the current value again
        SetLyricsOffset((int)Math.Round(args.NewValue * 1000));
    }

    private void OnLyricsEarlier(object sender, RoutedEventArgs e) => SetLyricsOffset(Vm.OffsetMs - 100);
    private void OnLyricsLater(object sender, RoutedEventArgs e) => SetLyricsOffset(Vm.OffsetMs + 100);
    private void OnLyricsOffsetReset(object sender, RoutedEventArgs e) => SetLyricsOffset(0);

    private void OnLyricsTimingSlider(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_offsetUpdating) SetLyricsOffset((int)Math.Round(e.NewValue * 1000));
    }

    private void SetLyricsOffset(int ms)
    {
        Vm.SetOffset(ms);
        _userScrolledAt = DateTime.MinValue;
        UpdateLyricsHighlight();
    }

    private async void OnFetchLyrics(object sender, RoutedEventArgs e)
    {
        if (await Vm.DownloadAsync()) Notifier.Success("Lyrics downloaded", "Saved for offline use.");
    }

    private async void OnEditLyrics(object sender, RoutedEventArgs e)
    {
        if (Player.CurrentTrack is not { } t) return;
        await MetadataEditorDialog.ShowAsync(new[] { t }, startOnLyrics: true);
        await Vm.ReloadAsync();
    }

    // ---------------- Per-frame ----------------

    private void OnRendering(object? sender, object e)
    {
        if (Visibility != Visibility.Visible) return;
        UpdateLyricsHighlight();

        // Gentle "breathing" of the artwork with the bass.
        var target = Player.IsPlaying ? Viz.BassLevel : 0;
        _pulse += (target - _pulse) * 0.2f;
        var s = 1 + _pulse * 0.025f;
        BigArt.CenterPoint = new Vector3((float)BigArt.ActualWidth / 2, (float)BigArt.ActualHeight / 2, 0);
        BigArt.Scale = new Vector3(s, s, 1);
        if (_glowShadow != null) _glowShadow.Opacity = 0.55f + _pulse * 0.35f;
    }

    // ---------------- Layout & tabs ----------------

    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _tab = sender.SelectedItem?.Tag as string ?? "lyrics";
        LyricsPanel.Visibility = _tab == "lyrics" ? Visibility.Visible : Visibility.Collapsed;
        QueuePanel.Visibility = _tab == "queue" ? Visibility.Visible : Visibility.Collapsed;
        DetailsPanel.Visibility = _tab == "details" ? Visibility.Visible : Visibility.Collapsed;
        if (_tab == "queue") QueueList.ScrollToPlaying();
        UpdateTimingControls();
        ApplyLayout();
        if (_tab != "art") Effects.EntranceSlide(RightPanel, 16, 0, 360);
    }

    private void ApplyLayout()
    {
        bool art = _tab == "art";
        bool showLeft = art || !_narrow;
        bool showRight = !art;
        LeftCol.Width = showLeft ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        RightCol.Width = showRight ? new GridLength(1.1, GridUnitType.Star) : new GridLength(0);
        LeftPanel.Visibility = showLeft ? Visibility.Visible : Visibility.Collapsed;
        RightPanel.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
        Columns.ColumnSpacing = showLeft && showRight ? 56 : 0;
        // Narrow window on another tab: the artwork column is hidden, so keep the song and its controls in a strip.
        bool mini = _narrow && !art;
        MiniBar.Visibility = mini ? Visibility.Visible : Visibility.Collapsed;
        if (mini) { _updating = true; MiniSeek.Value = Player.Position; _updating = false; }
    }

    // ---------------- Commands ----------------

    private void OnClose(object sender, RoutedEventArgs e) => App.MainWindow.ShowNowPlaying(false);
    private void OnPlayPause(object sender, RoutedEventArgs e) => Player.TogglePlayPause();
    private async void OnNext(object sender, RoutedEventArgs e) => await Player.NextAsync();
    private async void OnPrevious(object sender, RoutedEventArgs e) => await Player.PreviousAsync();
    private void OnRepeat(object sender, RoutedEventArgs e) => Player.CycleRepeat();

    private void OnArtist(object sender, RoutedEventArgs e)
    {
        if (Player.CurrentTrack is { } t) TrackActions.GoToArtist(t.ArtistNames, (FrameworkElement)sender);
    }

    private void OnAlbum(object sender, RoutedEventArgs e)
    {
        if (Player.CurrentTrack is { } t) App.MainWindow.Navigate(typeof(AlbumDetailPage), t.AlbumKey);
    }

    private void OnMore(object sender, RoutedEventArgs e)
    {
        if (Player.CurrentTrack is not { } t) return;
        var extra = new[] { new MenuAction("Mini player", "\uEE49", () => App.MainWindow.OpenMiniPlayer()) };
        TrackActions.BuildMenu(new[] { t }, extra).ShowAt((FrameworkElement)sender);
    }

    private void OnRatingChanged(RatingControl sender, object args)
    {
        if (Player.CurrentTrack is { } t) Svc.Library.SetRating(t, sender.Value < 0 ? 0 : (int)sender.Value);
    }

    private async void OnEditInfo(object sender, RoutedEventArgs e)
    {
        if (Player.CurrentTrack is { } t) { await MetadataEditorDialog.ShowAsync(new[] { t }); LoadTrack(t, false); }
    }

    private async void OnFixInfo(object sender, RoutedEventArgs e)
    {
        if (Player.CurrentTrack is { } t) { await FetchDialog.ShowAsync(new[] { t }); LoadTrack(t, false); }
    }
}

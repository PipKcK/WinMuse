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

namespace WinMuse.Views;

public sealed partial class NowPlayingView : UserControl
{
    private bool _dragging, _updating;
    private LyricsResult? _lyrics;
    private readonly List<TextBlock> _lineBlocks = new();
    private int _activeLine = -1;
    private DateTime _userScrolledAt = DateTime.MinValue;
    private bool _programmaticScroll;
    private CancellationTokenSource? _lyricsCts;
    private Track? _lyricsFor;
    /// <summary>Lyrics timing for the current song: positive shows each line later.</summary>
    private int _lyricsOffsetMs;
    private string _tab = "lyrics";
    private bool _narrow;
    private SpriteVisual? _glow;
    private DropShadow? _glowShadow;
    private float _pulse;

    public PlaybackService Player => Svc.Player;

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

        Seek.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _dragging = true), true);
        Seek.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndDrag()), true);
        Seek.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndDrag()), true);
        Seek.ValueChanged += (_, e) => { if (!_updating && !_dragging && Math.Abs(e.NewValue - Player.Position) > 1) Player.Seek(e.NewValue); };

        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaybackService.Position) && !_dragging)
            {
                _updating = true; Seek.Value = Player.Position; _updating = false;
            }
            else if (e.PropertyName == nameof(PlaybackService.IsPlaying) && Visibility == Visibility.Visible)
            {
                Effects.Pop(PlayGlyph, 0.6f);
            }
        };
        Player.TrackChanged += t => { if (Visibility == Visibility.Visible) LoadTrack(t, animate: true); };
        Player.Seeked += () => { _activeLine = -1; _userScrolledAt = DateTime.MinValue; };

        Svc.Theme.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ThemeService.Palette)) ApplyPalette();
        };

        ActualThemeChanged += (_, _) => Aurora.LightMode = ActualTheme == ElementTheme.Light;
        Loaded += (_, _) => { Aurora.LightMode = ActualTheme == ElementTheme.Light; BuildGlow(); };
        SizeChanged += (_, e) => { _narrow = e.NewSize.Width < 1000; ApplyLayout(); };
        LyricsScroll.PointerWheelChanged += (_, _) => _userScrolledAt = DateTime.Now;
        LyricsScroll.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _userScrolledAt = DateTime.Now), true);

        Tabs.SelectedItem = TabLyrics;
    }

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        Player.Seek(Seek.Value);
    }

    // ---------------- Open / close ----------------

    public void Open(bool lyrics)
    {
        Visibility = Visibility.Visible;
        Viz.IsActive = true;
        Viz.Style = Svc.Settings.Current.VisualizerStyle;
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

    private void ApplyPalette()
    {
        var p = Svc.Theme.AlbumPalette.Length > 0 ? Svc.Theme.AlbumPalette : Svc.Theme.Palette;
        Aurora.SetPalette(p);
        var accent = Svc.Theme.Accent;
        Viz.SetColors(accent, ColorKit.Lighten(accent, 0.18));
        if (_glowShadow != null && p.Length > 0)
        {
            var glowColor = ColorKit.PickAccent(p) ?? accent;
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
        Rating.Value = t?.Rating > 0 ? t.Rating : -1;
        BuildDetails(t);
        if (animate)
        {
            Effects.EntranceSlide(TitleBlock, 14, 0, 420);
        }
        if (_lyricsFor != t) _ = LoadLyricsAsync(t);
    }

    private void BuildDetails(Track? t)
    {
        DetailsGrid.Children.Clear();
        DetailsGrid.RowDefinitions.Clear();
        DetailsGrid.ColumnDefinitions.Clear();
        if (t == null) return;
        DetailsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        DetailsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rows = new List<(string, string)>
        {
            ("Title", t.DisplayTitle), ("Artist", t.DisplayArtist), ("Album", t.DisplayAlbum), ("Album artist", t.DisplayAlbumArtist),
            ("Genre", t.Genre ?? "—"), ("Year", t.Year > 0 ? t.Year.ToString() : "—"),
            ("Track", t.TrackNumber > 0 ? (t.TrackCount > 0 ? $"{t.TrackNumber} of {t.TrackCount}" : t.TrackNumber.ToString()) : "—"),
            ("Composer", t.Composer ?? "—"), ("Format", t.FormatText), ("Channels", t.Channels > 0 ? t.Channels.ToString() : "—"),
            ("Duration", t.DurationText), ("Size", Format.Size(t.FileSize)), ("Plays", t.PlayCount.ToString()),
            ("Added", DateTimeOffset.FromUnixTimeSeconds(t.AddedUtc).LocalDateTime.ToString("d")),
            ("File", t.Path),
        };
        for (int i = 0; i < rows.Count; i++)
        {
            DetailsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = rows[i].Item1, Opacity = 0.7 };
            var v = new TextBlock { Text = rows[i].Item2, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            k.SetValue(TextBlock.ForegroundProperty, ThemeRes.Brush("NowPlayingTextBrush", this));
            v.SetValue(TextBlock.ForegroundProperty, ThemeRes.Brush("NowPlayingTextBrush", this));
            Grid.SetRow(k, i); Grid.SetRow(v, i); Grid.SetColumn(v, 1);
            DetailsGrid.Children.Add(k); DetailsGrid.Children.Add(v);
        }
    }

    // ---------------- Lyrics ----------------

    private async Task LoadLyricsAsync(Track? t, bool online = true)
    {
        _lyricsCts?.Cancel();
        var cts = _lyricsCts = new CancellationTokenSource();
        _lyricsFor = t;
        _lyrics = null;
        _activeLine = -1;
        LyricsLines.Children.Clear();
        _lineBlocks.Clear();
        LyricsSource.Text = "";
        LyricsEmpty.Visibility = Visibility.Collapsed;
        if (t == null) return;

        LyricsProgress.IsActive = true;
        LyricsResult? result = null;
        try { result = await Svc.Lyrics.GetAsync(t, online, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { AppPaths.Log("Lyrics: " + ex.Message); }
        if (cts.IsCancellationRequested) return;
        LyricsProgress.IsActive = false;
        ShowLyrics(result);
    }

    private void ShowLyrics(LyricsResult? result)
    {
        _lyrics = result;
        LyricsLines.Children.Clear();
        _lineBlocks.Clear();
        _activeLine = -1;

        if (result == null || result.Lines.Count == 0)
        {
            _lyricsSynced = false;
            UpdateTimingButton();
            LyricsEmpty.Visibility = Visibility.Visible;
            LyricsEmptyText.Text = "Lyrics unavailable";
            return;
        }
        LyricsEmpty.Visibility = Visibility.Collapsed;
        LyricsSource.Text = (result.IsSynced ? "Synced lyrics" : "Lyrics") + (result.Source != null ? " · " + result.Source : "");
        _lyricsOffsetMs = _lyricsFor != null ? Svc.Library.PrefsFor(_lyricsFor).LyricsOffsetMs : 0;
        _lyricsSynced = result.IsSynced;
        UpdateTimingButton();
        UpdateOffsetText();

        var fg = ThemeRes.Brush("NowPlayingTextBrush", this);
        foreach (var line in result.Lines)
        {
            var tb = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(line.Text) ? "♪" : line.Text,
                FontSize = result.IsSynced ? 28 : 20,
                FontWeight = result.IsSynced ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Opacity = result.IsSynced ? 0.32 : 0.92,
                OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(350) },
                ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(400) },
                Scale = result.IsSynced ? new Vector3(0.94f, 0.94f, 1) : Vector3.One,
            };
            tb.SetValue(TextBlock.ForegroundProperty, fg);
            if (result.IsSynced)
            {
                var time = line.Time;
                tb.Tapped += (_, _) => { Player.Seek((time.TotalMilliseconds + _lyricsOffsetMs) / 1000.0); _userScrolledAt = DateTime.MinValue; };
                tb.PointerEntered += (_, _) => { if (_lineBlocks.IndexOf(tb) != _activeLine) tb.Opacity = 0.6; };
                tb.PointerExited += (_, _) => { if (_lineBlocks.IndexOf(tb) != _activeLine) tb.Opacity = 0.32; };
            }
            LyricsLines.Children.Add(tb);
            _lineBlocks.Add(tb);
        }
        LyricsScroll.ChangeView(null, 0, null, true);
        Effects.EntranceSlide(LyricsLines, 30, 0, 600);
    }

    private void UpdateLyricsHighlight()
    {
        if (_lyrics is not { IsSynced: true } || _lineBlocks.Count == 0) return;
        var pos = Player.Engine.Pipeline.Position + TimeSpan.FromMilliseconds(180 - _lyricsOffsetMs);
        int idx = -1;
        var lines = _lyrics.Lines;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Time <= pos) idx = i; else break;
        }
        if (idx == _activeLine) return;

        if (_activeLine >= 0 && _activeLine < _lineBlocks.Count)
        {
            var old = _lineBlocks[_activeLine];
            old.Opacity = 0.32;
            old.Scale = new Vector3(0.94f, 0.94f, 1);
        }
        _activeLine = idx;
        if (idx < 0) return;

        var tb = _lineBlocks[idx];
        tb.CenterPoint = new Vector3(0, (float)tb.ActualHeight / 2, 0);
        tb.Opacity = 1;
        tb.Scale = Vector3.One;

        if ((DateTime.Now - _userScrolledAt).TotalSeconds > 3.5)
        {
            var y = tb.TransformToVisual(LyricsLines).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            _programmaticScroll = true;
            LyricsScroll.ChangeView(null, Math.Max(0, y - LyricsScroll.ViewportHeight * 0.36), null, false);
        }
    }

    // ---------------- Lyrics timing ----------------

    private readonly Debouncer _saveOffset = new(TimeSpan.FromMilliseconds(500));
    private bool _offsetUpdating;
    private bool _lyricsSynced;

    /// <summary>The timing button sits with the tabs and only shows on the Lyrics tab, for synced lyrics.</summary>
    private void UpdateTimingButton() =>
        LyricsTimingButton.Visibility = _lyricsSynced && _tab == "lyrics" ? Visibility.Visible : Visibility.Collapsed;

    private void OnLyricsTimingTyped(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_offsetUpdating) return;
        if (double.IsNaN(args.NewValue)) { UpdateOffsetText(); return; } // cleared: show the current value again
        SetLyricsOffset((int)Math.Round(args.NewValue * 1000));
    }

    private void OnLyricsEarlier(object sender, RoutedEventArgs e) => SetLyricsOffset(_lyricsOffsetMs - 100);
    private void OnLyricsLater(object sender, RoutedEventArgs e) => SetLyricsOffset(_lyricsOffsetMs + 100);
    private void OnLyricsOffsetReset(object sender, RoutedEventArgs e) => SetLyricsOffset(0);

    private void OnLyricsTimingSlider(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (!_offsetUpdating) SetLyricsOffset((int)Math.Round(e.NewValue * 1000));
    }

    /// <summary>Applies a timing offset right away (positive shows lyrics later) and saves it for the song.</summary>
    private void SetLyricsOffset(int ms)
    {
        if (_lyricsFor is not { } t) return;
        _lyricsOffsetMs = Math.Clamp(ms, -5000, 5000);
        _saveOffset.Run(() => App.UI.TryEnqueue(() => Svc.Library.SaveLyricsOffset(t, _lyricsFor == t ? _lyricsOffsetMs : Svc.Library.PrefsFor(t).LyricsOffsetMs)));
        UpdateOffsetText();
        _userScrolledAt = DateTime.MinValue;
        UpdateLyricsHighlight();
    }

    private void UpdateOffsetText()
    {
        var text = $"{_lyricsOffsetMs / 1000.0:+0.00;-0.00;0.00} s";
        LyricsTimingText.Text = _lyricsOffsetMs == 0 ? "Timing" : $"Timing {text}";
        _offsetUpdating = true;
        LyricsTimingSlider.Value = _lyricsOffsetMs / 1000.0;
        LyricsTimingBox.Value = Math.Round(_lyricsOffsetMs / 1000.0, 2);
        _offsetUpdating = false;
    }

    private void OnLyricsScrolled(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!e.IsIntermediate) _programmaticScroll = false;
    }

    private async void OnFetchLyrics(object sender, RoutedEventArgs e)
    {
        if (Player.CurrentTrack is not { } t) return;
        LyricsProgress.IsActive = true;
        LyricsEmptyText.Text = "Searching for lyrics…";
        try
        {
            var r = await Svc.Lyrics.DownloadAsync(t);
            LyricsProgress.IsActive = false;
            if (r != null) { ShowLyrics(r); Notifier.Success("Lyrics downloaded", "Saved for offline use."); }
            else LyricsEmptyText.Text = "No lyrics were found for this song";
        }
        catch (Exception ex)
        {
            LyricsProgress.IsActive = false;
            LyricsEmptyText.Text = "Unable to reach the lyrics service";
            AppPaths.Log("Lyrics download: " + ex.Message);
        }
    }

    private async void OnEditLyrics(object sender, RoutedEventArgs e)
    {
        if (Player.CurrentTrack is not { } t) return;
        await MetadataEditorDialog.ShowAsync(new[] { t }, startOnLyrics: true);
        _lyricsFor = null;
        await LoadLyricsAsync(t, online: false);
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
        UpdateTimingButton();
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
        var extra = new[] { new MenuAction("Mini player", "", () => App.MainWindow.OpenMiniPlayer()) };
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

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;
using WinMuse.Services.Audio;
using WinMuse.Views;

namespace WinMuse.Controls;

public sealed partial class PlayerBar : UserControl
{
    private bool _dragging;
    private bool _updating;
    private readonly List<Slider> _bandSliders = new();
    private bool _eqLoading;

    public PlaybackService Player => Svc.Player;
    public UIElement ArtElement => Art;

    public PlayerBar()
    {
        InitializeComponent();

        SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _dragging = true), true);
        SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => EndDrag()), true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => EndDrag()), true);
        SeekSlider.ValueChanged += (_, e) =>
        {
            if (_updating || _dragging) return;
            if (Math.Abs(e.NewValue - Player.Position) > 1.0) Player.Seek(e.NewValue);
        };

        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaybackService.Position) && !_dragging)
            {
                _updating = true;
                SeekSlider.Value = Player.Position;
                _updating = false;
            }
            else if (e.PropertyName == nameof(PlaybackService.IsPlaying))
            {
                Effects.Pop(PlayGlyph, 0.6f);
            }
        };
        Player.TrackChanged += _ => Effects.EntranceSlide(InfoPanel, 0, 0, 380);

        ArtButton.PointerEntered += (_, _) => ArtHover.Opacity = 1;
        ArtButton.PointerExited += (_, _) => ArtHover.Opacity = 0;

        // Scroll on the volume slider / volume area adjusts volume
        VolumeSlider.PointerWheelChanged += (_, e) =>
        {
            var d = e.GetCurrentPoint(VolumeSlider).Properties.MouseWheelDelta;
            Player.Volume = Math.Clamp(Player.Volume + (d > 0 ? 0.05 : -0.05), 0, 1);
            e.Handled = true;
        };

        BuildEqualizer();
        Svc.Favorites.Changed += UpdateStar;
        Svc.Library.PlaylistsChanged += UpdateStar;
        Player.TrackChanged += _ => UpdateStar();
        // Its colors are picked in code, so they are picked again for the new theme (light/dark).
        ActualThemeChanged += (_, _) => UpdateStar();
        Player.EqualizerChanged += () => App.UI.TryEnqueue(RefreshEqualizer);
        QueueButton.IsChecked = Svc.Settings.Current.ShowQueuePanel;
        Svc.Settings.Changed += name => { if (name == nameof(AppSettings.ShowQueuePanel)) QueueButton.IsChecked = Svc.Settings.Current.ShowQueuePanel; };
        SizeChanged += (_, e) => ApplyResponsiveLayout(e.NewSize.Width);
    }

    /// <summary>Shows as many secondary buttons as fit beside the centred transport, in priority order.</summary>
    private void ApplyResponsiveLayout(double width)
    {
        var available = (width - 32 - 460 - 32) / 2 - 8;
        var items = new (FrameworkElement el, double w)[]
        {
            (EqButton, 42), (QueueButton, 42), (ExpandButton, 42), (LyricsButton, 42), (VolumeSlider, 94), (MiniButton, 48),
        };
        double used = 42; // mute button is always visible
        foreach (var (el, w) in items)
        {
            var show = used + w <= available;
            el.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) used += w;
        }
    }

    public void FocusPlayButton() => PlayButton.Focus(FocusState.Pointer);

    private void EndDrag()
    {
        if (!_dragging) return;
        _dragging = false;
        Player.Seek(SeekSlider.Value);
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => Player.TogglePlayPause();
    private async void OnNext(object sender, RoutedEventArgs e) => await Player.NextAsync();
    private async void OnPrevious(object sender, RoutedEventArgs e) => await Player.PreviousAsync();
    private void OnRepeat(object sender, RoutedEventArgs e) => Player.CycleRepeat();
    private void OnMute(object sender, RoutedEventArgs e)
    {
        if (VolumeSlider.Visibility == Visibility.Visible) { Player.IsMuted = !Player.IsMuted; return; }

        // Narrow window: the inline slider is hidden, so offer it in a flyout instead.
        var slider = new Slider { Orientation = Orientation.Vertical, Height = 140, Minimum = 0, Maximum = 1, StepFrequency = 0.01, Value = Player.Volume, ThumbToolTipValueConverter = new PercentTooltipConverter() };
        slider.ValueChanged += (_, a) => Player.Volume = a.NewValue;
        var mute = new ToggleButton { Content = new FontIcon { Glyph = "", FontSize = 14 }, IsChecked = Player.IsMuted, HorizontalAlignment = HorizontalAlignment.Center };
        mute.Click += (_, _) => Player.IsMuted = mute.IsChecked == true;
        new Flyout { Content = new StackPanel { Spacing = 10, Children = { slider, mute } }, Placement = FlyoutPlacementMode.Top }.ShowAt((FrameworkElement)sender);
    }
    private void OnArtClick(object sender, RoutedEventArgs e) => App.MainWindow.ShowNowPlaying(true);
    private void OnLyrics(object sender, RoutedEventArgs e) => App.MainWindow.ShowNowPlaying(true, lyrics: true);
    private void OnQueue(object sender, RoutedEventArgs e) =>
        Svc.Settings.Set(nameof(AppSettings.ShowQueuePanel), s => s.ShowQueuePanel = QueueButton.IsChecked == true);
    private void OnMiniPlayer(object sender, RoutedEventArgs e) => App.MainWindow.OpenMiniPlayer();

    private void OnArtistClick(object sender, RoutedEventArgs e)
    {
        if (Player.CurrentTrack is { } t) TrackActions.GoToArtist(t.ArtistNames, (FrameworkElement)sender);
    }

    // ---------------- Favorite ----------------

    private void UpdateStar()
    {
        var t = Player.CurrentTrack;
        StarButton.Visibility = t != null ? Visibility.Visible : Visibility.Collapsed;
        bool fav = t?.IsFavorite == true;
        StarButton.Content = fav ? "" : "";
        StarButton.Foreground = fav ? ThemeRes.Brush("AccentTextFillColorPrimaryBrush", this) : ThemeRes.Brush("TextFillColorSecondaryBrush", this);
        ToolTipService.SetToolTip(StarButton, fav ? "Remove from Favorite" : "Add to Favorite");
    }

    private void OnStar(object sender, RoutedEventArgs e)
    {
        if (Player.CurrentTrack is { } t) Svc.Favorites.ToggleFavorite(t);
    }

    // ---------------- Equalizer ----------------

    private void BuildEqualizer()
    {
        for (int i = 0; i < Equalizer.Frequencies.Length; i++)
        {
            EqBandsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var slider = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = -12,
                Maximum = 12,
                StepFrequency = 0.5,
                TickFrequency = 6,
                TickPlacement = TickPlacement.Outside,
                Height = 150,
                HorizontalAlignment = HorizontalAlignment.Center,
                ThumbToolTipValueConverter = new DbTooltipConverter(),
                Tag = i,
            };
            slider.ValueChanged += OnBandChanged;
            var label = new TextBlock
            {
                Text = Equalizer.Labels[i],
                FontSize = 11,
                Opacity = 0.7, // inherits the themed foreground
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var panel = new StackPanel { Spacing = 6, Children = { slider, label } };
            Grid.SetColumn(panel, i);
            EqBandsGrid.Children.Add(panel);
            _bandSliders.Add(slider);
        }
        foreach (var name in Equalizer.Presets.Keys) EqPresets.Items.Add(name);
        EqPresets.Items.Add("Custom");
    }

    private void OnEqOpening(object sender, object e) => RefreshEqualizer();

    /// <summary>Shows the equalizer playing now and where it comes from.</summary>
    private void RefreshEqualizer()
    {
        _eqLoading = true;
        EqGenreToggle.IsOn = Svc.Settings.Current.EqMatchGenre;
        for (int i = 0; i < _bandSliders.Count; i++) _bandSliders[i].Value = Player.ActiveEqBands[i];
        PreampSlider.Value = Player.ActiveEqPreamp;
        EqPresets.SelectedItem = Equalizer.Presets.ContainsKey(Player.ActiveEqPreset) ? Player.ActiveEqPreset : "Custom";
        EqSourceText.Text = Player.ActiveEqSource switch
        {
            PlaybackService.EqSource.Song => "Saved for this song",
            PlaybackService.EqSource.Genre => string.IsNullOrWhiteSpace(Player.CurrentTrack?.Genre) ? "No genre, so Flat" : $"Matched to {Player.CurrentTrack!.Genre}",
            _ => "",
        };
        // Save, or Saved (selecting it removes the song's saved equalizer).
        bool saved = Player.ActiveEqSource == PlaybackService.EqSource.Song;
        EqSaveButton.IsEnabled = Player.CurrentTrack != null;
        EqSaveButton.Style = (Style)Application.Current.Resources[saved ? "DefaultButtonStyle" : "AccentButtonStyle"];
        EqSaveIcon.Glyph = saved ? "\uE73E" : "\uE74E";
        EqSaveText.Text = saved ? "Saved" : "Save";
        ToolTipService.SetToolTip(EqSaveButton, saved ? "Saved for this song. Select to remove it." : "Save this equalizer for the song that is playing");
        _eqLoading = false;
    }

    private float[] SliderBands() => _bandSliders.Select(s => (float)s.Value).ToArray();

    private void OnEqGenreToggled(object sender, RoutedEventArgs e)
    {
        if (_eqLoading) return;
        Svc.Settings.Set(nameof(AppSettings.EqMatchGenre), s => s.EqMatchGenre = EqGenreToggle.IsOn);
        Player.ApplyTrackEqualizer();
    }

    private void OnEqPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_eqLoading || EqPresets.SelectedItem is not string name || !Equalizer.Presets.TryGetValue(name, out var bands)) return;
        Player.SetEqualizer(bands, (float)PreampSlider.Value, name);
    }

    private void OnBandChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_eqLoading) return;
        Player.SetEqualizer(SliderBands(), (float)PreampSlider.Value, "Custom");
    }

    private void OnPreampChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_eqLoading) return;
        Player.SetEqualizer(SliderBands(), (float)e.NewValue, EqPresets.SelectedItem as string ?? "Custom");
    }

    private void OnEqSave(object sender, RoutedEventArgs e)
    {
        bool saved = Player.ActiveEqSource == PlaybackService.EqSource.Song;
        Player.SaveEqualizerForSong(save: !saved);
        if (!saved) Notifier.Show("Equalizer saved", $"Used whenever \u201C{Player.CurrentTrack?.DisplayTitle}\u201D plays.");
    }
}

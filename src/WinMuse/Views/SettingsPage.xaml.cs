using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;
using WinMuse.Controls;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;
using WinMuse.Services.Audio;

namespace WinMuse.Views;

public sealed partial class SettingsPage : Page
{
    private bool _loading = true; // sliders raise ValueChanged while the XAML loads; ignore until settings are applied
    private static readonly string[] Swatches = { "#FF8B5CF6", "#FF6366F1", "#FF0EA5E9", "#FF14B8A6", "#FF22C55E", "#FFF59E0B", "#FFF97316", "#FFEF4444", "#FFEC4899", "#FFA855F7" };

    private AppSettings S => Svc.Settings.Current;

    public SettingsPage()
    {
        InitializeComponent();
        UpdateLogoButtons();
        foreach (var hex in Swatches)
        {
            var c = ColorKit.Parse(hex, Microsoft.UI.Colors.Purple);
            var b = new Button
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Padding = new Thickness(0), BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(c), Tag = hex,
            };
            Effects.SetHoverScale(b, 1.15);
            b.Click += (_, _) => { AccentPicker.Color = c; SetCustomAccent(c); };
            AccentSwatches.Children.Add(b);
        }
        AcoustIdKey.Validator = k => Svc.Online.ValidateAcoustIdKeyAsync(k);
        AcoustIdKey.Save = k => Svc.Settings.Set(nameof(AppSettings.AcoustIdApiKey), s => s.AcoustIdApiKey = k);
        YouTubeKey.Validator = k => Svc.Online.ValidateYouTubeKeyAsync(k);
        YouTubeKey.Save = k => Svc.Settings.Set(nameof(AppSettings.YouTubeApiKey), s => s.YouTubeApiKey = k);
        LastFmKey.Validator = k => Svc.Online.ValidateLastFmKeyAsync(k);
        FanartKey.Validator = k => Svc.Online.ValidateFanartKeyAsync(k);
        FanartKey.Save = k => Svc.Settings.Set(nameof(AppSettings.FanartApiKey), s => s.FanartApiKey = k);
        SpotifyKey.Placeholder = "Client ID";
        SpotifyKey.SecretPlaceholder = "Client secret";
        SpotifyKey.PairValidator = (id, secret) => Svc.Online.ValidateSpotifyAsync(id, secret);
        SpotifyKey.SavePair = (id, secret) => Svc.Settings.Set(nameof(AppSettings.SpotifyClientId), s => { s.SpotifyClientId = id; s.SpotifyClientSecret = secret; });
        LastFmKey.Save = k => Svc.Settings.Set(nameof(AppSettings.LastFmApiKey), s => s.LastFmApiKey = k);

        Action scanChanged = UpdateScanStatus;
        Action<ScanProgress> progress = _ => UpdateScanStatus();
        this.WhileLoaded(() => Svc.Library.ScanProgressChanged += progress, () => Svc.Library.ScanProgressChanged -= progress);
        this.WhileLoaded(() => Svc.Library.Changed += scanChanged, () => Svc.Library.Changed -= scanChanged);
        Action<int, int, string?> fixProgress = OnAutoFixProgress;
        this.WhileLoaded(() => Svc.AutoFix.Progress += fixProgress, () => Svc.AutoFix.Progress -= fixProgress);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        Select(ThemeBox, (int)S.Theme);
        Select(AccentBox, (int)S.AccentSource);
        AccentPicker.Color = ColorKit.Parse(S.CustomAccent, Microsoft.UI.Colors.Purple);
        Select(BackgroundBox, (int)S.BackgroundMode);
        IntensitySlider.Value = S.BackgroundIntensity;
        AnimateToggle.IsOn = S.AnimateBackground;
        BlurSlider.Value = S.WallpaperBlur;
        DimSlider.Value = S.WallpaperDim;
        VizToggle.IsOn = S.ShowVisualizer;
        VizBarToggle.IsOn = S.ShowBarVisualizer;
        Select(HighlightBox, (int)S.PlayingHighlight);
        Select(GreetingStyleBox, (int)S.GreetingStyle);
        Select(CardColorBox, Math.Max(0, Array.IndexOf(CardColorChoices, S.ShelfCardColor)));
        Select(VizStyleBox, (int)S.VisualizerStyle);
        EqGenreToggle.IsOn = S.EqMatchGenre;
        GaplessToggle.IsOn = S.Gapless;
        CrossfadeSlider.Value = S.CrossfadeSeconds;
        Select(WriteModeBox, (int)S.WriteMode);
        AutoLyricsToggle.IsOn = S.AutoFetchLyrics;
        AutoFixToggle.IsOn = S.AutoFixMetadata;
        QueuePanelToggle.IsOn = S.ShowQueuePanel;
        AutoArtToggle.IsOn = S.AutoFetchArt;
        EmbedArtToggle.IsOn = S.EmbedFetchedArt;
        AcoustIdKey.Load(S.AcoustIdApiKey);
        YouTubeKey.Load(S.YouTubeApiKey);
        LastFmKey.Load(S.LastFmApiKey);
        FanartKey.Load(S.FanartApiKey);
        SpotifyKey.Load(S.SpotifyClientId, S.SpotifyClientSecret);
        OnAutoFixProgress(0, Svc.AutoFix.IsRunning ? 1 : 0, null);
        VizStyleCard.Visibility = VizBarCard.Visibility = S.ShowVisualizer ? Visibility.Visible : Visibility.Collapsed;

        var devices = AudioEngine.GetDevices();
        DeviceBox.ItemsSource = devices;
        DeviceBox.SelectedItem = devices.FirstOrDefault(d => d.Id == S.OutputDeviceId) ?? devices[0];

        AboutCard.Description = $"Version 1.1 · FFmpeg {FFmpegLoader.Version} · {Svc.Library.Tracks.Count:N0} songs";
        _loading = false;

        RefreshFolders();
        RefreshGradientButtons();
        RefreshWallpaperPreview();
        UpdateBackgroundCards();
        UpdateScanStatus();
        UpdateCrossfadeText();
        PipelineText.Text = Svc.Player.OutputDescription;
        CustomAccentCard.Visibility = S.AccentSource == AccentSource.Custom ? Visibility.Visible : Visibility.Collapsed;
        Effects.EntranceSlide(Root, 20);
    }

    // ---------------- Library ----------------

    private void RefreshFolders()
    {
        FoldersPanel.Children.Clear();
        foreach (var folder in S.MusicFolders.ToList())
        {
            var g = new Grid { Padding = new Thickness(58, 10, 44, 10), ColumnSpacing = 12 };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(new FontIcon { Glyph = "", FontSize = 16 });
            var text = new TextBlock { Text = folder, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Padding = new Thickness(8, 6, 8, 6) };
            ToolTipService.SetToolTip(remove, "Remove from library");
            remove.Click += async (_, _) => { await Svc.Library.RemoveFolderAsync(folder); RefreshFolders(); };
            Grid.SetColumn(remove, 2);
            g.Children.Add(remove);
            FoldersPanel.Children.Add(g);
        }
        if (S.MusicFolders.Count == 0)
            FoldersPanel.Children.Add(new TextBlock { Text = "No folders added.", Margin = new Thickness(58, 10, 44, 10), Opacity = 0.7 });
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var folder = await Pickers.PickFolderAsync();
        if (folder == null) return;
        var task = Svc.Library.AddFolderAsync(folder);
        RefreshFolders();
        await task;
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await Svc.Library.ScanAsync();

    private async void OnRestoreRemoved(object sender, RoutedEventArgs e)
    {
        await Svc.Library.RestoreRemovedAsync();
        UpdateScanStatus();
    }

    private void UpdateScanStatus()
    {
        RemovedText.Text = Svc.Library.ExcludedCount == 0 ? "None" : $"{Svc.Library.ExcludedCount:N0} songs";
        RestoreButton.IsEnabled = Svc.Library.ExcludedCount > 0;
        UpdateScanText();
    }

    private void UpdateScanText() =>
        ScanStatus.Text = Svc.Library.IsScanning ? "Scanning…" : $"{Svc.Library.Tracks.Count:N0} songs";

    // ---------------- Appearance ----------------

    private void OnThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.Theme), s => s.Theme = (AppTheme)ThemeBox.SelectedIndex);
        UpdateBackgroundCards();
    }

    private void OnAccentSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.AccentSource), s => s.AccentSource = (AccentSource)AccentBox.SelectedIndex);
        CustomAccentCard.Visibility = S.AccentSource == AccentSource.Custom ? Visibility.Visible : Visibility.Collapsed;
    }

    private readonly Debouncer _accentDebounce = new(TimeSpan.FromMilliseconds(120));

    private void OnAccentPicked(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loading) return;
        var c = args.NewColor;
        _accentDebounce.Run(() => App.UI.TryEnqueue(() => SetCustomAccent(c)));
    }

    private void SetCustomAccent(Color c)
    {
        Svc.Settings.Set(nameof(AppSettings.CustomAccent), s => { s.CustomAccent = ColorKit.ToHex(c); s.AccentSource = AccentSource.Custom; });
        _loading = true; AccentBox.SelectedIndex = (int)AccentSource.Custom; _loading = false;
    }

    private void OnBackgroundModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.BackgroundMode), s => s.BackgroundMode = (BackgroundMode)BackgroundBox.SelectedIndex);
        UpdateBackgroundCards();
    }

    private void UpdateBackgroundCards()
    {
        bool oled = S.Theme == AppTheme.Oled;
        BackgroundCard.IsEnabled = !oled;
        BackgroundCard.Description = oled ? "Not available with the OLED theme, which keeps the background true black." : "Content displayed behind the app.";
        var m = oled ? BackgroundMode.None : S.BackgroundMode;
        IntensityCard.Visibility = AnimateCard.Visibility = m is BackgroundMode.AlbumArt or BackgroundMode.Gradient ? Visibility.Visible : Visibility.Collapsed;
        GradientCard.Visibility = m == BackgroundMode.Gradient ? Visibility.Visible : Visibility.Collapsed;
        WallpaperCard.Visibility = BlurCard.Visibility = DimCard.Visibility = m == BackgroundMode.Wallpaper ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnIntensityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.BackgroundIntensity), s => s.BackgroundIntensity = e.NewValue);
    }

    private void OnAnimateToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.AnimateBackground), s => s.AnimateBackground = AnimateToggle.IsOn);
    }

    private void RefreshGradientButtons()
    {
        Grad1.Background = new SolidColorBrush(ColorKit.Parse(S.GradientColor1, Microsoft.UI.Colors.SlateBlue));
        Grad2.Background = new SolidColorBrush(ColorKit.Parse(S.GradientColor2, Microsoft.UI.Colors.HotPink));
        Grad3.Background = new SolidColorBrush(ColorKit.Parse(S.GradientColor3, Microsoft.UI.Colors.DeepSkyBlue));
    }

    private void OnGradientColor(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        var index = (string)btn.Tag;
        var picker = new ColorPicker
        {
            Color = ((SolidColorBrush)btn.Background).Color,
            IsAlphaEnabled = false,
            ColorSpectrumShape = ColorSpectrumShape.Ring,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
        };
        var flyout = new Flyout { Content = picker };
        flyout.Closed += (_, _) =>
        {
            var hex = ColorKit.ToHex(picker.Color);
            var name = "GradientColor" + index;
            Svc.Settings.Set(name, s =>
            {
                if (index == "1") s.GradientColor1 = hex;
                else if (index == "2") s.GradientColor2 = hex;
                else s.GradientColor3 = hex;
            });
            RefreshGradientButtons();
        };
        flyout.ShowAt(btn);
    }

    private async void OnPickWallpaper(object sender, RoutedEventArgs e)
    {
        var path = await Pickers.PickImageAsync();
        if (path == null) return;
        // Copy into app data so the wallpaper survives the original being moved. Each pick gets its own name: with one
        // fixed name a new image looked like the one already shown, so nothing changed.
        var dest = Path.Combine(AppPaths.DataDir, $"wallpaper-{DateTime.UtcNow:yyyyMMddHHmmss}{Path.GetExtension(path)}");
        try
        {
            File.Copy(path, dest, true);
            foreach (var old in Directory.EnumerateFiles(AppPaths.DataDir, "wallpaper*"))
                if (!old.Equals(dest, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch { }
        }
        catch { dest = path; }
        Svc.Settings.Set(nameof(AppSettings.WallpaperPath), s => { s.WallpaperPath = dest; s.BackgroundMode = BackgroundMode.Wallpaper; });
        _loading = true; BackgroundBox.SelectedIndex = (int)BackgroundMode.Wallpaper; _loading = false;
        Svc.Settings.Set(nameof(AppSettings.BackgroundMode), _ => { });
        UpdateBackgroundCards();
        RefreshWallpaperPreview();
    }

    private void RefreshWallpaperPreview()
    {
        WallpaperPreview.Source = !string.IsNullOrEmpty(S.WallpaperPath) && File.Exists(S.WallpaperPath)
            ? new BitmapImage(new Uri(S.WallpaperPath)) { DecodePixelWidth = 160 }
            : null;
    }

    private void OnBlurChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.WallpaperBlur), s => s.WallpaperBlur = e.NewValue);
    }

    private void OnDimChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.WallpaperDim), s => s.WallpaperDim = e.NewValue);
    }

    private void OnVizToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.ShowVisualizer), s => s.ShowVisualizer = VizToggle.IsOn);
        VizStyleCard.Visibility = VizBarCard.Visibility = VizToggle.IsOn ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnHighlightChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.PlayingHighlight), s => s.PlayingHighlight = (PlayingHighlight)HighlightBox.SelectedIndex);
    }

    private void OnVizBarToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.ShowBarVisualizer), s => s.ShowBarVisualizer = VizBarToggle.IsOn);
    }

    private void OnVizStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.VisualizerStyle), s => s.VisualizerStyle = (VisualizerStyle)VizStyleBox.SelectedIndex);
    }

    // ---------------- Playback ----------------

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DeviceBox.SelectedItem is not AudioDevice d) return;
        Svc.Settings.Set(nameof(AppSettings.OutputDeviceId), s => s.OutputDeviceId = d.Id);
        Svc.Player.ReinitializeOutput();
        PipelineText.Text = Svc.Player.OutputDescription;
    }

    private void OnEqGenreToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.EqMatchGenre), s => s.EqMatchGenre = EqGenreToggle.IsOn);
        Svc.Player.ApplyTrackEqualizer();
    }

    private void OnGaplessToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.Gapless), s => s.Gapless = GaplessToggle.IsOn);
        Svc.Player.RefreshNext();
    }

    private void OnCrossfadeChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        UpdateCrossfadeText();
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.CrossfadeSeconds), s => s.CrossfadeSeconds = e.NewValue);
        Svc.Player.ApplyAudioSettings();
        Svc.Player.RefreshNext();
    }

    private void UpdateCrossfadeText() => CrossfadeText.Text = CrossfadeSlider.Value <= 0 ? "Off" : $"{CrossfadeSlider.Value:0.#} s";

    // ---------------- Metadata ----------------

    private void OnWriteModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.WriteMode), s => s.WriteMode = (MetadataWriteMode)WriteModeBox.SelectedIndex);
    }

    private void OnAutoLyricsToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.AutoFetchLyrics), s => s.AutoFetchLyrics = AutoLyricsToggle.IsOn);
    }

    private void OnAutoArtToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.AutoFetchArt), s => s.AutoFetchArt = AutoArtToggle.IsOn);
    }

    private void OnEmbedArtToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.EmbedFetchedArt), s => s.EmbedFetchedArt = EmbedArtToggle.IsOn);
    }


    private void OnAutoFixToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.AutoFixMetadata), s => s.AutoFixMetadata = AutoFixToggle.IsOn);
        if (AutoFixToggle.IsOn) _ = Svc.AutoFix.RunPendingAsync();
    }

    private void OnQueuePanelToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.ShowQueuePanel), s => s.ShowQueuePanel = QueuePanelToggle.IsOn);
    }

    private async void OnFixAll(object sender, RoutedEventArgs e)
    {
        if (Svc.AutoFix.IsRunning) { Svc.AutoFix.Stop(); return; }
        var pending = Svc.Library.Tracks.Count(t => t.IsCheckable && Svc.Library.SyncState(t) != 1);
        if (pending == 0) { FixAllText.Text = "All songs are complete"; return; }
        int edited = Svc.Library.Tracks.Count(t => t.IsCheckable && Svc.Library.SyncState(t) == -1);
        if (!await TrackActions.Confirm("Fetch for the whole library?",
                $"{pending:N0} songs will be updated with online metadata, album art, artist images and lyrics." +
                (edited > 0 ? $" This includes {edited:N0} songs you edited; your changes to them will be replaced." : ""),
                "Fetch")) return;
        await Svc.AutoFix.SyncAsync();
    }

    private void OnAutoFixProgress(int done, int total, string? current)
    {
        FixAllButton.Content = total > 0 ? "Stop" : "Fetch";
        FixAllText.Text = total > 0 ? $"{done + 1:N0} of {total:N0}" : "";
    }


    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        try { Process.Start("explorer.exe", AppPaths.DataDir); } catch { }
    }

    // ---------------- Debug (temporary) ----------------

    private void OnCardColorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.ShelfCardColor), s => s.ShelfCardColor = CardColorChoices[CardColorBox.SelectedIndex]);
    }

    /// <summary>
    /// Shows a saved choice in a box; a value the box doesn't list (e.g. from a newer option picked elsewhere) shows
    /// the first entry instead of failing, which made the whole page unopenable.
    /// </summary>
    private static void Select(ComboBox box, int index) => box.SelectedIndex = index >= 0 && index < box.Items.Count ? index : 0;

    // The order of the Card color box.
    private static readonly ShelfCardColor[] CardColorChoices =
        { ShelfCardColor.Neutral, ShelfCardColor.Glass, ShelfCardColor.Smoke, ShelfCardColor.Slate, ShelfCardColor.Elevated, ShelfCardColor.Outline, ShelfCardColor.None };

    private void OnGreetingStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        Svc.Settings.Set(nameof(AppSettings.GreetingStyle), s => s.GreetingStyle = (GreetingStyle)GreetingStyleBox.SelectedIndex);
    }

    private void OnCreateTestPlaylists(object sender, RoutedEventArgs e)
    {
        var tracks = Svc.Library.Tracks;
        if (tracks.Count == 0) { Notifier.Show("No songs in the library"); return; }
        for (int i = 1; i <= 10; i++)
            Svc.Library.CreatePlaylist($"Test playlist {i}", "Created for testing.", tracks: tracks.OrderBy(_ => Random.Shared.Next()).Take(20));
        Notifier.Show("Created 10 test playlists", tracks.Count < 20 ? $"Each has all {tracks.Count} songs in random order." : "Each has 20 random songs.");
    }

    // ---------------- App icon ----------------

    private void OnLogoStyle(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<AppLogoStyle>(tag, out var style))
            Svc.Settings.Set(nameof(AppSettings.AppLogoStyle), s => s.AppLogoStyle = style);
        UpdateLogoButtons();
    }

    private void UpdateLogoButtons()
    {
        var style = Svc.Settings.Current.AppLogoStyle ?? Branding.DefaultLogo;
        LogoRings.IsChecked = style == AppLogoStyle.Rings;
        LogoSimple.IsChecked = style == AppLogoStyle.Simple;
        LogoImage.IsChecked = style == AppLogoStyle.Image;
    }
}

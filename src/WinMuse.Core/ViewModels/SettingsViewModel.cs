using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;
using WinMuse.Services.Audio;
using Color = Windows.UI.Color;

namespace WinMuse.ViewModels;

/// <summary>
/// The Settings page: every option as a bindable property that reads and writes the saved settings, which cards are
/// shown for the current choices, and the status texts. Changing an option here applies it at once (the player, the
/// metadata fetcher), as the page did before. Call <see cref="Attach"/> while the page is shown and
/// <see cref="Detach"/> when it is left.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly LibraryService _library;
    private readonly PlaybackService _player;
    private readonly AutoFixService _autoFix;

    public SettingsViewModel(SettingsService settings, LibraryService library, PlaybackService player, AutoFixService autoFix)
    {
        _settings = settings; _library = library; _player = player; _autoFix = autoFix;
        RefreshFolders();
    }

    private AppSettings S => _settings.Current;

    /// <summary>Sets a saved option when it changed, then says which properties to read again.</summary>
    private void Change<T>(T current, T value, string setting, Action<AppSettings> apply, params string[] alsoChanged)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        _settings.Set(setting, apply);
        foreach (var name in alsoChanged) OnPropertyChanged(name);
    }

    // ---------------- Attach / refresh ----------------

    private bool _attached;

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _settings.Changed += OnSettingChanged;
        _library.Changed += OnLibraryChanged;
        _library.ScanProgressChanged += OnScanProgress;
        _autoFix.Progress += OnFetchProgress;
        Refresh();
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _settings.Changed -= OnSettingChanged;
        _library.Changed -= OnLibraryChanged;
        _library.ScanProgressChanged -= OnScanProgress;
        _autoFix.Progress -= OnFetchProgress;
    }

    /// <summary>Reads everything again (the page is shown, or an option changed somewhere else).</summary>
    public void Refresh()
    {
        Devices = AudioEngine.GetDevices();
        RefreshFolders();
        OnFetchProgress(0, _autoFix.IsRunning ? 1 : 0, null);
        OnPropertyChanged(string.Empty);
    }

    // Options can also change elsewhere (Home's appearance panel, the player bar): show what is saved now.
    private void OnSettingChanged(string name)
    {
        if (name == nameof(AppSettings.MusicFolders)) RefreshFolders();
        OnPropertyChanged(string.Empty);
    }

    private void OnLibraryChanged() { OnPropertyChanged(nameof(ScanStatus)); OnPropertyChanged(nameof(RemovedText)); OnPropertyChanged(nameof(CanRestore)); OnPropertyChanged(nameof(AboutDescription)); }
    private void OnScanProgress(ScanProgress _) => OnLibraryChanged();

    // ---------------- Library ----------------

    public ObservableCollection<string> Folders { get; } = new();
    public bool HasNoFolders => Folders.Count == 0;

    private void RefreshFolders()
    {
        var folders = S.MusicFolders.ToList();
        if (!CollectionSync.Sync(Folders, folders, f => f.ToLowerInvariant()))
        {
            Folders.Clear();
            foreach (var f in folders) Folders.Add(f);
        }
        OnPropertyChanged(nameof(HasNoFolders));
    }

    public async Task AddFolderAsync(string folder)
    {
        var scan = _library.AddFolderAsync(folder);
        RefreshFolders();
        await scan;
    }

    public async Task RemoveFolderAsync(string folder)
    {
        var scan = _library.RemoveFolderAsync(folder);
        RefreshFolders();
        await scan;
    }

    public Task ScanAsync() => _library.ScanAsync();

    public async Task RestoreRemovedAsync()
    {
        await _library.RestoreRemovedAsync();
        OnLibraryChanged();
    }

    public string ScanStatus => _library.IsScanning ? "Scanning…" : Format.Count(_library.Tracks.Count, "song", "songs");
    public string RemovedText => _library.ExcludedCount == 0 ? "None" : Format.Count(_library.ExcludedCount, "song", "songs");
    public bool CanRestore => _library.ExcludedCount > 0;

    // ---------------- Appearance ----------------

    public AppLogoStyle LogoStyle
    {
        get => S.AppLogoStyle ?? Branding.DefaultLogo;
        // Always says the three choices changed: a toggle button the user just clicked off must be shown on again.
        set
        {
            if (value != LogoStyle) _settings.Set(nameof(AppSettings.AppLogoStyle), s => s.AppLogoStyle = value);
            OnPropertyChanged(nameof(IsLogoRings)); OnPropertyChanged(nameof(IsLogoSimple)); OnPropertyChanged(nameof(IsLogoImage));
        }
    }
    public bool IsLogoRings => LogoStyle == AppLogoStyle.Rings;
    public bool IsLogoSimple => LogoStyle == AppLogoStyle.Simple;
    public bool IsLogoImage => LogoStyle == AppLogoStyle.Image;

    public int ThemeIndex
    {
        get => (int)S.Theme;
        set => Change(S.Theme, (AppTheme)value, nameof(AppSettings.Theme), s => s.Theme = (AppTheme)value,
            nameof(IsBackgroundAvailable), nameof(BackgroundDescription), nameof(ShowIntensity), nameof(ShowGradientColors), nameof(ShowImageOptions));
    }

    public int AccentSourceIndex
    {
        get => (int)S.AccentSource;
        set => Change(S.AccentSource, (AccentSource)value, nameof(AppSettings.AccentSource), s => s.AccentSource = (AccentSource)value, nameof(ShowCustomAccent));
    }

    public bool ShowCustomAccent => S.AccentSource == AccentSource.Custom;

    private static readonly Color Purple = Color.FromArgb(255, 0x80, 0x00, 0x80);
    public Color CustomAccent => ColorKit.Parse(S.CustomAccent, Purple);

    /// <summary>A color picked from the swatches or the color picker; switches the accent to Custom.</summary>
    public void SetCustomAccent(Color c)
    {
        _settings.Set(nameof(AppSettings.CustomAccent), s => { s.CustomAccent = ColorKit.ToHex(c); s.AccentSource = AccentSource.Custom; });
        OnPropertyChanged(nameof(AccentSourceIndex));
        OnPropertyChanged(nameof(ShowCustomAccent));
    }

    /// <summary>The swatches offered above the color picker, with the names screen readers say for them.</summary>
    public static IReadOnlyList<(string Name, Color Color)> AccentSwatches { get; } = new[]
    {
        ("Violet", "#FF8B5CF6"), ("Indigo", "#FF6366F1"), ("Sky blue", "#FF0EA5E9"), ("Teal", "#FF14B8A6"), ("Green", "#FF22C55E"),
        ("Amber", "#FFF59E0B"), ("Orange", "#FFF97316"), ("Red", "#FFEF4444"), ("Pink", "#FFEC4899"), ("Purple", "#FFA855F7"),
    }.Select(s => (s.Item1, ColorKit.Parse(s.Item2, Purple))).ToArray();

    // ---------------- Background ----------------

    private bool Oled => S.Theme == AppTheme.Oled;
    /// <summary>What is behind the app: always nothing with the OLED theme (true black).</summary>
    private BackgroundMode EffectiveBackground => Oled ? BackgroundMode.None : S.BackgroundMode;

    public bool IsBackgroundAvailable => !Oled;
    public string BackgroundDescription => Oled ? "Not available with the OLED theme, which keeps the background true black." : "Content displayed behind the app.";

    public int BackgroundModeIndex
    {
        get => (int)S.BackgroundMode;
        set => Change(S.BackgroundMode, (BackgroundMode)value, nameof(AppSettings.BackgroundMode), s => s.BackgroundMode = (BackgroundMode)value,
            nameof(ShowIntensity), nameof(ShowGradientColors), nameof(ShowImageOptions));
    }

    public bool ShowIntensity => EffectiveBackground is BackgroundMode.AlbumArt or BackgroundMode.Gradient;
    public bool ShowGradientColors => EffectiveBackground == BackgroundMode.Gradient;
    public bool ShowImageOptions => EffectiveBackground == BackgroundMode.Wallpaper;

    public double BackgroundIntensity
    {
        get => S.BackgroundIntensity;
        set => Change(S.BackgroundIntensity, value, nameof(AppSettings.BackgroundIntensity), s => s.BackgroundIntensity = value);
    }

    public bool AnimateBackground
    {
        get => S.AnimateBackground;
        set => Change(S.AnimateBackground, value, nameof(AppSettings.AnimateBackground), s => s.AnimateBackground = value);
    }

    public Color GradientColor1 => ColorKit.Parse(S.GradientColor1, Color.FromArgb(255, 0x6A, 0x5A, 0xCD));
    public Color GradientColor2 => ColorKit.Parse(S.GradientColor2, Color.FromArgb(255, 0xFF, 0x69, 0xB4));
    public Color GradientColor3 => ColorKit.Parse(S.GradientColor3, Color.FromArgb(255, 0x00, 0xBF, 0xFF));

    /// <summary>Sets gradient color 1, 2 or 3.</summary>
    public void SetGradientColor(int index, Color c)
    {
        var hex = ColorKit.ToHex(c);
        _settings.Set("GradientColor" + index, s =>
        {
            if (index == 1) s.GradientColor1 = hex;
            else if (index == 2) s.GradientColor2 = hex;
            else s.GradientColor3 = hex;
        });
        OnPropertyChanged("GradientColor" + index);
    }

    public string? WallpaperPath => !string.IsNullOrEmpty(S.WallpaperPath) && File.Exists(S.WallpaperPath) ? S.WallpaperPath : null;

    /// <summary>
    /// Shows <paramref name="picked"/> behind the app. It is copied into the data folder first, so it stays when the
    /// original is moved; each pick gets its own name (with one fixed name a new image looked like the one already
    /// shown, so nothing changed) and earlier copies are deleted.
    /// </summary>
    public void UseWallpaper(string picked)
    {
        var dest = Path.Combine(AppPaths.DataDir, $"wallpaper-{DateTime.UtcNow:yyyyMMddHHmmssfff}{Path.GetExtension(picked)}");
        try
        {
            File.Copy(picked, dest, true);
            foreach (var old in Directory.EnumerateFiles(AppPaths.DataDir, "wallpaper*"))
                if (!old.Equals(dest, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch { }
        }
        catch (Exception ex) { AppPaths.Log("Background image copy: " + ex.Message); dest = picked; }
        _settings.Set(nameof(AppSettings.WallpaperPath), s => s.WallpaperPath = dest);
        _settings.Set(nameof(AppSettings.BackgroundMode), s => s.BackgroundMode = BackgroundMode.Wallpaper);
    }

    public double WallpaperBlur
    {
        get => S.WallpaperBlur;
        set => Change(S.WallpaperBlur, value, nameof(AppSettings.WallpaperBlur), s => s.WallpaperBlur = value);
    }

    public int BlurStyleIndex
    {
        get => (int)S.WallpaperBlurStyle;
        set => Change(S.WallpaperBlurStyle, (BlurStyle)value, nameof(AppSettings.WallpaperBlurStyle), s => s.WallpaperBlurStyle = (BlurStyle)value,
            nameof(BlurStyleDescription));
    }

    public string BlurStyleDescription => S.WallpaperBlurStyle switch
    {
        BlurStyle.Lens => "Like an out-of-focus camera lens. Bright spots become soft circles.",
        BlurStyle.Frosted => "Like looking through frosted glass, with a fine grain.",
        BlurStyle.Motion => "Sideways streaks, as if the camera moved.",
        _ => "Smooth and even.",
    };

    public double WallpaperDim
    {
        get => S.WallpaperDim;
        set => Change(S.WallpaperDim, value, nameof(AppSettings.WallpaperDim), s => s.WallpaperDim = value);
    }

    // ---------------- Visualizer, cards, highlight ----------------

    public bool ShowVisualizer
    {
        get => S.ShowVisualizer;
        set => Change(S.ShowVisualizer, value, nameof(AppSettings.ShowVisualizer), s => s.ShowVisualizer = value);
    }

    public bool ShowBarVisualizer
    {
        get => S.ShowBarVisualizer;
        set => Change(S.ShowBarVisualizer, value, nameof(AppSettings.ShowBarVisualizer), s => s.ShowBarVisualizer = value);
    }

    public int VisualizerStyleIndex
    {
        get => (int)S.VisualizerStyle;
        set => Change(S.VisualizerStyle, (VisualizerStyle)value, nameof(AppSettings.VisualizerStyle), s => s.VisualizerStyle = (VisualizerStyle)value);
    }

    /// <summary>The Card style box's entries, in order (retired values read as the first).</summary>
    public static IReadOnlyList<ShelfCardColor> CardColorChoices { get; } = new[]
    {
        ShelfCardColor.Neutral, ShelfCardColor.Glass, ShelfCardColor.Smoke, ShelfCardColor.Slate, ShelfCardColor.Elevated, ShelfCardColor.Outline, ShelfCardColor.None,
    };

    public int CardColorIndex
    {
        get => Math.Max(0, CardColorChoices.ToList().IndexOf(S.ShelfCardColor));
        set
        {
            if (value < 0 || value >= CardColorChoices.Count) return;
            var choice = CardColorChoices[value];
            Change(S.ShelfCardColor, choice, nameof(AppSettings.ShelfCardColor), s => s.ShelfCardColor = choice);
        }
    }

    public int PlayingHighlightIndex
    {
        get => (int)S.PlayingHighlight;
        set => Change(S.PlayingHighlight, (PlayingHighlight)value, nameof(AppSettings.PlayingHighlight), s => s.PlayingHighlight = (PlayingHighlight)value);
    }

    public bool ShowQueuePanel
    {
        get => S.ShowQueuePanel;
        set => Change(S.ShowQueuePanel, value, nameof(AppSettings.ShowQueuePanel), s => s.ShowQueuePanel = value);
    }

    // ---------------- Playback ----------------

    public IReadOnlyList<AudioDevice> Devices { get; private set; } = Array.Empty<AudioDevice>();

    public AudioDevice? SelectedDevice
    {
        get => Devices.FirstOrDefault(d => d.Id == S.OutputDeviceId) ?? Devices.FirstOrDefault();
        set
        {
            if (value == null || value.Id == S.OutputDeviceId) return;
            _settings.Set(nameof(AppSettings.OutputDeviceId), s => s.OutputDeviceId = value.Id);
            _player.ReinitializeOutput();
            OnPropertyChanged(nameof(OutputDescription));
        }
    }

    public string OutputDescription => _player.OutputDescription;

    public bool EqMatchGenre
    {
        get => S.EqMatchGenre;
        set
        {
            if (value == S.EqMatchGenre) return;
            _settings.Set(nameof(AppSettings.EqMatchGenre), s => s.EqMatchGenre = value);
            _player.ApplyTrackEqualizer();
        }
    }

    public bool Gapless
    {
        get => S.Gapless;
        set
        {
            if (value == S.Gapless) return;
            _settings.Set(nameof(AppSettings.Gapless), s => s.Gapless = value);
            _player.RefreshNext();
        }
    }

    public double CrossfadeSeconds
    {
        get => S.CrossfadeSeconds;
        set
        {
            if (value == S.CrossfadeSeconds) return;
            _settings.Set(nameof(AppSettings.CrossfadeSeconds), s => s.CrossfadeSeconds = value);
            _player.ApplyAudioSettings();
            _player.RefreshNext();
            OnPropertyChanged(nameof(CrossfadeText));
        }
    }

    public string CrossfadeText => S.CrossfadeSeconds <= 0 ? "Off" : $"{S.CrossfadeSeconds:0.#} s";

    // ---------------- Metadata ----------------

    public bool AutoFixMetadata
    {
        get => S.AutoFixMetadata;
        set
        {
            if (value == S.AutoFixMetadata) return;
            _settings.Set(nameof(AppSettings.AutoFixMetadata), s => s.AutoFixMetadata = value);
            if (value) _ = _autoFix.RunPendingAsync();
        }
    }

    public bool AutoFetchLyrics
    {
        get => S.AutoFetchLyrics;
        set => Change(S.AutoFetchLyrics, value, nameof(AppSettings.AutoFetchLyrics), s => s.AutoFetchLyrics = value);
    }

    public bool AutoFetchArt
    {
        get => S.AutoFetchArt;
        set => Change(S.AutoFetchArt, value, nameof(AppSettings.AutoFetchArt), s => s.AutoFetchArt = value);
    }

    /// <summary>Songs Fetch would update, and how many of them have edits by the user (which Fetch replaces).</summary>
    public (int Pending, int Edited) FetchPlan()
    {
        var checkable = _library.Tracks.Where(t => t.IsCheckable).Select(_library.SyncState).ToList();
        return (checkable.Count(s => s != 1), checkable.Count(s => s == -1));
    }

    public bool IsFetching => _autoFix.IsRunning;
    public void StopFetch() => _autoFix.Stop();
    public Task FetchAllAsync() => _autoFix.SyncAsync();

    [ObservableProperty] public partial string FetchButtonText { get; set; } = "Fetch";
    [ObservableProperty] public partial string FetchStatus { get; set; } = "";

    /// <summary>Nothing to fetch: says so where the progress shows.</summary>
    public void ShowNothingToFetch() => FetchStatus = "All songs are complete";

    private void OnFetchProgress(int done, int total, string? current)
    {
        FetchButtonText = total > 0 ? "Stop" : "Fetch";
        FetchStatus = total > 0 ? $"{done + 1:N0} of {total:N0}" : "";
    }

    // ---------------- Your music files ----------------

    public bool AllowFileChanges
    {
        get => S.AllowFileChanges;
        set => Change(S.AllowFileChanges, value, nameof(AppSettings.AllowFileChanges), s => s.AllowFileChanges = value);
    }

    /// <summary>The Save edits to box's entries, in order.</summary>
    private static readonly MetadataWriteMode[] WriteModes = { MetadataWriteMode.LibraryOnly, MetadataWriteMode.WriteToFiles, MetadataWriteMode.Ask };

    public int WriteModeIndex
    {
        get => Math.Max(0, Array.IndexOf(WriteModes, S.WriteMode));
        set
        {
            if (value < 0 || value >= WriteModes.Length) return;
            var mode = WriteModes[value];
            Change(S.WriteMode, mode, nameof(AppSettings.WriteMode), s => s.WriteMode = mode, nameof(WriteModeDescription));
        }
    }

    public string WriteModeDescription => S.WriteMode switch
    {
        MetadataWriteMode.WriteToFiles => "Edits are written into the files' tags. Downloaded album art is embedded in the files too.",
        MetadataWriteMode.Ask => "Edit windows offer Save to library and Save to files. Automatic updates stay in your library.",
        _ => "Edits stay in your library. Your files aren't changed until you pick another option.",
    };

    public bool AllowDeleteFiles
    {
        get => S.AllowDeleteFiles;
        set => Change(S.AllowDeleteFiles, value, nameof(AppSettings.AllowDeleteFiles), s => s.AllowDeleteFiles = value);
    }

    // ---------------- API keys ----------------

    public string? AcoustIdApiKey { get => S.AcoustIdApiKey; set => Change(S.AcoustIdApiKey, value, nameof(AppSettings.AcoustIdApiKey), s => s.AcoustIdApiKey = value); }
    public string? YouTubeApiKey { get => S.YouTubeApiKey; set => Change(S.YouTubeApiKey, value, nameof(AppSettings.YouTubeApiKey), s => s.YouTubeApiKey = value); }
    public string? LastFmApiKey { get => S.LastFmApiKey; set => Change(S.LastFmApiKey, value, nameof(AppSettings.LastFmApiKey), s => s.LastFmApiKey = value); }
    public string? FanartApiKey { get => S.FanartApiKey; set => Change(S.FanartApiKey, value, nameof(AppSettings.FanartApiKey), s => s.FanartApiKey = value); }
    public string? SpotifyClientId => S.SpotifyClientId;
    public string? SpotifyClientSecret => S.SpotifyClientSecret;

    public void SetSpotify(string? clientId, string? secret) =>
        _settings.Set(nameof(AppSettings.SpotifyClientId), s => { s.SpotifyClientId = clientId; s.SpotifyClientSecret = secret; });

    // ---------------- Debug ----------------

    public int GreetingStyleIndex
    {
        get => (int)S.GreetingStyle;
        set => Change(S.GreetingStyle, (GreetingStyle)value, nameof(AppSettings.GreetingStyle), s => s.GreetingStyle = (GreetingStyle)value);
    }

    public bool MatchWholeAlbums
    {
        get => S.MatchWholeAlbums;
        set => Change(S.MatchWholeAlbums, value, nameof(AppSettings.MatchWholeAlbums), s => s.MatchWholeAlbums = value);
    }

    public bool ShowAllHomeRows
    {
        get => S.ShowAllHomeRows;
        set => Change(S.ShowAllHomeRows, value, nameof(AppSettings.ShowAllHomeRows), s => s.ShowAllHomeRows = value);
    }

    /// <summary>Ten playlists of up to 20 random songs each; false when the library is empty.</summary>
    public bool CreateTestPlaylists()
    {
        var tracks = _library.Tracks;
        if (tracks.Count == 0) return false;
        for (int i = 1; i <= 10; i++)
            _library.CreatePlaylist($"Test playlist {i}", "Created for testing.", tracks: tracks.OrderBy(_ => Random.Shared.Next()).Take(20));
        return true;
    }

    // ---------------- About ----------------

    public string AppName => Branding.Name;
    public string AboutDescription => $"{AppInfo.DisplayVersion} · FFmpeg {FFmpegLoader.Version} · {_library.Tracks.Count:N0} songs";
}

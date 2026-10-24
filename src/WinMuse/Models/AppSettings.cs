namespace WinMuse.Models;

public enum AppTheme { System, Light, Dark, Oled }
/// <summary>Animated border around the playing song in song lists.</summary>
public enum PlayingHighlight { Off, Accent, Rainbow }
/// <summary>Home greeting: a gradient card (Banner), or a glow behind the top of the page (Gradient). Debug option.</summary>
/// <summary>Home's greeting: on a card (Banner), on a glow (Gradient), or on the page itself (None, laid out like Gradient).</summary>
public enum GreetingStyle { Banner, Gradient, None }
/// <summary>Look of the cards in Home's rows (Settings > Appearance > Card style). Accent and Opposite are retired
/// (they read as Neutral) but stay so older settings files still load.</summary>
public enum ShelfCardColor { Neutral, Accent, Opposite, Glass, Smoke, Slate, Elevated, Outline, None }
/// <summary>Order of the Playlists row on Home.</summary>
public enum PlaylistSort { Alphabetical, RecentActivity, DateCreated, Custom }
public enum AccentSource { Windows, Custom, AlbumArt }
public enum BackgroundMode { None, AlbumArt, Gradient, Wallpaper }
public enum MetadataWriteMode { WriteToFiles, LibraryOnly }
/// <summary>The logo in the title bar (drawn in the accent color).</summary>
public enum AppLogoStyle { Rings, Simple, Image }
public enum RepeatMode { Off, All, One }
public enum VisualizerStyle { Bars, Mirror, Line }

/// <summary>Persisted user settings (JSON in %LocalAppData%\WinMuse\settings.json).</summary>
public sealed class AppSettings
{
    // Library
    public List<string> MusicFolders { get; set; } = new();

    // Appearance
    public AppTheme Theme { get; set; } = AppTheme.System;
    public AccentSource AccentSource { get; set; } = AccentSource.AlbumArt;
    public string CustomAccent { get; set; } = "#FF8B5CF6";
    public BackgroundMode BackgroundMode { get; set; } = BackgroundMode.AlbumArt;
    public string? WallpaperPath { get; set; }
    public double WallpaperBlur { get; set; } = 40;
    public double WallpaperDim { get; set; } = 35;
    public double BackgroundIntensity { get; set; } = 55;
    public string GradientColor1 { get; set; } = "#FF6D5DFC";
    public string GradientColor2 { get; set; } = "#FFE0569B";
    public string GradientColor3 { get; set; } = "#FF22C3E6";
    public bool AnimateBackground { get; set; } = true;
    public bool ShowVisualizer { get; set; } = true;
    /// <summary>Spectrum drawn behind the player bar.</summary>
    public bool ShowBarVisualizer { get; set; } = true;
    public VisualizerStyle VisualizerStyle { get; set; } = VisualizerStyle.Bars;
    public bool ShowQueuePanel { get; set; } = true;
    public PlayingHighlight PlayingHighlight { get; set; } = PlayingHighlight.Rainbow;
    public ShelfCardColor ShelfCardColor { get; set; } = ShelfCardColor.Neutral;
    public GreetingStyle GreetingStyle { get; set; } = GreetingStyle.Banner;

    // Favorites and Quick access
    /// <summary>The playlist that holds favorite songs ("Favorite"); created on the first star.</summary>
    public long FavoritesPlaylistId { get; set; }
    public List<string> FavoriteArtists { get; set; } = new();
    /// <summary>Items pinned to Quick access, in pin order: "song:&lt;path&gt;" or "artist:&lt;name&gt;".</summary>
    public List<string> QuickAccessPins { get; set; } = new();

    // Playlists row on Home
    public PlaylistSort HomePlaylistSort { get; set; } = PlaylistSort.RecentActivity;
    /// <summary>Custom order (playlist IDs, dragged on Home); playlists not listed go last, newest first.</summary>
    public List<long> PlaylistOrder { get; set; } = new();
    /// <summary>Last time each playlist was played, opened or changed (playlist ID → Unix seconds).</summary>
    public Dictionary<long, long> PlaylistActivity { get; set; } = new();
    /// <summary>Last time a song was credited to each artist by an edit or a fetch (lower-case name → Unix seconds).</summary>
    public Dictionary<string, long> ArtistActivity { get; set; } = new();
    /// <summary>Continue Listening on Home: where each playlist/album/artist/song was left, newest first.</summary>
    public List<Services.ContinueEntry> ContinueListening { get; set; } = new();

    // Playback
    public double Volume { get; set; } = 0.8;
    public bool Muted { get; set; }
    public bool Shuffle { get; set; }
    public RepeatMode Repeat { get; set; } = RepeatMode.Off;
    public bool Gapless { get; set; } = true;
    public double CrossfadeSeconds { get; set; }
    public string? OutputDeviceId { get; set; }
    /// <summary>Pick the equalizer preset from each song's genre (songs with their own saved EQ keep it).</summary>
    public bool EqMatchGenre { get; set; } = true;
    public string EqPreset { get; set; } = "Flat";
    public float[] EqBands { get; set; } = new float[10];
    public float EqPreamp { get; set; }

    // Metadata
    public MetadataWriteMode WriteMode { get; set; } = MetadataWriteMode.LibraryOnly;
    public bool AutoFixMetadata { get; set; } = true;
    public bool AutoFetchArt { get; set; } = true;
    public bool AutoFetchLyrics { get; set; } = true;
    public bool EmbedFetchedArt { get; set; } = true;
    public string? AcoustIdApiKey { get; set; }
    public string? YouTubeApiKey { get; set; }
    public string? LastFmApiKey { get; set; }
    public string? FanartApiKey { get; set; }
    public string? SpotifyClientId { get; set; }
    public string? SpotifyClientSecret { get; set; }
    /// <summary>The first-launch API key dialog was shown (saved or skipped).</summary>
    public bool ApiKeysPrompted { get; set; }

    /// <summary>Settings format: older files are upgraded once to new defaults (see SettingsService).</summary>
    public int SettingsVersion { get; set; }

    // Session
    public List<string> LastQueue { get; set; } = new();
    public int LastQueueIndex { get; set; } = -1;
    /// <summary>What the restored queue was started from (Continue Listening keeps following it after a restart).</summary>
    public PlayContext? LastContext { get; set; }

    /// <summary>Picked in Settings; null: the build's default (Helpers/Branding).</summary>
    public AppLogoStyle? AppLogoStyle { get; set; }
    public double LastPositionSeconds { get; set; }
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 820;

    // Download (mdown.exe; the secret file is kept in the data folder)
    public string? DownloadFolder { get; set; }
}

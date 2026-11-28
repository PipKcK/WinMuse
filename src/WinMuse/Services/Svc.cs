namespace WinMuse.Services;

/// <summary>App-wide service singletons.</summary>
public static class Svc
{
    public static SettingsService Settings { get; private set; } = null!;
    public static ArtworkService Art { get; private set; } = null!;
    public static TagService Tags { get; private set; } = null!;
    public static LibraryService Library { get; private set; } = null!;
    public static PlaybackService Player { get; private set; } = null!;
    public static ThemeService Theme { get; private set; } = null!;
    public static OnlineMetadataService Online { get; private set; } = null!;
    public static FingerprintService Fingerprint { get; private set; } = null!;
    public static LyricsService Lyrics { get; private set; } = null!;
    public static MetadataEditService Metadata { get; private set; } = null!;
    public static SmtcService Smtc { get; private set; } = null!;
    public static TaskbarService Taskbar { get; private set; } = null!;
    public static DownloadService Download { get; private set; } = null!;
    public static AutoFixService AutoFix { get; private set; } = null!;
    public static FavoritesService Favorites { get; private set; } = null!;
    public static ContinueListeningService Continue { get; private set; } = null!;

    public static void Create()
    {
        Settings = new SettingsService();
        Art = new ArtworkService();
        Tags = new TagService(Art);
        Library = new LibraryService(Tags, Art, Settings);
        Player = new PlaybackService(Settings, Library);
        Theme = new ThemeService(Settings);
        Online = new OnlineMetadataService();
        Fingerprint = new FingerprintService();
        Lyrics = new LyricsService(Online, Settings, Library);
        Metadata = new MetadataEditService(Tags, Art, Library, Settings, Online, Fingerprint);
        Smtc = new SmtcService(Player);
        Taskbar = new TaskbarService(Player);
        AutoFix = new AutoFixService(Library, Metadata, Lyrics, Settings);
        Download = new DownloadService(Settings, Library, AutoFix);
        Favorites = new FavoritesService(Settings, Library);
        Continue = new ContinueListeningService(Settings, Library, Player);
    }
}

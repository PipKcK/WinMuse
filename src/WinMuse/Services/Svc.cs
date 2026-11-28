using WinMuse.Models;

namespace WinMuse.Services;

/// <summary>
/// The composition root: builds every service once at startup and hands each one its dependencies through its
/// constructor. Services live in WinMuse.Core and can't see this class, so they never reach for a global. Only views
/// and Windows integration read it, because WinUI creates pages and controls itself and can't pass them arguments;
/// pages pass what they need on to their view models.
/// </summary>
public static class Svc
{
    /// <summary>The UI thread, as services and view models are given it.</summary>
    public static IUiDispatcher Ui { get; private set; } = null!;
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

    public static void Create(IUiDispatcher ui, INotifier notifier)
    {
        Ui = ui;
        Settings = new SettingsService(notifier);
        Art = new ArtworkService();
        Tags = new TagService(Art);
        Library = new LibraryService(new LibraryDatabase(), Tags, Art, Settings, ui);
        Player = new PlaybackService(Settings, Library, ui);
        Theme = new ThemeService(Settings);
        Online = new OnlineMetadataService();
        Fingerprint = new FingerprintService();
        Lyrics = new LyricsService(Online, Settings, Library);
        Metadata = new MetadataEditService(Tags, Art, Library, Settings, Online, Fingerprint, Player, ui);
        Smtc = new SmtcService(Player);
        Taskbar = new TaskbarService(Player);
        AutoFix = new AutoFixService(Library, Metadata, Lyrics, Settings, Player, ui, notifier);
        Download = new DownloadService(Settings, Library, AutoFix);
        Favorites = new FavoritesService(Settings, Library, notifier);
        Continue = new ContinueListeningService(Settings, Library, Player);
        Track.CheckedChanged += SelectionService.OnTrackChecked;
    }
}

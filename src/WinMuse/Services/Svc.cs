using Microsoft.UI.Xaml.Controls;

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

/// <summary>In-app notifications rendered by the main window as InfoBars.</summary>
public static class Notifier
{
    /// <summary>A button on a notification ("Open log", "Restart").</summary>
    public sealed record ToastAction(string Label, Action Run);

    public static event Action<string, string?, InfoBarSeverity, ToastAction?>? Raised;

    public static void Show(string title, string? message = null, InfoBarSeverity severity = InfoBarSeverity.Informational, ToastAction? action = null) =>
        App.UI.TryEnqueue(() => Raised?.Invoke(title, message, severity, action));

    public static void Error(string title, string? message = null) => Show(title, message, InfoBarSeverity.Error);

    /// <summary>An error from an exception: the details go to the log, the notification says what happened in plain words.</summary>
    public static void Error(string title, Exception ex)
    {
        Helpers.AppPaths.Log($"{title}: {ex}");
        Show(title, Helpers.ErrorText.Describe(ex), InfoBarSeverity.Error, OpenLog);
    }

    /// <summary>Opens the log file in the default text editor.</summary>
    public static readonly ToastAction OpenLog = new("Open log", () =>
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Helpers.AppPaths.LogPath) { UseShellExecute = true }); }
        catch (Exception e) { Helpers.AppPaths.Log("Open log: " + e.Message); }
    });
    public static void Success(string title, string? message = null) => Show(title, message, InfoBarSeverity.Success);
}

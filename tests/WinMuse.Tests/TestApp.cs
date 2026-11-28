using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Tests;

/// <summary>Runs "UI thread" work at once, on the calling thread. Timers never tick on their own.</summary>
internal sealed class InlineDispatcher : IUiDispatcher
{
    public bool HasThreadAccess => true;
    public bool TryEnqueue(Action action) { action(); return true; }
    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new ManualTimer(tick);

    internal sealed class ManualTimer(Action tick) : IUiTimer
    {
        public bool IsRunning { get; private set; }
        public void Start() => IsRunning = true;
        public void Stop() => IsRunning = false;
        public void Tick() => tick();
    }
}

/// <summary>Keeps the notifications instead of showing them.</summary>
internal sealed class RecordingNotifier : INotifier
{
    public List<(string Title, string? Message, NoticeSeverity Severity)> Shown { get; } = new();
    public void Show(string title, string? message = null, NoticeSeverity severity = NoticeSeverity.Informational, NoticeAction? action = null) =>
        Shown.Add((title, message, severity));
}

/// <summary>
/// The app's services, built the way the app builds them (constructor injection, see Svc), over a fresh settings file
/// and library database of their own. The audio output is never started.
/// </summary>
internal sealed class TestApp
{
    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "WinMuseUnitTests", "app-" + Guid.NewGuid().ToString("N"));
    public InlineDispatcher Ui { get; } = new();
    public RecordingNotifier Notifier { get; } = new();
    public SettingsService Settings { get; }
    public LibraryDatabase Database { get; }
    public LibraryService Library { get; }
    public PlaybackService Player { get; }
    public LyricsService Lyrics { get; }
    public MetadataEditService Metadata { get; }
    public AutoFixService AutoFix { get; }
    public FavoritesService Favorites { get; }
    public ContinueListeningService Continue { get; }

    public TestApp()
    {
        Directory.CreateDirectory(Folder);
        Settings = new SettingsService(Notifier, Folder);
        Settings.Current.AutoFixMetadata = false; // no online lookups from tests
        Settings.Current.AutoFetchLyrics = false;
        var art = new ArtworkService();
        var tags = new TagService(art);
        Database = new LibraryDatabase(Path.Combine(Folder, "library.db"));
        Library = new LibraryService(Database, tags, art, Settings, Ui);
        Player = new PlaybackService(Settings, Library, Ui);
        var online = new OnlineMetadataService();
        Lyrics = new LyricsService(online, Settings, Library);
        Metadata = new MetadataEditService(tags, art, Library, Settings, online, new FingerprintService(), Player, Ui);
        AutoFix = new AutoFixService(Library, Metadata, Lyrics, Settings, Player, Ui, Notifier);
        Favorites = new FavoritesService(Settings, Library, Notifier);
        Continue = new ContinueListeningService(Settings, Library, Player);
    }

    /// <summary>Puts songs in the database and loads the library from it, as at startup.</summary>
    public async Task<List<Track>> LoadSongsAsync(params Track[] songs)
    {
        Database.UpsertTracks(songs);
        await Library.LoadAsync();
        return Library.Tracks.ToList();
    }

    private static int _counter;

    /// <summary>A song with a unique path (the file needn't exist).</summary>
    public static Track Song(string title, string artist = "Artist", string album = "Album", int plays = 0, long added = 1_700_000_000) => new()
    {
        Path = $@"C:\test-library\{Interlocked.Increment(ref _counter)}-{title}.mp3", Title = title, Artist = artist, Album = album,
        DurationMs = 180_000, PlayCount = plays, AddedUtc = added,
    };
}

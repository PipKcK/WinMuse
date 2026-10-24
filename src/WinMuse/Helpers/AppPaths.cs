namespace WinMuse.Helpers;

public static class AppPaths
{
    /// <summary>
    /// %LocalAppData%\WinMuse, or the folder in the WINMUSE_DATA_DIR environment variable when set (used for testing,
    /// so test runs never touch the real library, settings and playlists).
    /// </summary>
    public static string DataDir { get; } = Ensure(Environment.GetEnvironmentVariable("WINMUSE_DATA_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinMuse"));
    public static string ArtDir { get; } = Ensure(Path.Combine(DataDir, "Artwork"));
    public static string LyricsDir { get; } = Ensure(Path.Combine(DataDir, "Lyrics"));
    public static string DatabasePath => Path.Combine(DataDir, "library.db");
    public static string SettingsPath => Path.Combine(DataDir, "settings.json");
    public static string LogPath => Path.Combine(DataDir, "log.txt");
    public static string FFmpegDir => Path.Combine(AppContext.BaseDirectory, "ffmpeg");
    public static string AssetsDir => Path.Combine(AppContext.BaseDirectory, "Assets");

    public static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".mp2", ".mp1", ".flac", ".m4a", ".m4b", ".mp4", ".aac", ".alac", ".ogg", ".oga", ".opus", ".spx",
        ".wav", ".wave", ".w64", ".rf64", ".aif", ".aiff", ".aifc", ".wma", ".ape", ".wv", ".mpc", ".tta", ".tak",
        ".dsf", ".dff", ".ac3", ".eac3", ".dts", ".mka", ".webm", ".weba", ".amr", ".au", ".caf", ".shn", ".ra", ".3gp", ".mlp", ".thd",
    };

    public static bool IsAudio(string path) => AudioExtensions.Contains(Path.GetExtension(path));

    private static string Ensure(string dir) { Directory.CreateDirectory(dir); return dir; }

    public static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}"); } catch { }
    }
}

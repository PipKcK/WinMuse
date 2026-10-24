using System.Runtime.CompilerServices;
using WinMuse.Models;

namespace WinMuse.Tests;

internal static class TestSupport
{
    /// <summary>Every run gets its own data folder, so the app's real library and settings are never touched.</summary>
    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WinMuseUnitTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("WINMUSE_DATA_DIR", dir);
    }

    /// <summary>Folder of real songs for decoder tests (MUSIC_TEST_DIR), or null to skip them.</summary>
    public static string? MusicDir => Environment.GetEnvironmentVariable("MUSIC_TEST_DIR") is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    public static List<string> Songs(int max = int.MaxValue) => MusicDir == null ? new()
        : Directory.EnumerateFiles(MusicDir, "*", SearchOption.AllDirectories).Where(Helpers.AppPaths.IsAudio).OrderBy(p => p).Take(max).ToList();

    public static Track T(string? artist, string? albumArtist = null, string title = "t") => new() { Artist = artist, AlbumArtist = albumArtist, Title = title, Path = $@"C:\x\{Guid.NewGuid():N}.mp3" };
}

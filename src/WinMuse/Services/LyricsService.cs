using System.Security.Cryptography;
using System.Text;
using WinMuse.Helpers;
using WinMuse.Models;

namespace WinMuse.Services;

/// <summary>
/// Finds, downloads and stores lyrics. Lyrics Music saves (downloaded or added by you) go to the app's data folder, so
/// music folders are never changed; an .lrc file already next to a song is still read.
/// </summary>
public sealed class LyricsService
{
    private readonly OnlineMetadataService _online;
    private readonly SettingsService _settings;
    private readonly LibraryService _library;

    public LyricsService(OnlineMetadataService online, SettingsService settings, LibraryService library)
    {
        _online = online; _settings = settings; _library = library;
    }

    /// <summary>Where Music keeps a song's lyrics: %LocalAppData%\WinMuse\Lyrics, named after the song's path.</summary>
    public static string AppLyricsPath(string songPath) =>
        Path.Combine(AppPaths.LyricsDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(songPath.ToLowerInvariant()))) + ".lrc");

    private static string AppDataLrc(Track t) => AppLyricsPath(t.Path);

    /// <summary>
    /// The song's lyrics: those Music saved (the latest you downloaded or added) first, then an .lrc file next to the
    /// song, then lyrics embedded in the file.
    /// </summary>
    public LyricsResult? GetLocal(Track t)
    {
        try
        {
            var app = AppDataLrc(t);
            if (File.Exists(app)) return LrcParser.Parse(File.ReadAllText(app), "Saved in your library");
            var side = Path.ChangeExtension(t.Path, ".lrc");
            if (File.Exists(side)) return LrcParser.Parse(File.ReadAllText(side), "Lyrics file");
            if (!string.IsNullOrWhiteSpace(t.Lyrics)) return LrcParser.Parse(t.Lyrics!, "Embedded");
        }
        catch (Exception ex) { AppPaths.Log("Lyrics read failed: " + ex.Message); }
        return null;
    }

    public async Task<LyricsResult?> GetAsync(Track t, bool allowOnline, CancellationToken ct = default)
    {
        var local = await Task.Run(() => GetLocal(t), ct);
        if (local != null) return local;
        if (!allowOnline || !_settings.Current.AutoFetchLyrics) return null;
        return await DownloadAsync(t, ct);
    }

    /// <summary>
    /// Downloads lyrics from LRCLIB and stores them locally. Instrumentals (the title or genre says so, or LRCLIB lists
    /// the song as one) are marked <see cref="ItemState.Instrumental"/>: they have no lyrics, so they count as complete
    /// and Sync stops looking them up.
    /// </summary>
    public async Task<LyricsResult?> DownloadAsync(Track t, CancellationToken ct = default)
    {
        if (ItemState.LooksInstrumental(t))
        {
            MarkNoLyrics(t, instrumental: true);
            return null;
        }
        var (text, instrumental) = await _online.FetchLyricsAsync(t.Artist, t.DisplayTitle, t.Album, (int)t.Duration.TotalSeconds, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            MarkNoLyrics(t, instrumental);
            return null;
        }
        Save(t, text);
        t.LyricsState = ItemState.Downloaded;
        _library.SaveStates(new[] { t });
        return LrcParser.Parse(text, "Downloaded");
    }

    /// <summary>
    /// Records a lookup that found no lyrics: Instrumental when the song has none, otherwise Not found. Only replaces a
    /// "nothing yet" state (lyrics from the file, downloaded earlier or edited by you are kept).
    /// </summary>
    private void MarkNoLyrics(Track t, bool instrumental)
    {
        if (t.LyricsState is not (ItemState.Missing or ItemState.NotFound or ItemState.Instrumental)) return;
        var state = instrumental ? ItemState.Instrumental : ItemState.NotFound;
        if (t.LyricsState == state) return;
        t.LyricsState = state;
        _library.SaveStates(new[] { t });
    }

    /// <summary>Saves lyrics in the app's data folder (never next to the song: music folders aren't changed).</summary>
    public void Save(Track t, string text)
    {
        try { File.WriteAllText(AppDataLrc(t), text, Encoding.UTF8); }
        catch (Exception ex) { AppPaths.Log("Lyrics save failed: " + ex.Message); }
    }
}

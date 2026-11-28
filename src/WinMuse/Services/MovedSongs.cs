using WinMuse.Models;

namespace WinMuse.Services;

/// <summary>
/// Recognizes songs whose file was moved or renamed since the last scan, so they keep their plays, Favorites, playlist
/// places and edits instead of coming back as new songs. A match must be the only possible one on both sides: when two
/// missing songs, or two new files, fit equally well, none of them is matched, since a new song is safer than another
/// song's history.
/// </summary>
public static class MovedSongs
{
    /// <summary>Songs whose length differs by at most this much can still be the same file after its tags were rewritten.</summary>
    public const int LengthToleranceMs = 1000;

    /// <summary>An audio file in a library folder that the library doesn't know yet.</summary>
    public readonly record struct NewFile(string Path, long Size, long ModifiedUtc);

    /// <summary>
    /// Files that are unchanged: moved (same name, size and modified date), then renamed, moved or both (same size and
    /// modified date; moving, renaming and copying a file all keep its modified date). New file path to missing song.
    /// </summary>
    public static Dictionary<string, Track> MatchUnchanged(IReadOnlyCollection<Track> missing, IReadOnlyCollection<NewFile> files)
    {
        var result = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        var songs = missing.Where(t => t.FileSize > 0 && t.ModifiedUtc > 0).ToList();
        var stamped = files.Where(f => f.Size > 0 && f.ModifiedUtc > 0).ToList();

        PairUnique(songs, stamped, t => (FileName(t.Path), t.FileSize, t.ModifiedUtc), f => (FileName(f.Path), f.Size, f.ModifiedUtc), result);
        var claimed = result.Values.ToHashSet();
        PairUnique(songs.Where(t => !claimed.Contains(t)).ToList(), stamped.Where(f => !result.ContainsKey(f.Path)).ToList(),
            t => (t.FileSize, t.ModifiedUtc), f => (f.Size, f.ModifiedUtc), result);
        return result;
    }

    /// <summary>
    /// Files that moved or were renamed and changed too, as when a tag editor rewrites the tags and renames the file in
    /// one step. The tags may have changed, so the audio must agree (codec, sample rate, channels, and a length within
    /// <see cref="LengthToleranceMs"/>) and so must the title, or the file name when the old song had no title.
    /// <paramref name="read"/> are the new files as just read from disk. New file path to missing song.
    /// </summary>
    public static Dictionary<string, Track> MatchChanged(IReadOnlyCollection<Track> missing, IReadOnlyCollection<Track> read)
    {
        var result = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        var songs = missing.Where(t => t.DurationMs > 0).ToList();
        var files = read.Where(t => t.DurationMs > 0).ToList();
        if (songs.Count == 0 || files.Count == 0) return result;

        var fits = files.ToDictionary(f => f, f => songs.Where(m => SameSong(m, f)).ToList());
        foreach (var (file, options) in fits)
        {
            if (options.Count != 1) continue;
            var song = options[0];
            if (fits.Count(other => other.Value.Contains(song)) != 1) continue; // another new file fits this song too
            result[file.Path] = song;
        }
        return result;
    }

    private static bool SameSong(Track old, Track now)
    {
        if (!string.Equals(old.Codec, now.Codec, StringComparison.OrdinalIgnoreCase)) return false;
        if (old.SampleRate != now.SampleRate || old.Channels != now.Channels) return false;
        if (Math.Abs(old.DurationMs - now.DurationMs) > LengthToleranceMs) return false;
        var title = Matching.Normalize(old.Title);
        if (title.Length > 0) return title == Matching.Normalize(now.Title);
        return Matching.Normalize(Path.GetFileNameWithoutExtension(old.Path)) == Matching.Normalize(Path.GetFileNameWithoutExtension(now.Path));
    }

    private static string FileName(string path) => Path.GetFileName(path).ToLowerInvariant();

    /// <summary>Pairs songs and files that share a key, where the key is the only one of its kind on both sides.</summary>
    private static void PairUnique<TKey>(List<Track> songs, List<NewFile> files, Func<Track, TKey> songKey, Func<NewFile, TKey> fileKey,
        Dictionary<string, Track> result) where TKey : notnull
    {
        var bySong = songs.GroupBy(songKey).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.First());
        var fileCounts = files.GroupBy(fileKey).ToDictionary(g => g.Key, g => g.Count());
        foreach (var f in files)
        {
            var key = fileKey(f);
            if (fileCounts[key] == 1 && bySong.TryGetValue(key, out var song)) result[f.Path] = song;
        }
    }
}

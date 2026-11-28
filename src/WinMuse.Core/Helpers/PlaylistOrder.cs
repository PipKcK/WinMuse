namespace WinMuse.Helpers;

/// <summary>Playlist ordering rules that don't depend on the UI.</summary>
public static class PlaylistOrder
{
    /// <summary>
    /// The playlist's new song list with its hidden (missing) songs put back where they were: each right after the song
    /// it followed before, or at the start when nothing before it is still in the list.
    /// </summary>
    /// <param name="saved">The song ids saved for the playlist before the change, hidden ones included.</param>
    /// <param name="shown">The song ids now shown (after the user's change), without hidden ones.</param>
    /// <param name="missing">Ids of songs whose files are missing.</param>
    public static List<long> KeepMissingPlaces(IReadOnlyList<long> saved, IReadOnlyList<long> shown, IReadOnlySet<long> missing)
    {
        var result = shown.ToList();
        var kept = shown.ToHashSet();
        long? anchor = null;
        int run = 0; // missing songs already placed after the current anchor
        foreach (var id in saved)
        {
            if (missing.Contains(id))
            {
                int at = (anchor is long a ? result.IndexOf(a) + 1 : 0) + run;
                result.Insert(Math.Min(at, result.Count), id);
                run++;
            }
            else if (kept.Contains(id)) { anchor = id; run = 0; }
        }
        return result;
    }
}

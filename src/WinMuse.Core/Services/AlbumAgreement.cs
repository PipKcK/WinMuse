using WinMuse.Models;

namespace WinMuse.Services;

/// <summary>One song of an album being matched: what online matching chose for it and what else it found.</summary>
/// <param name="Matched">False for songs that weren't matched in this run (already in the library, or not found):
/// they vote with the album they have now.</param>
public sealed record AlbumMember(Track Song, MetadataCandidate? Match, IReadOnlyList<MetadataCandidate> Candidates, bool Matched);

/// <summary>
/// The album the songs in <see cref="Songs"/> get. <see cref="Releases"/>: songs moved onto the chosen release, with the
/// result that puts them there (their track and disc numbers come from it). Songs not listed keep their own album.
/// </summary>
public sealed record AlbumDecision(string Album, string? AlbumArtist, int Year, string? Genre,
    IReadOnlyList<Track> Songs, IReadOnlyDictionary<Track, MetadataCandidate> Releases);

/// <summary>
/// Albums are decided as a whole. Matching looks songs up one at a time, and one recording is usually on several
/// releases (the album, a single, a compilation, a reissue), so songs that shared an album label could each land on a
/// different one, splitting the album. Here the release most of them can belong to wins, and its name, album artist,
/// year and genre go to every song on it.
/// </summary>
public static class AlbumAgreement
{
    /// <param name="labelBefore">The album label the songs shared before matching.</param>
    public static AlbumDecision? Decide(IReadOnlyList<AlbumMember> members, string? labelBefore)
    {
        if (members.Count < 2) return null;

        // Releases each song can belong to: its match's, and those of other results for the same recording.
        var options = members.ToDictionary(m => m, m => Releases(m).ToList());
        var coverage = options.Values.SelectMany(o => o.Select(c => Key(c.Album)).Distinct()).Where(k => k.Length > 0)
            .GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        if (coverage.Count == 0) return null;

        string before = Key(labelBefore);
        var (winner, count) = coverage
            .OrderByDescending(kv => kv.Value)
            .ThenByDescending(kv => members.Count(m => Key(m.Song.Album) == kv.Key)) // what songs already say
            .ThenByDescending(kv => kv.Key == before)
            .ThenBy(kv => EarliestYear(options.Values.SelectMany(o => o).Where(c => Key(c.Album) == kv.Key)))
            .Select(kv => (kv.Key, kv.Value)).First();
        // Most songs must be able to belong to it; otherwise these are songs from different albums, left as matched.
        if (count < 2 || count * 2 <= members.Count) return null;

        // Songs in track order, so ties (genre, spelling) go the same way every time.
        var ordered = members.OrderBy(m => m.Song.DiscNumber).ThenBy(m => m.Song.TrackNumber == 0 ? int.MaxValue : m.Song.TrackNumber).ToList();
        var songs = new List<Track>();
        var releases = new Dictionary<Track, MetadataCandidate>();
        var rest = new List<AlbumMember>();
        foreach (var m in ordered)
        {
            if (Key(m.Song.Album) == winner) songs.Add(m.Song);
            else if (options[m].Where(c => Key(c.Album) == winner).OrderByDescending(c => c.Score).FirstOrDefault() is { } on)
            {
                songs.Add(m.Song); releases[m.Song] = on;
            }
            else rest.Add(m);
        }
        // The album's artists, from the songs already on it.
        var albumArtists = songs.Select(t => releases.TryGetValue(t, out var c) ? c.AlbumArtist ?? c.Artist : t.AlbumArtist ?? t.Artist)
            .Where(a => !string.IsNullOrWhiteSpace(a) && a != "Various Artists").Distinct().ToList();
        foreach (var m in rest)
        {
            // No result puts it on the album, but nothing says it belongs elsewhere: its label is still the shared one,
            // it names the same album ("Album (Deluxe)"), or it's by the album's artist and matching found it on a
            // single or a compilation (it shared the label and the folder with the album's songs before matching).
            var now = Key(m.Song.Album);
            if (now.Length == 0 || now == before || SameName(now, winner) || albumArtists.Any(a => Matching.ArtistMatches(m.Song.Artist, a)))
                songs.Add(m.Song);
        }
        songs = ordered.Select(m => m.Song).Where(songs.Contains).ToList();
        // The release's name as most sources spell it; then the album artist, year and genre most of its songs have.
        var spellings = options.Values.SelectMany(o => o).Where(c => Key(c.Album) == winner).Select(c => c.Album!.Trim());
        string album = Mode(spellings)!;

        string? AlbumArtistOf(Track t) => releases.TryGetValue(t, out var c) ? c.AlbumArtist ?? c.Artist
            : Key(t.Album) == winner ? t.AlbumArtist : null;
        var albumArtist = Mode(songs.Select(AlbumArtistOf).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!.Trim()));

        int YearOf(Track t) => releases.TryGetValue(t, out var c) && c.Year > 0 ? c.Year : t.Year;
        var years = songs.Select(YearOf).Where(y => y > 0).GroupBy(y => y).ToList();
        int year = years.Count == 0 ? 0 : years.OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First().Key;

        var genre = Mode(songs.Select(t => t.Genre).Where(Genres.IsKnown).Select(g => g!.Trim()));
        return new AlbumDecision(album, albumArtist, year, genre, songs, releases);
    }

    /// <summary>The match's release and every other result for the same recording (same ID, or same title, artist and length).</summary>
    private static IEnumerable<MetadataCandidate> Releases(AlbumMember m)
    {
        if (!m.Matched || m.Match == null)
        {
            if (!string.IsNullOrWhiteSpace(m.Song.Album))
                yield return new MetadataCandidate { Album = m.Song.Album, AlbumArtist = m.Song.AlbumArtist, Year = m.Song.Year, Score = 100 };
            yield break;
        }
        var match = m.Match;
        if (!string.IsNullOrWhiteSpace(match.Album)) yield return match;
        foreach (var c in m.Candidates)
            if (c != match && !string.IsNullOrWhiteSpace(c.Album) && SameRecording(c, match) && c.Score >= Math.Min(match.Score, 90) - 25)
                yield return c;
    }

    private static bool SameRecording(MetadataCandidate c, MetadataCandidate match)
    {
        if (c.RecordingId != null && c.RecordingId == match.RecordingId) return true;
        if (!Matching.Similar(Matching.StripFeat(c.Title), Matching.StripFeat(match.Title)) || !Matching.ArtistMatches(c.Artist, match.Artist)) return false;
        return c.DurationMs <= 0 || match.DurationMs <= 0 || Math.Abs(c.DurationMs - match.DurationMs) <= 5000;
    }

    /// <summary>One name extends the other: "Album" and "Album (Deluxe Edition)".</summary>
    private static bool SameName(string a, string b) => Math.Min(a.Length, b.Length) >= 4 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal));

    /// <summary>Release names compared without case, punctuation or "&amp;" versus "and".</summary>
    public static string Key(string? album) => Matching.Normalize(album);

    private static int EarliestYear(IEnumerable<MetadataCandidate> cs) => cs.Where(c => c.Year > 0).Select(c => c.Year).DefaultIfEmpty(9999).Min();

    /// <summary>The most common value; ties go to the one seen first.</summary>
    private static string? Mode(IEnumerable<string> values) =>
        values.Select((v, i) => (v, i)).GroupBy(x => x.v, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Min(x => x.i)).Select(g => g.Key).FirstOrDefault();
}

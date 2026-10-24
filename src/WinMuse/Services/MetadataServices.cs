using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AcoustID;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services.Audio;

namespace WinMuse.Services;

/// <summary>Computes Chromaprint fingerprints by decoding audio through FFmpeg.</summary>
public sealed class FingerprintService
{
    public Task<(string fingerprint, int durationSec)> ComputeAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        const int rate = 11025;
        // Background work: playback (decode-ahead and audio threads) comes first.
        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        thread.Priority = ThreadPriority.BelowNormal;
        try { return Compute(path, rate, ct); }
        finally { thread.Priority = priority; }
    }, ct);

    private static (string fingerprint, int durationSec) Compute(string path, int rate, CancellationToken ct)
    {
        using var dec = new FFmpegDecoder(path, rate, 1);
        var ctx = new ChromaContext();
        ctx.Start(rate, 1);
        var f = new float[rate];
        var s = new short[rate];
        int total = 0, max = rate * 120;
        while (total < max)
        {
            ct.ThrowIfCancellationRequested();
            int n = dec.Read(f, 0, f.Length);
            if (n <= 0) break;
            for (int i = 0; i < n; i++) s[i] = (short)Math.Clamp(f[i] * 32767f, short.MinValue, short.MaxValue);
            ctx.Feed(s, n);
            total += n;
        }
        ctx.Finish();
        var dur = (int)Math.Round(dec.Duration.TotalSeconds > 0 ? dec.Duration.TotalSeconds : total / (double)rate);
        return (ctx.GetFingerprint(), dur);
    }
}

/// <summary>Finds, downloads and stores lyrics. Synced lyrics are saved as .lrc next to the song (or in app data).</summary>
public sealed class LyricsService
{
    private readonly OnlineMetadataService _online;
    private readonly SettingsService _settings;
    private readonly LibraryService _library;

    public LyricsService(OnlineMetadataService online, SettingsService settings, LibraryService library)
    {
        _online = online; _settings = settings; _library = library;
    }

    private static string AppDataLrc(Track t) =>
        Path.Combine(AppPaths.LyricsDir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(t.Path.ToLowerInvariant()))) + ".lrc");

    public LyricsResult? GetLocal(Track t)
    {
        try
        {
            var side = Path.ChangeExtension(t.Path, ".lrc");
            if (File.Exists(side)) return LrcParser.Parse(File.ReadAllText(side), "Lyrics file");
            var app = AppDataLrc(t);
            if (File.Exists(app)) return LrcParser.Parse(File.ReadAllText(app), "Downloaded");
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

    /// <summary>Downloads lyrics from LRCLIB and stores them locally.</summary>
    public async Task<LyricsResult?> DownloadAsync(Track t, CancellationToken ct = default)
    {
        var text = await _online.FetchLyricsAsync(t.Artist, t.DisplayTitle, t.Album, (int)t.Duration.TotalSeconds, ct);
        if (string.IsNullOrWhiteSpace(text))
        {
            if (t.LyricsState == ItemState.Missing) { t.LyricsState = ItemState.NotFound; _library.SaveStates(new[] { t }); }
            return null;
        }
        Save(t, text);
        t.LyricsState = ItemState.Downloaded;
        _library.SaveStates(new[] { t });
        return LrcParser.Parse(text, "Downloaded");
    }

    public void Save(Track t, string text)
    {
        var side = Path.ChangeExtension(t.Path, ".lrc");
        try { File.WriteAllText(side, text, Encoding.UTF8); return; }
        catch { /* read-only folder: fall back to app data */ }
        try { File.WriteAllText(AppDataLrc(t), text, Encoding.UTF8); }
        catch (Exception ex) { AppPaths.Log("Lyrics save failed: " + ex.Message); }
    }
}

/// <summary>Applies tag edits (to files or library only) and runs the "identify &amp; fix" lookups.</summary>
public sealed partial class MetadataEditService
{
    private readonly TagService _tags;
    private readonly ArtworkService _art;
    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly OnlineMetadataService _online;
    private readonly FingerprintService _fingerprint;

    public MetadataEditService(TagService tags, ArtworkService art, LibraryService library, SettingsService settings, OnlineMetadataService online, FingerprintService fingerprint)
    {
        _tags = tags; _art = art; _library = library; _settings = settings; _online = online; _fingerprint = fingerprint;
    }

    public bool WritesToFiles => _settings.Current.WriteMode == MetadataWriteMode.WriteToFiles;


    /// <summary>Applies the edit to each track. Returns error messages for files that couldn't be written.</summary>
    /// <param name="byUser">True for edits made by the user (states become "edited by you"); false for online results.</param>
    public async Task<List<string>> ApplyAsync(IList<Track> tracks, TagEdit edit, bool markReviewed = true, bool byUser = false)
    {
        var errors = new List<string>();
        var toFiles = WritesToFiles;
        var artPath = edit.Artwork is { Length: > 0 } ? _art.Save(edit.Artwork) : null;
        // Artists each song had before, to tell which artists just gained a song.
        Dictionary<Track, IReadOnlyList<string>>? before = null;
        if (edit.Artist != null || edit.AlbumArtist != null)
        {
            before = new();
            foreach (var t in tracks) before.TryAdd(t, t.ArtistNames);
        }

        await Task.Run(() =>
        {
            foreach (var t in tracks)
            {
                bool written = false;
                if (toFiles && DeferIfPlaying(t, edit)) { /* written when the song stops playing */ }
                else if (toFiles)
                {
                    try
                    {
                        _tags.Write(t.Path, edit);
                        var fi = new FileInfo(t.Path);
                        t.ModifiedUtc = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                        t.FileSize = fi.Length;
                        written = true;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{t.FileName}: {ex.Message} (saved to the library only)");
                    }
                }
                ApplyToTrack(t, edit);
                if (edit.Artwork != null) t.ArtPath = edit.Artwork.Length == 0 ? _art.FindFolderArt(t.Path) : artPath;
                if (!written) t.EditedLocally = true;
                if (markReviewed) t.AutoFixed = true; // edited (manually or by a fix): automatic processing leaves it alone from now on
                var origin = byUser ? ItemState.EditedByUser : ItemState.Downloaded;
                if (edit.HasMetadataFields) t.MetaState = origin;
                if (edit.Artwork != null) t.ArtState = edit.Artwork.Length == 0 ? (byUser ? ItemState.EditedByUser : ItemState.Missing) : origin;
                if (edit.Lyrics != null) t.LyricsState = origin;
            }
        });

        _library.SaveTracks(tracks);
        if (before != null)
            _library.TouchArtists(before.SelectMany(kv => kv.Key.ArtistNames.Except(kv.Value, StringComparer.OrdinalIgnoreCase)));
        return errors;
    }

    // ---------------- Writes to the playing song ----------------

    // Rewriting a file's tags while it is being played makes the audio jump, or cuts it short. That includes the next
    // song, whose file is opened ahead of time for a gapless start. Those writes wait: the library has the change at
    // once (kept as a library edit until then), the file gets it once the song is neither playing nor up next.
    private readonly Dictionary<Track, (List<TagEdit> edits, bool wasLocal)> _deferred = new();

    private bool DeferIfPlaying(Track t, TagEdit edit)
    {
        if (!Svc.Player.IsOpen(t)) return false;
        lock (_deferred)
        {
            if (_deferred.TryGetValue(t, out var d)) d.edits.Add(edit);
            else _deferred[t] = (new List<TagEdit> { edit }, t.EditedLocally);
        }
        AppPaths.Log($"Tag write for {t.FileName} waits until it is no longer playing or up next");
        return true;
    }

    /// <summary>Writes the edits that waited for their song to stop playing (every one with <paramref name="all"/>, when closing).</summary>
    public void WriteDeferred(bool all = false)
    {
        List<(Track t, List<TagEdit> edits, bool wasLocal)> due;
        lock (_deferred)
        {
            due = _deferred.Where(kv => all || !Svc.Player.IsOpen(kv.Key))
                .Select(kv => (kv.Key, kv.Value.edits, kv.Value.wasLocal)).ToList();
            foreach (var d in due) _deferred.Remove(d.t);
        }
        var written = new List<Track>();
        foreach (var (t, edits, wasLocal) in due)
        {
            try
            {
                foreach (var e in edits) _tags.Write(t.Path, e);
                var fi = new FileInfo(t.Path);
                t.ModifiedUtc = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds();
                t.FileSize = fi.Length;
                t.EditedLocally = wasLocal;
                written.Add(t);
            }
            catch (Exception ex) { AppPaths.Log($"Delayed tag write failed for {t.Path}: {ex.Message}"); }
        }
        if (written.Count == 0) return;
        if (App.UI.HasThreadAccess) _library.SaveStates(written);
        else App.UI.TryEnqueue(() => _library.SaveStates(written));
    }

    private static void ApplyToTrack(Track t, TagEdit e)
    {
        if (e.Title != null) t.Title = Null(e.Title);
        if (e.Artist != null) t.Artist = Null(e.Artist);
        if (e.AlbumArtist != null) t.AlbumArtist = Null(e.AlbumArtist);
        if (e.Album != null) t.Album = Null(e.Album);
        if (e.Genre != null) t.Genre = Null(e.Genre);
        if (e.Year != null) t.Year = e.Year.Value;
        if (e.TrackNumber != null) t.TrackNumber = e.TrackNumber.Value;
        if (e.TrackCount != null) t.TrackCount = e.TrackCount.Value;
        if (e.DiscNumber != null) t.DiscNumber = e.DiscNumber.Value;
        if (e.Composer != null) t.Composer = Null(e.Composer);
        if (e.Comment != null) t.Comment = Null(e.Comment);
        if (e.Lyrics != null) t.Lyrics = Null(e.Lyrics);
        if (e.Bpm != null) t.Bpm = e.Bpm.Value;
        if (e.Rating != null) t.Rating = e.Rating.Value;
        static string? Null(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    /// <summary>Best guess of (artist, title) using tags, then the file name ("01 - Artist - Title.mp3").</summary>
    /// <summary>Possible (artist, title) readings of the song's title tag / file name, most likely first.</summary>
    public static List<(string? artist, string title)> Readings(Track t)
    {
        // Tags written by downloaders often hold the whole video title ("Artist - Song (Official Video)").
        var fileName = Matching.Clean(Path.GetFileNameWithoutExtension(t.Path));
        var tagTitle = Matching.Clean(t.Title);
        var artist = string.IsNullOrWhiteSpace(t.Artist) || t.Artist == "Unknown Artist" ? null : t.Artist!.Trim();
        var source = string.IsNullOrWhiteSpace(tagTitle) ? fileName : tagTitle;
        var list = Matching.Readings(source, artist);
        // A real title that happens to contain a dash, with a separate artist tag.
        if (artist != null && !string.IsNullOrWhiteSpace(tagTitle) && Matching.SplitDash(tagTitle) is { } p && !Matching.ArtistMatches(p.a, artist) && !Matching.ArtistMatches(p.b, artist))
            list.Insert(0, (artist, tagTitle));
        if (!ReferenceEquals(source, fileName) && fileName.Length > 0)
            foreach (var r in Matching.Readings(fileName, artist)) if (!list.Contains(r)) list.Add(r);
        return list.Take(5).ToList();
    }

    public static (string? artist, string? title) GuessQuery(Track t)
    {
        var r = Readings(t).FirstOrDefault();
        return (r.artist, r.title);
    }

    /// <summary>Search text for engines that take free text (iTunes, Deezer, YouTube).</summary>
    public static string FreeText(Track t)
    {
        var (artist, title) = GuessQuery(t);
        return Matching.StripFeat($"{artist} {title}".Trim());
    }

    /// <summary>
    /// Identifies the recording. Order: AcoustID fingerprint (if a key is set), MusicBrainz, iTunes, Deezer, and — only
    /// when nothing else matched well — YouTube (if a key is set). A manual query ("Artist - Title") skips fingerprinting.
    /// Name-search results are scored by how well title, artist and length agree with the song, not by the services' own relevance.
    /// </summary>
    public async Task<List<MetadataCandidate>> IdentifyAsync(Track t, string? manualQuery, IProgress<string>? progress, CancellationToken ct,
        bool allowFingerprint = true, Action<string>? fingerprintError = null, bool stopWhenConfident = false, Action<string>? serviceBusy = null)
    {
        var all = new List<MetadataCandidate>();
        // Automatic processing stops once the decision is settled to save requests; the manual dialog shows everything.
        var nameReadings = Readings(t);
        var strongReadings = nameReadings.Where(r => !string.IsNullOrWhiteSpace(r.artist)).ToList();
        bool Done()
        {
            if (!stopWhenConfident) return false;
            var byPrint = all.Where(IsFingerprintMatch).ToList();
            bool byName = all.Any(c => IsNameMatch(t, c, strongReadings));
            // A fingerprint match is settled when the file name agrees with it (or the file name names no artist);
            // otherwise the name search may still find the song the file name describes (see ChooseAutomatic).
            var file = FileReadings(t);
            return byPrint.Count > 0
                ? byName || byPrint.Any(c => AgreesWithName(c, file)) || !file.Any(r => !string.IsNullOrWhiteSpace(r.artist))
                : byName;
        }
        var key = _settings.Current.AcoustIdApiKey;

        if (allowFingerprint && string.IsNullOrWhiteSpace(manualQuery) && !string.IsNullOrWhiteSpace(key))
        {
            try
            {
                progress?.Report("Generating audio fingerprint…");
                var (fp, dur) = await _fingerprint.ComputeAsync(t.Path, ct);
                progress?.Report("Querying AcoustID…");
                var prints = await _online.LookupAcoustIdAsync(key!, fp, dur, t.DurationMs, ct);
                all.AddRange(prints);
                AppPaths.Log($"Fingerprint {Path.GetFileName(t.Path)}: " + (prints.Count == 0 ? "no match"
                    : string.Join(" | ", prints.GroupBy(c => c.RecordingId).Take(4).Select(g => g.OrderByDescending(c => c.Score).First())
                        .Select(c => $"{c.Artist} - {c.Title} ({c.Score:0}, {c.Sources} sources)"))));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                AppPaths.Log("Fingerprint lookup failed: " + ex.Message);
                fingerprintError?.Invoke(ex.Message.Contains("invalid API key", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("client", StringComparison.OrdinalIgnoreCase)
                    ? "The AcoustID API key was rejected. Verify the key in Settings > API keys."
                    : "Audio fingerprint lookup failed: " + ex.Message);
                progress?.Report("Fingerprint lookup failed. Searching by name…");
            }
        }

        if (Done()) return Ranked(all);
        // Readings of the name ("Artist - Title", "Title By Artist", 「Artist」, ...) or the manual query.
        List<(string? artist, string title)> readings;
        string? album = null;
        if (!string.IsNullOrWhiteSpace(manualQuery))
        {
            var parts = manualQuery.Split(" - ", 2, StringSplitOptions.TrimEntries);
            readings = parts.Length == 2 ? new() { (parts[0], parts[1]), (null, manualQuery.Trim()) } : new() { (null, manualQuery.Trim()) };
        }
        else
        {
            readings = Readings(t);
            album = string.IsNullOrWhiteSpace(t.Album) ? null : t.Album;
        }
        // Nightcore / sped-up / slowed versions never match the original length.
        long localMs = Matching.IsSpeedVariant(t.Path) || Matching.IsSpeedVariant(t.Title) ? 0 : t.DurationMs;
        var (artist, title) = readings.Count > 0 ? readings[0] : (null, "");
        string freeText = Matching.StripFeat($"{artist} {title}".Trim());

        // MusicBrainz: for each reading try title + whole credit, title + each credited artist, then the title alone.
        // Stops at the first result that clearly matches; search terms differ often ("A, B" vs "A & B").
        progress?.Report("Searching MusicBrainz…");
        await Safe("MusicBrainz", async () =>
        {
            var tried = new HashSet<string>();
            foreach (var (ra, rt) in readings)
            {
                var variants = new List<(string? artist, string? album)>();
                if (ra != null) { if (album != null) variants.Add((ra, album)); variants.Add((ra, null)); }
                foreach (var name in Matching.ArtistNames(ra).Take(2)) variants.Add((name, null));
                variants.Add((null, null));
                foreach (var (va, valb) in variants)
                {
                    if (!tried.Add($"{rt}|{va}|{valb}")) continue;
                    var found = await _online.SearchRecordingsAsync(rt, va, valb, localMs, ct);
                    // MusicBrainz's score only says how well the search words matched ("Aquarius" by anyone):
                    // weigh it far below our own title / artist / length check.
                    foreach (var c in found)
                        c.Score = Math.Round(0.75 * readings.Max(r => Matching.Score(c.Artist, c.Title, c.DurationMs, r.artist, r.title, freeText, localMs)) + 0.25 * c.Score);
                    all.AddRange(found);
                    if (found.Any(c => c.Score >= 90 && Matching.Similar(Matching.StripFeat(c.Title), Matching.StripFeat(rt)) && (ra == null || Matching.ArtistMatches(c.Artist, ra))))
                        return;
                    if (tried.Count >= 6) return; // MusicBrainz allows one request per second
                }
            }
        });

        if (Done()) return Ranked(all);
        progress?.Report("Searching iTunes and Deezer…");
        var catalog = new List<MetadataCandidate>();
        // Catalog search text: "first artist + title" per reading, then the plain title.
        var terms = readings.SelectMany(r => new[] { Matching.StripFeat($"{Matching.ArtistNames(r.artist).FirstOrDefault()} {r.title}".Trim()), Matching.StripFeat(r.title) })
            .Where(x => x.Length > 1).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
        foreach (var term in terms)
        {
            var before = catalog.Count;
            await Safe("iTunes", async () => catalog.AddRange(await _online.SearchITunesAsync(term, ct)));
            await Safe("Deezer", async () => catalog.AddRange(await _online.SearchDeezerAsync(term, ct)));
            foreach (var c in catalog.Skip(before))
                c.Score = readings.Max(r => Matching.Score(c.Artist, c.Title, c.DurationMs, r.artist, r.title, freeText, localMs));
            if (catalog.Any(c => c.Score >= 90)) break;
        }
        foreach (var c in catalog.Where(c => c.Source == "Deezer").OrderByDescending(c => c.Score).Take(1).Where(c => c.Score >= 80))
            await _online.EnrichDeezerAsync(c, ct);
        all.AddRange(catalog.Where(c => c.Score >= 40));

        // YouTube is the last resort: only when nothing above is a confident match.
        var ytKey = _settings.Current.YouTubeApiKey;
        // Automatic: YouTube runs whenever the rules above haven't settled on a match. Manual: when nothing scored well.
        bool confident = stopWhenConfident ? ChooseAutomatic(t, all) != null
                                           : all.Any(IsFingerprintMatch) || all.Any(c => c.Source != "AcoustID" && c.Score >= 90);
        if (!confident && !string.IsNullOrWhiteSpace(ytKey))
        {
            progress?.Report("Searching YouTube…");
            await Safe("YouTube", async () =>
            {
                var id = Matching.YouTubeId(t.Path);
                if (await _online.LookupYouTubeAsync(ytKey!, id, id == null ? freeText : null, t.DurationMs, ct) is { } yt) all.Add(yt);
            });
        }

        // Fingerprint results: rank the recording the name agrees with first; one that contradicts a clear
        // "Artist - Title" may be the same audio re-released under another name.
        var strongQuery = readings.Where(r => !string.IsNullOrWhiteSpace(r.artist)).ToList();
        foreach (var c in all.Where(c => c.Source == "AcoustID"))
        {
            if (AgreesWithName(c, readings)) c.Score = Math.Min(100, c.Score + 5);
            else if (strongQuery.Count > 0) c.Score = Math.Max(0, c.Score - 15);
        }
        return Ranked(all);

        static List<MetadataCandidate> Ranked(List<MetadataCandidate> list) => list
            .Where(c => !string.IsNullOrWhiteSpace(c.Title))
            .GroupBy(c => $"{c.Title}|{c.Artist}|{c.Album}|{c.Year}|{c.Source}".ToLowerInvariant())
            .Select(g => g.OrderByDescending(c => c.Score).First())
            .OrderByDescending(c => c.Score)
            .Take(25)
            .ToList();

        async Task Safe(string name, Func<Task> work)
        {
            try { await work(); }
            catch (OperationCanceledException) { throw; }
            catch (ServiceBusyException ex) { AppPaths.Log(ex.Message); serviceBusy?.Invoke(ex.Service); }
            catch (Exception ex) { AppPaths.Log($"{name} lookup failed: {ex.Message}"); }
        }
    }

    /// <summary>A fingerprint match: AcoustID's audio comparison, length included, scored 75 or more.</summary>
    public static bool IsFingerprintMatch(MetadataCandidate c) => c.Source == "AcoustID" && c.Score >= 75;

    /// <summary>The candidate's title (and artist, where the reading names one) agrees with a reading of the song's name.</summary>
    public static bool AgreesWithName(MetadataCandidate c, IEnumerable<(string? artist, string title)> readings) =>
        readings.Any(r => Matching.Similar(Matching.StripFeat(c.Title), Matching.StripFeat(r.title))
                          && (string.IsNullOrWhiteSpace(r.artist) || Matching.ArtistMatches(c.Artist, r.artist)));

    /// <summary>
    /// A name-search match strong enough to apply without a fingerprint: the song's tags or file name give both the
    /// artist and the title, the result agrees with both, and the lengths are within 3 seconds. A file called "Send"
    /// never qualifies; neither do nightcore / sped-up versions, whose length can't confirm anything.
    /// </summary>
    public static bool IsNameMatch(Track t, MetadataCandidate c, IReadOnlyList<(string? artist, string title)> strongReadings)
    {
        if (c.Source is "AcoustID" or "YouTube" || strongReadings.Count == 0 || !AgreesWithName(c, strongReadings)) return false;
        if (Matching.IsSpeedVariant(t.Path) || Matching.IsSpeedVariant(t.Title)) return false;
        return t.DurationMs > 0 && c.DurationMs > 0 && Math.Abs(t.DurationMs - c.DurationMs) <= 3000;
    }

    /// <summary>
    /// The match applied automatically, or null to leave the song untouched:
    /// 1. a fingerprint match that agrees with the name or tags;
    /// 2. if the fingerprint contradicts a clear "Artist - Title", a strict name match (the same audio is often
    ///    re-released under other names, especially copyright-free music);
    /// 3. otherwise the fingerprint match with the most AcoustID submissions;
    /// 4. with no fingerprint match, a strict name match only.
    /// </summary>
    public static MetadataCandidate? ChooseAutomatic(Track t, IReadOnlyList<MetadataCandidate> candidates)
    {
        // The file name outranks the tags: tags may have been written by an earlier wrong match (or a downloader),
        // and a wrong tag must not "confirm" a wrong fingerprint link.
        var fileReadings = FileReadings(t);
        var tagReadings = TagReadings(t);
        var byPrint = candidates.Where(IsFingerprintMatch).OrderByDescending(c => c.Score).ThenByDescending(c => c.Sources).ToList();

        if (byPrint.FirstOrDefault(c => AgreesWithName(c, fileReadings)) is { } byFile) return byFile;
        if (BestNameMatch(t, candidates, fileReadings) is { } nameFromFile)
        {
            if (byPrint.Count > 0)
                AppPaths.Log($"Fingerprint said \"{byPrint[0].Artist} - {byPrint[0].Title}\"; the file name and length agree on \"{nameFromFile.Artist} - {nameFromFile.Title}\": {t.Path}");
            return nameFromFile;
        }
        if (byPrint.FirstOrDefault(c => AgreesWithName(c, tagReadings)) is { } byTags) return byTags;
        if (BestNameMatch(t, candidates, tagReadings) is { } nameFromTags) return nameFromTags;

        // Official / lyric videos are longer than the album version, so their length can't confirm a name match.
        // A YouTube video with the file's exact length, uploaded by the artist the name gives, confirms it instead;
        // album, year and track details then come from the catalog result for that artist and title.
        var named = fileReadings.Concat(tagReadings).Where(r => !string.IsNullOrWhiteSpace(r.artist)).ToList();
        if (candidates.FirstOrDefault(c => c.Source == "YouTube" && c.Verified && AgreesWithName(c, named)) is { } video)
        {
            var confirmed = new List<(string? artist, string title)> { (video.Artist, video.Title ?? "") };
            return PreferOriginal(candidates.Where(c => c.Source is not ("YouTube" or "AcoustID") && AgreesWithName(c, confirmed)).ToList()) ?? video;
        }
        return byPrint.OrderByDescending(c => c.Sources).ThenByDescending(c => c.Score).FirstOrDefault();
    }

    /// <summary>
    /// The strongest strict name match for these readings. Among equally good matches the original release wins over
    /// compilations: dated first, earliest first.
    /// </summary>
    private static MetadataCandidate? BestNameMatch(Track t, IReadOnlyList<MetadataCandidate> candidates, List<(string? artist, string title)> readings)
    {
        var strong = readings.Where(r => !string.IsNullOrWhiteSpace(r.artist)).ToList();
        return PreferOriginal(candidates.Where(c => IsNameMatch(t, c, strong)).ToList());
    }

    private static MetadataCandidate? PreferOriginal(List<MetadataCandidate> matches)
    {
        if (matches.Count == 0) return null;
        var top = matches.Max(c => c.Score);
        return matches.Where(c => c.Score >= top - 3)
            .OrderBy(c => c.Year > 0 ? 0 : 1).ThenBy(c => c.Year).ThenByDescending(c => c.Score).First();
    }

    /// <summary>Readings of the file name alone ("Artist - Title", "Title By Artist", ...), ignoring the tags.</summary>
    public static List<(string? artist, string title)> FileReadings(Track t) =>
        Matching.Readings(Matching.Clean(Path.GetFileNameWithoutExtension(t.Path)), null);

    /// <summary>The song's own title and artist tags.</summary>
    public static List<(string? artist, string title)> TagReadings(Track t)
    {
        var title = Matching.Clean(t.Title);
        if (string.IsNullOrWhiteSpace(title)) return new();
        var artist = string.IsNullOrWhiteSpace(t.Artist) || t.Artist == "Unknown Artist" ? null : t.Artist!.Trim();
        return Matching.Readings(title, artist);
    }

    /// <summary>
    /// Genre order: MusicBrainz/AcoustID genre of the match, Last.fm tags (if a key is set), then iTunes/Deezer.
    /// Only recognised genres are returned.
    /// </summary>
    public async Task<string?> ResolveGenreAsync(Track t, MetadataCandidate? c, CancellationToken ct = default)
    {
        if (c is { Source: "MusicBrainz" or "AcoustID" } && Genres.IsKnown(c.Genre)) return c.Genre;
        var artist = Matching.ArtistNames(c?.Artist ?? t.Artist).FirstOrDefault();
        var title = Matching.StripFeat(c?.Title ?? t.Title);
        if (string.IsNullOrWhiteSpace(artist) || artist == "Unknown Artist") return c?.Genre;

        if (_settings.Current.LastFmApiKey is { Length: > 0 } lfm)
        {
            try { if (await _online.LastFmGenreAsync(lfm, artist, title, ct) is { } g) return g; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AppPaths.Log("Last.fm genre: " + ex.Message); }
        }
        if (Genres.IsKnown(c?.Genre)) return c!.Genre;

        try
        {
            foreach (var r in await _online.SearchITunesAsync($"{artist} {title}", ct))
                if (Matching.ArtistMatches(r.Artist, artist) && Genres.IsKnown(r.Genre)) return r.Genre;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppPaths.Log("iTunes genre: " + ex.Message); }
        return null;
    }

    /// <summary>Downloads a photo for the artist: Deezer, then Wikipedia, and the YouTube channel avatar as a last resort.</summary>
    /// <summary>Uses an image chosen by the user as the artist photo.</summary>
    public void SetArtistImageByUser(string artist, byte[] bytes)
    {
        if (_art.Save(bytes) is { } path) _library.SetArtistImage(artist, path, tried: true, byUser: true);
    }

    /// <summary>
    /// Finds a photo for the artist (each credited artist is tried in turn). ID-based sources first — the artist's
    /// MusicBrainz entry links to Wikidata, Spotify, Deezer, YouTube and Bandcamp — then name searches.
    /// </summary>
    /// <param name="sample">A song by the artist; its YouTube video ID lets the uploader's avatar be used (2 quota units).</param>
    public async Task<bool> FetchArtistImageAsync(string artist, MetadataCandidate? c, bool force = false, CancellationToken ct = default, Track? sample = null)
    {
        if (string.IsNullOrWhiteSpace(artist) || artist is "Unknown Artist" or "Various Artists") return false;
        if (!force && (_library.GetArtistImage(artist) != null || _library.ArtistImageTried(artist))) return false;

        try
        {
            var path = _art.Save(await DownloadArtistImageAsync(artist, c, sample, ct));
            if (path == null) AppPaths.Log($"Artist image not found for \"{artist}\" (MusicBrainz artist: {c?.ArtistMbid ?? "searched by name"})");
            App.UI.TryEnqueue(() => _library.SetArtistImage(artist, path));
            return path != null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppPaths.Log("Artist image: " + ex.Message);
            return false;
        }
    }

    /// <summary>Finds and downloads a photo for the artist without using it yet (null when none was found).</summary>
    public async Task<byte[]?> DownloadArtistImageAsync(string artist, MetadataCandidate? c = null, Track? sample = null, CancellationToken ct = default)
    {
        string? url = null;
        var names = Matching.ArtistNames(artist).Prepend(artist).Distinct(StringComparer.OrdinalIgnoreCase).Take(4).ToList();
        for (int i = 0; i < names.Count && url == null; i++)
            url = await FindArtistImageUrlAsync(names[i], i == 0 ? c : null, sample, ct);
        return url != null ? await _online.DownloadImageAsync(url, ct) : null;
    }

    // ---------------- Artist edits ----------------

    /// <summary>
    /// Renames an artist on every song that credits them (as artist or album artist). Other artists on those songs stay
    /// as written ("A & B" becomes "New & B"); the artist's photo moves to the new name. Returns files that couldn't be written.
    /// </summary>
    public async Task<List<string>> RenameArtistAsync(string oldName, string newName)
    {
        newName = newName.Trim();
        var errors = new List<string>();
        if (newName.Length == 0 || newName == oldName) return errors;
        bool unknown = oldName == "Unknown Artist"; // songs without any artist get one
        var tracks = _library.Tracks.Where(t => t.ArtistNames.Contains(oldName, StringComparer.OrdinalIgnoreCase)).ToList();
        // Songs that end up with the same credits are written together.
        var groups = tracks.GroupBy(t => (
            artist: unknown && string.IsNullOrWhiteSpace(t.Artist) ? newName : ArtistCredit.Rename(t.Artist, oldName, newName),
            albumArtist: ArtistCredit.Rename(t.AlbumArtist, oldName, newName)));
        foreach (var g in groups)
            errors.AddRange(await ApplyAsync(g.ToList(), new TagEdit { Artist = g.Key.artist, AlbumArtist = g.Key.albumArtist }, byUser: true));
        _library.MoveArtistImage(oldName, newName);
        return errors;
    }

    /// <summary>Adds the artist to each song's artists (the songs keep their other artists). Returns files that couldn't be written.</summary>
    public async Task<List<string>> AddArtistToTracksAsync(string artist, IEnumerable<Track> tracks)
    {
        var errors = new List<string>();
        foreach (var g in tracks.Where(t => !t.ArtistNames.Contains(artist, StringComparer.OrdinalIgnoreCase)).GroupBy(t => ArtistCredit.Add(t.Artist, artist)))
            errors.AddRange(await ApplyAsync(g.ToList(), new TagEdit { Artist = g.Key }, byUser: true));
        return errors;
    }

    // ---------------- Genre edits ----------------

    /// <summary>A song's genres ("Pop; Dance", "Pop/Dance").</summary>
    public static List<string> GenresOf(string? genre) =>
        string.IsNullOrWhiteSpace(genre) ? new List<string>()
            : genre.Split(new[] { ';', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Renames a genre on every song that has it (their other genres stay). Returns files that couldn't be written.</summary>
    public async Task<List<string>> RenameGenreAsync(string oldName, string newName)
    {
        newName = newName.Trim();
        var errors = new List<string>();
        if (newName.Length == 0 || newName == oldName) return errors;
        var tracks = _library.Tracks.Where(t => GenresOf(t.Genre).Contains(oldName, StringComparer.OrdinalIgnoreCase)).ToList();
        foreach (var g in tracks.GroupBy(t =>
                 {
                     var list = GenresOf(t.Genre).Select(x => x.Equals(oldName, StringComparison.OrdinalIgnoreCase) ? newName : x)
                         .Distinct(StringComparer.OrdinalIgnoreCase);
                     return string.Join("; ", list);
                 }))
            errors.AddRange(await ApplyAsync(g.ToList(), new TagEdit { Genre = g.Key }, byUser: true));
        return errors;
    }

    /// <summary>Adds the genre to each song (they keep their other genres). Returns files that couldn't be written.</summary>
    public async Task<List<string>> AddGenreToTracksAsync(string genre, IEnumerable<Track> tracks)
    {
        var errors = new List<string>();
        foreach (var g in tracks.Where(t => !GenresOf(t.Genre).Contains(genre, StringComparer.OrdinalIgnoreCase))
                                .GroupBy(t => string.Join("; ", GenresOf(t.Genre).Append(genre))))
            errors.AddRange(await ApplyAsync(g.ToList(), new TagEdit { Genre = g.Key }, byUser: true));
        return errors;
    }

    private async Task<string?> FindArtistImageUrlAsync(string name, MetadataCandidate? c, Track? sample, CancellationToken ct)
    {
        var s = _settings.Current;
        var fanart = s.FanartApiKey;
        var (spId, spSecret) = (s.SpotifyClientId, s.SpotifyClientSecret);
        bool spotify = !string.IsNullOrWhiteSpace(spId) && !string.IsNullOrWhiteSpace(spSecret);
        var yt = s.YouTubeApiKey;

        async Task<T?> Try<T>(string source, Func<Task<T?>> lookup) where T : class
        {
            try { return await lookup(); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { AppPaths.Log($"Artist image ({source}) for {name}: {ex.Message}"); return null; }
        }

        // 1. Exact: the MusicBrainz artist and the profiles it links to.
        var mbid = c?.ArtistMbid ?? await Try("MusicBrainz", () => _online.FindMusicBrainzArtistIdAsync(name, ct));
        if (mbid != null)
        {
            var links = await Try("MusicBrainz links", () => _online.GetMusicBrainzArtistLinksAsync(mbid, ct));
            if (links?.WikidataId is { } wd && await Try("Wikidata", () => _online.GetWikidataImageAsync(wd, ct)) is { } u1) return u1;
            if (!string.IsNullOrWhiteSpace(fanart) && await Try("Fanart.tv", () => _online.GetFanartArtistImageAsync(fanart!, mbid, ct)) is { } u2) return u2;
            if (await Try("TheAudioDB", () => _online.GetAudioDbArtistImageAsync(mbid, null, ct)) is { } u3) return u3;
            if (spotify && links?.SpotifyId is { } sid && await Try("Spotify", () => _online.GetSpotifyArtistImageAsync(spId!, spSecret!, sid, null, ct)) is { } u4) return u4;
            if (links?.DeezerId > 0 && await Try("Deezer", () => _online.GetDeezerArtistImageByIdAsync(links.DeezerId, ct)) is { } u5) return u5;
            if (!string.IsNullOrWhiteSpace(yt) && links?.YouTubeChannelId is { } ch && await Try("YouTube", () => _online.GetYouTubeChannelImageAsync(yt!, ch, ct)) is { } u6) return u6;
            if (links?.BandcampUrl is { } bc && await Try("Bandcamp", () => _online.GetBandcampImageAsync(bc, ct)) is { } u7) return u7;
        }

        // 2. Name searches (exact name match required).
        if (c is { Source: "Deezer", ArtistImageUrl: { } du } && Matching.Normalize(c.Artist) == Matching.Normalize(name)) return du;
        if (await Try("TheAudioDB", () => _online.GetAudioDbArtistImageAsync(null, name, ct)) is { } n1) return n1;
        if (await Try("Deezer", () => _online.FindDeezerArtistImageAsync(name, ct)) is { } n2) return n2;
        if (spotify && await Try("Spotify", () => _online.GetSpotifyArtistImageAsync(spId!, spSecret!, null, name, ct)) is { } n3) return n3;

        // 3. YouTube uploader avatar, only when the uploader is this artist (2 quota units).
        if (!string.IsNullOrWhiteSpace(yt))
        {
            var channelId = c is { Source: "YouTube" } ? c.YouTubeChannelId : null;
            if (channelId == null && sample != null && Matching.YouTubeId(sample.Path) is { } vid
                && await Try<string>("YouTube", async () => await _online.GetYouTubeUploaderAsync(yt!, vid, ct) is { } up
                        && Matching.ArtistMatches(up.channel.Replace(" - Topic", "").Replace("VEVO", ""), name) ? up.channelId : null) is { } found)
                channelId = found;
            if (channelId != null && await Try("YouTube", () => _online.GetYouTubeChannelImageAsync(yt!, channelId, ct)) is { } y) return y;
        }

        // 4. Wikipedia by name (strict).
        return await Try("Wikipedia", () => _online.FindWikipediaArtistImageAsync(name, ct));
    }

    /// <summary>Downloads cover art for a candidate (or the track's current album) and applies it.</summary>
    /// <summary>
    /// Gives songs without album art the cover another song of the same album already has, so it isn't looked up
    /// again. Returns true when every song now has art. Songs with an unknown album are never matched up this way.
    /// </summary>
    public bool ReuseAlbumArt(IList<Track> tracks)
    {
        var missing = tracks.Where(x => x.ArtPath == null).ToList();
        if (missing.Count == 0) return true;
        if (string.IsNullOrWhiteSpace(tracks[0].Album)) return false;
        var donor = _library.FindAlbum(tracks[0].AlbumKey)?.Tracks.FirstOrDefault(x => x.ArtPath != null && File.Exists(x.ArtPath));
        if (donor == null) return false;
        foreach (var x in missing)
        {
            x.ArtPath = donor.ArtPath;
            x.ArtState = donor.ArtState == ItemState.Downloaded ? ItemState.Downloaded : ItemState.FromFile;
        }
        _library.SaveTracks(missing);
        return true;
    }

    /// <summary>A song moved to another album by a new match: its downloaded cover belonged to the old album.</summary>
    public void DropStaleArt(Track t, string albumKeyBefore)
    {
        if (t.AlbumKey == albumKeyBefore || t.ArtState != ItemState.Downloaded) return;
        t.ArtPath = null;
        t.ArtState = ItemState.Missing;
        _library.SaveStates(new[] { t });
    }

    public async Task<bool> FetchAndApplyArtAsync(IList<Track> tracks, MetadataCandidate? candidate, CancellationToken ct = default)
    {
        var t = tracks[0];
        var (bytes, fromYouTube) = await _online.FetchCoverAsync(candidate, t.AlbumArtist ?? t.Artist, t.Album, ct, t.Title, Matching.YouTubeId(t.Path));
        if (bytes == null) return false;
        if (fromYouTube) bytes = await ArtworkService.CropToSquareAsync(bytes); // 16:9 video frame -> square cover

        if (_settings.Current.EmbedFetchedArt)
        {
            await ApplyAsync(tracks, new TagEdit { Artwork = bytes }, markReviewed: false);
        }
        else
        {
            var path = _art.Save(bytes);
            foreach (var x in tracks) { x.ArtPath = path; x.ArtState = ItemState.Downloaded; }
            _library.SaveTracks(tracks);
        }
        return true;
    }
}

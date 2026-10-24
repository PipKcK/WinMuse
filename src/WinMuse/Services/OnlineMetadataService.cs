using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinMuse.Helpers;
using WinMuse.Models;

namespace WinMuse.Services;

/// <summary>
/// Talks to MusicBrainz, Cover Art Archive, AcoustID and LRCLIB. Everything fetched here is downloaded and stored
/// locally by the callers; nothing is streamed.
/// </summary>
/// <summary>A service answered "too many requests" / "busy": the song should be retried later, not marked as not found.</summary>
public sealed class ServiceBusyException(string service) : Exception($"{service} is busy")
{
    public string Service { get; } = service;
}

public sealed partial class OnlineMetadataService
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _mbGate = new(1, 1);
    private DateTime _lastMb = DateTime.MinValue;

    public OnlineMetadataService()
    {
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(25) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WinMuse", "1.0"));
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("(local-music-player)"));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ---------------- MusicBrainz ----------------

    /// <summary>MusicBrainz allows ~1 request per second.</summary>
    private async Task<JsonDocument?> MbGetAsync(string url, CancellationToken ct)
    {
        await _mbGate.WaitAsync(ct);
        try
        {
            var wait = TimeSpan.FromMilliseconds(1100) - (DateTime.UtcNow - _lastMb);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastMb = DateTime.UtcNow;
            using var resp = await _http.GetAsync(url, ct);
            if ((int)resp.StatusCode == 503)
            {
                // MusicBrainz is busy (rate limit): wait and retry once instead of treating it as "not found".
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                _lastMb = DateTime.UtcNow;
                using var retry = await _http.GetAsync(url, ct);
                if ((int)retry.StatusCode is 503 or 429) throw new ServiceBusyException("MusicBrainz");
                if (!retry.IsSuccessStatusCode) return null;
                return await JsonDocument.ParseAsync(await retry.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            }
            if (!resp.IsSuccessStatusCode) return null;
            return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        finally { _mbGate.Release(); }
    }

    private static string Lucene(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public async Task<List<MetadataCandidate>> SearchRecordingsAsync(string? title, string? artist, string? album, long durationMs, CancellationToken ct = default)
    {
        var result = new List<MetadataCandidate>();
        if (string.IsNullOrWhiteSpace(title)) return result;

        var q = "recording:" + Lucene(title);
        if (!string.IsNullOrWhiteSpace(artist)) q += " AND artist:" + Lucene(artist);
        if (!string.IsNullOrWhiteSpace(album)) q += " AND release:" + Lucene(album);
        var url = $"https://musicbrainz.org/ws/2/recording?fmt=json&limit=15&query={Uri.EscapeDataString(q)}";

        using var doc = await MbGetAsync(url, ct);
        if (doc == null || !doc.RootElement.TryGetProperty("recordings", out var recs)) return result;

        foreach (var rec in recs.EnumerateArray())
        {
            var recTitle = Str(rec, "title");
            var recArtist = ArtistCredit(rec);
            var length = rec.TryGetProperty("length", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt64() : 0;
            var mbScore = rec.TryGetProperty("score", out var sc) ? sc.GetDouble() : 50;
            var genre = TopTag(rec);

            if (!rec.TryGetProperty("releases", out var releases) || releases.GetArrayLength() == 0)
            {
                result.Add(new MetadataCandidate { Source = "MusicBrainz", Title = recTitle, Artist = recArtist, DurationMs = length, Score = mbScore, RecordingId = Str(rec, "id"), Genre = genre, ArtistMbid = FirstArtistId(rec) });
                continue;
            }

            foreach (var rel in releases.EnumerateArray().Take(4))
            {
                var c = new MetadataCandidate
                {
                    Source = "MusicBrainz",
                    RecordingId = Str(rec, "id"),
                    ArtistMbid = FirstArtistId(rec),
                    ReleaseId = Str(rel, "id"),
                    Title = recTitle,
                    Artist = recArtist,
                    Album = Str(rel, "title"),
                    AlbumArtist = ArtistCredit(rel) ?? recArtist,
                    Year = YearOf(Str(rel, "date")),
                    DurationMs = length,
                    Genre = genre,
                };
                if (rel.TryGetProperty("release-group", out var rg))
                {
                    c.ReleaseGroupId = Str(rg, "id");
                    // prefer albums over compilations / singles
                    var type = Str(rg, "primary-type");
                    if (type == "Album") mbScore += 3;
                    if (rg.TryGetProperty("secondary-types", out var st) && st.GetArrayLength() > 0) mbScore -= 6;
                }
                if (rel.TryGetProperty("track-count", out var tc) && tc.ValueKind == JsonValueKind.Number) c.TrackCount = tc.GetInt32();
                if (rel.TryGetProperty("media", out var media) && media.GetArrayLength() > 0)
                {
                    var m = media[0];
                    if (m.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Number) c.DiscNumber = pos.GetInt32();
                    if (m.TryGetProperty("track", out var tr) && tr.GetArrayLength() > 0)
                        c.TrackNumber = int.TryParse(Str(tr[0], "number"), out var n) ? n : 0;
                    if (m.TryGetProperty("track-count", out var mtc) && mtc.ValueKind == JsonValueKind.Number) c.TrackCount = mtc.GetInt32();
                }
                if (Str(rel, "status") == "Official") mbScore += 2;
                c.Score = Math.Clamp(mbScore - DurationPenalty(durationMs, length), 0, 100);
                result.Add(c);
            }
        }
        return result;
    }

    private static double DurationPenalty(long localMs, long remoteMs)
    {
        if (localMs <= 0 || remoteMs <= 0) return 0;
        var diff = Math.Abs(localMs - remoteMs) / 1000.0;
        return diff < 3 ? 0 : Math.Min(40, diff * 1.5);
    }

    // ---------------- AcoustID ----------------

    public async Task<List<MetadataCandidate>> LookupAcoustIdAsync(string apiKey, string fingerprint, int durationSec, long localMs, CancellationToken ct = default)
    {
        var list = new List<MetadataCandidate>();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client"] = apiKey,
            ["meta"] = "recordings releasegroups releases tracks sources compress",
            ["duration"] = durationSec.ToString(CultureInfo.InvariantCulture),
            ["fingerprint"] = fingerprint,
            ["format"] = "json",
        });
        using var resp = await _http.PostAsync("https://api.acoustid.org/v2/lookup", form, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (Str(root, "status") != "ok")
        {
            var msg = root.TryGetProperty("error", out var err) ? Str(err, "message") : "unknown error";
            throw new InvalidOperationException("AcoustID: " + msg);
        }

        foreach (var res in root.GetProperty("results").EnumerateArray())
        {
            double score = res.TryGetProperty("score", out var s) ? s.GetDouble() * 100 : 0;
            if (!res.TryGetProperty("recordings", out var recs)) continue;
            foreach (var rec in recs.EnumerateArray())
            {
                var title = Str(rec, "title");
                if (title == null) continue;
                var artist = AcoustIdCredit(rec);
                long len = rec.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? (long)(d.GetDouble() * 1000) : 0;
                int sources = rec.TryGetProperty("sources", out var src) && src.ValueKind == JsonValueKind.Number ? src.GetInt32() : 0;

                if (!rec.TryGetProperty("releasegroups", out var rgs) || rgs.GetArrayLength() == 0)
                {
                    list.Add(new MetadataCandidate { Source = "AcoustID", Title = title, Artist = artist, RecordingId = Str(rec, "id"), Score = score, DurationMs = len, ArtistMbid = AcoustIdArtistId(rec), Sources = sources });
                    continue;
                }

                foreach (var rg in rgs.EnumerateArray().Take(3))
                {
                    var c = new MetadataCandidate
                    {
                        Source = "AcoustID",
                        RecordingId = Str(rec, "id"),
                        ArtistMbid = AcoustIdArtistId(rec),
                        ReleaseGroupId = Str(rg, "id"),
                        Title = title,
                        Artist = artist,
                        Album = Str(rg, "title"),
                        AlbumArtist = AcoustIdCredit(rg) ?? artist,
                        DurationMs = len,
                        Sources = sources,
                        Score = score - (Str(rg, "type") == "Album" ? 0 : 4) - (rg.TryGetProperty("secondarytypes", out _) ? 6 : 0),
                    };
                    if (rg.TryGetProperty("releases", out var rels) && rels.GetArrayLength() > 0)
                    {
                        var rel = rels.EnumerateArray()
                            .OrderBy(r => r.TryGetProperty("date", out var dt) && dt.TryGetProperty("year", out var y) ? y.GetInt32() : 9999)
                            .First();
                        c.ReleaseId = Str(rel, "id");
                        if (rel.TryGetProperty("date", out var date) && date.TryGetProperty("year", out var yr)) c.Year = yr.GetInt32();
                        if (rel.TryGetProperty("mediums", out var meds) && meds.GetArrayLength() > 0)
                        {
                            var m = meds[0];
                            if (m.TryGetProperty("position", out var p)) c.DiscNumber = p.GetInt32();
                            if (m.TryGetProperty("track_count", out var tc)) c.TrackCount = tc.GetInt32();
                            if (m.TryGetProperty("tracks", out var trs) && trs.GetArrayLength() > 0 && trs[0].TryGetProperty("position", out var tp)) c.TrackNumber = tp.GetInt32();
                        }
                    }
                    // The fingerprint already compared the audio; a longer video version (intro, outro) only costs a little.
                    c.Score = Math.Clamp(c.Score - Math.Min(10, DurationPenalty(localMs, len)), 0, 100);
                    list.Add(c);
                }
            }
        }
        return list;
    }

    /// <summary>Checks an AcoustID application key with a lightweight lookup. Returns (valid, message).</summary>
    public async Task<(bool ok, string message)> ValidateAcoustIdKeyAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (false, "Enter an API key.");
        try
        {
            // Looking up a fixed track ID needs no fingerprint; the service still authenticates the key.
            var url = $"https://api.acoustid.org/v2/lookup?format=json&client={Uri.EscapeDataString(apiKey.Trim())}&trackid=9ff43b6a-4f16-427c-93c2-92307ca505e0";
            using var resp = await _http.GetAsync(url, ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (Str(root, "status") == "ok") return (true, "API key verified.");
            var msg = root.TryGetProperty("error", out var err) ? Str(err, "message") : null;
            return (false, string.IsNullOrEmpty(msg) ? "The API key was rejected." : $"The API key was rejected ({msg}).");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, "Unable to reach AcoustID: " + ex.Message); }
    }

    // ---------------- Cover Art Archive ----------------

    /// <summary>
    /// Album art lookup order: the candidate's own art (iTunes/Deezer), Cover Art Archive, MusicBrainz release-group
    /// search, iTunes, Deezer, and finally the YouTube video thumbnail (last resort; cropped to a square by the caller).
    /// </summary>
    public async Task<(byte[]? bytes, bool fromYouTube)> FetchCoverAsync(MetadataCandidate? c, string? artist, string? album, CancellationToken ct = default,
        string? title = null, string? youTubeId = null)
    {
        if (c is { Source: "iTunes" or "Deezer", ArtworkUrl: { } direct } && await GetBytes(direct, ct) is { } d) return (d, false);
        if (c?.ReleaseId != null && await GetBytes($"https://coverartarchive.org/release/{c.ReleaseId}/front-500", ct) is { } a1) return (a1, false);
        if (c?.ReleaseGroupId != null && await GetBytes($"https://coverartarchive.org/release-group/{c.ReleaseGroupId}/front-500", ct) is { } b1) return (b1, false);

        artist ??= c?.AlbumArtist ?? c?.Artist;
        album ??= c?.Album;
        title ??= c?.Title;

        if (!string.IsNullOrWhiteSpace(album))
        {
            var q = "releasegroup:" + Lucene(album);
            if (!string.IsNullOrWhiteSpace(artist)) q += " AND artist:" + Lucene(artist);
            using var doc = await MbGetAsync($"https://musicbrainz.org/ws/2/release-group?fmt=json&limit=5&query={Uri.EscapeDataString(q)}", ct);
            if (doc != null && doc.RootElement.TryGetProperty("release-groups", out var groups))
                foreach (var g in groups.EnumerateArray())
                {
                    if (g.TryGetProperty("score", out var sc) && sc.GetInt32() < 80) continue;
                    if (await GetBytes($"https://coverartarchive.org/release-group/{Str(g, "id")}/front-500", ct) is { } bytes) return (bytes, false);
                }
        }

        // iTunes / Deezer: accept only results whose artist and title/album clearly match.
        if (!string.IsNullOrWhiteSpace(artist) && (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(album)))
        {
            var term = $"{Matching.StripFeat(artist)} {Matching.StripFeat(title ?? album)}";
            foreach (var r in (await SearchITunesAsync(term, ct)).Concat(await SearchDeezerAsync(term, ct)))
            {
                bool titleOk = title != null && Matching.Similar(Matching.StripFeat(r.Title), Matching.StripFeat(title));
                bool albumOk = album != null && Matching.Similar(r.Album, album);
                if (Matching.ArtistMatches(r.Artist, artist) && (titleOk || albumOk) && r.ArtworkUrl != null && await GetBytes(r.ArtworkUrl, ct) is { } img)
                    return (img, false);
            }
        }

        if (c is { Source: "YouTube", ArtworkUrl: { } yt } && await GetBytes(yt, ct) is { } ytb) return (ytb, true);
        if (youTubeId != null && await FetchYouTubeThumbnailAsync(youTubeId, ct) is { } thumb) return (thumb, true);
        return (null, false);
    }

    /// <summary>Downloads an image. Slow or failing hosts yield null after 12 s instead of stalling the caller.</summary>
    private async Task<byte[]?> GetBytes(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            // Ask for an image explicitly: some hosts (TheAudioDB) answer 406 to the client's default JSON Accept header.
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("image/*");
            req.Headers.Accept.ParseAdd("*/*;q=0.8");
            using var resp = await _http.SendAsync(req, timeout.Token);
            if (!resp.IsSuccessStatusCode)
            {
                AppPaths.Log($"Image download skipped ({(int)resp.StatusCode}): {url}");
                return null;
            }
            return await resp.Content.ReadAsByteArrayAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            AppPaths.Log($"Image download skipped ({ex.GetType().Name}): {url}");
            return null;
        }
    }

    // ---------------- LRCLIB ----------------

    /// <summary>Returns LRC (synced) text if available, else plain lyrics, else null.</summary>
    public async Task<string?> FetchLyricsAsync(string? artist, string title, string? album, int durationSec, CancellationToken ct = default)
    {
        try
        {
            var url = $"https://lrclib.net/api/get?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist ?? "")}";
            if (!string.IsNullOrWhiteSpace(album)) url += $"&album_name={Uri.EscapeDataString(album)}";
            if (durationSec > 0) url += $"&duration={durationSec}";
            using (var resp = await _http.GetAsync(url, ct))
            {
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                    if (PickLyrics(doc.RootElement) is { } found) return found;
                }
            }

            var search = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}";
            if (!string.IsNullOrWhiteSpace(artist)) search += $"&artist_name={Uri.EscapeDataString(artist)}";
            using var sresp = await _http.GetAsync(search, ct);
            if (!sresp.IsSuccessStatusCode) return null;
            using var sdoc = JsonDocument.Parse(await sresp.Content.ReadAsStringAsync(ct));
            var items = sdoc.RootElement.EnumerateArray()
                .OrderBy(e => durationSec > 0 && e.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? Math.Abs(d.GetDouble() - durationSec) : 0)
                .ToList();
            foreach (var e in items.Where(e => Str(e, "syncedLyrics") != null)) return Str(e, "syncedLyrics");
            foreach (var e in items) if (PickLyrics(e) is { } p) return p;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppPaths.Log("Lyrics fetch failed: " + ex.Message); }
        return null;
    }

    private static string? PickLyrics(JsonElement e) => Str(e, "syncedLyrics") ?? Str(e, "plainLyrics");

    // ---------------- JSON helpers ----------------

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString()) ? v.GetString() : null;

    private static string? FirstArtistId(JsonElement e) =>
        e.TryGetProperty("artist-credit", out var ac) && ac.GetArrayLength() > 0 && ac[0].TryGetProperty("artist", out var a) ? Str(a, "id") : null;

    /// <summary>"LSR/CITY & Gareth Emery": AcoustID's artist names joined with their join phrases.</summary>
    private static string? AcoustIdCredit(JsonElement e)
    {
        if (!e.TryGetProperty("artists", out var arts) || arts.GetArrayLength() == 0) return null;
        var sb = new System.Text.StringBuilder();
        var list = arts.EnumerateArray().ToList();
        for (int i = 0; i < list.Count; i++)
        {
            sb.Append(Str(list[i], "name"));
            // Only ordinary join words; anything unusual in the data becomes " & ".
            if (i < list.Count - 1)
                sb.Append(Str(list[i], "joinphrase") is { } jp && Regex.IsMatch(jp, @"^\s*(&|,|x|×|and|with|vs\.?|feat\.?|ft\.?|featuring)\s*$", RegexOptions.IgnoreCase)
                    ? (jp.Trim() == "," ? ", " : $" {jp.Trim()} ") : " & ");
        }
        return sb.ToString().Trim();
    }

    private static string? AcoustIdArtistId(JsonElement rec) =>
        rec.TryGetProperty("artists", out var arts) && arts.GetArrayLength() > 0 ? Str(arts[0], "id") : null;

    private static string? ArtistCredit(JsonElement e)
    {
        if (!e.TryGetProperty("artist-credit", out var ac)) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var part in ac.EnumerateArray())
        {
            sb.Append(Str(part, "name") ?? (part.TryGetProperty("artist", out var a) ? Str(a, "name") : ""));
            sb.Append(part.TryGetProperty("joinphrase", out var jp) ? jp.GetString() : "");
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    /// <summary>Picks the most-voted MusicBrainz tag that is a recognised genre (ignores tags like "missing isrc").</summary>
    private static string? TopTag(JsonElement rec)
    {
        if (!rec.TryGetProperty("tags", out var tags) || tags.GetArrayLength() == 0) return null;
        return tags.EnumerateArray()
            .OrderByDescending(t => t.TryGetProperty("count", out var c) ? c.GetInt32() : 0)
            .Select(t => Genres.FromTag(Str(t, "name")))
            .FirstOrDefault(g => g != null);
    }

    private static int YearOf(string? date) => date != null && date.Length >= 4 && int.TryParse(date[..4], out var y) ? y : 0;
}

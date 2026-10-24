using System.Globalization;
using System.Text.Json;
using System.Xml;
using WinMuse.Helpers;
using WinMuse.Models;

namespace WinMuse.Services;

/// <summary>
/// Free lookup sources used after AcoustID and MusicBrainz: iTunes Search and Deezer (no key), Last.fm (free key, genres)
/// and the YouTube Data API (free key, last resort).
/// </summary>
public sealed partial class OnlineMetadataService
{
    private readonly SemaphoreSlim _itunesGate = new(1, 1);
    private DateTime _lastItunes = DateTime.MinValue;

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
    }

    public Task<byte[]?> DownloadImageAsync(string url, CancellationToken ct = default) => GetBytes(url, ct);

    // ---------------- iTunes Search (free, no key; ~20 requests/minute) ----------------

    public async Task<List<MetadataCandidate>> SearchITunesAsync(string term, CancellationToken ct = default)
    {
        var list = new List<MetadataCandidate>();
        if (string.IsNullOrWhiteSpace(term)) return list;
        await _itunesGate.WaitAsync(ct);
        try
        {
            var wait = TimeSpan.FromSeconds(3.1) - (DateTime.UtcNow - _lastItunes);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            _lastItunes = DateTime.UtcNow;
            var url = $"https://itunes.apple.com/search?media=music&entity=song&limit=10&term={Uri.EscapeDataString(term)}";
            using var resp = await _http.GetAsync(url, ct);
            if ((int)resp.StatusCode is 403 or 429)
            {
                // Rate limited (~20 searches/minute): pause all iTunes searches, then retry once.
                AppPaths.Log("iTunes rate limit reached; waiting 30 s.");
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
                _lastItunes = DateTime.UtcNow;
                using var retry = await _http.GetAsync(url, ct);
                if ((int)retry.StatusCode is 403 or 429) throw new ServiceBusyException("iTunes");
                if (!retry.IsSuccessStatusCode) return list;
                return ParseITunes(await JsonDocument.ParseAsync(await retry.Content.ReadAsStreamAsync(ct), cancellationToken: ct), list);
            }
            if (!resp.IsSuccessStatusCode) return list;
            using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return ParseITunes(doc, list);
        }
        finally { _itunesGate.Release(); }
    }

    private static List<MetadataCandidate> ParseITunes(JsonDocument doc, List<MetadataCandidate> list)
    {
        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("results", out var results)) return list;
            foreach (var r in results.EnumerateArray())
            {
                var c = new MetadataCandidate
                {
                    Source = "iTunes",
                    Title = Str(r, "trackName"),
                    Artist = Str(r, "artistName"),
                    Album = TrimAlbumSuffix(Str(r, "collectionName")),
                    AlbumArtist = Str(r, "collectionArtistName") ?? Str(r, "artistName"),
                    Year = YearOf(Str(r, "releaseDate")),
                    TrackNumber = Int(r, "trackNumber"),
                    TrackCount = Int(r, "trackCount"),
                    DiscNumber = Int(r, "discNumber"),
                    Genre = Genres.FromITunes(Str(r, "primaryGenreName")),
                    DurationMs = r.TryGetProperty("trackTimeMillis", out var ms) && ms.ValueKind == JsonValueKind.Number ? ms.GetInt64() : 0,
                    ArtworkUrl = Str(r, "artworkUrl100")?.Replace("100x100bb", "1200x1200bb"),
                };
                if (c.Title != null && c.Artist != null) list.Add(c);
            }
        }
        return list;
    }

    private static string? TrimAlbumSuffix(string? album) =>
        album?.Replace(" - Single", "").Replace(" - EP", "");

    // ---------------- Deezer (free, no key) ----------------

    public async Task<List<MetadataCandidate>> SearchDeezerAsync(string query, CancellationToken ct = default)
    {
        var list = new List<MetadataCandidate>();
        if (string.IsNullOrWhiteSpace(query)) return list;
        using var doc = await GetJsonAsync($"https://api.deezer.com/search?limit=10&q={Uri.EscapeDataString(query)}", ct);
        if (doc != null && doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("code", out var code) && code.GetInt32() == 4)
            throw new ServiceBusyException("Deezer"); // "Quota limit exceeded"
        if (doc == null || !doc.RootElement.TryGetProperty("data", out var data)) return list;
        foreach (var r in data.EnumerateArray())
        {
            r.TryGetProperty("artist", out var artist);
            r.TryGetProperty("album", out var album);
            var c = new MetadataCandidate
            {
                Source = "Deezer",
                Title = Str(r, "title"),
                Artist = artist.ValueKind == JsonValueKind.Object ? Str(artist, "name") : null,
                Album = album.ValueKind == JsonValueKind.Object ? Str(album, "title") : null,
                DurationMs = Int(r, "duration") * 1000L,
                ArtworkUrl = album.ValueKind == JsonValueKind.Object ? Str(album, "cover_xl") : null,
                ArtistImageUrl = artist.ValueKind == JsonValueKind.Object ? ValidDeezerPicture(Str(artist, "picture_xl")) : null,
                DeezerTrackId = r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number ? id.GetInt64() : 0,
                DeezerAlbumId = album.ValueKind == JsonValueKind.Object && album.TryGetProperty("id", out var aid) && aid.ValueKind == JsonValueKind.Number ? aid.GetInt64() : 0,
            };
            c.AlbumArtist = c.Artist;
            if (c.Title != null && c.Artist != null) list.Add(c);
        }
        return list;
    }

    /// <summary>Adds year, track number and genre from Deezer's track and album endpoints (two requests).</summary>
    public async Task EnrichDeezerAsync(MetadataCandidate c, CancellationToken ct = default)
    {
        if (c.Source != "Deezer") return;
        try
        {
            if (c.DeezerTrackId > 0)
            {
                using var t = await GetJsonAsync($"https://api.deezer.com/track/{c.DeezerTrackId}", ct);
                if (t != null)
                {
                    c.TrackNumber = Int(t.RootElement, "track_position");
                    c.DiscNumber = Int(t.RootElement, "disk_number");
                    c.Year = YearOf(Str(t.RootElement, "release_date"));
                }
            }
            if (c.DeezerAlbumId > 0)
            {
                using var a = await GetJsonAsync($"https://api.deezer.com/album/{c.DeezerAlbumId}", ct);
                if (a != null)
                {
                    c.TrackCount = Int(a.RootElement, "nb_tracks");
                    if (c.Year == 0) c.Year = YearOf(Str(a.RootElement, "release_date"));
                    if (a.RootElement.TryGetProperty("genres", out var g) && g.TryGetProperty("data", out var gd))
                        c.Genre ??= gd.EnumerateArray().Select(x => Genres.FromTag(Str(x, "name")) ?? Genres.FromITunes(Str(x, "name"))).FirstOrDefault(x => x != null);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { AppPaths.Log("Deezer details: " + ex.Message); }
    }

    /// <summary>Artist photo from Deezer (no key). Returns null unless the artist name clearly matches.</summary>
    public async Task<string?> FindDeezerArtistImageAsync(string artist, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"https://api.deezer.com/search/artist?limit=5&q={Uri.EscapeDataString(artist)}", ct);
        if (doc == null || !doc.RootElement.TryGetProperty("data", out var data)) return null;
        foreach (var a in data.EnumerateArray())
            if (Matching.Normalize(Str(a, "name")) == Matching.Normalize(artist) && ValidDeezerPicture(Str(a, "picture_xl")) is { } pic)
                return pic;
        return null;
    }

    /// <summary>
    /// Artist photo from Wikipedia (free, no key). Accepted only when the page describes a musical act, so a person or
    /// thing with the same name is never used.
    /// </summary>
    public async Task<string?> FindWikipediaArtistImageAsync(string artist, CancellationToken ct = default)
    {
        foreach (var title in new[] { artist, artist + " (musician)", artist + " (band)", artist + " (DJ)" })
        {
            using var doc = await GetJsonAsync($"https://en.wikipedia.org/api/rest_v1/page/summary/{Uri.EscapeDataString(title.Replace(' ', '_'))}", ct);
            if (doc == null) continue;
            var root = doc.RootElement;
            if (Str(root, "type") != "standard") continue;
            var description = (Str(root, "description") ?? "") + " " + (Str(root, "extract") ?? "")[..Math.Min(200, (Str(root, "extract") ?? "").Length)];
            if (!MusicWords.IsMatch(description)) continue;
            // The original can be several MB (4000+ px); a 600 px rendition downloads quickly and is plenty.
            if (root.TryGetProperty("thumbnail", out var th) && Str(th, "source") is { } t)
                return System.Text.RegularExpressions.Regex.Replace(t, @"/\d+px-", "/960px-"); // Wikimedia only serves standard sizes
            if (root.TryGetProperty("originalimage", out var img) && Str(img, "source") is { } src) return src;
        }
        return null;
    }

    // Whole words only ("DJ" must not match "adjacent").
    private static readonly System.Text.RegularExpressions.Regex MusicWords = new(
        @"\b(singer|rapper|musician|band|DJ|disc jockey|record producer|songwriter|composer|music group|musical duo|musical group|music producer|electronic music)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // Deezer returns a generic silhouette (".../artist//...") when it has no photo.
    private static string? ValidDeezerPicture(string? url) => url == null || url.Contains("/artist//") ? null : url;

    // ---------------- YouTube Data API v3 (free key; last resort) ----------------

    /// <summary>
    /// Reads a video's title and uploader and derives artist/title only when the uploader matches one side of the
    /// title ("Beautiful - Giulio Cercato" by "Giulio Cercato") or is an auto-generated "Artist - Topic" channel.
    /// Uses the video ID from the file name when available (1 quota unit), otherwise a search (100 units).
    /// </summary>
    public async Task<MetadataCandidate?> LookupYouTubeAsync(string apiKey, string? videoId, string? query, long localMs, CancellationToken ct = default)
    {
        var ids = new List<string>();
        if (videoId != null) ids.Add(videoId);
        else if (!string.IsNullOrWhiteSpace(query))
        {
            using var s = await GetJsonAsync($"https://www.googleapis.com/youtube/v3/search?part=id&type=video&maxResults=3&q={Uri.EscapeDataString(query)}&key={Uri.EscapeDataString(apiKey)}", ct);
            if (s != null && s.RootElement.TryGetProperty("items", out var items))
                foreach (var it in items.EnumerateArray())
                    if (it.TryGetProperty("id", out var idObj) && Str(idObj, "videoId") is { } vid) ids.Add(vid);
        }
        if (ids.Count == 0) return null;

        using var doc = await GetJsonAsync($"https://www.googleapis.com/youtube/v3/videos?part=snippet,contentDetails&id={string.Join(",", ids)}&key={Uri.EscapeDataString(apiKey)}", ct);
        if (doc == null || !doc.RootElement.TryGetProperty("items", out var videos)) return null;
        foreach (var v in videos.EnumerateArray())
        {
            if (!v.TryGetProperty("snippet", out var sn)) continue;
            var title = Str(sn, "title");
            var channel = Str(sn, "channelTitle");
            if (title == null || channel == null) continue;

            long ms = 0;
            if (v.TryGetProperty("contentDetails", out var cd) && Str(cd, "duration") is { } iso)
                try { ms = (long)XmlConvert.ToTimeSpan(iso).TotalMilliseconds; } catch { }
            // A search result must be the file itself: a downloaded video has the video's length to the second.
            if (videoId == null && (localMs <= 0 || ms <= 0 || Math.Abs(localMs - ms) > 3_000)) continue;

            var parsed = ParseYouTubeTitle(title, channel);
            if (parsed == null) continue;
            string? thumb = null;
            if (sn.TryGetProperty("thumbnails", out var th))
                foreach (var size in new[] { "maxres", "standard", "high" })
                    if (th.TryGetProperty(size, out var t) && Str(t, "url") is { } u) { thumb = u; break; }

            return new MetadataCandidate
            {
                Source = "YouTube",
                Verified = true,
                Score = 80,
                Title = parsed.Value.title,
                Artist = parsed.Value.artist,
                AlbumArtist = parsed.Value.artist,
                Year = YearOf(Str(sn, "publishedAt")),
                DurationMs = ms,
                ArtworkUrl = thumb,
                YouTubeChannelId = Str(sn, "channelId"),
            };
        }
        return null;
    }

    public static (string artist, string title)? ParseYouTubeTitle(string videoTitle, string channel)
    {
        var isTopic = channel.EndsWith(" - Topic", StringComparison.OrdinalIgnoreCase);
        var ch = channel;
        foreach (var suffix in new[] { " - Topic", "VEVO", " Official", "Official" })
            if (ch.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) ch = ch[..^suffix.Length].Trim();

        var clean = Matching.Clean(videoTitle);
        if (Matching.SplitDash(clean) is { } parts)
        {
            if (Matching.ArtistMatches(parts.a, ch)) return (parts.a, parts.b);
            if (Matching.ArtistMatches(parts.b, ch)) return (parts.b, parts.a);
        }
        if (isTopic && clean.Length > 0) return (ch, clean);
        return null; // uploader doesn't identify the artist: leave the song as Unknown Artist
    }

    /// <summary>Uploader of a video (1 quota unit): channel name and ID.</summary>
    public async Task<(string channel, string channelId)?> GetYouTubeUploaderAsync(string apiKey, string videoId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"https://www.googleapis.com/youtube/v3/videos?part=snippet&id={Uri.EscapeDataString(videoId)}&key={Uri.EscapeDataString(apiKey)}", ct);
        if (doc == null || !doc.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0 || !items[0].TryGetProperty("snippet", out var sn)) return null;
        return Str(sn, "channelTitle") is { } ch && Str(sn, "channelId") is { } id ? (ch, id) : null;
    }

    public async Task<string?> GetYouTubeChannelImageAsync(string apiKey, string channelId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"https://www.googleapis.com/youtube/v3/channels?part=snippet&id={Uri.EscapeDataString(channelId)}&key={Uri.EscapeDataString(apiKey)}", ct);
        if (doc == null || !doc.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0) return null;
        if (!items[0].TryGetProperty("snippet", out var sn) || !sn.TryGetProperty("thumbnails", out var th)) return null;
        foreach (var size in new[] { "high", "medium", "default" })
            if (th.TryGetProperty(size, out var t) && Str(t, "url") is { } u) return u;
        return null;
    }

    /// <summary>Video thumbnail straight from YouTube's image server (no key needed).</summary>
    public async Task<byte[]?> FetchYouTubeThumbnailAsync(string videoId, CancellationToken ct = default) =>
        await GetBytes($"https://i.ytimg.com/vi/{videoId}/maxresdefault.jpg", ct)
        ?? await GetBytes($"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg", ct);

    // ---------------- Last.fm (free key; genres) ----------------

    public async Task<string?> LastFmGenreAsync(string apiKey, string artist, string? title, CancellationToken ct = default)
    {
        var baseUrl = $"https://ws.audioscrobbler.com/2.0/?format=json&autocorrect=1&api_key={Uri.EscapeDataString(apiKey)}&artist={Uri.EscapeDataString(artist)}";
        if (!string.IsNullOrWhiteSpace(title))
        {
            using var t = await GetJsonAsync(baseUrl + $"&method=track.gettoptags&track={Uri.EscapeDataString(title)}", ct);
            if (FirstGenre(t) is { } g) return g;
        }
        using var a = await GetJsonAsync(baseUrl + "&method=artist.gettoptags", ct);
        return FirstGenre(a);

        static string? FirstGenre(JsonDocument? doc)
        {
            if (doc == null || !doc.RootElement.TryGetProperty("toptags", out var tt) || !tt.TryGetProperty("tag", out var tags) || tags.ValueKind != JsonValueKind.Array) return null;
            return tags.EnumerateArray().Take(10).Select(x => Genres.FromTag(Str(x, "name"))).FirstOrDefault(x => x != null);
        }
    }

    // ---------------- Key verification ----------------

    public async Task<(bool ok, string message)> ValidateYouTubeKeyAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (false, "Enter an API key.");
        try
        {
            using var resp = await _http.GetAsync($"https://www.googleapis.com/youtube/v3/videos?part=id&id=jNQXAC9IVRw&key={Uri.EscapeDataString(apiKey.Trim())}", ct);
            if (resp.IsSuccessStatusCode) return (true, "API key verified.");
            var body = await resp.Content.ReadAsStringAsync(ct);
            string? reason = null, message = null;
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    message = Str(err, "message");
                    if (err.TryGetProperty("errors", out var errs) && errs.GetArrayLength() > 0) reason = Str(errs[0], "reason");
                }
            }
            catch { }
            return reason switch
            {
                "quotaExceeded" or "dailyLimitExceeded" => (true, "API key verified. Today's free quota is used up; lookups resume tomorrow."),
                "accessNotConfigured" => (false, "YouTube Data API v3 is not enabled for this key's project."),
                "keyInvalid" or "badRequest" => (false, "The API key was rejected."),
                _ => (false, "The API key was rejected" + (message != null ? $" ({message})." : ".")),
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, "Unable to reach YouTube: " + ex.Message); }
    }

    public async Task<(bool ok, string message)> ValidateLastFmKeyAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (false, "Enter an API key.");
        try
        {
            using var resp = await _http.GetAsync($"https://ws.audioscrobbler.com/2.0/?method=chart.gettopartists&limit=1&format=json&api_key={Uri.EscapeDataString(apiKey.Trim())}", ct);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("artists", out _)) return (true, "API key verified.");
            var msg = Str(doc.RootElement, "message");
            return (false, "The API key was rejected" + (msg != null ? $" ({msg})." : "."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, "Unable to reach Last.fm: " + ex.Message); }
    }

    private static int Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}

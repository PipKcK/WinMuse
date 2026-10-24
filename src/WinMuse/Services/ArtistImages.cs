using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinMuse.Helpers;

namespace WinMuse.Services;

/// <summary>
/// Artist photo sources. The preferred path is ID-based: the artist's MusicBrainz entry links to Wikidata, Spotify,
/// Deezer, YouTube and Bandcamp, so the photo belongs to exactly that artist. Name searches are only fallbacks.
/// </summary>
public sealed partial class OnlineMetadataService
{
    private const string AudioDbKey = "123"; // TheAudioDB's free public key

    /// <summary>Links found on an artist's MusicBrainz entry.</summary>
    public sealed class ArtistLinks
    {
        public string? WikidataId { get; set; }
        public string? SpotifyId { get; set; }
        public long DeezerId { get; set; }
        public string? YouTubeChannelId { get; set; }
        public string? BandcampUrl { get; set; }
        public string? ImageUrl { get; set; }
    }

    /// <summary>Finds the MusicBrainz artist whose name matches exactly (accents/case/punctuation ignored).</summary>
    public async Task<string?> FindMusicBrainzArtistIdAsync(string name, CancellationToken ct = default)
    {
        using var doc = await MbGetAsync($"https://musicbrainz.org/ws/2/artist?fmt=json&limit=5&query={Uri.EscapeDataString("artist:" + Lucene(name))}", ct);
        if (doc == null || !doc.RootElement.TryGetProperty("artists", out var artists)) return null;
        foreach (var a in artists.EnumerateArray())
        {
            var score = a.TryGetProperty("score", out var s) ? s.GetInt32() : 0;
            if (score >= 90 && Matching.Normalize(Str(a, "name")) == Matching.Normalize(name)) return Str(a, "id");
        }
        return null;
    }

    public async Task<ArtistLinks?> GetMusicBrainzArtistLinksAsync(string mbid, CancellationToken ct = default)
    {
        using var doc = await MbGetAsync($"https://musicbrainz.org/ws/2/artist/{mbid}?fmt=json&inc=url-rels", ct);
        if (doc == null || !doc.RootElement.TryGetProperty("relations", out var rels)) return null;
        var links = new ArtistLinks();
        foreach (var r in rels.EnumerateArray())
        {
            var url = r.TryGetProperty("url", out var u) ? Str(u, "resource") : null;
            if (url == null) continue;
            var type = Str(r, "type");
            if (type == "wikidata") links.WikidataId = url.Split('/').Last();
            else if (type == "image") links.ImageUrl ??= url;
            else if (url.Contains("open.spotify.com/artist/")) links.SpotifyId ??= url.Split("/artist/")[1].Split('?', '/')[0];
            else if (url.Contains("deezer.com/") && url.Contains("/artist/") && long.TryParse(url.Split("/artist/")[1].Split('?', '/')[0], out var dz)) links.DeezerId = dz;
            else if (url.Contains("youtube.com/channel/")) links.YouTubeChannelId ??= url.Split("/channel/")[1].Split('?', '/')[0];
            else if (url.Contains(".bandcamp.com")) links.BandcampUrl ??= url;
        }
        return links;
    }

    /// <summary>Wikidata "image" (P18) → a 600 px version from Wikimedia Commons.</summary>
    public async Task<string?> GetWikidataImageAsync(string wikidataId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"https://www.wikidata.org/wiki/Special:EntityData/{wikidataId}.json", ct);
        if (doc == null || !doc.RootElement.TryGetProperty("entities", out var ents)) return null;
        foreach (var e in ents.EnumerateObject())
            if (e.Value.TryGetProperty("claims", out var claims) && claims.TryGetProperty("P18", out var p18) && p18.GetArrayLength() > 0
                && p18[0].TryGetProperty("mainsnak", out var snak) && snak.TryGetProperty("datavalue", out var dv) && Str(dv, "value") is { } file)
                return $"https://commons.wikimedia.org/wiki/Special:FilePath/{Uri.EscapeDataString(file.Replace(' ', '_'))}?width=600";
        return null;
    }

    public async Task<string?> GetFanartArtistImageAsync(string apiKey, string mbid, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"https://webservice.fanart.tv/v3/music/{mbid}?api_key={Uri.EscapeDataString(apiKey)}", ct);
        if (doc == null || !doc.RootElement.TryGetProperty("artistthumb", out var thumbs) || thumbs.GetArrayLength() == 0) return null;
        return Str(thumbs[0], "url")?.Replace("/fanart/", "/preview/"); // preview = smaller download
    }

    public async Task<string?> GetAudioDbArtistImageAsync(string? mbid, string? name, CancellationToken ct = default)
    {
        var url = mbid != null
            ? $"https://www.theaudiodb.com/api/v1/json/{AudioDbKey}/artist-mb.php?i={mbid}"
            : $"https://www.theaudiodb.com/api/v1/json/{AudioDbKey}/search.php?s={Uri.EscapeDataString(name ?? "")}";
        using var doc = await GetJsonAsync(url, ct);
        if (doc == null || !doc.RootElement.TryGetProperty("artists", out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var a in arr.EnumerateArray())
        {
            if (mbid == null && Matching.Normalize(Str(a, "strArtist")) != Matching.Normalize(name)) continue;
            if (Str(a, "strArtistThumb") is { } thumb) return thumb;
        }
        return null;
    }

    public async Task<string?> GetDeezerArtistImageByIdAsync(long id, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"https://api.deezer.com/artist/{id}", ct);
        return doc == null ? null : ValidDeezerPicture(Str(doc.RootElement, "picture_xl"));
    }

    /// <summary>The artist photo shown on a Bandcamp page (its og:image tag).</summary>
    public async Task<string?> GetBandcampImageAsync(string pageUrl, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(pageUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var html = await resp.Content.ReadAsStringAsync(ct);
            var m = Regex.Match(html, "<meta\\s+property=\"og:image\"\\s+content=\"([^\"]+)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    // ---------------- Spotify (free Client ID + secret, client-credentials flow) ----------------

    private string? _spotifyToken;
    private DateTime _spotifyTokenExpires;
    private string? _spotifyTokenFor;

    private async Task<string?> SpotifyTokenAsync(string clientId, string secret, CancellationToken ct)
    {
        var who = clientId + ":" + secret;
        if (_spotifyToken != null && _spotifyTokenFor == who && DateTime.UtcNow < _spotifyTokenExpires) return _spotifyToken;
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(who)));
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        _spotifyToken = Str(doc.RootElement, "access_token");
        var expires = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _spotifyTokenExpires = DateTime.UtcNow.AddSeconds(expires - 60);
        _spotifyTokenFor = who;
        return _spotifyToken;
    }

    private async Task<JsonDocument?> SpotifyGetAsync(string clientId, string secret, string url, CancellationToken ct)
    {
        var token = await SpotifyTokenAsync(clientId, secret, ct);
        if (token == null) return null;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    private static string? LargestSpotifyImage(JsonElement artist) =>
        artist.TryGetProperty("images", out var imgs) && imgs.GetArrayLength() > 0 ? Str(imgs[0], "url") : null;

    public async Task<string?> GetSpotifyArtistImageAsync(string clientId, string secret, string? spotifyId, string? name, CancellationToken ct = default)
    {
        if (spotifyId != null)
        {
            using var a = await SpotifyGetAsync(clientId, secret, $"https://api.spotify.com/v1/artists/{spotifyId}", ct);
            return a == null ? null : LargestSpotifyImage(a.RootElement);
        }
        using var s = await SpotifyGetAsync(clientId, secret, $"https://api.spotify.com/v1/search?type=artist&limit=5&q={Uri.EscapeDataString(name ?? "")}", ct);
        if (s == null || !s.RootElement.TryGetProperty("artists", out var res) || !res.TryGetProperty("items", out var items)) return null;
        // Exact name only; the most popular of several same-named artists wins.
        return items.EnumerateArray()
            .Where(x => Matching.Normalize(Str(x, "name")) == Matching.Normalize(name))
            .OrderByDescending(x => x.TryGetProperty("popularity", out var p) ? p.GetInt32() : 0)
            .Select(LargestSpotifyImage).FirstOrDefault(u => u != null);
    }

    public async Task<(bool ok, string message)> ValidateSpotifyAsync(string clientId, string secret, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret)) return (false, "Enter both the client ID and the client secret.");
        try
        {
            _spotifyToken = null;
            return await SpotifyTokenAsync(clientId.Trim(), secret.Trim(), ct) != null
                ? (true, "Client ID and secret verified.")
                : (false, "The client ID or secret was rejected.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, "Unable to reach Spotify: " + ex.Message); }
    }

    public async Task<(bool ok, string message)> ValidateFanartKeyAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (false, "Enter an API key.");
        try
        {
            // AJR's MusicBrainz ID: any known artist works for the check.
            using var resp = await _http.GetAsync($"https://webservice.fanart.tv/v3/music/2eb55bd9-cfbf-44fd-a5da-92dd825284c7?api_key={Uri.EscapeDataString(apiKey.Trim())}", ct);
            if (resp.IsSuccessStatusCode) return (true, "API key verified.");
            return resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                ? (false, "The API key was rejected.")
                : (false, $"Fanart.tv returned {(int)resp.StatusCode}.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, "Unable to reach Fanart.tv: " + ex.Message); }
    }
}

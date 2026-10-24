namespace WinMuse.Models;

/// <summary>A proposed set of tags from an online source (MusicBrainz / AcoustID).</summary>
public sealed class MetadataCandidate
{
    public string Source { get; set; } = "";
    public double Score { get; set; }
    public string? RecordingId { get; set; }
    public string? ReleaseId { get; set; }
    public string? ReleaseGroupId { get; set; }

    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public int Year { get; set; }
    public int TrackNumber { get; set; }
    public int TrackCount { get; set; }
    public int DiscNumber { get; set; }
    public string? Genre { get; set; }
    public long DurationMs { get; set; }

    /// <summary>Direct album art URL supplied by the source (iTunes, Deezer, YouTube thumbnail).</summary>
    public string? ArtworkUrl { get; set; }
    /// <summary>Artist photo URL supplied by the source (Deezer artist picture, YouTube channel avatar).</summary>
    public string? ArtistImageUrl { get; set; }
    /// <summary>YouTube only: the uploader was matched to the artist using the video title.</summary>
    public bool Verified { get; set; }
    public long DeezerTrackId { get; set; }
    public long DeezerAlbumId { get; set; }
    public string? YouTubeChannelId { get; set; }
    /// <summary>MusicBrainz ID of the first credited artist (exact artist-photo lookups).</summary>
    public string? ArtistMbid { get; set; }
    /// <summary>AcoustID only: how many submissions link the fingerprint to this recording.</summary>
    public int Sources { get; set; }

    public string ScoreText => $"{Score:0}% match";
    public string Headline => $"{Title} — {Artist}";
    public string Detail => string.Join(" · ", new[] { Album, Year > 0 ? Year.ToString() : null, TrackNumber > 0 ? $"Track {TrackNumber}" : null, Source }.Where(s => !string.IsNullOrEmpty(s)));
}

public sealed record LyricLine(TimeSpan Time, string Text);

public sealed class LyricsResult
{
    public bool IsSynced { get; init; }
    public List<LyricLine> Lines { get; init; } = new();
    public string PlainText { get; init; } = "";
    public string? Source { get; init; }
}

/// <summary>A set of field edits applied to one or more tracks. Null = leave unchanged.</summary>
public sealed class TagEdit
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public int? Year { get; set; }
    public int? TrackNumber { get; set; }
    public int? TrackCount { get; set; }
    public int? DiscNumber { get; set; }
    public string? Composer { get; set; }
    public string? Comment { get; set; }
    public string? Lyrics { get; set; }
    public int? Bpm { get; set; }
    public int? Rating { get; set; }
    public bool HasMetadataFields => Title != null || Artist != null || AlbumArtist != null || Album != null || Year != null || TrackNumber != null
                                     || TrackCount != null || DiscNumber != null || Composer != null || Comment != null || Bpm != null;

    /// <summary>New cover image bytes; empty array removes the art.</summary>
    public byte[]? Artwork { get; set; }

    public static TagEdit FromCandidate(MetadataCandidate c) => new()
    {
        Title = c.Title,
        Artist = c.Artist,
        Album = c.Album,
        AlbumArtist = c.AlbumArtist,
        Year = c.Year > 0 ? c.Year : null,
        TrackNumber = c.TrackNumber > 0 ? c.TrackNumber : null,
        TrackCount = c.TrackCount > 0 ? c.TrackCount : null,
        DiscNumber = c.DiscNumber > 0 ? c.DiscNumber : null,
        Genre = string.IsNullOrEmpty(c.Genre) ? null : c.Genre,
    };
}

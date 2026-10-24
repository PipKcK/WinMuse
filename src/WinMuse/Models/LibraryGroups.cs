namespace WinMuse.Models;

public sealed class Album
{
    public string Key { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public int Year { get; init; }
    public string? Genre { get; init; }
    public string? ArtPath { get; init; }
    public List<Track> Tracks { get; init; } = new();
    public long AddedUtc { get; init; }

    public int TrackCount => Tracks.Count;
    public TimeSpan Duration => TimeSpan.FromMilliseconds(Tracks.Sum(t => t.DurationMs));
    public string YearText => Year > 0 ? Year.ToString() : "";
    public string Subtitle => Year > 0 ? $"{Artist} · {Year}" : Artist;
    public string Summary => $"{TrackCount} {(TrackCount == 1 ? "song" : "songs")} · {Helpers.Format.LongTime(Duration)}";
}

public sealed class Artist
{
    public string Name { get; init; } = "";
    public List<Album> Albums { get; init; } = new();
    public List<Track> Tracks { get; init; } = new();
    /// <summary>Last time a song was added to this artist (a new file, an edit or a fetch).</summary>
    public long ChangedUtc { get; init; }
    /// <summary>Downloaded artist photo, if any.</summary>
    public string? ImagePath { get; init; }
    public string? ArtPath => ImagePath ?? Albums.FirstOrDefault(a => a.ArtPath != null)?.ArtPath;
    public string Summary => $"{Albums.Count} {(Albums.Count == 1 ? "album" : "albums")} · {Tracks.Count} {(Tracks.Count == 1 ? "song" : "songs")}";
    public string Initials => string.Concat(Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));
    public bool IsFavorite => Services.Svc.Favorites.IsFavoriteArtist(Name);
}

public sealed class Genre
{
    public string Name { get; init; } = "";
    public List<Track> Tracks { get; init; } = new();
    public List<string> ArtPaths { get; init; } = new();
    public string? Art1 => ArtPaths.ElementAtOrDefault(0);
    public string? Art2 => ArtPaths.ElementAtOrDefault(1);
    public string? Art3 => ArtPaths.ElementAtOrDefault(2);
    public string? Art4 => ArtPaths.ElementAtOrDefault(3);
    public string Summary => $"{Tracks.Count} {(Tracks.Count == 1 ? "song" : "songs")}";
}

/// <summary>Navigation parameter for a generic song list page (genre, search, etc.).</summary>
public sealed record SongListRequest(string Title, string? Subtitle, Func<IEnumerable<Track>> Source, string? ArtPath = null);

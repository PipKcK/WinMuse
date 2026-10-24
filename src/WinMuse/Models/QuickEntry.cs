using Microsoft.UI.Xaml;

namespace WinMuse.Models;

/// <summary>A Quick access tile on Home: a song or an artist, possibly pinned or a favorite.</summary>
public sealed class QuickEntry
{
    public Track? Track { get; init; }
    public Artist? Artist { get; init; }
    public bool IsPinned { get; init; }
    public bool IsFavorite { get; init; }

    public bool IsArtist => Artist != null;
    public string Key => IsArtist ? "artist:" + Artist!.Name : "song:" + Track!.Path;
    public string Title => IsArtist ? Artist!.Name : Track!.DisplayTitle;
    /// <summary>Artists say so; songs show their artist.</summary>
    public string Subtitle => IsArtist ? $"Artist · {Artist!.Tracks.Count} {(Artist.Tracks.Count == 1 ? "song" : "songs")}" : Track!.DisplayArtist;
    public string? ArtPath => IsArtist ? Artist!.ArtPath : Track!.ArtPath;
    public string Seed => IsArtist ? Artist!.Name : Track!.AlbumKey;
    public string KindGlyph => IsArtist ? "" : "";
    public string KindName => IsArtist ? "Artist" : "Song";

    // Artists get a round photo inset in the tile; song art fills the tile's left end, rounded like the tile.
    public CornerRadius ArtCorner => IsArtist ? new CornerRadius(26) : new CornerRadius(7, 0, 0, 7);
    public Thickness ArtMargin => IsArtist ? new Thickness(6) : new Thickness(0);

    public string BadgeGlyph => IsPinned ? "" : IsFavorite ? "" : "";
    public Visibility BadgeVisibility => IsPinned || IsFavorite ? Visibility.Visible : Visibility.Collapsed;
    public string BadgeTip => IsPinned ? "Pinned" : "Favorite";

    public bool SameAs(QuickEntry o) => o.Key == Key && o.Title == Title && o.ArtPath == ArtPath && o.Subtitle == Subtitle && o.BadgeGlyph == BadgeGlyph;
}

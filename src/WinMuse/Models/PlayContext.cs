namespace WinMuse.Models;

public enum PlayContextKind { Song, Album, Artist, Playlist }

/// <summary>
/// Where playback was started from: a playlist, an album, an artist or a single song (Continue Listening on Home
/// remembers these). Key: the song's path, the album key, the artist name, or the playlist id.
/// </summary>
public sealed record PlayContext(PlayContextKind Kind, string Key, string Name)
{
    public static PlayContext Of(Track t) => new(PlayContextKind.Song, t.Path, t.DisplayTitle);
    public static PlayContext Of(Album a) => new(PlayContextKind.Album, a.Key, a.Title);
    public static PlayContext Of(Artist a) => new(PlayContextKind.Artist, a.Name, a.Name);
    public static PlayContext Of(Playlist p) => new(PlayContextKind.Playlist, p.Id.ToString(), p.Name);
}

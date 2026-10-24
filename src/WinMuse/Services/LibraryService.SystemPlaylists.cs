using WinMuse.Helpers;
using WinMuse.Models;

namespace WinMuse.Services;

/// <summary>
/// Automatic playlists: Fresh in Your Library, On Repeat and Long Time No Play. Music keeps them up to
/// date from the library and play history; they are never saved and don't appear on the Playlists page.
/// </summary>
public sealed partial class LibraryService
{
    public const long RecentlyAddedId = -1, MostPlayedId = -3, NotPlayedRecentlyId = -4;

    /// <summary>Songs in each automatic playlist.</summary>
    public const int SystemPlaylistSize = 30;

    private readonly Dictionary<long, Playlist> _system = new()
    {
        [RecentlyAddedId] = new() { Id = RecentlyAddedId, Name = "Fresh in Your Library", Description = "Your latest additions to the library." },
        [MostPlayedId] = new() { Id = MostPlayedId, Name = "On Repeat", Description = "Tracks you can’t stop playing." },
        [NotPlayedRecentlyId] = new() { Id = NotPlayedRecentlyId, Name = "Long Time No Play", Description = "Rediscover tracks you haven’t played in a while." },
    };

    /// <summary>The automatic playlists in Home order, with their current songs.</summary>
    public IReadOnlyList<Playlist> SystemPlaylists
    {
        get
        {
            foreach (var p in _system.Values) Fill(p);
            return _system.Values.OrderByDescending(p => p.Id).ToList();
        }
    }

    /// <summary>One automatic playlist, with its current songs.</summary>
    public Playlist? SystemPlaylist(long id)
    {
        if (!_system.TryGetValue(id, out var p)) return null;
        Fill(p);
        return p;
    }

    private void Fill(Playlist p)
    {
        var tracks = _tracks;
        var now = Format.NowUnix();
        p.Tracks = p.Id switch
        {
            RecentlyAddedId => tracks.OrderByDescending(t => t.AddedUtc).Take(SystemPlaylistSize).ToList(),
            MostPlayedId => tracks.Where(t => t.PlayCount > 1).OrderByDescending(t => t.PlayCount).ThenByDescending(t => t.LastPlayedUtc).Take(SystemPlaylistSize).ToList(),
            NotPlayedRecentlyId => Rediscover(p.Tracks, tracks.Where(t => t.LastPlayedUtc == 0 || now - t.LastPlayedUtc > 60 * 86400).ToList()),
            _ => p.Tracks,
        };
    }

    /// <summary>A random pick that stays put: songs already in the list keep their place while they still qualify.</summary>
    private static List<Track> Rediscover(List<Track> current, List<Track> eligible)
    {
        var set = eligible.ToHashSet();
        var keep = current.Where(set.Contains).Take(SystemPlaylistSize).ToList();
        var kept = keep.ToHashSet();
        keep.AddRange(eligible.Where(t => !kept.Contains(t)).OrderBy(_ => Random.Shared.Next()).Take(SystemPlaylistSize - keep.Count));
        return keep;
    }
}

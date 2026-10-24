using WinMuse.Models;

namespace WinMuse.Services;

/// <summary>
/// Favorites and Quick access pins. A favorite song is a song in the "Favorite" playlist (an ordinary playlist the
/// user can also edit); favorite artists are listed first on the Artists page. Pinned songs and artists always lead
/// Quick access on Home, before favorites.
/// </summary>
public sealed class FavoritesService
{
    public const string PlaylistName = "Favorite";
    public const int MaxQuickAccess = 10;
    private readonly SettingsService _settings;
    private readonly LibraryService _library;

    /// <summary>Raised after any favorite or pin changes.</summary>
    public event Action? Changed;

    public FavoritesService(SettingsService settings, LibraryService library)
    {
        _settings = settings; _library = library;
        // The playlist can be edited like any other: song flags follow it.
        _library.PlaylistsChanged += RefreshFlags;
        _library.Changed += RefreshFlags;
    }

    // ---------------- Songs ----------------

    /// <summary>The "Favorite" playlist: the one remembered by ID, else an existing playlist with that name.</summary>
    public Playlist? Playlist =>
        _library.Playlists.FirstOrDefault(p => p.Id == _settings.Current.FavoritesPlaylistId && !p.IsSmart)
        ?? _library.Playlists.FirstOrDefault(p => !p.IsSmart && string.Equals(p.Name, PlaylistName, StringComparison.OrdinalIgnoreCase));

    public void SetFavorite(IEnumerable<Track> tracks, bool favorite)
    {
        var list = tracks.ToList();
        var p = Playlist;
        if (p == null)
        {
            if (!favorite) return;
            p = _library.CreatePlaylist(PlaylistName, Description);
        }
        if (_settings.Current.FavoritesPlaylistId != p.Id) _settings.Set(nameof(AppSettings.FavoritesPlaylistId), s => s.FavoritesPlaylistId = p.Id);

        if (favorite) _library.AddToPlaylist(p, list.Where(t => !p.Tracks.Contains(t)));
        else _library.SetPlaylistTracks(p, p.Tracks.Where(t => !list.Contains(t)));
        RefreshFlags();
        Changed?.Invoke();
    }

    public void ToggleFavorite(Track t) => SetFavorite(new[] { t }, !t.IsFavorite);

    public List<Track> FavoriteSongs => Playlist?.Tracks.ToList() ?? new();

    private const string Description = "Songs you marked with a heart.";

    private void RefreshFlags()
    {
        // Favorites used to be stars: playlists created then still say so.
        if (Playlist is { Description: "Songs you marked with a star." } old) { old.Description = Description; _library.UpdatePlaylist(old); }
        var set = Playlist?.Tracks.ToHashSet() ?? new HashSet<Track>();
        foreach (var t in _library.Tracks)
        {
            var fav = set.Contains(t);
            if (t.IsFavorite != fav) t.IsFavorite = fav;
        }
    }

    // ---------------- Artists ----------------

    public bool IsFavoriteArtist(string name) => _settings.Current.FavoriteArtists.Contains(name, StringComparer.OrdinalIgnoreCase);

    public void SetFavoriteArtist(string name, bool favorite)
    {
        if (favorite == IsFavoriteArtist(name)) return;
        _settings.Set(nameof(AppSettings.FavoriteArtists), s =>
        {
            if (favorite) s.FavoriteArtists.Add(name);
            else s.FavoriteArtists.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        });
        Changed?.Invoke();
    }

    /// <summary>A renamed artist stays a favorite and stays pinned, in the same place.</summary>
    public void RenameArtist(string oldName, string newName)
    {
        var oldKey = ArtistKey(oldName);
        bool fav = IsFavoriteArtist(oldName), pinned = IsPinned(oldKey);
        if (!fav && !pinned) return;
        _settings.Set(nameof(AppSettings.FavoriteArtists), s =>
        {
            Rename(s.FavoriteArtists, oldName, newName);
            Rename(s.QuickAccessPins, oldKey, ArtistKey(newName));
        });
        Changed?.Invoke();
    }

    /// <summary>Replaces an entry in place; a duplicate of the new value elsewhere is dropped.</summary>
    private static void Rename(List<string> list, string oldValue, string newValue)
    {
        int i = list.FindIndex(v => string.Equals(v, oldValue, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return;
        list[i] = newValue;
        for (int k = list.Count - 1; k >= 0; k--)
            if (k != i && string.Equals(list[k], newValue, StringComparison.OrdinalIgnoreCase)) list.RemoveAt(k);
    }

    public List<Artist> FavoriteArtists => _library.Artists.Where(a => IsFavoriteArtist(a.Name)).ToList();

    // ---------------- Quick access pins ----------------

    public static string Key(Track t) => "song:" + t.Path;
    public static string Key(Artist a) => "artist:" + a.Name;
    public static string ArtistKey(string name) => "artist:" + name;

    public bool IsPinned(string key) => _settings.Current.QuickAccessPins.Contains(key, StringComparer.OrdinalIgnoreCase);

    public void SetPinned(string key, bool pinned)
    {
        if (pinned == IsPinned(key)) return;
        // Quick access holds 10 tiles: at most 10 pins (favorites fill the rest).
        if (pinned && _settings.Current.QuickAccessPins.Count >= MaxQuickAccess)
        {
            Notifier.Show("Quick Access is full", $"Up to {MaxQuickAccess} items can be pinned. Unpin one first.");
            return;
        }
        _settings.Set(nameof(AppSettings.QuickAccessPins), s =>
        {
            if (pinned) s.QuickAccessPins.Add(key);
            else s.QuickAccessPins.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        });
        Changed?.Invoke();
    }

    /// <summary>Pinned items that still exist in the library, in pin order.</summary>
    public List<object> PinnedItems()
    {
        var list = new List<object>();
        foreach (var key in _settings.Current.QuickAccessPins)
        {
            if (key.StartsWith("song:") && _library.GetByPath(key[5..]) is { } t) list.Add(t);
            else if (key.StartsWith("artist:") && _library.FindArtist(key[7..]) is { } a) list.Add(a);
        }
        return list;
    }
}

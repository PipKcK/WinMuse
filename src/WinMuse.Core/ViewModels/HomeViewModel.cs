using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.ViewModels;

/// <summary>
/// Home: the greeting and library summary, Quick access, the automatic rows, Continue Listening and the Playlists row
/// with its order. The rows' collections are updated in place (cards that stay keep their covers); a collection is
/// replaced only when updating it in place would take too many moves, and the property then says it changed.
/// </summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly FavoritesService _favorites;
    private readonly ContinueListeningService _continue;
    private readonly PlaybackService _player;
    private readonly Func<DateTime> _now;

    /// <summary>Quick access shows at most this many tiles.</summary>
    public const int QuickAccessSize = FavoritesService.MaxQuickAccess;

    public HomeViewModel(LibraryService library, SettingsService settings, FavoritesService favorites, ContinueListeningService continueListening,
        PlaybackService player, Func<DateTime>? now = null)
    {
        _library = library; _settings = settings; _favorites = favorites; _continue = continueListening; _player = player;
        _now = now ?? (() => DateTime.Now);
    }

    private AppSettings S => _settings.Current;

    /// <summary>Everything again: the library, favorites or settings changed.</summary>
    public void Refresh()
    {
        Sync(ref _quick, BuildQuickAccess(), q => q.Key, (a, b) => a.SameAs(b), nameof(Quick));
        SyncRow(ref _recentlyAdded, LibraryService.RecentlyAddedId, true, nameof(RecentlyAdded));
        SyncRow(ref _mostPlayed, LibraryService.MostPlayedId, true, nameof(MostPlayed));
        // Long Time No Play waits for listening history (Settings > Debug can show it anyway).
        SyncRow(ref _rediscover, LibraryService.NotPlayedRecentlyId, _library.HasListeningHistory || S.ShowAllHomeRows, nameof(Rediscover));
        RefreshContinue();
        RefreshPlaylists();
        ArtFanAlbums = _library.Albums.Where(a => a.ArtPath != null).OrderByDescending(a => a.AddedUtc).Take(3).ToList();
        OnPropertyChanged(string.Empty);
    }

    // ---------------- Greeting ----------------

    public string Greeting => Format.Greeting(_now().Hour);
    /// <summary>The sun with Good Morning and Good Afternoon, the moon with Good Evening.</summary>
    public string GreetingGlyph => _now().Hour is >= 5 and < 17 ? "\uE706" : "\uE708";

    /// <summary>The time of day moved on (Home was shown again).</summary>
    public void RefreshGreeting() { OnPropertyChanged(nameof(Greeting)); OnPropertyChanged(nameof(GreetingGlyph)); }

    public bool HasMusic => _library.Tracks.Count > 0;
    private bool HasFolders => S.MusicFolders.Count > 0;

    public string Stats
    {
        get
        {
            var tracks = _library.Tracks;
            if (tracks.Count == 0) return HasFolders && _library.IsScanning ? "Scanning library…" : "Your library is empty";
            // Each part keeps its words together (no-break spaces), so a narrow card breaks the line only between parts.
            static string Keep(string s) => s.Replace(' ', '\u00A0');
            return string.Join(" · ", Keep(Format.Count(tracks.Count, "song", "songs")), Keep(Format.Count(_library.Albums.Count, "album", "albums")),
                Keep(Format.Count(_library.Artists.Count, "artist", "artists")),
                Keep($"{Format.LongTime(TimeSpan.FromMilliseconds(tracks.Sum(t => t.DurationMs)))} of music"));
        }
    }

    public bool ShowWelcome => !HasMusic && !_library.IsScanning;
    public string WelcomeTitle => HasFolders ? "No Songs Found" : "Set Up Your Library";
    public string WelcomeText => HasFolders
        ? $"Copy music into {AppPaths.MusicFolder} or add another folder. New files are detected automatically and their metadata, album art and lyrics are updated."
        : "Add the folders that contain your music. Files remain on this PC. Supported formats include MP3, FLAC, ALAC, AAC, Opus, Vorbis, WAV, AIFF, APE, WavPack and DSD.";

    /// <summary>The Music folder is already in the library: its button opens it instead of adding it.</summary>
    public bool MusicFolderAdded => S.MusicFolders.Contains(AppPaths.MusicFolder, StringComparer.OrdinalIgnoreCase);
    public string MusicFolderButtonText => MusicFolderAdded ? "Open Music Folder" : "Add Music Folder";

    /// <summary>Adds the Windows Music folder to the library (when it exists).</summary>
    public async Task AddMusicFolderAsync()
    {
        if (Directory.Exists(AppPaths.MusicFolder)) await _library.AddFolderAsync(AppPaths.MusicFolder);
        Refresh();
    }

    public async Task AddFolderAsync(string folder)
    {
        await _library.AddFolderAsync(folder);
        Refresh();
    }

    // ---------------- Resume ----------------

    public Track? ResumeTrack => _player.CurrentTrack;
    public bool CanResume => ResumeTrack != null;
    public string ResumeText => ResumeTrack is { } t ? $"{t.DisplayTitle} · {t.DisplayArtist}" : "";
    public string ResumeName => ResumeTrack is { } t ? $"Resume {t.DisplayTitle} by {t.DisplayArtist}" : "Resume";

    /// <summary>The song changed: only the Resume button depends on it.</summary>
    public void RefreshResume()
    {
        OnPropertyChanged(nameof(ResumeTrack)); OnPropertyChanged(nameof(CanResume));
        OnPropertyChanged(nameof(ResumeText)); OnPropertyChanged(nameof(ResumeName));
    }

    public void Resume() => _player.Play();
    public Task ShuffleAllAsync() => _player.PlayAsync(_library.Tracks, 0, shuffle: true);

    /// <summary>The three newest albums with covers, fanned out on the greeting.</summary>
    public IReadOnlyList<Album> ArtFanAlbums { get; private set; } = Array.Empty<Album>();

    // ---------------- Quick access ----------------

    private ObservableCollection<QuickEntry> _quick = new();
    public ObservableCollection<QuickEntry> Quick => _quick;

    /// <summary>
    /// Quick access, at most 10 tiles, in this order: pinned (up to 10), then favorites (as many as fit), then random
    /// tiles only for the places left (up to 2 artists and 3 songs, preferring ones with a photo or album art). Within
    /// each group, artists come before songs. Favorites and random picks already shown keep their places, so tiles don't
    /// reshuffle on every update.
    /// </summary>
    public List<QuickEntry> BuildQuickAccess()
    {
        var list = new List<QuickEntry>();
        var keys = new HashSet<string>();
        void Add(QuickEntry e) { if (list.Count < QuickAccessSize && keys.Add(e.Key)) list.Add(e); }
        void AddGroup(IEnumerable<QuickEntry> group) { foreach (var e in group.OrderBy(e => e.IsArtist ? 0 : 1)) Add(e); } // stable: keeps order inside
        QuickEntry Song(Track t) => new() { Track = t, IsPinned = _favorites.IsPinned(FavoritesService.Key(t)), IsFavorite = t.IsFavorite };
        QuickEntry ArtistTile(Artist a) => new() { Artist = a, IsPinned = _favorites.IsPinned(FavoritesService.Key(a)), IsFavorite = _favorites.IsFavoriteArtist(a.Name) };

        // Pinned, in pin order.
        AddGroup(_favorites.PinnedItems().Select(item => item is Track t ? Song(t) : ArtistTile((Artist)item)));

        // Favorites: ones already shown stay, the rest in random order.
        var shown = _quick.Select(q => q.Key).ToHashSet();
        var favorites = _favorites.FavoriteArtists.Select(ArtistTile).Concat(_favorites.FavoriteSongs.Select(Song)).ToList();
        AddGroup(favorites.Where(e => shown.Contains(e.Key)).Concat(favorites.Where(e => !shown.Contains(e.Key)).OrderBy(_ => Random.Shared.Next())));

        // Random: 2 artists and 3 songs; earlier picks first, then new ones with artwork before ones without.
        var artists = _library.Artists.Where(a => a.Name is not ("Unknown Artist" or "Various Artists")).ToList();
        var randomArtists = _quick.Where(q => q is { IsPinned: false, IsFavorite: false, Artist: not null } && !keys.Contains(q.Key))
            .Select(q => _library.FindArtist(q.Artist!.Name)).OfType<Artist>().Take(2).ToList();
        randomArtists.AddRange(artists.Where(a => !keys.Contains(FavoritesService.Key(a)) && !randomArtists.Any(r => r.Name == a.Name))
            .OrderBy(a => a.ImagePath != null ? 0 : a.ArtPath != null ? 1 : 2).ThenBy(_ => Random.Shared.Next()).Take(2 - randomArtists.Count));
        var randomSongs = _quick.Where(q => q is { IsPinned: false, IsFavorite: false, Track: not null } && _library.Tracks.Contains(q.Track!) && !keys.Contains(q.Key))
            .Select(q => q.Track!).Take(3).ToList();
        randomSongs.AddRange(_library.Tracks.Where(t => !keys.Contains(FavoritesService.Key(t)) && !randomSongs.Contains(t))
            .OrderBy(t => t.ArtPath != null ? 0 : 1).ThenBy(_ => Random.Shared.Next()).Take(3 - randomSongs.Count));
        AddGroup(randomArtists.Select(ArtistTile).Concat(randomSongs.Select(Song)));
        return list;
    }

    // ---------------- Automatic rows ----------------

    private ObservableCollection<Track> _recentlyAdded = new(), _mostPlayed = new(), _rediscover = new();
    public ObservableCollection<Track> RecentlyAdded => _recentlyAdded;
    public ObservableCollection<Track> MostPlayed => _mostPlayed;
    public ObservableCollection<Track> Rediscover => _rediscover;

    private void SyncRow(ref ObservableCollection<Track> row, long playlistId, bool ready, string property)
    {
        var tracks = ready ? _library.SystemPlaylist(playlistId)?.Tracks ?? new List<Track>() : new List<Track>();
        Sync(ref row, tracks, t => t, null, property);
    }

    // ---------------- Continue Listening ----------------

    private ObservableCollection<ContinueItem> _continueItems = new();
    public ObservableCollection<ContinueItem> Continue => _continueItems;

    /// <summary>
    /// Continue Listening cards (entries whose song is no longer in the library are skipped). Cards that stay are
    /// updated in place. Returns the cards whose song moved on (the next song of their playlist, album or artist).
    /// </summary>
    public IReadOnlyList<ContinueItem> RefreshContinue()
    {
        var current = _continueItems.ToDictionary(c => c.Key);
        var items = new List<ContinueItem>();
        var changed = new List<ContinueItem>();
        foreach (var e in _continue.Entries)
        {
            if (_continue.TrackOf(e) is not { } t) continue;
            if (current.TryGetValue($"{e.Kind}:{e.Key}", out var item)) { if (item.Update(e, t)) changed.Add(item); }
            else item = new ContinueItem(e, t);
            items.Add(item);
        }
        Sync(ref _continueItems, items, c => c.Key, null, nameof(Continue));
        return changed;
    }

    public Task ResumeAsync(ContinueItem item) => _continue.ResumeAsync(item.Entry);
    public void RemoveFromContinue(ContinueItem item) => _continue.Remove(item.Entry);

    // ---------------- Playlists ----------------

    private ObservableCollection<Playlist> _playlists = new();
    public ObservableCollection<Playlist> Playlists => _playlists;
    private readonly Dictionary<long, string> _playlistSignatures = new();

    public string PlaylistCountText => $"{_playlists.Count} {(_playlists.Count == 1 ? "playlist" : "playlists")}";

    public int PlaylistSortIndex
    {
        get => (int)S.HomePlaylistSort;
        set
        {
            var sort = (PlaylistSort)value;
            if (sort == S.HomePlaylistSort) return;
            var shown = _playlists.Select(p => p.Id).ToList();
            _settings.Set(nameof(AppSettings.HomePlaylistSort), s =>
            {
                // Custom starts from the order shown now.
                if (sort == PlaylistSort.Custom && s.PlaylistOrder.Count == 0) s.PlaylistOrder = shown;
                s.HomePlaylistSort = sort;
            });
            RefreshPlaylists();
        }
    }

    /// <summary>Playlists with songs, in the order picked for the row.</summary>
    public List<Playlist> SortedPlaylists()
    {
        var all = _library.Playlists.Where(p => p.Tracks.Count > 0).ToList();
        IEnumerable<Playlist> sorted = S.HomePlaylistSort switch
        {
            PlaylistSort.Alphabetical => all.OrderBy(p => p.Name, NaturalOrder.Instance),
            PlaylistSort.DateCreated => all.OrderByDescending(p => p.CreatedUtc).ThenByDescending(p => p.Id),
            PlaylistSort.Custom => all.OrderBy(p => S.PlaylistOrder.IndexOf(p.Id) is var i && i >= 0 ? i : int.MaxValue).ThenByDescending(p => p.Id),
            _ => all.OrderByDescending(_library.LastActivity).ThenByDescending(p => p.Id),
        };
        return sorted.ToList();
    }

    /// <summary>
    /// The Playlists row, updated in place: only playlists whose name or songs changed are rebuilt, and a new order is
    /// applied by moving rows. Rebuilding every row on the way back to Home made it visibly refresh and reload covers.
    /// </summary>
    public void RefreshPlaylists()
    {
        var list = SortedPlaylists();
        static string Signature(Playlist p) => p.Name + "\u001f" + string.Join(",", p.Tracks.Select(t => t.Id));
        foreach (var p in _playlists.ToList())
            if (!_playlistSignatures.TryGetValue(p.Id, out var sig) || sig != Signature(p)) _playlists.Remove(p); // re-added fresh below
        Sync(ref _playlists, list, p => p.Id, null, nameof(Playlists));
        _playlistSignatures.Clear();
        foreach (var p in list) { _playlistSignatures[p.Id] = Signature(p); p.CanMove = S.HomePlaylistSort == PlaylistSort.Custom; }
        OnPropertyChanged(nameof(PlaylistCountText));
        OnPropertyChanged(nameof(PlaylistSortIndex));
    }

    /// <summary>Moves a playlist one place up (-1) or down (+1) in the custom order.</summary>
    public void MovePlaylist(Playlist p, int by)
    {
        var order = _playlists.Select(x => x.Id).ToList();
        int i = order.IndexOf(p.Id), j = i + by;
        if (i < 0 || j < 0 || j >= order.Count) return;
        (order[i], order[j]) = (order[j], order[i]);
        _settings.Set(nameof(AppSettings.PlaylistOrder), s => s.PlaylistOrder = order);
        RefreshPlaylists();
    }

    public void OpenPlaylist(Playlist p) => _library.TouchPlaylist(p);

    // ---------------- Helpers ----------------

    /// <summary>Updates <paramref name="current"/> in place, or replaces it (and says so) when that would take too long.</summary>
    private void Sync<T>(ref ObservableCollection<T> current, IList<T> items, Func<T, object> key, Func<T, T, bool>? same, string property)
    {
        var bound = CollectionSync.SyncOrReplace(current, items, key, same);
        if (ReferenceEquals(bound, current)) return;
        current = bound;
        OnPropertyChanged(property);
    }
}

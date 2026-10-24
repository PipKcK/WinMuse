using System.Collections.Concurrent;
using WinMuse.Helpers;
using WinMuse.Models;

namespace WinMuse.Services;

public sealed record ScanProgress(int Done, int Total, string? Current);

/// <summary>In-memory library backed by SQLite. All public mutation happens on the UI thread.</summary>
public sealed partial class LibraryService
{
    private readonly LibraryDatabase _db = new();
    private readonly TagService _tags;
    private readonly ArtworkService _art;
    private readonly SettingsService _settings;
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly Debouncer _watchDebounce = new(TimeSpan.FromSeconds(2.5));

    private List<Track> _tracks = new();
    private Dictionary<long, Track> _byId = new();
    private List<Album>? _albums;
    private List<Artist>? _artists;
    private List<Genre>? _genres;

    public List<Playlist> Playlists { get; } = new();

    // Artist photos (name -> image path; null path = looked up but not found).
    private readonly Dictionary<string, (string? path, bool tried, bool byUser)> _artistImages = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    public string? GetArtistImage(string name) => _artistImages.TryGetValue(name, out var v) ? v.path : null;
    public bool ArtistImageTried(string name) => _artistImages.TryGetValue(name, out var v) && v.tried;

    /// <summary>Source state of an artist's photo (an <see cref="ItemState"/> value).</summary>
    public int ArtistImageState(string name)
    {
        if (name is "Unknown Artist" or "Various Artists") return ItemState.NotFound;
        if (!_artistImages.TryGetValue(name, out var v)) return ItemState.Missing;
        if (v.path != null) return v.byUser ? ItemState.EditedByUser : ItemState.Downloaded;
        return v.tried ? ItemState.NotFound : ItemState.Missing;
    }

    /// <summary>
    /// Overall fetch status: -1 when anything was edited by the user, 1 when metadata, album art, artist image and
    /// lyrics are all in place, otherwise 0.
    /// </summary>
    public int SyncState(Track t)
    {
        var artist = ArtistImageStateOf(t);
        if (t.MetaState == ItemState.EditedByUser || t.ArtState == ItemState.EditedByUser || t.LyricsState == ItemState.EditedByUser || artist == ItemState.EditedByUser)
            return -1;
        bool complete = t.MetaState == ItemState.Downloaded && ItemState.IsSatisfied(t.ArtState) && ItemState.IsSatisfied(t.LyricsState)
                        && t.ArtistNames.All(n => n == "Unknown Artist" || ArtistImageState(n) == ItemState.Downloaded);
        return complete ? 1 : 0;
    }

    /// <summary>Artist photo state for a song with one or more artists: edited if any photo was chosen by you,
    /// pending while any is missing, downloaded when all are, otherwise not found.</summary>
    public int ArtistImageStateOf(Track t)
    {
        var states = t.ArtistNames.Select(ArtistImageState).ToList();
        if (states.Contains(ItemState.EditedByUser)) return ItemState.EditedByUser;
        if (states.Contains(ItemState.Missing)) return ItemState.Missing;
        return states.All(s => s == ItemState.Downloaded) ? ItemState.Downloaded : ItemState.NotFound;
    }

    public int ExcludedCount => _excluded.Count;

    public void SetArtistImage(string name, string? path, bool tried = true, bool byUser = false)
    {
        _artistImages[name] = (path, tried, byUser);
        _db.SaveArtistImage(name, path, tried, byUser);
        _artists = null;
        _changedDebounce.Run(() => App.UI.TryEnqueue(() => Changed?.Invoke()));
    }

    /// <summary>Records that songs were just credited to these artists (Artists page, "Recently changed").</summary>
    public void TouchArtists(IEnumerable<string> names)
    {
        var keys = names.Select(n => n.ToLowerInvariant()).Distinct().ToList();
        if (keys.Count == 0) return;
        var now = Format.NowUnix();
        App.UI.TryEnqueue(() =>
        {
            _settings.Set(nameof(AppSettings.ArtistActivity), s => { foreach (var k in keys) s.ArtistActivity[k] = now; });
            _artists = null;
        });
    }

    /// <summary>A renamed artist keeps their photo (unless the new name already has one).</summary>
    public void MoveArtistImage(string oldName, string newName)
    {
        if (!_artistImages.TryGetValue(oldName, out var old) || old.path == null || GetArtistImage(newName) != null) return;
        SetArtistImage(newName, old.path, old.tried, old.byUser);
    }

    public void ResetArtistImageAttempts()
    {
        foreach (var k in _artistImages.Where(kv => kv.Value.path == null).Select(kv => kv.Key).ToList()) _artistImages.Remove(k);
        _db.ResetArtistImageAttempts();
    }

    /// <summary>Saves source-state changes without a full refresh of every page.</summary>
    public void SaveStates(IEnumerable<Track> tracks) => SaveTracks(tracks);
    public IReadOnlyList<Track> Tracks => _tracks;
    public bool IsScanning { get; private set; }

    public event Action? Changed;
    public event Action? PlaylistsChanged;
    public event Action<ScanProgress>? ScanProgressChanged;
    public event Action? ScanCompleted;
    /// <summary>Songs that appeared in the library folders during a scan (not ones already in the library).</summary>
    public event Action<IReadOnlyList<Track>>? TracksAdded;

    public LibraryService(TagService tags, ArtworkService art, SettingsService settings)
    {
        _tags = tags; _art = art; _settings = settings;
    }

    // ---------------- Per-song settings ----------------

    /// <summary>A song's own equalizer (null = none saved) and lyrics offset (positive = lyrics later).</summary>
    public sealed record SongPrefs(float[]? EqBands, float EqPreamp, int LyricsOffsetMs);

    private readonly Dictionary<string, SongPrefs> _songPrefs = new(StringComparer.OrdinalIgnoreCase);

    public SongPrefs PrefsFor(Track t) => _songPrefs.GetValueOrDefault(t.Path) ?? new SongPrefs(null, 0, 0);

    public void SaveSongEq(Track t, float[]? bands, float preamp) => SavePrefs(t, PrefsFor(t) with { EqBands = bands?.ToArray(), EqPreamp = bands == null ? 0 : preamp });

    public void SaveLyricsOffset(Track t, int ms) => SavePrefs(t, PrefsFor(t) with { LyricsOffsetMs = ms });

    private void SavePrefs(Track t, SongPrefs p)
    {
        if (p.EqBands == null && p.LyricsOffsetMs == 0) _songPrefs.Remove(t.Path); else _songPrefs[t.Path] = p;
        var bands = p.EqBands == null ? null : string.Join(",", p.EqBands.Select(b => b.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        try { _db.SaveSongSettings(t.Path, bands, p.EqPreamp, p.LyricsOffsetMs); }
        catch (Exception ex) { AppPaths.Log("Song settings: " + ex.Message); }
    }

    public async Task LoadAsync()
    {
        var (tracks, playlists, images) = await Task.Run(() => (_db.LoadTracks(), _db.LoadPlaylists(), _db.LoadArtistImages()));
        foreach (var (path, eq, preamp, offset) in await Task.Run(_db.LoadSongSettings))
        {
            var bands = eq?.Split(',').Select(x => float.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
            _songPrefs[path] = new SongPrefs(bands is { Length: 10 } ? bands : null, (float)preamp, (int)offset);
        }
        foreach (var (name, path, tried, byUser) in images) _artistImages[name] = (path != null && File.Exists(path) ? path : null, tried, byUser);
        _excluded = await Task.Run(_db.LoadExcluded);
        SetTracks(tracks);
        Playlists.Clear();
        foreach (var (p, ids) in playlists)
        {
            p.Tracks = ids.Select(id => _byId.GetValueOrDefault(id)).Where(t => t != null).ToList()!;
            Playlists.Add(p);
        }
        ResolveSmartPlaylists();
        Changed?.Invoke();
        PlaylistsChanged?.Invoke();
        StartWatching();
    }

    private void SetTracks(List<Track> tracks)
    {
        _tracks = tracks;
        _byId = tracks.ToDictionary(t => t.Id);
        _albums = null; _artists = null; _genres = null;
    }

    public Track? GetById(long id) => _byId.GetValueOrDefault(id);
    public Track? GetByPath(string path) => _tracks.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));

    // ---------------- Grouping ----------------

    public List<Album> Albums => _albums ??= _tracks
        .GroupBy(t => t.AlbumKey)
        .Select(g =>
        {
            var ordered = g.OrderBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ThenBy(t => t.DisplayTitle).ToList();
            var first = ordered[0];
            return new Album
            {
                Key = g.Key,
                Title = first.DisplayAlbum,
                Artist = first.DisplayAlbumArtist,
                Year = ordered.Max(t => t.Year),
                Genre = ordered.Select(t => t.Genre).FirstOrDefault(x => !string.IsNullOrEmpty(x)),
                ArtPath = ordered.Select(t => t.ArtPath).FirstOrDefault(p => p != null),
                Tracks = ordered,
                AddedUtc = ordered.Max(t => t.AddedUtc),
            };
        })
        .OrderBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public List<Artist> Artists => _artists ??= BuildArtists();

    /// <summary>
    /// One artist per credited name: a song by "A & B" (or "A feat. B", "A; B") is on both A's and B's page. An artist's
    /// albums are the ones they appear on, their own (album artist) first.
    /// </summary>
    private List<Artist> BuildArtists()
    {
        var tracks = new Dictionary<string, List<Track>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _tracks)
            foreach (var n in t.ArtistNames)
            {
                if (!tracks.TryGetValue(n, out var list)) tracks[n] = list = new List<Track>();
                list.Add(t);
            }
        var albums = new Dictionary<string, List<Album>>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in Albums)
            foreach (var n in a.Tracks.SelectMany(t => t.ArtistNames).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!albums.TryGetValue(n, out var list)) albums[n] = list = new List<Album>();
                list.Add(a);
            }
        return tracks.Select(kv => new Artist
            {
                Name = kv.Key,
                ImagePath = GetArtistImage(kv.Key),
                Tracks = kv.Value.OrderBy(t => t.DisplayAlbum).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber).ToList(),
                ChangedUtc = Math.Max(_settings.Current.ArtistActivity.GetValueOrDefault(kv.Key.ToLowerInvariant()), kv.Value.Max(t => t.AddedUtc)),
                Albums = (albums.GetValueOrDefault(kv.Key) ?? new List<Album>())
                    .OrderByDescending(a => ArtistCredit.Contains(a.Artist, kv.Key)).ThenByDescending(a => a.Year).ToList(),
            })
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public List<Genre> Genres => _genres ??= _tracks
        .SelectMany(t => (t.Genre ?? "Unknown").Split(new[] { ';', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(g => (g, t)))
        .GroupBy(x => x.g, StringComparer.OrdinalIgnoreCase)
        .Select(g => new Genre
        {
            Name = g.First().g,
            Tracks = g.Select(x => x.t).Distinct().ToList(),
            ArtPaths = g.Select(x => x.t.ArtPath).Where(p => p != null).Distinct().Take(4).ToList()!,
        })
        .OrderByDescending(g => g.Tracks.Count)
        .ToList();

    public Album? FindAlbum(string key) => Albums.FirstOrDefault(a => a.Key == key);
    public Artist? FindArtist(string name) => Artists.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

    public (List<Track> songs, List<Album> albums, List<Artist> artists) Search(string query, int max = 200)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool Hit(params string?[] fields) => terms.All(term => fields.Any(f => f?.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true));
        var songs = _tracks.Where(t => Hit(t.DisplayTitle, t.Artist, t.Album, t.AlbumArtist, t.Genre, t.Composer)).Take(max).ToList();
        var albums = Albums.Where(a => Hit(a.Title, a.Artist)).Take(max).ToList();
        var artists = Artists.Where(a => Hit(a.Name)).Take(max).ToList();
        return (songs, albums, artists);
    }

    // ---------------- Scanning ----------------

    public async Task ScanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        try
        {
            var folders = _settings.Current.MusicFolders.Where(Directory.Exists).ToList();
            var existing = _tracks.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
            var result = await Task.Run(() => ScanCore(folders, existing));
            if (result.changed.Count > 0 || result.removed.Count > 0)
            {
                var merged = _tracks.Where(t => !result.removed.Contains(t)).ToList();
                var added = new List<Track>();
                foreach (var t in result.changed)
                {
                    if (existing.TryGetValue(t.Path, out var old))
                    {
                        CopyScanned(t, old);
                        old.Refresh();
                    }
                    else { merged.Add(t); added.Add(t); }
                }
                SetTracks(merged);
                ResolvePlaylistsAfterChange();
                Changed?.Invoke();
                if (added.Count > 0) TracksAdded?.Invoke(added);
            }
        }
        finally
        {
            IsScanning = false;
            ScanProgressChanged?.Invoke(new ScanProgress(0, 0, null));
            ScanCompleted?.Invoke();
        }
    }

    private (List<Track> changed, HashSet<Track> removed) ScanCore(List<string> folders, Dictionary<string, Track> existing)
    {
        var files = new List<string>();
        foreach (var folder in folders)
        {
            try
            {
                files.AddRange(Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System })
                    .Where(AppPaths.IsAudio));
            }
            catch (Exception ex) { AppPaths.Log($"Enumerate {folder}: {ex.Message}"); }
        }

        files.RemoveAll(_excluded.Contains);
        var seen = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var removed = existing.Values.Where(t => !seen.Contains(t.Path)).ToHashSet();

        var todo = files.Where(f =>
        {
            if (!existing.TryGetValue(f, out var t)) return true;
            try
            {
                var fi = new FileInfo(f);
                return new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeSeconds() != t.ModifiedUtc || fi.Length != t.FileSize;
            }
            catch { return false; }
        }).ToList();

        var changed = new ConcurrentBag<Track>();
        int done = 0;
        var lastReport = DateTime.MinValue;
        Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 6) }, f =>
        {
            try
            {
                var t = _tags.Read(f);
                if (existing.TryGetValue(f, out var old))
                {
                    t.Id = old.Id;
                    t.AddedUtc = old.AddedUtc;
                }
                changed.Add(t);
            }
            catch (Exception ex) { AppPaths.Log($"Read {f}: {ex.Message}"); }
            var n = Interlocked.Increment(ref done);
            if ((DateTime.Now - lastReport).TotalMilliseconds > 150)
            {
                lastReport = DateTime.Now;
                App.UI.TryEnqueue(() => ScanProgressChanged?.Invoke(new ScanProgress(n, todo.Count, Path.GetFileName(f))));
            }
        });

        var list = changed.ToList();
        // Preserve library-only edits and stats for changed files.
        foreach (var t in list)
        {
            if (existing.TryGetValue(t.Path, out var old) && old.EditedLocally)
            {
                t.Title = old.Title; t.Artist = old.Artist; t.AlbumArtist = old.AlbumArtist; t.Album = old.Album;
                t.Genre = old.Genre; t.Year = old.Year; t.TrackNumber = old.TrackNumber; t.TrackCount = old.TrackCount;
                t.DiscNumber = old.DiscNumber; t.Composer = old.Composer; t.Comment = old.Comment; t.Lyrics = old.Lyrics;
                t.ArtPath = old.ArtPath ?? t.ArtPath; t.EditedLocally = true;
            }
            if (old != null)
            {
                t.PlayCount = old.PlayCount; t.LastPlayedUtc = old.LastPlayedUtc; t.Rating = old.Rating; t.AutoFixed = old.AutoFixed;
                t.MetaState = old.MetaState;
                if (old.ArtState != ItemState.Missing) t.ArtState = old.ArtState;
                if (old.LyricsState != ItemState.Missing) t.LyricsState = old.LyricsState;
            }
        }

        _db.UpsertTracks(list);
        if (removed.Count > 0) _db.DeleteTracks(removed.Select(t => t.Id));
        return (list, removed);
    }

    private static void CopyScanned(Track from, Track to)
    {
        to.Title = from.Title; to.Artist = from.Artist; to.AlbumArtist = from.AlbumArtist; to.Album = from.Album; to.Genre = from.Genre;
        to.Year = from.Year; to.TrackNumber = from.TrackNumber; to.TrackCount = from.TrackCount; to.DiscNumber = from.DiscNumber;
        to.Composer = from.Composer; to.Comment = from.Comment; to.Lyrics = from.Lyrics; to.Bpm = from.Bpm;
        to.DurationMs = from.DurationMs; to.Bitrate = from.Bitrate; to.SampleRate = from.SampleRate; to.Channels = from.Channels;
        to.BitsPerSample = from.BitsPerSample; to.Codec = from.Codec; to.FileSize = from.FileSize; to.ModifiedUtc = from.ModifiedUtc;
        to.ArtPath = from.ArtPath; to.EditedLocally = from.EditedLocally;
        to.MetaState = from.MetaState; to.ArtState = from.ArtState; to.LyricsState = from.LyricsState;
    }

    // ---------------- Folders & watching ----------------

    public async Task AddFolderAsync(string folder)
    {
        if (_settings.Current.MusicFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
        _settings.Set(nameof(AppSettings.MusicFolders), s => s.MusicFolders.Add(folder));
        StartWatching();
        await ScanAsync();
    }

    public async Task RemoveFolderAsync(string folder)
    {
        _settings.Set(nameof(AppSettings.MusicFolders), s => s.MusicFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)));
        StartWatching();
        await ScanAsync();
    }

    public void StartWatching()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
        foreach (var folder in _settings.Current.MusicFolders.Where(Directory.Exists))
        {
            try
            {
                var w = new FileSystemWatcher(folder) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler h = (_, e) => OnFsEvent(e.FullPath);
                w.Created += h; w.Changed += h; w.Deleted += h;
                w.Renamed += (_, e) => OnFsEvent(e.FullPath);
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
            }
            catch (Exception ex) { AppPaths.Log("Watch failed: " + ex.Message); }
        }
    }

    private void OnFsEvent(string path)
    {
        var ext = Path.GetExtension(path);
        if (ext.Length > 0 && !AppPaths.IsAudio(path) && !ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".png", StringComparison.OrdinalIgnoreCase)) return;
        if (Path.GetDirectoryName(path) is { } dir) _art.ForgetFolder(dir);
        _watchDebounce.Run(() => App.UI.TryEnqueue(async () => await ScanAsync()));
    }

    // ---------------- Track updates ----------------

    /// <summary>Persists edited tracks and notifies listeners.</summary>
    public void SaveTracks(IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        _db.UpsertTracks(list);
        _albums = null; _artists = null; _genres = null;
        foreach (var t in list) t.Refresh();
        // Debounced so batch updates (automatic metadata processing) don't refresh every page per song.
        _changedDebounce.Run(() => App.UI.TryEnqueue(() =>
        {
            _albums = null; _artists = null; _genres = null;
            ResolvePlaylistsAfterChange();
            Changed?.Invoke();
        }));
    }

    private readonly Debouncer _changedDebounce = new(TimeSpan.FromMilliseconds(350));

    /// <summary>Removes songs from the library only (files stay on disk) and remembers them so rescans skip them.</summary>
    public void RemoveTracks(IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        foreach (var t in list) _excluded.Add(t.Path);
        _db.AddExcluded(list.Select(t => t.Path));
        Forget(list);
    }

    /// <summary>Brings removed songs back on the next scan.</summary>
    public async Task RestoreRemovedAsync()
    {
        _excluded.Clear();
        _db.ClearExcluded();
        await ScanAsync();
    }

    /// <summary>Moves the files (and their .lrc lyrics) to the Recycle Bin and removes them from the library.</summary>
    public List<string> DeleteFiles(IEnumerable<Track> tracks)
    {
        var errors = new List<string>();
        var deleted = new List<Track>();
        foreach (var t in tracks)
        {
            try
            {
                if (File.Exists(t.Path))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(t.Path, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                var lrc = Path.ChangeExtension(t.Path, ".lrc");
                if (File.Exists(lrc))
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(lrc, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                deleted.Add(t);
            }
            catch (Exception ex) { errors.Add($"{t.FileName}: {ex.Message}"); }
        }
        Forget(deleted);
        return errors;
    }

    private void Forget(List<Track> list)
    {
        var set = list.ToHashSet();
        _db.DeleteTracks(set.Select(t => t.Id));
        SetTracks(_tracks.Where(t => !set.Contains(t)).ToList());
        foreach (var p in Playlists.Where(p => !p.IsSmart)) p.Tracks.RemoveAll(set.Contains);
        ResolvePlaylistsAfterChange();
        Changed?.Invoke();
    }

    public void SetAutoFixed(IEnumerable<Track> tracks, bool value)
    {
        var list = tracks.ToList();
        foreach (var t in list) t.AutoFixed = value;
        _db.SetAutoFixed(list, value);
    }

    public void RecordPlay(Track t)
    {
        t.PlayCount++;
        t.LastPlayedUtc = Format.NowUnix();
        Task.Run(() => _db.UpdatePlayStats(t));
        _albums = null;
    }

    public void SetRating(Track t, int rating)
    {
        t.Rating = rating;
        Task.Run(() => _db.UpdateRating(t));
        t.Refresh();
    }

    // ---------------- Playlists ----------------

    public Playlist CreatePlaylist(string name, string? description = null, SmartPlaylistDefinition? rules = null, IEnumerable<Track>? tracks = null)
    {
        var p = new Playlist { Name = name, Description = description, CreatedUtc = Format.NowUnix(), IsSmart = rules != null, Rules = rules };
        _db.SavePlaylist(p);
        if (tracks != null && rules == null)
        {
            p.Tracks = tracks.ToList();
            _db.SetPlaylistItems(p.Id, p.Tracks.Select(t => t.Id));
        }
        Playlists.Add(p);
        if (p.IsSmart) ResolveSmartPlaylists();
        PlaylistsChanged?.Invoke();
        return p;
    }

    public void UpdatePlaylist(Playlist p)
    {
        _db.SavePlaylist(p);
        if (p.IsSmart) ResolveSmartPlaylists();
        p.Refresh();
        PlaylistsChanged?.Invoke();
    }

    public void DeletePlaylist(Playlist p)
    {
        _db.DeletePlaylist(p.Id);
        Playlists.Remove(p);
        PlaylistsChanged?.Invoke();
    }

    /// <summary>Records that a playlist was played, opened or changed (for "Recent activity" sorting).</summary>
    public void TouchPlaylist(Playlist p)
    {
        if (p.IsSystem) return; // automatic playlists aren't sorted with yours
        _settings.Set(nameof(AppSettings.PlaylistActivity), s => s.PlaylistActivity[p.Id] = Format.NowUnix());
    }

    public long LastActivity(Playlist p) => _settings.Current.PlaylistActivity.TryGetValue(p.Id, out var t) ? t : p.CreatedUtc;

    public void AddToPlaylist(Playlist p, IEnumerable<Track> tracks)
    {
        TouchPlaylist(p);
        if (p.IsSmart) return;
        p.Tracks.AddRange(tracks);
        _db.SetPlaylistItems(p.Id, p.Tracks.Select(t => t.Id));
        p.Refresh();
        PlaylistsChanged?.Invoke();
    }

    public void SetPlaylistTracks(Playlist p, IEnumerable<Track> tracks)
    {
        TouchPlaylist(p);
        if (p.IsSmart) return;
        p.Tracks = tracks.ToList();
        _db.SetPlaylistItems(p.Id, p.Tracks.Select(t => t.Id));
        p.Refresh();
        PlaylistsChanged?.Invoke();
    }

    private void ResolveSmartPlaylists()
    {
        foreach (var p in Playlists.Where(p => p.IsSmart))
        {
            p.Tracks = p.Rules?.Apply(_tracks).ToList() ?? new();
            p.Refresh();
        }
    }

    private void ResolvePlaylistsAfterChange()
    {
        ResolveSmartPlaylists();
        PlaylistsChanged?.Invoke();
    }
}

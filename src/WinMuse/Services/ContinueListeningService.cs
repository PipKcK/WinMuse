using System.ComponentModel;
using WinMuse.Helpers;
using WinMuse.Models;

namespace WinMuse.Services;

/// <summary>One remembered place: what was being played (playlist, album, artist or song), and the song and position reached.</summary>
public sealed class ContinueEntry
{
    public PlayContextKind Kind { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string TrackPath { get; set; } = "";
    public double PositionSeconds { get; set; }
    public long UpdatedUtc { get; set; }
}

/// <summary>
/// Continue Listening on Home: every playlist, album, artist or single song you play is remembered with the song and
/// position you reached, newest first, so it can be resumed. Playing from the queue, Shuffle All or a selection is
/// not remembered. Entries are kept in settings and survive restarts.
/// </summary>
public sealed class ContinueListeningService
{
    public const int MaxEntries = 20;

    private readonly SettingsService _settings;
    private readonly LibraryService _library;
    private readonly PlaybackService _player;
    private DateTime _lastSave;

    /// <summary>Raised on the UI thread when an entry is added, moves, changes song or is removed (not for position updates).</summary>
    public event Action? Changed;

    public ContinueListeningService(SettingsService settings, LibraryService library, PlaybackService player)
    {
        _settings = settings; _library = library; _player = player;
        _player.TrackChanged += _ => Update();
        _player.PropertyChanged += OnPlayerChanged;
    }

    public IReadOnlyList<ContinueEntry> Entries => _settings.Current.ContinueListening;

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaybackService.IsPlaying)) Update(); // keep the exact spot on pause
        else if (e.PropertyName == nameof(PlaybackService.Position) && DateTime.UtcNow - _lastSave > TimeSpan.FromSeconds(5)) Update();
    }

    /// <summary>Records the current song and position for the playing context.</summary>
    private void Update()
    {
        if (_player.Context is not { } ctx || _player.CurrentTrack is not { } track) return;
        // Songs added to the queue from elsewhere don't belong to what is being continued.
        if (!Belongs(ctx, track)) return;
        _lastSave = DateTime.UtcNow;
        var list = _settings.Current.ContinueListening;
        int i = list.FindIndex(e => e.Kind == ctx.Kind && e.Key == ctx.Key);
        var entry = i >= 0 ? list[i] : new ContinueEntry { Kind = ctx.Kind, Key = ctx.Key };
        bool moved = i != 0 || entry.TrackPath != track.Path || entry.Name != ctx.Name;
        _settings.Set(nameof(AppSettings.ContinueListening), s =>
        {
            entry.Name = ctx.Name;
            entry.TrackPath = track.Path;
            entry.PositionSeconds = _player.Position;
            entry.UpdatedUtc = Format.NowUnix();
            if (i != 0)
            {
                if (i > 0) s.ContinueListening.RemoveAt(i);
                s.ContinueListening.Insert(0, entry);
                while (s.ContinueListening.Count > MaxEntries) s.ContinueListening.RemoveAt(s.ContinueListening.Count - 1);
            }
        });
        if (moved) Changed?.Invoke();
    }

    private bool Belongs(PlayContext ctx, Track t) => ctx.Kind switch
    {
        PlayContextKind.Song => t.Path == ctx.Key,
        _ => Resolve(ctx.Kind, ctx.Key) is { } tracks && tracks.Contains(t),
    };

    /// <summary>The songs of a remembered context, or null if it no longer exists.</summary>
    public List<Track>? Resolve(PlayContextKind kind, string key) => kind switch
    {
        PlayContextKind.Album => _library.FindAlbum(key)?.Tracks,
        PlayContextKind.Artist => _library.FindArtist(key)?.Tracks,
        PlayContextKind.Playlist => long.TryParse(key, out var id)
            ? (id < 0 ? _library.SystemPlaylist(id) : _library.Playlists.FirstOrDefault(p => p.Id == id))?.Tracks
            : null,
        _ => _library.GetByPath(key) is { } t ? new List<Track> { t } : null,
    };

    public Track? TrackOf(ContinueEntry e) => _library.GetByPath(e.TrackPath);

    /// <summary>Picks up where the entry left off: its playlist/album/artist from the saved song and position.</summary>
    public async Task ResumeAsync(ContinueEntry e)
    {
        var track = TrackOf(e);
        var tracks = Resolve(e.Kind, e.Key);
        var context = new PlayContext(e.Kind, e.Key, e.Name);
        if (tracks == null || tracks.Count == 0)
        {
            // The playlist/album/artist is gone: play the song on its own.
            if (track == null) { Remove(e); return; }
            tracks = new List<Track> { track };
            context = PlayContext.Of(track);
        }
        int index = track != null ? Math.Max(0, tracks.IndexOf(track)) : 0;
        var startAt = track != null && tracks[index] == track && e.PositionSeconds > 2 && e.PositionSeconds < track.Duration.TotalSeconds - 5
            ? TimeSpan.FromSeconds(e.PositionSeconds) : (TimeSpan?)null;
        if (e.Kind == PlayContextKind.Playlist && long.TryParse(e.Key, out var id) && _library.Playlists.FirstOrDefault(p => p.Id == id) is { } pl)
            _library.TouchPlaylist(pl);
        await _player.PlayAsync(tracks, index, context: context, startAt: startAt);
    }

    public void Remove(ContinueEntry e)
    {
        _settings.Set(nameof(AppSettings.ContinueListening), s => s.ContinueListening.RemoveAll(x => x.Kind == e.Kind && x.Key == e.Key));
        Changed?.Invoke();
    }
}

using WinMuse.Helpers;
using WinMuse.Models;

namespace WinMuse.Services;

/// <summary>
/// Processes songs new to the library (and, on the very first launch, the whole library): identifies them online, applies confident matches, and downloads
/// missing album art and lyrics. Each song is processed once; afterwards user edits persist until the user runs
/// Sync on the song or Sync in Settings.
/// </summary>
public sealed class AutoFixService : IDisposable
{
    private readonly LibraryService _library;
    private readonly MetadataEditService _metadata;
    private readonly LyricsService _lyrics;
    private readonly SettingsService _settings;
    private CancellationTokenSource _cts = new();
    private bool _rerun;
    private bool _force;
    private bool _fingerprintRejected; // per run: stop retrying AcoustID after the key is rejected
    private readonly List<Track> _redo = new(); // songs the user asked to sync individually (Source page)
    // Songs that appeared in the library folders while the app runs (on the very first launch: the whole library).
    // Automatic processing only ever looks at these; the whole library is only processed by a manual Sync.
    private readonly HashSet<Track> _new = new();

    public void AddNew(IEnumerable<Track> tracks)
    {
        foreach (var t in tracks) _new.Add(t);
        _ = RunPendingAsync();
    }
    // Downloaded songs: their embedded picture is often wrong, so a cover found online replaces it (none found: kept).
    private readonly HashSet<Track> _replaceArt = new();

    public bool IsRunning { get; private set; }
    /// <summary>While true nothing is processed (the first-launch API key dialog is open).</summary>
    public bool IsHeld { get; set; }

    /// <summary>(done, total, current song) on the UI thread; total 0 means finished.</summary>
    public event Action<int, int, string?>? Progress;

    private readonly PlaybackService _player;
    private readonly IUiDispatcher _ui;
    private readonly INotifier _notifier;

    public AutoFixService(LibraryService library, MetadataEditService metadata, LyricsService lyrics, SettingsService settings,
        PlaybackService player, IUiDispatcher ui, INotifier notifier)
    {
        _library = library; _metadata = metadata; _lyrics = lyrics; _settings = settings;
        _player = player; _ui = ui; _notifier = notifier;
    }

    /// <summary>Songs processed at the same time. Rate-limited services (MusicBrainz, iTunes) keep their own limits.</summary>
    private const int Parallelism = 4;

    /// <summary>Processes every song not yet handled. Safe to call repeatedly; extra calls queue one more pass.</summary>
    public async Task RunPendingAsync()
    {
        if (IsHeld) return;
        if (IsRunning) { _rerun = true; return; }
        var s = _settings.Current;
        bool auto = s.AutoFixMetadata || s.AutoFetchArt || s.AutoFetchLyrics;
        if (!_force && !auto && _redo.Count == 0) return;

        IsRunning = true;
        _fingerprintRejected = false;
        // This run's token: Stop() cancels it and hands later runs a fresh one, so every worker here must check this one.
        var token = _cts.Token;
        var artTried = new HashSet<string>();
        try
        {
            do
            {
                _rerun = false;
                // Automatic mode: songs never processed. Sync: every song that isn't complete (flag 0) or was edited by the user (flag -1).
                // Songs synced individually go first.
                var fresh = _new.ToList();
                _new.Clear();
                var basis = _sync ? _library.Tracks.Where(t => _library.SyncState(t) != 1)
                          : auto ? fresh.Where(t => !t.AutoFixed && _library.Tracks.Contains(t)) : Enumerable.Empty<Track>();
                var queue = _redo.ToList().Concat(basis.Where(t => t.IsCheckable && !_redo.Contains(t))).ToList();
                // Songs of one album (same folder, same album label) are handled together, so the album can be decided as a whole.
                var pending = new Queue<List<Track>>(queue.GroupBy(AlbumGroup).Select(g => g.ToList()));
                int total = queue.Count, done = 0, busyCount = 0;
                bool offline = false;
                // Fewer songs at once while music plays: each one is decoded to fingerprint it, which competes with playback.
                int workers = _player.IsPlaying ? 2 : Parallelism;
                using var gate = new SemaphoreSlim(workers);

                // One step for one song. False: the song is finished for this run (failed, skipped, or stopped).
                async Task<bool> Step(Track t, Func<Task> work)
                {
                    try
                    {
                        await gate.WaitAsync(token);
                        try { if (offline) return false; await work(); return true; }
                        finally { gate.Release(); }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (ServiceBusyException ex)
                    {
                        AppPaths.Log($"Retrying later ({ex.Service} busy): {t.DisplayTitle}");
                        busyCount++;
                    }
                    catch (HttpRequestException ex) when (ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError)
                    {
                        // Offline: keep songs pending and try again next time.
                        AppPaths.Log("Automatic metadata paused (offline): " + ex.Message);
                        offline = true;
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                    {
                        // One slow or failing service: the song stays pending and is retried on the next run.
                        AppPaths.Log($"Automatic metadata skipped {t.DisplayTitle}: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        AppPaths.Log($"Automatic metadata failed for {t.Path}: {ex.Message}");
                        _library.SetAutoFixed(new[] { t }, true);
                    }
                    return false;
                }

                void Finished()
                {
                    done++;
                    Progress?.Invoke(Math.Min(done, total - 1), total, null);
                }

                // Workers share the queue. They run on the UI thread's context, so the queue and counters need no locks;
                // the work itself (network, fingerprinting, tag writing) happens asynchronously in parallel.
                async Task Worker()
                {
                    while (pending.Count > 0 && !offline && !token.IsCancellationRequested)
                    {
                        var album = pending.Dequeue();
                        var songs = new List<Track>();
                        foreach (var t in album)
                        {
                            if (!_sync && t.AutoFixed && !_redo.Contains(t)) done++; // edited by the user meanwhile
                            else songs.Add(t);
                        }
                        if (songs.Count == 0) continue;
                        var labelBefore = songs[0].Album;
                        var siblings = Siblings(songs);

                        // 1. Each song is identified online.
                        var found = new Dictionary<Track, Identified>();
                        await Task.WhenAll(songs.Select(async t =>
                        {
                            if (await Step(t, async () => { Progress?.Invoke(done, total, t.DisplayTitle); found[t] = await IdentifyAsync(t, token); })) return;
                            Finished();
                        }));
                        if (token.IsCancellationRequested) return;

                        // 2. The songs agree on one album.
                        if (_settings.Current.MatchWholeAlbums)
                        {
                            try { await AgreeOnAlbumAsync(found, songs, siblings, labelBefore); }
                            catch (Exception ex) { AppPaths.Log($"Album agreement failed for \"{labelBefore}\": {ex.Message}"); }
                        }

                        // 3. Artist photos, album art and lyrics, now that the album is settled.
                        await Task.WhenAll(songs.Where(found.ContainsKey).Select(async t =>
                        {
                            if (await Step(t, () => CompleteAsync(t, found[t], artTried, token))) _library.SetAutoFixed(new[] { t }, true);
                            Finished();
                        }));
                    }
                }

                await Task.WhenAll(Enumerable.Range(0, Math.Min(workers, Math.Max(1, pending.Count))).Select(_ => Worker()));
                if (offline || token.IsCancellationRequested)
                {
                    // Offline: the new songs stay queued for the next run. Stopped: forgotten (Stop is final).
                    if (offline) foreach (var t in fresh.Where(t => !t.AutoFixed)) _new.Add(t);
                    return;
                }
                if (busyCount > 0 && !_sync)
                {
                    foreach (var t in fresh.Where(t => !t.AutoFixed)) _new.Add(t);
                    // Some services were rate-limited: give them a moment, then process those songs again.
                    _ = Task.Delay(TimeSpan.FromSeconds(90), token).ContinueWith(_ => _ui.TryEnqueue(async () => await RunPendingAsync()),
                        token, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
                }
            } while (_rerun && !token.IsCancellationRequested);
        }
        finally
        {
            IsRunning = false;
            _force = false;
            Progress?.Invoke(0, 0, null);
        }
    }

    /// <summary>
    /// Sync: downloads metadata, album art, artist images and lyrics for every song whose flag is 0 (incomplete) or
    /// -1 (edited by the user). Songs that are already complete (flag 1) are skipped.
    /// </summary>
    public async Task SyncAsync()
    {
        if (IsRunning) Stop();
        while (IsRunning) await Task.Delay(100);
        _library.ResetArtistImageAttempts();
        _sync = true;
        _force = true;
        try { await RunPendingAsync(); }
        finally { _sync = false; }
    }

    private bool _sync;

    /// <summary>
    /// Redoes the online checks for specific songs (the Sync button on a Source page row): metadata, artist image, and
    /// any album art or lyrics that didn't come from the file. Runs right away, or next if a run is in progress.
    /// </summary>
    public async Task SyncTracksAsync(IEnumerable<Track> tracks, bool replaceArt = false)
    {
        foreach (var t in tracks)
        {
            if (t.IsCheckable && !_redo.Contains(t)) _redo.Add(t);
            if (replaceArt) _replaceArt.Add(t);
        }
        if (IsRunning) { _rerun = true; return; }
        await RunPendingAsync();
    }

    public bool IsQueued(Track t) => _redo.Contains(t);

    /// <summary>Stops the whole run: the song being processed, the songs waiting, queued single-song syncs and retries.</summary>
    public void Stop()
    {
        _redo.Clear();
        _new.Clear();
        _rerun = false;
        _cts.Cancel();
        // The cancelled source isn't disposed here: songs still finishing may yet register with its token.
        _cts = new CancellationTokenSource();
    }

    /// <summary>Stops everything (when the app closes).</summary>
    public void Dispose()
    {
        Stop();
        _cts.Dispose();
    }

    /// <summary>What identifying a song found: the match applied (null: none), everything else found, and whether it
    /// was a single-song sync.</summary>
    private sealed record Identified(MetadataCandidate? Match, IReadOnlyList<MetadataCandidate> Candidates, bool Redo);

    /// <summary>Songs handled together: same folder and same album label. A song without an album label is on its own.</summary>
    private static string AlbumGroup(Track t) => string.IsNullOrWhiteSpace(t.Album)
        ? "#" + t.Id
        : (Path.GetDirectoryName(t.Path) ?? "").ToLowerInvariant() + "\u001e" + t.AlbumKey;

    /// <summary>Songs of the same album (folder and label) already in the library and not being processed: they vote too.</summary>
    private List<Track> Siblings(List<Track> songs)
    {
        if (string.IsNullOrWhiteSpace(songs[0].Album)) return new();
        var key = AlbumGroup(songs[0]);
        var set = songs.ToHashSet();
        return _library.Tracks.Where(x => !set.Contains(x) && AlbumGroup(x) == key).ToList();
    }

    /// <summary>
    /// Gives the songs of one album the same album name, album artist, year and genre (see <see cref="AlbumAgreement"/>).
    /// Songs that couldn't be looked up this time (a service was busy) and siblings vote with the label they have.
    /// Songs you edited are never changed.
    /// </summary>
    private async Task AgreeOnAlbumAsync(Dictionary<Track, Identified> found, List<Track> songs, List<Track> siblings, string? labelBefore)
    {
        if (!found.Values.Any(f => f.Match != null)) return; // nothing was matched: nothing to agree on
        var members = found.Select(kv => new AlbumMember(kv.Key, kv.Value.Match, kv.Value.Candidates, kv.Value.Match != null))
            .Concat(songs.Where(t => !found.ContainsKey(t) && _library.Tracks.Contains(t)).Concat(siblings)
                .Select(t => new AlbumMember(t, null, Array.Empty<MetadataCandidate>(), false))).ToList();
        if (AlbumAgreement.Decide(members, labelBefore) is not { } d) return;

        int changed = 0;
        foreach (var t in d.Songs)
        {
            if (t.MetaState == ItemState.EditedByUser) continue;
            var e = new TagEdit();
            bool any = false;
            if (d.Releases.TryGetValue(t, out var on))
            {
                if (on.TrackNumber > 0 && on.TrackNumber != t.TrackNumber) { e.TrackNumber = on.TrackNumber; any = true; }
                if (on.TrackCount > 0 && on.TrackCount != t.TrackCount) { e.TrackCount = on.TrackCount; any = true; }
                if (on.DiscNumber > 0 && on.DiscNumber != t.DiscNumber) { e.DiscNumber = on.DiscNumber; any = true; }
            }
            if (!string.Equals(t.Album, d.Album, StringComparison.Ordinal)) { e.Album = d.Album; any = true; }
            if (d.AlbumArtist != null && !string.Equals(t.AlbumArtist, d.AlbumArtist, StringComparison.Ordinal)) { e.AlbumArtist = d.AlbumArtist; any = true; }
            if (d.Year > 0 && t.Year != d.Year) { e.Year = d.Year; any = true; }
            if (d.Genre != null && !string.Equals(t.Genre, d.Genre, StringComparison.Ordinal)) { e.Genre = d.Genre; any = true; }
            if (!any) continue;

            var keyBefore = t.AlbumKey;
            var state = t.MetaState;
            await _metadata.ApplyAsync(new[] { t }, e, markReviewed: found.ContainsKey(t));
            // A song that wasn't found online keeps saying so: only its album details were aligned.
            if (state != ItemState.Downloaded) { t.MetaState = state; _library.SaveStates(new[] { t }); }
            _metadata.DropStaleArt(t, keyBefore);
            changed++;
        }
        if (changed > 0)
            AppPaths.Log($"Album agreement: \"{d.Album}\" ({d.Year}) for {d.Songs.Count} songs, {changed} changed, was \"{labelBefore}\"");
    }

    /// <summary>Identifies the song online and applies the match, genre included (step 1 of processing a song).</summary>
    private async Task<Identified> IdentifyAsync(Track t, CancellationToken ct)
    {
        var s = _settings.Current;
        MetadataCandidate? match = null;
        IReadOnlyList<MetadataCandidate> candidates = Array.Empty<MetadataCandidate>();
        bool redo = _redo.Remove(t);
        var albumBefore = t.AlbumKey;

        // Metadata (Sync redoes anything not downloaded, including user edits; a single-song sync redoes everything).
        bool doMeta = redo || (_sync ? t.MetaState != ItemState.Downloaded : (s.AutoFixMetadata && t.MetaState == ItemState.Missing));
        if (doMeta)
        {
            string? fpError = null, busy = null;
            candidates = await _metadata.IdentifyAsync(t, null, null, ct, allowFingerprint: !_fingerprintRejected, fingerprintError: e => fpError = e,
                stopWhenConfident: true, serviceBusy: svc => busy ??= svc);
            if (!_fingerprintRejected && fpError != null && fpError.Contains("rejected"))
            {
                _fingerprintRejected = true;
                _notifier.Error("AcoustID API key rejected", "Songs are being matched by tags only. Verify the key in Settings > API keys.");
            }
            match = MetadataEditService.ChooseAutomatic(t, candidates);
            // A busy service is not "not found": leave the song pending so it is retried shortly.
            if (match == null && busy != null) throw new ServiceBusyException(busy);
            if (match != null)
            {
                var edit = _sync || redo ? TagEdit.FromCandidate(match) : ChangesFrom(t, match);
                if (edit != null && edit.HasMetadataFields) await _metadata.ApplyAsync(new[] { t }, edit);
                t.MetaState = ItemState.Downloaded; // matched online (tags confirmed or updated)
            }
            else if (t.MetaState == ItemState.Missing) t.MetaState = ItemState.NotFound;
            _library.SaveStates(new[] { t });
            _metadata.DropStaleArt(t, albumBefore);
        }

        // Genre: fill when missing or not a recognised genre.
        if (doMeta && !Genres.IsKnown(t.Genre))
        {
            if (await _metadata.ResolveGenreAsync(t, match, ct) is { } genre)
                await _metadata.ApplyAsync(new[] { t }, new TagEdit { Genre = genre });
        }
        return new Identified(match, candidates, redo);
    }

    /// <summary>Artist photos, album art and lyrics (step 2, once the album is settled).</summary>
    private async Task CompleteAsync(Track t, Identified found, HashSet<string> artTried, CancellationToken ct)
    {
        var s = _settings.Current;
        var (match, _, redo) = found;

        // Artist photo: one per artist; an artist that already has one is never looked up again for another song
        // (Sync still replaces photos you chose, as it does with every edit).
        // Each credited artist has their own photo; the match describes the first one.
        var names = t.ArtistNames;
        for (int i = 0; i < names.Count; i++)
        {
            var artistState = _library.ArtistImageState(names[i]);
            bool artistHasPhoto = _library.GetArtistImage(names[i]) != null;
            if (redo ? !artistHasPhoto : _sync ? artistState != ItemState.Downloaded : s.AutoFetchArt)
                await _metadata.FetchArtistImageAsync(names[i], i == 0 ? match : null, force: _sync || redo, ct: ct, sample: i == 0 ? t : null);
        }

        // Album art (once per album per run).
        if (_replaceArt.Remove(t))
        {
            artTried.Add(t.AlbumKey);
            if (await _metadata.FetchAndApplyArtAsync(new[] { t }, match, ct)) AppPaths.Log($"Cover replaced for downloaded song: {t.Path}");
        }
        bool doArt = redo ? t.ArtState != ItemState.FromFile : _sync ? !ItemState.IsSatisfied(t.ArtState) : (s.AutoFetchArt && t.ArtPath == null);
        if (doArt && artTried.Add(t.AlbumKey))
        {
            var albumTracks = (_library.FindAlbum(t.AlbumKey)?.Tracks ?? new List<Track> { t })
                .Where(x => x == t || (x.IsCheckable && !ItemState.IsSatisfied(x.ArtState))).ToList();
            // Another song of the album already has a cover: share it instead of looking it up again.
            if (!_metadata.ReuseAlbumArt(albumTracks) && !await _metadata.FetchAndApplyArtAsync(albumTracks, match, ct))
            {
                foreach (var x in albumTracks.Where(x => x.ArtState == ItemState.Missing)) x.ArtState = ItemState.NotFound;
                _library.SaveStates(albumTracks);
            }
        }

        // Lyrics.
        bool doLyrics = redo ? t.LyricsState != ItemState.FromFile : _sync ? !ItemState.IsSatisfied(t.LyricsState) : (s.AutoFetchLyrics && _lyrics.GetLocal(t) == null);
        if (doLyrics) await _lyrics.DownloadAsync(t, ct);
    }

    private static TagEdit? ChangesFrom(Track t, MetadataCandidate c)
    {
        var e = new TagEdit();
        bool any = false;
        void Str(string? now, string? next, Action<string> set)
        {
            if (string.IsNullOrWhiteSpace(next) || string.Equals(now, next, StringComparison.Ordinal)) return;
            set(next); any = true;
        }
        void Num(int now, int next, Action<int> set)
        {
            if (next <= 0 || now == next) return;
            set(next); any = true;
        }
        Str(t.Title, c.Title, v => e.Title = v);
        Str(t.Artist, c.Artist, v => e.Artist = v);
        Str(t.Album, c.Album, v => e.Album = v);
        Str(t.AlbumArtist, c.AlbumArtist, v => e.AlbumArtist = v);
        if (!Genres.IsKnown(t.Genre) && Genres.IsKnown(c.Genre)) Str(t.Genre, c.Genre, v => e.Genre = v);
        Num(t.Year, c.Year, v => e.Year = v);
        Num(t.TrackNumber, c.TrackNumber, v => e.TrackNumber = v);
        Num(t.TrackCount, c.TrackCount, v => e.TrackCount = v);
        Num(t.DiscNumber, c.DiscNumber, v => e.DiscNumber = v);
        return any ? e : null;
    }
}

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services.Audio;

namespace WinMuse.Services;

/// <summary>Queue, transport and the bridge between the UI thread and the audio engine.</summary>
public sealed partial class PlaybackService : ObservableObject
{
    private readonly AudioEngine _engine = new();
    private readonly SettingsService _settings;
    private readonly LibraryService _library;
    private DispatcherQueueTimer? _timer;
    private List<Track>? _unshuffled;
    private bool _bulkQueueChange;
    private int _loadVersion, _prepareVersion;
    private int _consecutiveFailures;
    private Track? _preparedTrack;
    private double _listenedFloor; // start position of the current track (shown position never goes below it)

    public ObservableCollection<Track> Queue { get; } = new();

    [ObservableProperty] public partial Track? CurrentTrack { get; private set; }
    [ObservableProperty] public partial int CurrentIndex { get; private set; }
    [ObservableProperty] public partial bool IsPlaying { get; private set; }
    [ObservableProperty] public partial double Position { get; private set; }
    [ObservableProperty] public partial double Duration { get; private set; }
    [ObservableProperty] public partial bool Shuffle { get; set; }
    [ObservableProperty] public partial RepeatMode Repeat { get; set; }
    [ObservableProperty] public partial double Volume { get; set; }
    [ObservableProperty] public partial bool IsMuted { get; set; }

    public AudioEngine Engine => _engine;
    public SpectrumAnalyzer Spectrum => _engine.Pipeline.Spectrum;
    public string OutputDescription => _engine.OutputDescription;
    public bool HasTrack => CurrentTrack != null;

    /// <summary>Whether the song's file is open for playback: the current song, or the next one opened ahead for a gapless start.</summary>
    public bool IsOpen(Track t) => ReferenceEquals(CurrentTrack, t) || ReferenceEquals(_preparedTrack, t);

    /// <summary>Raised on the UI thread whenever the current track changes (including to null).</summary>
    public event Action<Track?>? TrackChanged;
    /// <summary>Raised when the user seeks (UI uses it to snap lyrics without animation).</summary>
    public event Action? Seeked;
    public event Action<string>? PlaybackError;

    public PlaybackService(SettingsService settings, LibraryService library)
    {
        _settings = settings;
        _library = library;
        var s = settings.Current;
        CurrentIndex = -1;
        Volume = s.Volume;
        IsMuted = s.Muted;
        Shuffle = s.Shuffle;
        Repeat = s.Repeat;
        Queue.CollectionChanged += OnQueueChanged;
    }

    public void Initialize()
    {
        InitEngine();
        _engine.DeviceReset += () => App.UI.TryEnqueue(ReinitializeOutput);
        _engine.Fatal += msg => App.UI.TryEnqueue(() =>
        {
            IsPlaying = false;
            _engine.Pause();
            PlaybackError?.Invoke("Audio output stopped: " + msg);
        });

        _timer = App.UI.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(200);
        _timer.Tick += (_, _) =>
        {
            if (CurrentTrack == null) return;
            // What is heard now: the decoder is ahead by the output buffer while playing.
            var heard = _engine.Pipeline.Position - (IsPlaying ? _engine.OutputLatency : TimeSpan.Zero);
            Position = Math.Max(_listenedFloor, heard.TotalSeconds);
        };
        _timer.Start();

        RestoreSession();
    }

    private void InitEngine(PlaybackPipeline? carry = null)
    {
        var s = _settings.Current;
        try { _engine.Initialize(s.OutputDeviceId, false, carry); }
        catch (Exception ex)
        {
            AppPaths.Log("Audio init failed: " + ex);
            PlaybackError?.Invoke("Unable to open the audio device: " + ex.Message);
        }
        var p = _engine.Pipeline;
        p.Advanced -= OnAdvanced; p.Advanced += OnAdvanced;
        p.Ended -= OnEnded; p.Ended += OnEnded;
        ApplyTrackEqualizer();
        OnPropertyChanged(nameof(Spectrum));
        OnPropertyChanged(nameof(OutputDescription));
    }

    /// <summary>Rebuilds the output (device / exclusive mode changed) and resumes where we were.</summary>
    public async void ReinitializeOutput()
    {
        var pos = Position;
        var wasPlaying = IsPlaying;
        var oldRate = _engine.SampleRate;
        InitEngine(_engine.Pipeline);
        if (CurrentTrack != null && (_engine.SampleRate != oldRate || _engine.Pipeline.Current == null))
            await PlayIndexAsync(CurrentIndex, TimeSpan.FromSeconds(pos), wasPlaying);
        else if (wasPlaying) _engine.Play();
    }

    public void ApplyAudioSettings()
    {
        var s = _settings.Current;
        var p = _engine.Pipeline;
        p.Volume = (float)Volume;
        p.Muted = IsMuted;
        p.CrossfadeSeconds = s.CrossfadeSeconds;
        p.Equalizer.Enabled = true; // always on; the Flat preset is neutral
        p.Equalizer.SetBands(ActiveEqBands, ActiveEqPreamp);
    }

    // ---------------- Equalizer ----------------

    public enum EqSource { Manual, Genre, Song }

    /// <summary>Where the equalizer playing now comes from: the song's saved EQ, its genre, or the manual setting.</summary>
    public EqSource ActiveEqSource { get; private set; }
    public string ActiveEqPreset { get; private set; } = "Flat";
    public float[] ActiveEqBands { get; private set; } = new float[10];
    public float ActiveEqPreamp { get; private set; }
    public event Action? EqualizerChanged;

    /// <summary>
    /// Picks the equalizer for the current song: its saved EQ, else the preset for its genre (when Match genre is on),
    /// else the manual setting.
    /// </summary>
    public void ApplyTrackEqualizer()
    {
        var s = _settings.Current;
        var t = CurrentTrack;
        var prefs = t != null ? _library.PrefsFor(t) : null;
        if (prefs?.EqBands is { } saved)
            SetActiveEq(EqSource.Song, "Custom", saved, prefs.EqPreamp);
        else if (s.EqMatchGenre && Equalizer.Presets.TryGetValue(Equalizer.PresetForGenre(t?.Genre), out var bands))
            SetActiveEq(EqSource.Genre, Equalizer.PresetForGenre(t?.Genre), bands, s.EqPreamp);
        else
            SetActiveEq(EqSource.Manual, s.EqPreset, s.EqBands, s.EqPreamp);
    }

    /// <summary>
    /// Edits from the equalizer panel: they play right away and become the manual setting. Saving keeps them for
    /// the current song; otherwise the next song picks its own equalizer again.
    /// </summary>
    public void SetEqualizer(float[] bands, float preamp, string preset)
    {
        _settings.Set(nameof(AppSettings.EqBands), x => { x.EqBands = bands.ToArray(); x.EqPreamp = preamp; x.EqPreset = preset; });
        SetActiveEq(EqSource.Manual, preset, bands, preamp);
    }

    /// <summary>Saves the equalizer playing now for the current song, or removes the song's saved EQ (save = false).</summary>
    public void SaveEqualizerForSong(bool save = true)
    {
        if (CurrentTrack is not { } t) return;
        _library.SaveSongEq(t, save ? ActiveEqBands : null, ActiveEqPreamp);
        ApplyTrackEqualizer();
    }

    private void SetActiveEq(EqSource source, string preset, float[] bands, float preamp)
    {
        ActiveEqSource = source;
        ActiveEqPreset = preset;
        ActiveEqBands = bands.ToArray();
        ActiveEqPreamp = preamp;
        ApplyAudioSettings();
        EqualizerChanged?.Invoke();
    }

    /// <summary>Re-evaluates the pre-loaded next track (gapless / crossfade settings changed).</summary>
    public void RefreshNext() => PrepareNext();

    // ---------------- Property side effects ----------------

    partial void OnVolumeChanged(double value)
    {
        _engine.Pipeline.Volume = (float)value;
        _settings.Set(nameof(AppSettings.Volume), s => s.Volume = value);
    }

    partial void OnIsMutedChanged(bool value)
    {
        _engine.Pipeline.Muted = value;
        _settings.Set(nameof(AppSettings.Muted), s => s.Muted = value);
    }

    partial void OnRepeatChanged(RepeatMode value)
    {
        _settings.Set(nameof(AppSettings.Repeat), s => s.Repeat = value);
        PrepareNext();
    }

    partial void OnShuffleChanged(bool value)
    {
        _settings.Set(nameof(AppSettings.Shuffle), s => s.Shuffle = value);
        if (value) ShuffleQueue(); else UnshuffleQueue();
    }

    partial void OnCurrentTrackChanged(Track? oldValue, Track? newValue)
    {
        if (oldValue != null) oldValue.IsPlaying = false;
        if (newValue != null) newValue.IsPlaying = true;
        OnPropertyChanged(nameof(HasTrack));
        ApplyTrackEqualizer();
        TrackChanged?.Invoke(newValue);
    }

    // ---------------- Transport ----------------

    /// <summary>Where the current playback was started from (null: the queue, Shuffle All, a selection).</summary>
    public PlayContext? Context { get; private set; }

    /// <param name="context">What is being played, for Continue Listening (null: not remembered).</param>
    /// <param name="startAt">Position in the first song (resuming).</param>
    public async Task PlayAsync(IEnumerable<Track> tracks, int startIndex = 0, bool? shuffle = null, PlayContext? context = null, TimeSpan? startAt = null)
    {
        Context = context;
        var list = tracks.ToList();
        if (list.Count == 0) return;
        startIndex = Math.Clamp(startIndex, 0, list.Count - 1);

        _bulkQueueChange = true;
        Queue.Clear();
        foreach (var t in list) Queue.Add(t);
        _bulkQueueChange = false;
        _unshuffled = null;

        if (shuffle == true && !Shuffle) { Shuffle = true; } // triggers ShuffleQueue
        else if (shuffle == false && Shuffle) { Shuffle = false; }

        int index = startIndex;
        if (Shuffle)
        {
            var start = shuffle == true ? list[Random.Shared.Next(list.Count)] : list[startIndex];
            ShuffleQueue(start);
            index = 0;
        }
        await PlayIndexAsync(index, startAt);
    }

    /// <summary>Plays one song on its own (remembered in Continue Listening as that song).</summary>
    public Task PlayTrackAsync(Track t) => PlayAsync(new[] { t }, context: PlayContext.Of(t));

    public async Task PlayIndexAsync(int index, TimeSpan? startAt = null, bool autoplay = true)
    {
        if (index < 0 || index >= Queue.Count) return;
        var track = Queue[index];
        var version = ++_loadVersion;
        var rate = _engine.SampleRate;

        RecordPlayIfListened();

        FFmpegDecoder dec;
        try
        {
            dec = await Task.Run(() =>
            {
                var d = new FFmpegDecoder(track.Path, rate) { Tag = track };
                if (startAt is { } s && s > TimeSpan.Zero) d.Seek(s);
                d.StartPrefetch();
                return d;
            });
            _consecutiveFailures = 0;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Failed to open {track.Path}: {ex.Message}");
            if (version != _loadVersion) return;
            PlaybackError?.Invoke($"Unable to play {track.DisplayTitle}: {ex.Message}");
            if (++_consecutiveFailures < 5 && autoplay && index + 1 < Queue.Count) await PlayIndexAsync(index + 1);
            return;
        }
        if (version != _loadVersion) { dec.Dispose(); return; }

        _engine.Pipeline.SetCurrent(dec);
        CurrentIndex = index;
        CurrentTrack = track;
        Duration = dec.Duration.TotalSeconds > 0 ? dec.Duration.TotalSeconds : track.Duration.TotalSeconds;
        Position = _listenedFloor = startAt?.TotalSeconds ?? 0;
        _listenedFrom = Position;

        if (autoplay) { _engine.Play(); IsPlaying = true; }
        else { _engine.Pause(); IsPlaying = false; }

        PrepareNext();
    }

    public void TogglePlayPause()
    {
        if (CurrentTrack == null)
        {
            if (Queue.Count > 0) _ = PlayIndexAsync(Math.Max(0, CurrentIndex));
            else if (_library.Tracks.Count > 0) _ = PlayAsync(_library.Tracks, 0, shuffle: true);
            return;
        }
        if (_engine.Pipeline.Current == null)
        {
            // Track finished earlier; restart it.
            _ = PlayIndexAsync(CurrentIndex);
            return;
        }
        if (IsPlaying) { _engine.Pause(); IsPlaying = false; }
        else { _engine.Play(); IsPlaying = true; }
    }

    public void Play() { if (!IsPlaying) TogglePlayPause(); }
    public void Pause() { if (IsPlaying) TogglePlayPause(); }

    public async Task NextAsync()
    {
        if (Queue.Count == 0) return;
        int next = CurrentIndex + 1;
        if (next >= Queue.Count)
        {
            if (Repeat == RepeatMode.Off) { await PlayIndexAsync(0, autoplay: false); return; }
            next = 0;
        }
        await PlayIndexAsync(next);
    }

    public async Task PreviousAsync()
    {
        if (Queue.Count == 0) return;
        if (Position > 3 || CurrentIndex <= 0 && Repeat != RepeatMode.All) { Seek(0); return; }
        int prev = CurrentIndex - 1;
        if (prev < 0) prev = Queue.Count - 1;
        await PlayIndexAsync(prev, autoplay: true);
    }

    public void Seek(double seconds)
    {
        if (CurrentTrack == null) return;
        seconds = Math.Clamp(seconds, 0, Math.Max(0, Duration - 0.25));
        if (_engine.Pipeline.Current == null)
        {
            _ = PlayIndexAsync(CurrentIndex, TimeSpan.FromSeconds(seconds), IsPlaying);
            return;
        }
        _engine.Pipeline.Seek(TimeSpan.FromSeconds(seconds));
        Position = _listenedFloor = seconds;
        _listenedFrom = Math.Min(_listenedFrom, seconds);
        Seeked?.Invoke();
        PrepareNext();
    }

    public void CycleRepeat() => Repeat = Repeat switch { RepeatMode.Off => RepeatMode.All, RepeatMode.All => RepeatMode.One, _ => RepeatMode.Off };

    // ---------------- Queue editing ----------------

    public void PlayNext(IEnumerable<Track> tracks)
    {
        int at = Math.Min(CurrentIndex + 1, Queue.Count);
        foreach (var t in tracks) Queue.Insert(at++, t);
        if (CurrentTrack == null && Queue.Count > 0) _ = PlayIndexAsync(0);
    }

    public void AddToQueue(IEnumerable<Track> tracks)
    {
        bool wasEmpty = Queue.Count == 0;
        foreach (var t in tracks) Queue.Add(t);
        _unshuffled?.AddRange(tracks);
        if (wasEmpty && CurrentTrack == null && Queue.Count > 0) _ = PlayIndexAsync(0);
    }

    public void RemoveFromQueue(IEnumerable<int> indices)
    {
        foreach (var i in indices.OrderByDescending(i => i))
        {
            if (i < 0 || i >= Queue.Count) continue;
            var t = Queue[i];
            Queue.RemoveAt(i);
            _unshuffled?.Remove(t);
        }
    }

    public void ClearQueue(bool keepCurrent = true)
    {
        _bulkQueueChange = true;
        var cur = CurrentTrack;
        Queue.Clear();
        if (keepCurrent && cur != null) Queue.Add(cur);
        _bulkQueueChange = false;
        _unshuffled = null;
        SyncIndexAfterQueueChange();
        if (!keepCurrent) Stop();
    }

    public void Stop()
    {
        _loadVersion++;
        _engine.Pause();
        _engine.Pipeline.SetCurrent(null);
        IsPlaying = false;
        CurrentTrack = null;
        CurrentIndex = -1;
        Position = 0;
        Duration = 0;
    }

    private void OnQueueChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_bulkQueueChange) return;
        SyncIndexAfterQueueChange();
    }

    private void SyncIndexAfterQueueChange()
    {
        if (CurrentTrack != null)
        {
            // keep the same position if still valid (handles duplicate entries), else look it up
            if (CurrentIndex < 0 || CurrentIndex >= Queue.Count || Queue[CurrentIndex] != CurrentTrack)
                CurrentIndex = Queue.IndexOf(CurrentTrack);
        }
        PrepareNext();
    }

    private void ShuffleQueue(Track? start = null)
    {
        if (Queue.Count < 2) return;
        _unshuffled = Queue.ToList();
        var first = start ?? CurrentTrack;
        var rest = Queue.Where(t => t != first).OrderBy(_ => Random.Shared.Next()).ToList();
        _bulkQueueChange = true;
        Queue.Clear();
        if (first != null) Queue.Add(first);
        foreach (var t in rest) Queue.Add(t);
        _bulkQueueChange = false;
        if (CurrentTrack != null) CurrentIndex = Queue.IndexOf(CurrentTrack);
        PrepareNext();
    }

    private void UnshuffleQueue()
    {
        if (_unshuffled == null) return;
        var extra = Queue.Where(t => !_unshuffled.Contains(t)).ToList();
        _bulkQueueChange = true;
        Queue.Clear();
        foreach (var t in _unshuffled.Concat(extra)) Queue.Add(t);
        _bulkQueueChange = false;
        _unshuffled = null;
        if (CurrentTrack != null) CurrentIndex = Queue.IndexOf(CurrentTrack);
        PrepareNext();
    }

    // ---------------- Gapless pre-loading ----------------

    /// <summary>Index of the song that plays after the current one (-1 at the end of the queue).</summary>
    public int UpcomingIndex => AutoNextIndex();

    private int AutoNextIndex()
    {
        if (CurrentIndex < 0 || Queue.Count == 0) return -1;
        if (Repeat == RepeatMode.One) return CurrentIndex;
        if (CurrentIndex + 1 < Queue.Count) return CurrentIndex + 1;
        return Repeat == RepeatMode.All ? 0 : -1;
    }

    private void PrepareNext()
    {
        var version = ++_prepareVersion;
        var s = _settings.Current;
        int ni = AutoNextIndex();
        if (CurrentTrack == null || ni < 0 || (!s.Gapless && s.CrossfadeSeconds <= 0))
        {
            _preparedTrack = null;
            _engine.Pipeline.SetNext(null);
            return;
        }

        var track = Queue[ni];
        var rate = _engine.SampleRate;
        _preparedTrack = track;
        Task.Run(() =>
        {
            try
            {
                var d = new FFmpegDecoder(track.Path, rate) { Tag = track };
                d.StartPrefetch();
                App.UI.TryEnqueue(() =>
                {
                    if (version == _prepareVersion) _engine.Pipeline.SetNext(d);
                    else d.Dispose();
                });
            }
            catch (Exception ex) { AppPaths.Log("Prepare next failed: " + ex.Message); }
        });
    }

    private void OnAdvanced(FFmpegDecoder dec) => App.UI.TryEnqueue(() =>
    {
        if (CurrentTrack != null) _library.RecordPlay(CurrentTrack);
        var track = dec.Tag as Track;
        int idx = AutoNextIndex();
        if (idx < 0 || idx >= Queue.Count || Queue[idx] != track) idx = track != null ? Queue.IndexOf(track) : -1;
        if (idx < 0) return;
        CurrentIndex = idx;
        if (CurrentTrack == track) TrackChanged?.Invoke(track); // repeat-one: same track restarted
        CurrentTrack = track;
        Duration = dec.Duration.TotalSeconds > 0 ? dec.Duration.TotalSeconds : track!.Duration.TotalSeconds;
        Position = dec.Position.TotalSeconds;
        _listenedFloor = 0;
        _listenedFrom = 0;
        PrepareNext();
    });

    private void OnEnded(FFmpegDecoder dec) => App.UI.TryEnqueue(async () =>
    {
        if (CurrentTrack != null) _library.RecordPlay(CurrentTrack);
        _listenedFrom = double.MaxValue;
        int ni = AutoNextIndex();
        if (ni >= 0)
        {
            // Gapless disabled (or next failed to preload): open it now.
            await PlayIndexAsync(ni);
            return;
        }
        IsPlaying = false;
        _engine.Pause();
        await PlayIndexAsync(Math.Max(0, CurrentIndex), autoplay: false);
    });

    private double _listenedFrom;

    private void RecordPlayIfListened()
    {
        if (CurrentTrack == null || _listenedFrom == double.MaxValue) return;
        var listened = Position - _listenedFrom;
        if (listened >= Math.Min(Duration * 0.5, 240)) _library.RecordPlay(CurrentTrack);
        _listenedFrom = double.MaxValue;
    }

    // ---------------- Session ----------------

    public void SaveSession()
    {
        var s = _settings.Current;
        s.LastQueue = Queue.Select(t => t.Path).Take(5000).ToList();
        s.LastQueueIndex = CurrentIndex;
        s.LastPositionSeconds = Position;
        s.LastContext = Context;
        _settings.Save();
    }

    private void RestoreSession()
    {
        var s = _settings.Current;
        if (s.LastQueue.Count == 0) return;
        var byPath = _library.Tracks.ToDictionary(t => t.Path, StringComparer.OrdinalIgnoreCase);
        _bulkQueueChange = true;
        foreach (var p in s.LastQueue) if (byPath.TryGetValue(p, out var t)) Queue.Add(t);
        _bulkQueueChange = false;
        if (Queue.Count == 0) return;
        var idx = Math.Clamp(s.LastQueueIndex, 0, Queue.Count - 1);
        Context = s.LastContext; // the playlist/album/artist this queue came from
        _ = PlayIndexAsync(idx, TimeSpan.FromSeconds(s.LastPositionSeconds), autoplay: false);
    }

    public void Shutdown()
    {
        SaveSession();
        _timer?.Stop();
        _engine.Dispose();
    }
}

using CommunityToolkit.Mvvm.ComponentModel;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.ViewModels;

/// <summary>
/// The Now Playing screen's lyrics and details: loading lyrics for the song, which synced line is being sung, the
/// per-song timing offset, and the rows of the Details tab. The view draws the lines and animates them.
/// </summary>
public sealed partial class NowPlayingViewModel : ObservableObject, IDisposable
{
    private readonly LyricsService _lyrics;
    private readonly LibraryService _library;
    private readonly IUiDispatcher _ui;
    private readonly Debouncer _saveOffset = new(TimeSpan.FromMilliseconds(500));
    private CancellationTokenSource? _loading;

    /// <summary>Synced lines light up this much before their time stamp, so the highlight lands as the line is sung.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(180);
    /// <summary>The timing offset is kept within ±5 seconds.</summary>
    public const int MaxOffsetMs = 5000;

    public NowPlayingViewModel(LyricsService lyrics, LibraryService library, IUiDispatcher ui)
    {
        _lyrics = lyrics; _library = library; _ui = ui;
    }

    /// <summary>The song whose lyrics are shown (null: none).</summary>
    public Track? Track { get; private set; }

    /// <summary>The lyrics shown, or null while loading or when there are none.</summary>
    [ObservableProperty] public partial LyricsResult? Lyrics { get; private set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    /// <summary>Why no lyrics are shown ("Lyrics unavailable", "Instrumental", ...); empty while lyrics are shown or loading.</summary>
    [ObservableProperty] public partial string Message { get; private set; } = "";
    /// <summary>The synced line being sung (-1 before the first).</summary>
    [ObservableProperty] public partial int ActiveLine { get; private set; } = -1;
    /// <summary>Lyrics timing for this song: positive shows each line later.</summary>
    [ObservableProperty] public partial int OffsetMs { get; private set; }

    public bool HasLyrics => Lyrics is { Lines.Count: > 0 };
    public bool IsSynced => Lyrics is { IsSynced: true, Lines.Count: > 0 };
    public string SourceText => Lyrics is not { Lines.Count: > 0 } l ? "" : (l.IsSynced ? "Synced lyrics" : "Lyrics") + (l.Source != null ? " · " + l.Source : "");
    public string TimingText => OffsetMs == 0 ? "Timing" : $"Timing {OffsetMs / 1000.0:+0.00;-0.00;0.00} s";

    /// <summary>Raised when a new set of lines is shown (or none), so the view rebuilds them.</summary>
    public event Action? LinesChanged;

    // ---------------- Loading ----------------

    /// <summary>Shows the lyrics of <paramref name="t"/>, unless they are already shown.</summary>
    public Task ShowAsync(Track? t) => t == Track ? Task.CompletedTask : LoadAsync(t);

    /// <summary>Loads the lyrics of <paramref name="t"/> again (saved ones only, after the user edited them).</summary>
    public Task ReloadAsync() => LoadAsync(Track, online: false);

    /// <summary>
    /// The library changed, perhaps the song's lyrics were edited from a song menu: shows the saved lyrics again, but
    /// only when they differ (the lines aren't rebuilt, and nothing flickers, for unrelated changes).
    /// </summary>
    public async Task RefreshIfChangedAsync()
    {
        if (Track is not { } t || IsLoading) return;
        LyricsResult? saved;
        try { saved = await _lyrics.GetAsync(t, allowOnline: false); }
        catch (Exception ex) { AppPaths.Log("Lyrics: " + ex.Message); return; }
        if (t != Track || IsLoading || SameLines(saved, Lyrics)) return;
        SetLyrics(saved, t.LyricsState == ItemState.Instrumental ? "Instrumental" : "Lyrics unavailable");
    }

    private static bool SameLines(LyricsResult? a, LyricsResult? b)
    {
        var x = a is { Lines.Count: > 0 } ? a : null;
        var y = b is { Lines.Count: > 0 } ? b : null;
        if (x == null || y == null) return x == y;
        return x.IsSynced == y.IsSynced && x.Lines.SequenceEqual(y.Lines);
    }

    private async Task LoadAsync(Track? t, bool online = true)
    {
        _loading?.Cancel();
        var cts = _loading = new CancellationTokenSource();
        Track = t;
        SetLyrics(null, "");
        if (t == null) return;

        IsLoading = true;
        LyricsResult? result = null;
        try { result = await _lyrics.GetAsync(t, online, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { AppPaths.Log("Lyrics: " + ex.Message); }
        if (cts.IsCancellationRequested) return;
        IsLoading = false;
        SetLyrics(result, t.LyricsState == ItemState.Instrumental ? "Instrumental" : "Lyrics unavailable");
    }

    /// <summary>Looks the song's lyrics up online now. True when lyrics were found and saved.</summary>
    public async Task<bool> DownloadAsync()
    {
        if (Track is not { } t) return false;
        IsLoading = true;
        Message = "Searching for lyrics…";
        try
        {
            var r = await _lyrics.DownloadAsync(t);
            IsLoading = false;
            if (r is { Lines.Count: > 0 }) { SetLyrics(r, ""); return true; }
            Message = t.LyricsState == ItemState.Instrumental ? "Instrumental: this song has no lyrics" : "No lyrics were found for this song";
        }
        catch (Exception ex)
        {
            IsLoading = false;
            Message = "Unable to reach the lyrics service";
            AppPaths.Log("Lyrics download: " + ex.Message);
        }
        return false;
    }

    private void SetLyrics(LyricsResult? result, string emptyMessage)
    {
        bool has = result is { Lines.Count: > 0 };
        Lyrics = has ? result : null;
        Message = has || Track == null ? "" : emptyMessage;
        ActiveLine = -1;
        OffsetMs = has && Track != null ? _library.PrefsFor(Track).LyricsOffsetMs : 0;
        OnPropertyChanged(nameof(HasLyrics));
        OnPropertyChanged(nameof(IsSynced));
        OnPropertyChanged(nameof(SourceText));
        OnPropertyChanged(nameof(TimingText));
        LinesChanged?.Invoke();
    }

    // ---------------- Synced lines ----------------

    /// <summary>
    /// The synced line at <paramref name="position"/> in the song, with the lead and the song's offset applied.
    /// True when it is a different line than before (the view moves the highlight).
    /// </summary>
    public bool UpdateActiveLine(TimeSpan position)
    {
        if (!IsSynced) return false;
        int line = LineAt(Lyrics!.Lines, position + Lead - TimeSpan.FromMilliseconds(OffsetMs));
        if (line == ActiveLine) return false;
        ActiveLine = line;
        return true;
    }

    /// <summary>The user sought: the next update picks the line again from scratch.</summary>
    public void ResetActiveLine() => ActiveLine = -1;

    /// <summary>The last line whose time stamp is at or before <paramref name="position"/>; -1 before the first.</summary>
    public static int LineAt(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int found = -1;
        for (int i = 0; i < lines.Count && lines[i].Time <= position; i++) found = i;
        return found;
    }

    /// <summary>Where to seek (seconds) to hear line <paramref name="index"/>, with the song's offset.</summary>
    public double SeekSecondsFor(int index) => Lyrics is { } l && index >= 0 && index < l.Lines.Count
        ? (l.Lines[index].Time.TotalMilliseconds + OffsetMs) / 1000.0
        : 0;

    // ---------------- Timing ----------------

    /// <summary>Applies a timing offset right away (positive shows lyrics later) and saves it for the song shortly after.</summary>
    public void SetOffset(int ms)
    {
        if (Track is not { } t) return;
        OffsetMs = Math.Clamp(ms, -MaxOffsetMs, MaxOffsetMs);
        OnPropertyChanged(nameof(TimingText));
        ActiveLine = -1;
        // The song may have changed by the time this runs: it saves the offset that song ended with.
        _saveOffset.Run(() => _ui.TryEnqueue(() => _library.SaveLyricsOffset(t, Track == t ? OffsetMs : _library.PrefsFor(t).LyricsOffsetMs)));
    }

    public void NudgeOffset(int ms) => SetOffset(OffsetMs + ms);

    /// <summary>Stops a lyrics lookup still running.</summary>
    public void Dispose()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
    }

    // ---------------- Details ----------------

    /// <summary>The rows of the Details tab for <paramref name="t"/> (none for no song).</summary>
    public static IReadOnlyList<(string Label, string Value)> DetailsOf(Track? t) => t == null ? Array.Empty<(string, string)>() : new[]
    {
        ("Title", t.DisplayTitle), ("Artist", t.DisplayArtist), ("Album", t.DisplayAlbum), ("Album artist", t.DisplayAlbumArtist),
        ("Genre", t.Genre ?? "—"), ("Year", t.Year > 0 ? t.Year.ToString() : "—"),
        ("Track", t.TrackNumber > 0 ? (t.TrackCount > 0 ? $"{t.TrackNumber} of {t.TrackCount}" : t.TrackNumber.ToString()) : "—"),
        ("Composer", t.Composer ?? "—"), ("Format", t.FormatText), ("Channels", t.Channels > 0 ? t.Channels.ToString() : "—"),
        ("Duration", t.DurationText), ("Size", Format.Size(t.FileSize)), ("Plays", t.PlayCount.ToString()),
        ("Added", DateTimeOffset.FromUnixTimeSeconds(t.AddedUtc).LocalDateTime.ToString("d")),
        ("File", t.Path),
    };
}

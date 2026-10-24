using CommunityToolkit.Mvvm.ComponentModel;

namespace WinMuse.Models;

/// <summary>A single audio file in the library. Column names match the SQLite schema (Dapper maps by name).</summary>
public sealed class Track : ObservableObject
{
    public long Id { get; set; }
    public string Path { get; set; } = "";

    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public int Year { get; set; }
    public int TrackNumber { get; set; }
    public int TrackCount { get; set; }
    public int DiscNumber { get; set; }
    public string? Composer { get; set; }
    public string? Comment { get; set; }
    public string? Lyrics { get; set; }
    public int Bpm { get; set; }

    public long DurationMs { get; set; }
    public int Bitrate { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public int BitsPerSample { get; set; }
    public string? Codec { get; set; }
    public long FileSize { get; set; }
    public long ModifiedUtc { get; set; }
    public long AddedUtc { get; set; }

    public int PlayCount { get; set; }
    public long LastPlayedUtc { get; set; }
    public int Rating { get; set; }
    public string? ArtPath { get; set; }
    /// <summary>True when the user edited tags in "library only" mode; rescans then keep the library values.</summary>
    public bool EditedLocally { get; set; }
    /// <summary>True once automatic metadata processing ran (or the user edited the song); it is not processed again unless the user requests it.</summary>
    public bool AutoFixed { get; set; }

    /// <summary>Where each item came from (see <see cref="ItemState"/>).</summary>
    public int MetaState { get; set; }
    public int ArtState { get; set; }
    public int LyricsState { get; set; }

    private bool _isChecked;
    /// <summary>UI-only: checked in selection mode (shared across every song list).</summary>
    public bool IsChecked { get => _isChecked; set { if (SetProperty(ref _isChecked, value)) Services.SelectionService.OnTrackChecked(this, value); } }

    private bool _isFavorite;
    /// <summary>In the "Favorite" playlist (kept up to date by FavoritesService).</summary>
    public bool IsFavorite { get => _isFavorite; set => SetProperty(ref _isFavorite, value); }

    private bool _isPlaying;
    /// <summary>UI-only flag for the "now playing" indicator.</summary>
    public bool IsPlaying { get => _isPlaying; set => SetProperty(ref _isPlaying, value); }

    // ---- Display helpers ----
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? System.IO.Path.GetFileNameWithoutExtension(Path) : Title!;
    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "Unknown Artist" : Artist!;
    public string DisplayAlbum => string.IsNullOrWhiteSpace(Album) ? "Unknown Album" : Album!;
    /// <summary>Album as shown in song lists: "-" when unknown.</summary>
    public string AlbumText => string.IsNullOrWhiteSpace(Album) ? "-" : Album!;
    public string DisplayAlbumArtist => !string.IsNullOrWhiteSpace(AlbumArtist) ? AlbumArtist! : DisplayArtist;
    public string AlbumKey => (DisplayAlbum + "\u001f" + DisplayAlbumArtist).ToLowerInvariant();

    /// <summary>
    /// Every artist the song credits, from its artist and album artist ("A & B", "A feat. B" and "A; B" are split).
    /// Various Artists is left out; a song with no artist at all belongs to Unknown Artist.
    /// </summary>
    public IReadOnlyList<string> ArtistNames
    {
        get
        {
            var names = Helpers.ArtistCredit.Split(Artist);
            foreach (var n in Helpers.ArtistCredit.Split(AlbumArtist))
                if (!names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
            names.RemoveAll(n => n.Equals("Various Artists", StringComparison.OrdinalIgnoreCase));
            if (names.Count == 0) names.Add("Unknown Artist");
            return names;
        }
    }

    /// <summary>The main artist (the first album artist, else the first artist).</summary>
    public string PrimaryArtist =>
        Helpers.ArtistCredit.Split(AlbumArtist).FirstOrDefault(n => !n.Equals("Various Artists", StringComparison.OrdinalIgnoreCase)) ?? ArtistNames[0];
    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);
    public string DurationText => Helpers.Format.Time(Duration);
    public string YearText => Year > 0 ? Year.ToString() : "";
    public string DisplayGenre => string.IsNullOrWhiteSpace(Genre) ? "" : Genre!;
    public string TrackNumberText => TrackNumber > 0 ? TrackNumber.ToString() : "–";
    public string FileName => System.IO.Path.GetFileName(Path);
    public string Extension => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();

    public string FormatText
    {
        get
        {
            var parts = new List<string> { string.IsNullOrEmpty(Codec) ? Extension : Codec!.ToUpperInvariant() };
            if (BitsPerSample > 0) parts.Add($"{BitsPerSample}-bit");
            if (SampleRate > 0) parts.Add($"{SampleRate / 1000.0:0.#} kHz");
            if (Bitrate > 0) parts.Add($"{Bitrate} kbps");
            return string.Join(" · ", parts);
        }
    }

    public bool IsHiRes => BitsPerSample > 16 || SampleRate > 48000;

    /// <summary>Online checks only run on songs from 30 seconds to 15 minutes: shorter clips and long mixes aren't catalogued songs.</summary>
    public bool IsCheckable => DurationMs is >= 30_000 and <= 15 * 60_000;

    /// <summary>Raise change notifications for every property after an edit.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    public Track Clone() => (Track)MemberwiseClone();
}

/// <summary>Source state of a song's metadata, album art, artist image or lyrics.</summary>
public static class ItemState
{
    public const int EditedByUser = -1; // changed by the user
    public const int Missing = 0;       // not processed yet
    public const int Downloaded = 1;    // provided by the online sources
    public const int NotFound = 2;      // looked up, nothing found
    public const int FromFile = 3;      // already present in the file/folder

    public static bool IsSatisfied(int s) => s is Downloaded or FromFile;
}

using CommunityToolkit.Mvvm.ComponentModel;
using WinMuse.Services;

namespace WinMuse.Models;

/// <summary>
/// A Continue Listening card: the song reached, where it was played from and how far into it. Updated in place
/// (<see cref="Update"/>) when the song moves on, so its card can change smoothly instead of being replaced.
/// </summary>
public sealed class ContinueItem : ObservableObject
{
    public ContinueItem(ContinueEntry entry, Track track) { Entry = entry; Track = track; }

    public ContinueEntry Entry { get; private set; }
    public Track Track { get; private set; }

    /// <summary>Shows a newer state of the same entry. Returns true when the song changed (not just the progress).</summary>
    public bool Update(ContinueEntry entry, Track track)
    {
        bool songChanged = track != Track;
        Entry = entry; Track = track;
        OnPropertyChanged(string.Empty);
        return songChanged;
    }

    public string Key => $"{Entry.Kind}:{Entry.Key}";
    public string Title => Track.DisplayTitle;
    public string Artist => Track.DisplayArtist;
    public string? ArtPath => Track.ArtPath;
    public string Seed => Track.AlbumKey;

    /// <summary>Where it was played from, e.g. "Playlist · Road trip".</summary>
    public string Source => Entry.Kind switch
    {
        PlayContextKind.Playlist => $"Playlist · {Entry.Name}",
        PlayContextKind.Album => $"Album · {Entry.Name}",
        PlayContextKind.Artist => $"Artist · {Entry.Name}",
        _ => "Song",
    };

    /// <summary>Width of the progress bar on a 174 px card.</summary>
    public double ProgressWidth => 174 * Progress / 100;

    /// <summary>Share of the song already heard (0–100), shown as a thin bar on the cover.</summary>
    public double Progress => Track.DurationMs > 0 ? Math.Clamp(Entry.PositionSeconds * 1000 / Track.DurationMs * 100, 0, 100) : 0;
}

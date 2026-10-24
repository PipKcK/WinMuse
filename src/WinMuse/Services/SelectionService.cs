using WinMuse.Controls;
using WinMuse.Models;

namespace WinMuse.Services;

/// <summary>
/// App-wide selection mode. Right-click → Select turns it on; checkboxes then appear in every song list, and checked
/// songs stay checked across pages (they are the same Track objects) until the selection is cleared.
/// </summary>
public static class SelectionService
{
    private static readonly List<Track> CheckedTracks = new();

    public static bool IsActive { get; private set; }

    /// <summary>Checked songs in the order they were checked.</summary>
    public static IReadOnlyList<Track> Checked => CheckedTracks;

    /// <summary>The song list on the current page (target of "Select all").</summary>
    public static TrackList? ActiveList { get; set; }

    public static event Action? ModeChanged;
    public static event Action? SelectionChanged;

    public static void Begin(IEnumerable<Track>? tracks = null)
    {
        if (!IsActive)
        {
            IsActive = true;
            ModeChanged?.Invoke();
        }
        if (tracks != null) foreach (var t in tracks) t.IsChecked = true;
    }

    private static bool _ending;

    public static void End()
    {
        _ending = true;
        try { foreach (var t in CheckedTracks.ToList()) t.IsChecked = false; }
        finally { _ending = false; }
        if (!IsActive) return;
        IsActive = false;
        ModeChanged?.Invoke();
    }

    public static void SelectAll()
    {
        if (ActiveList == null) return;
        foreach (var t in ActiveList.Items) t.IsChecked = true;
    }

    /// <summary>Called by <see cref="Track.IsChecked"/>.</summary>
    internal static void OnTrackChecked(Track t, bool isChecked)
    {
        if (isChecked) { if (!CheckedTracks.Contains(t)) CheckedTracks.Add(t); }
        else CheckedTracks.Remove(t);
        SelectionChanged?.Invoke();
        // Unchecking the last song closes Select mode (after the change finishes, e.g. a "Select all" loop).
        if (!isChecked && IsActive && CheckedTracks.Count == 0 && !_ending)
            App.UI.TryEnqueue(() => { if (IsActive && CheckedTracks.Count == 0) End(); });
    }
}

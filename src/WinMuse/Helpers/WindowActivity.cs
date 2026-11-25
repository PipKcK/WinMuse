namespace WinMuse.Helpers;

/// <summary>
/// Whether the main window is minimized. Moving effects (the album-art background, the spectrum) stop drawing while
/// nobody can see them and carry on where they were when the window comes back.
/// </summary>
public static class WindowActivity
{
    public static bool IsMinimized { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="IsMinimized"/> changes.</summary>
    public static event Action? Changed;

    public static void SetMinimized(bool minimized)
    {
        if (IsMinimized == minimized) return;
        IsMinimized = minimized;
        AppPaths.Log(minimized ? "Window minimized: effects paused" : "Window restored: effects resumed");
        Changed?.Invoke();
    }
}

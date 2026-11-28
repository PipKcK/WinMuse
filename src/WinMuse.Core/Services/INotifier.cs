using WinMuse.Helpers;

namespace WinMuse.Services;

/// <summary>How a notification is shown: problems stay on screen longer than information.</summary>
public enum NoticeSeverity { Informational, Success, Warning, Error }

/// <summary>A button on a notification ("Open log", "Restart").</summary>
public sealed record NoticeAction(string Label, Action Run)
{
    /// <summary>Opens the log file in the default text editor.</summary>
    public static readonly NoticeAction OpenLog = new("Open log", () =>
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.LogPath) { UseShellExecute = true }); }
        catch (Exception e) { AppPaths.Log("Open log: " + e.Message); }
    });
}

/// <summary>Shows a short message to the user. In the app, an InfoBar at the top of the window.</summary>
public interface INotifier
{
    void Show(string title, string? message = null, NoticeSeverity severity = NoticeSeverity.Informational, NoticeAction? action = null);
}

public static class NotifierExtensions
{
    public static void Error(this INotifier n, string title, string? message = null) => n.Show(title, message, NoticeSeverity.Error);
    public static void Success(this INotifier n, string title, string? message = null) => n.Show(title, message, NoticeSeverity.Success);
}

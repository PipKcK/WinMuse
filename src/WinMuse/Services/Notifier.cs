using Microsoft.UI.Xaml.Controls;

namespace WinMuse.Services;

/// <summary>In-app notifications rendered by the main window as InfoBars.</summary>
public static class Notifier
{
    /// <summary>A button on a notification ("Open log", "Restart").</summary>
    public sealed record ToastAction(string Label, Action Run);

    public static event Action<string, string?, InfoBarSeverity, ToastAction?>? Raised;

    public static void Show(string title, string? message = null, InfoBarSeverity severity = InfoBarSeverity.Informational, ToastAction? action = null) =>
        App.UI.TryEnqueue(() => Raised?.Invoke(title, message, severity, action));

    public static void Error(string title, string? message = null) => Show(title, message, InfoBarSeverity.Error);

    /// <summary>An error from an exception: the details go to the log, the notification says what happened in plain words.</summary>
    public static void Error(string title, Exception ex)
    {
        Helpers.AppPaths.Log($"{title}: {ex}");
        Show(title, Helpers.ErrorText.Describe(ex), InfoBarSeverity.Error, OpenLog);
    }

    /// <summary>Opens the log file in the default text editor.</summary>
    public static readonly ToastAction OpenLog = new("Open log", () =>
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Helpers.AppPaths.LogPath) { UseShellExecute = true }); }
        catch (Exception e) { Helpers.AppPaths.Log("Open log: " + e.Message); }
    });
    public static void Success(string title, string? message = null) => Show(title, message, InfoBarSeverity.Success);
}

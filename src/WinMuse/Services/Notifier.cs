namespace WinMuse.Services;

/// <summary>
/// In-app notifications, rendered by the main window as InfoBars. Pages call the static methods; services get
/// <see cref="Instance"/> as their <see cref="INotifier"/>. Safe to call from any thread.
/// </summary>
public static class Notifier
{
    /// <summary>Raised on the UI thread for each notification.</summary>
    public static event Action<string, string?, NoticeSeverity, NoticeAction?>? Raised;

    /// <summary>The same notifications, for services built with an <see cref="INotifier"/>.</summary>
    public static INotifier Instance { get; } = new Adapter();

    public static void Show(string title, string? message = null, NoticeSeverity severity = NoticeSeverity.Informational, NoticeAction? action = null) =>
        App.UI.TryEnqueue(() => Raised?.Invoke(title, message, severity, action));

    public static void Error(string title, string? message = null) => Show(title, message, NoticeSeverity.Error);

    /// <summary>An error from an exception: the details go to the log, the notification says what happened in plain words.</summary>
    public static void Error(string title, Exception ex)
    {
        Helpers.AppPaths.Log($"{title}: {ex}");
        Show(title, Helpers.ErrorText.Describe(ex), NoticeSeverity.Error, OpenLog);
    }

    /// <summary>Opens the log file in the default text editor.</summary>
    public static NoticeAction OpenLog => NoticeAction.OpenLog;

    public static void Success(string title, string? message = null) => Show(title, message, NoticeSeverity.Success);

    private sealed class Adapter : INotifier
    {
        public void Show(string title, string? message, NoticeSeverity severity, NoticeAction? action) => Notifier.Show(title, message, severity, action);
    }
}

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinMuse.Helpers;
using WinMuse.Services;

namespace WinMuse;

public partial class App : Application
{
    private readonly List<string> _startupFiles;

    public static DispatcherQueue UI { get; private set; } = null!;
    public static MainWindow MainWindow { get; private set; } = null!;

    public App(List<string> startupFiles)
    {
        _startupFiles = startupFiles;
        InitializeComponent();
        // An error from a file, the network or an online service leaves the app as it was: it keeps running. Any other
        // error is a bug that may have left something half-done, so a restart is offered (nothing is lost: settings
        // and the library are saved as they change).
        UnhandledException += (_, e) =>
        {
            AppPaths.Log("Unhandled: " + e.Exception);
            e.Handled = true;
            if (ErrorText.IsRecoverable(e.Exception))
                Notifier.Show("Something went wrong", ErrorText.Describe(e.Exception), Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error, Notifier.OpenLog);
            else
                Notifier.Show("Something went wrong", "If something doesn't work as expected, restart the app. Details were saved to the log.",
                    Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error, new Notifier.ToastAction("Restart", Restart));
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { AppPaths.Log("Unobserved: " + e.Exception); e.SetObserved(); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UI = DispatcherQueue.GetForCurrentThread();
        AppPaths.Log($"{AppInfo.DisplayVersion} started (Windows {Environment.OSVersion.Version}, .NET {Environment.Version})");
        Svc.Create();
        MainWindow = new MainWindow();
        MainWindow.Activate();
        _ = MainWindow.InitializeAsync(_startupFiles);
    }

    /// <summary>Closes and reopens the app (after an unexpected error).</summary>
    private static void Restart()
    {
        try { Microsoft.Windows.AppLifecycle.AppInstance.Restart(""); }
        catch (Exception ex) { AppPaths.Log("Restart: " + ex.Message); Notifier.Error("Unable to restart", "Close the app and open it again."); }
    }

    /// <summary>Another launch was redirected here (e.g. "Open with Music").</summary>
    public void HandleExternalActivation(List<string> files)
    {
        MainWindow.BringToFront();
        if (files.Count > 0) _ = MainWindow.OpenExternalFilesAsync(files);
    }
}

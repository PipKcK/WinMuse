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
        UnhandledException += (_, e) =>
        {
            AppPaths.Log("Unhandled: " + e.Exception);
            e.Handled = true;
            Notifier.Error("An unexpected error occurred", e.Exception.Message);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => { AppPaths.Log("Unobserved: " + e.Exception); e.SetObserved(); };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        UI = DispatcherQueue.GetForCurrentThread();
        Svc.Create();
        MainWindow = new MainWindow();
        MainWindow.Activate();
        _ = MainWindow.InitializeAsync(_startupFiles);
    }

    /// <summary>Another launch was redirected here (e.g. "Open with Music").</summary>
    public void HandleExternalActivation(List<string> files)
    {
        MainWindow.BringToFront();
        if (files.Count > 0) _ = MainWindow.OpenExternalFilesAsync(files);
    }
}

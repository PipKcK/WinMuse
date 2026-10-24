using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace WinMuse;

/// <summary>Custom entry point: keeps a single instance and forwards "open with" files to it.</summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        // A full (blocking) garbage collection pauses every thread, the audio thread too; switching pages triggered them
        // often enough to stutter playback. This mode keeps full collections in the background while possible.
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
#if DEBUG
        Diagnostics.GcReasonLog.Start();
#endif

        var current = AppInstance.GetCurrent();
        var key = AppInstance.FindOrRegisterForKey(
            // A copy using its own data folder (testing) runs alongside, instead of handing off to the main window.
            Environment.GetEnvironmentVariable("WINMUSE_DATA_DIR") is { Length: > 0 } dir ? "WinMuse.Test." + dir.GetHashCode() : "WinMuse.Main");
        if (!key.IsCurrent)
        {
            var activation = current.GetActivatedEventArgs();
            Task.Run(() => key.RedirectActivationToAsync(activation).AsTask()).Wait();
            return 0;
        }

        key.Activated += (_, e) =>
        {
            var files = e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs l ? ParseArgs(l.Arguments) : new List<string>();
            App.UI?.TryEnqueue(() => (Application.Current as App)?.HandleExternalActivation(files));
        };

        Application.Start(p =>
        {
            var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(ctx);
            _ = new App(args.Where(File.Exists).ToList());
        });
        return 0;
    }

    /// <summary>Splits a raw command line into existing file paths.</summary>
    public static List<string> ParseArgs(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine)) return result;
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (var ch in commandLine + " ")
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (ch == ' ' && !quoted)
            {
                if (sb.Length > 0) { var s = sb.ToString(); if (File.Exists(s)) result.Add(s); sb.Clear(); }
                continue;
            }
            sb.Append(ch);
        }
        return result;
    }
}

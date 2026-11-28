namespace WinMuse.Helpers;

/// <summary>
/// Runs an action once after calls stop arriving for the given delay. Each call supersedes the one before it; the
/// action runs on a thread-pool thread.
/// </summary>
public sealed class Debouncer(TimeSpan delay)
{
    private int _latest;

    public void Run(Action action)
    {
        int call = Interlocked.Increment(ref _latest);
        // A newer call meanwhile means this one was superseded: its wait ends without running anything.
        Task.Delay(delay).ContinueWith(_ => { if (Volatile.Read(ref _latest) == call) action(); }, TaskScheduler.Default);
    }
}

namespace WinMuse.Helpers;

/// <summary>Runs an action once after calls stop arriving for the given delay.</summary>
public sealed class Debouncer(TimeSpan delay)
{
    private CancellationTokenSource? _cts;

    public void Run(Action action)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Task.Delay(delay, cts.Token).ContinueWith(t => { if (!t.IsCanceled) action(); }, TaskScheduler.Default);
    }
}

namespace WinMuse.Helpers;

/// <summary>
/// Taking a lock from the UI thread without message pumping.
/// <para>
/// A plain <c>lock</c> that has to wait on the UI thread lets Windows deliver other messages to the thread while it
/// waits. If that re-enters XAML mid-frame (for example the next render callback), WinUI fails fast with error
/// 8000ffff and the app closes. The audio thread holds its locks only for a few milliseconds, so the UI thread
/// spins briefly instead of waiting.
/// </para>
/// </summary>
public static class UiLock
{
    /// <summary>Enters <paramref name="gate"/> by spinning (no pumping). Pair with <c>Monitor.Exit</c>.</summary>
    public static void Enter(object gate)
    {
        var spin = new SpinWait();
        while (!Monitor.TryEnter(gate)) spin.SpinOnce(sleep1Threshold: -1); // -1: never Sleep(1), only yields
    }

    /// <summary>Runs <paramref name="action"/> while holding <paramref name="gate"/>, taken without pumping.</summary>
    public static void Run(object gate, Action action)
    {
        Enter(gate);
        try { action(); }
        finally { Monitor.Exit(gate); }
    }
}

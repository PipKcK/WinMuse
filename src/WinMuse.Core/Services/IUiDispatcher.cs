namespace WinMuse.Services;

/// <summary>
/// The UI thread, as services see it. Services hand events and results back to it so pages can update controls
/// directly. The app passes one built on the window's dispatcher; tests pass one that runs everything inline.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Whether the calling code is already on the UI thread.</summary>
    bool HasThreadAccess { get; }

    /// <summary>Runs <paramref name="action"/> on the UI thread soon. False if the UI thread is shutting down.</summary>
    bool TryEnqueue(Action action);

    /// <summary>A repeating timer whose <paramref name="tick"/> runs on the UI thread. Created stopped.</summary>
    IUiTimer CreateTimer(TimeSpan interval, Action tick);
}

/// <summary>A repeating timer made by <see cref="IUiDispatcher.CreateTimer"/>.</summary>
public interface IUiTimer
{
    bool IsRunning { get; }
    void Start();
    void Stop();
}

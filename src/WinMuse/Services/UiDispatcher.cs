using Microsoft.UI.Dispatching;

namespace WinMuse.Services;

/// <summary>The window's <see cref="DispatcherQueue"/> as the services' <see cref="IUiDispatcher"/>.</summary>
public sealed class UiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public UiDispatcher(DispatcherQueue queue) => _queue = queue;

    public bool HasThreadAccess => _queue.HasThreadAccess;

    public bool TryEnqueue(Action action) => _queue.TryEnqueue(() => action());

    public IUiTimer CreateTimer(TimeSpan interval, Action tick) => new Timer(_queue.CreateTimer(), interval, tick);

    /// <summary>Holds its DispatcherQueueTimer, so the timer lives as long as whoever created it.</summary>
    private sealed class Timer : IUiTimer
    {
        private readonly DispatcherQueueTimer _timer;

        public Timer(DispatcherQueueTimer timer, TimeSpan interval, Action tick)
        {
            _timer = timer;
            _timer.Interval = interval;
            _timer.Tick += (_, _) => tick();
        }

        public bool IsRunning => _timer.IsRunning;
        public void Start() => _timer.Start();
        public void Stop() => _timer.Stop();
    }
}

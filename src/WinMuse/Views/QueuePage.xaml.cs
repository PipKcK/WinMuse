using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinMuse.Controls;
using WinMuse.Dialogs;
using WinMuse.Helpers;
using WinMuse.Services;

namespace WinMuse.Views;

public sealed partial class QueuePage : Page
{
    public QueuePage()
    {
        InitializeComponent();
        var player = Svc.Player;
        Tracks.BindTo(player.Queue);
        Tracks.Sortable = false;
        Tracks.Reorderable = true;
        Tracks.PlayRequested = i => _ = player.PlayIndexAsync(i);
        Tracks.DeleteRequested += idx => player.RemoveFromQueue(idx);
        Tracks.ExtraMenuItems = (_, idx) => new[] { new MenuAction("Remove from queue", "", () => player.RemoveFromQueue(idx)) };
        player.Queue.CollectionChanged += (_, _) => UpdateSummary();
        player.TrackChanged += _ => DispatcherQueue.TryEnqueue(UpdateSummary);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        UpdateSummary();
        Effects.EntranceSlide(Header, 16);
        DispatcherQueue.TryEnqueue(Tracks.ScrollToPlaying);
    }

    private void UpdateSummary()
    {
        var q = Svc.Player.Queue;
        SummaryText.Text = $"{q.Count} {(q.Count == 1 ? "song" : "songs")} · {Format.LongTime(TimeSpan.FromMilliseconds(q.Sum(t => t.DurationMs)))}";
        // Nothing to jump to, save or clear in an empty queue; Clear keeps the current song, so it needs another one.
        JumpButton.IsEnabled = Svc.Player.CurrentTrack != null && q.Count > 0;
        SaveButton.IsEnabled = q.Count > 0;
        ClearButton.IsEnabled = q.Count > (Svc.Player.CurrentTrack != null ? 1 : 0);
    }

    private void OnJump(object sender, RoutedEventArgs e) => Tracks.ScrollToPlaying();
    private void OnClear(object sender, RoutedEventArgs e) => Svc.Player.ClearQueue(keepCurrent: true);

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (Svc.Player.Queue.Count == 0) return;
        var p = await PlaylistDialog.ShowCreateAsync(Svc.Player.Queue.ToList());
        if (p != null) Notifier.Success($"Queue saved as {p.Name}");
    }
}

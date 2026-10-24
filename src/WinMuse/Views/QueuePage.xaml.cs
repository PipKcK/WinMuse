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
        SummaryText.Text = $"{q.Count} songs · {Format.LongTime(TimeSpan.FromMilliseconds(q.Sum(t => t.DurationMs)))}";
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

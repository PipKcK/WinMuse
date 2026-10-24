using Microsoft.UI.Xaml;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Themes;

/// <summary>Shared card templates. Code-behind exists so the templates can use x:Bind and click handlers.</summary>
public sealed partial class Templates : ResourceDictionary
{
    public Templates() => InitializeComponent();

    /// <summary>Card play buttons give their card the same quick press flash (Controls/ShelfEffects) as they act.</summary>
    private static Task Feedback(object sender)
    {
        Controls.ShelfEffects.PulseCardOf(sender as DependencyObject);
        return Task.CompletedTask;
    }

    private async void OnPlayAlbum(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Album a) return;
        await Feedback(sender);
        await Svc.Player.PlayAsync(a.Tracks, context: PlayContext.Of(a));
    }

    private async void OnPlayTrack(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Track t) return;
        await Feedback(sender);
        // In a playlist's row on Home: open the playlist and play it from this song.
        if (FindPlaylist(sender as DependencyObject) is { } p && p.Tracks.Contains(t))
        {
            await Views.PlaylistDetailPage.OpenAndPlayAsync(p, t);
            return;
        }
        await Svc.Player.PlayTrackAsync(t);
    }

    private static Playlist? FindPlaylist(DependencyObject? d)
    {
        for (; d != null; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
            if (d is Microsoft.UI.Xaml.Controls.ListViewBase { DataContext: Playlist p }) return p;
        return null;
    }

    private async void OnResumeEntry(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ContinueItem c) return;
        await Feedback(sender);
        await Svc.Continue.ResumeAsync(c.Entry);
    }

    private async void OnPlayPlaylist(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Playlist p && p.Tracks.Count > 0)
        {
            Svc.Library.TouchPlaylist(p);
            await Svc.Player.PlayAsync(p.Tracks, context: PlayContext.Of(p));
        }
    }
}

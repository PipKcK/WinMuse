using System.ComponentModel;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;
using WinMuse.Views;

namespace WinMuse.Controls;

/// <summary>Compact "up next" card: the current song, the highlighted next song, and a few that follow.</summary>
public sealed partial class QueuePanel : UserControl
{
    private const int FollowingCount = 4;
    private bool _pending;

    public QueuePanel()
    {
        InitializeComponent();
        var player = Svc.Player;
        player.Queue.CollectionChanged += (_, _) => Schedule();
        player.PropertyChanged += OnPlayerChanged;
        ActualThemeChanged += (_, _) => Schedule();
        Loaded += (_, _) => Rebuild();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackService.CurrentIndex) or nameof(PlaybackService.CurrentTrack)
            or nameof(PlaybackService.Repeat) or nameof(PlaybackService.Shuffle))
            Schedule();
    }

    /// <summary>Coalesces bursts of queue changes (e.g. shuffling) into one rebuild.</summary>
    private void Schedule()
    {
        if (_pending) return;
        _pending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { _pending = false; Rebuild(); });
    }

    private void Rebuild()
    {
        var player = Svc.Player;
        var queue = player.Queue;
        Rows.Children.Clear();

        int remaining = player.CurrentIndex >= 0 ? Math.Max(0, queue.Count - player.CurrentIndex - 1) : queue.Count;
        var remainingTime = TimeSpan.FromMilliseconds(queue.Skip(Math.Max(0, player.CurrentIndex + 1)).Sum(t => t.DurationMs));
        SummaryText.Text = remaining == 0 ? "End of queue" : $"{remaining} {(remaining == 1 ? "song" : "songs")} · {Format.LongTime(remainingTime)}";

        if (player.CurrentTrack is { } current && player.CurrentIndex >= 0)
            Rows.Children.Add(Row(current, player.CurrentIndex, "Now playing", RowKind.Current));

        int next = player.UpcomingIndex;
        if (next >= 0 && next < queue.Count)
        {
            var label = player.Repeat == RepeatMode.One ? "Next · Repeat" : "Next";
            Rows.Children.Add(Row(queue[next], next, label, RowKind.Next));
            for (int k = 1, i = next + 1; k <= FollowingCount && i < queue.Count; k++, i++)
                Rows.Children.Add(Row(queue[i], i, null, RowKind.Later));
        }
        EmptyText.Visibility = next < 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private enum RowKind { Current, Next, Later }

    private FrameworkElement Row(Track t, int index, string? label, RowKind kind)
    {
        var size = kind == RowKind.Later ? 36 : 44;
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(6), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(new ArtImage { Width = size, Height = size, CornerRadius = new CornerRadius(6), DecodeSize = 56, Source = t.ArtPath, Seed = t.AlbumKey });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (label != null)
        {
            var tag = new TextBlock { Text = label.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeights.SemiBold, CharacterSpacing = 80 };
            tag.Foreground = kind == RowKind.Next ? (Brush)Application.Current.Resources["AccentGradientBrush"] : ThemeRes.Brush("TextFillColorTertiaryBrush", this);
            text.Children.Add(tag);
        }
        text.Children.Add(new TextBlock { Text = t.DisplayTitle, FontWeight = kind == RowKind.Later ? FontWeights.Normal : FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        text.Children.Add(new TextBlock { Text = t.DisplayArtist, FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var time = new TextBlock { Text = t.DurationText, FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(time, 2);
        grid.Children.Add(time);

        if (kind == RowKind.Next)
        {
            // Highlight the next song with an accent wash and edge.
            grid.Background = ThemeRes.Brush("SubtleFillColorSecondaryBrush", this);
            grid.BorderBrush = (Brush)Application.Current.Resources["AccentGradientBrush"];
            grid.BorderThickness = new Thickness(1);
        }
        if (kind == RowKind.Later) grid.Opacity = 0.85;

        grid.PointerEntered += (_, _) => { if (kind != RowKind.Next) grid.Background = ThemeRes.Brush("SubtleFillColorSecondaryBrush", this); };
        grid.PointerExited += (_, _) => { if (kind != RowKind.Next) grid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); };
        grid.Tapped += async (_, _) => { if (kind != RowKind.Current) await Svc.Player.PlayIndexAsync(index); };
        ToolTipService.SetToolTip(grid, kind == RowKind.Current ? t.DisplayTitle : $"Play {t.DisplayTitle}");
        return grid;
    }

    private void OnViewAll(object sender, RoutedEventArgs e) => App.MainWindow.Navigate(typeof(QueuePage));

    private void OnCollapse(object sender, RoutedEventArgs e) =>
        Svc.Settings.Set(nameof(AppSettings.ShowQueuePanel), s => s.ShowQueuePanel = false);
}

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Controls;

public enum TrackIndexMode { Art, TrackNumber }

/// <summary>Reusable song list: sortable header, selection mode with checkboxes, context menu, click to play, optional drag reorder.</summary>
public sealed partial class TrackList : UserControl
{
    private string? _sortField;
    private bool _sortDescending;
    private bool _narrow, _compact;

    public ObservableCollection<Track> Items { get; private set; } = new();

    public TrackIndexMode IndexMode { get; set; } = TrackIndexMode.Art;
    public bool ShowAlbum { get; set; } = true;
    public bool Sortable { get; set; } = true;
    public string EmptyMessage { get => EmptyText.Text; set => EmptyText.Text = value; }

    public bool Reorderable
    {
        get => List.CanReorderItems;
        set { List.CanReorderItems = value; List.CanDragItems = value; List.AllowDrop = value; }
    }

    public bool ShowHeader
    {
        get => Header.Visibility == Visibility.Visible;
        set => Header.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// What this list's songs belong to (album, artist, playlist), remembered by Continue Listening when a song is
    /// played from it. Null: a song played from the list is remembered as that song on its own.
    /// </summary>
    public PlayContext? Context { get; set; }

    /// <summary>If set, double-click calls this instead of replacing the queue with this list.</summary>
    public Action<int>? PlayRequested { get; set; }
    public Func<IList<Track>, List<int>, IEnumerable<MenuAction>>? ExtraMenuItems { get; set; }
    public event Action<IList<Track>>? Reordered;
    public event Action<List<int>>? DeleteRequested;

    public ListView ListView => List;

    public TrackList()
    {
        InitializeComponent();
        List.ItemsSource = Items;
        // Stripes follow positions: when rows are added, removed or moved, the visible ones are redrawn.
        List.Items.VectorChanged += (_, _) => DispatcherQueue.TryEnqueue(DecorateAll);
        List.SelectionChanged += (_, _) => DecorateAll();
        SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 600;
            var compact = e.NewSize.Width < 720;
            if (narrow == _narrow && compact == _compact) return;
            _narrow = narrow;
            _compact = compact;
            ApplyColumnsEverywhere();
        };
        Loaded += (_, _) => { SelectionService.ActiveList = this; ApplyColumnsEverywhere(); };
        Action modeChanged = ApplyColumnsEverywhere;
        Action selectionChanged = UpdateSelectAllBox;
        this.WhileLoaded(() => { SelectionService.ModeChanged += modeChanged; SelectionService.SelectionChanged += selectionChanged; },
                         () => { SelectionService.ModeChanged -= modeChanged; SelectionService.SelectionChanged -= selectionChanged; });
        Unloaded += (_, _) => { if (SelectionService.ActiveList == this) SelectionService.ActiveList = null; };
    }

    public void SetItems(IEnumerable<Track> tracks)
    {
        var list = tracks.ToList();
        if (_sortField != null) list = Sort(list, _sortField, _sortDescending);
        var bound = _boundExternally ? null : CollectionSync.SyncOrReplace(Items, list, t => t);
        if (bound == null || !ReferenceEquals(bound, Items))
        {
            Items = bound ?? new ObservableCollection<Track>(list);
            _boundExternally = false;
            List.ItemsSource = Items;
        }
        EmptyState.Visibility = Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyColumnsEverywhere();
    }

    /// <summary>Bind directly to an existing collection (e.g. the live play queue).</summary>
    private bool _boundExternally;

    public void BindTo(ObservableCollection<Track> source)
    {
        _boundExternally = true;
        Items = source;
        List.ItemsSource = source;
        EmptyState.Visibility = source.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        source.CollectionChanged += (_, _) => EmptyState.Visibility = source.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The songs an action applies to: the checked ones in Select mode, otherwise the selected row.</summary>
    public List<int> SelectedIndices()
    {
        if (SelectionService.IsActive)
        {
            var checkedRows = Items.Select((t, i) => (t, i)).Where(x => x.t.IsChecked).Select(x => x.i).ToList();
            if (checkedRows.Count > 0) return checkedRows;
        }
        var result = new List<int>();
        foreach (var r in List.SelectedRanges)
            for (int i = r.FirstIndex; i <= r.LastIndex; i++) result.Add(i);
        result.Sort();
        return result;
    }

    public List<Track> SelectedTracks() => SelectedIndices().Where(i => i < Items.Count).Select(i => Items[i]).ToList();

    /// <summary>Scrolls so the playing song is in view, with a couple of songs above it.</summary>
    public void ScrollToPlaying()
    {
        var i = Items.IndexOf(Items.FirstOrDefault(x => x.IsPlaying)!);
        if (i >= 0) List.ScrollIntoView(Items[Math.Max(0, i - 2)], ScrollIntoViewAlignment.Leading);
    }

    // ---------------- Columns ----------------

    // Column indices in the header and row templates.
    private const int ColCheck = 0, ColLead = 1, ColArtist = 4, ColYear = 5, ColAlbum = 6, ColGenre = 7;

    private void ApplyColumnsEverywhere()
    {
        ApplyColumns(Header);
        for (int i = 0; i < Items.Count; i++)
        {
            if (List.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: Grid g }) ApplyColumns(g);
        }
        SelectAllBox.Visibility = SelectionService.IsActive ? Visibility.Visible : Visibility.Collapsed;
        UpdateSelectAllBox();
    }

    private void ApplyColumns(Grid g)
    {
        if (g.ColumnDefinitions.Count < 9) return;
        bool selecting = SelectionService.IsActive;
        bool hideAlbum = !ShowAlbum || _narrow;
        g.ColumnDefinitions[ColCheck].Width = selecting ? new GridLength(28) : new GridLength(0);
        g.ColumnDefinitions[ColYear].Width = _narrow ? new GridLength(0) : new GridLength(56);
        g.ColumnDefinitions[ColAlbum].Width = hideAlbum ? new GridLength(0) : new GridLength(1.4, GridUnitType.Star);
        g.ColumnDefinitions[ColGenre].Width = _compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        foreach (var child in g.Children.OfType<FrameworkElement>())
        {
            if (child.Tag is "PlayingBorder" or "Stripe" or "SelMark") continue; // span every column; set elsewhere
            switch (Grid.GetColumn(child))
            {
                case ColCheck when g != Header: child.Visibility = selecting ? Visibility.Visible : Visibility.Collapsed; break;
                case ColYear: child.Visibility = _narrow ? Visibility.Collapsed : Visibility.Visible; break;
                case ColAlbum: child.Visibility = hideAlbum ? Visibility.Collapsed : Visibility.Visible; break;
                case ColGenre: child.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible; break;
            }
        }
        if (g != Header && g.Children.Count > ColLead && g.Children[ColLead] is Grid lead && lead.Children.Count >= 2)
        {
            lead.Children[0].Visibility = IndexMode == TrackIndexMode.Art ? Visibility.Visible : Visibility.Collapsed;
            lead.Children[1].Visibility = IndexMode == TrackIndexMode.TrackNumber ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---------------- Selection ----------------

    private void UpdateSelectAllBox()
    {
        if (!SelectionService.IsActive || Items.Count == 0) { SelectAllBox.IsChecked = false; return; }
        int n = Items.Count(t => t.IsChecked);
        SelectAllBox.IsChecked = n == 0 ? false : n == Items.Count ? true : null;
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        bool check = Items.Any(t => !t.IsChecked);
        foreach (var t in Items) t.IsChecked = check;
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.ItemContainer?.ContentTemplateRoot is Grid g)
        {
            ApplyColumns(g);
            Decorate(g, args.ItemIndex, args.ItemContainer.IsSelected);
        }
    }

    /// <summary>
    /// Zebra stripe (odd rows) and the selection marker, drawn inside the row. Set on the row's container they only
    /// appeared after the pointer passed over a row.
    /// </summary>
    private static void Decorate(Grid row, int index, bool selected)
    {
        foreach (var child in row.Children)
        {
            if (child is FrameworkElement { Tag: "Stripe" } stripe) stripe.Visibility = index % 2 == 1 ? Visibility.Visible : Visibility.Collapsed;
            else if (child is FrameworkElement { Tag: "SelMark" } mark) mark.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void DecorateAll()
    {
        for (int i = 0; i < Items.Count; i++)
            if (List.ContainerFromIndex(i) is ListViewItem { ContentTemplateRoot: Grid g } c) Decorate(g, i, c.IsSelected);
    }

    // ---------------- Sorting ----------------

    private void OnSortClick(object sender, RoutedEventArgs e)
    {
        if (!Sortable) return;
        var field = (string)((FrameworkElement)sender).Tag;
        _sortDescending = _sortField == field && !_sortDescending;
        _sortField = field;
        var sorted = Sort(Items.ToList(), field, _sortDescending);
        Items = new ObservableCollection<Track>(sorted);
        List.ItemsSource = Items;
    }

    private static List<Track> Sort(List<Track> list, string field, bool desc)
    {
        IOrderedEnumerable<Track> q = field switch
        {
            "Album" => list.OrderBy(t => t.DisplayAlbum, StringComparer.CurrentCultureIgnoreCase).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber),
            "Year" => list.OrderBy(t => t.Year).ThenBy(t => t.DisplayAlbum),
            "Duration" => list.OrderBy(t => t.DurationMs),
            "Artist" => list.OrderBy(t => t.DisplayArtist, StringComparer.CurrentCultureIgnoreCase),
            "Genre" => list.OrderBy(t => string.IsNullOrEmpty(t.Genre) ? "\uffff" : t.Genre, StringComparer.CurrentCultureIgnoreCase),
            _ => list.OrderBy(t => t.DisplayTitle, StringComparer.CurrentCultureIgnoreCase),
        };
        var result = q.ToList();
        if (desc) result.Reverse();
        return result;
    }

    // ---------------- Interaction ----------------

    private int IndexFromSource(object source)
    {
        var el = source as DependencyObject;
        while (el != null && el is not ListViewItem) el = VisualTreeHelper.GetParent(el);
        return el is ListViewItem item ? List.IndexFromContainer(item) : -1;
    }

    private int _anchor = -1; // last row clicked, for Shift+click ranges

    /// <summary>
    /// A click (or Enter) plays the song. Ctrl+click checks/unchecks it and Shift+click checks the range from the last
    /// clicked row (or unchecks it when the clicked row was checked), both in Select mode (the same one as right-click → Select); in Select mode a plain click checks too.
    /// </summary>
    private async void OnItemClick(object sender, ItemClickEventArgs e)
    {
        var t = (Track)e.ClickedItem;
        var index = List.ContainerFromItem(t) is ListViewItem c ? List.IndexFromContainer(c) : Items.IndexOf(t);
        if (index < 0) return;
        bool ctrl = IsDown(VirtualKey.Control), shift = IsDown(VirtualKey.Shift);
        if (shift && _anchor >= 0 && _anchor < Items.Count)
        {
            // The range takes the opposite of the clicked song's state: ticks it, or unticks it if it was ticked.
            bool check = !t.IsChecked;
            if (check) SelectionService.Begin();
            for (int i = Math.Min(_anchor, index); i <= Math.Max(_anchor, index); i++) Items[i].IsChecked = check;
            _anchor = index;
            return;
        }
        _anchor = index;
        if (ctrl || shift || SelectionService.IsActive)
        {
            SelectionService.Begin();
            t.IsChecked = !t.IsChecked;
            return;
        }
        await PlayAt(index);
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private async Task PlayAt(int index)
    {
        if (PlayRequested != null) PlayRequested(index);
        else await Svc.Player.PlayAsync(Items.ToList(), index, context: Context ?? PlayContext.Of(Items[index]));
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var index = IndexFromSource(e.OriginalSource);
        if (index < 0) return;
        if (!SelectedIndices().Contains(index)) List.SelectedIndex = index;
        ShowMenu(e.OriginalSource as FrameworkElement ?? List, e.GetPosition(e.OriginalSource as UIElement));
        e.Handled = true;
    }

    private void OnStarClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Track t) Svc.Favorites.ToggleFavorite(t);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        var index = IndexFromSource(sender);
        if (index < 0) return;
        if (!SelectedIndices().Contains(index)) List.SelectedIndex = index;
        ShowMenu((FrameworkElement)sender, null);
    }

    private void ShowMenu(FrameworkElement target, Windows.Foundation.Point? at)
    {
        var indices = SelectedIndices();
        var tracks = indices.Select(i => Items[i]).ToList();
        var extra = ExtraMenuItems?.Invoke(tracks, indices);
        // Selection mode entry point: checkboxes then appear in every song list.
        bool allChecked = SelectionService.IsActive && tracks.All(t => t.IsChecked);
        var select = allChecked
            ? new MenuAction("Deselect", "\uE8E6", () => { foreach (var t in tracks) t.IsChecked = false; })
            : new MenuAction("Select", "\uE73A", () => SelectionService.Begin(tracks));
        var menu = TrackActions.BuildMenu(tracks, extra, new[] { select });
        if (at is { } p) menu.ShowAt(target, new FlyoutShowOptions { Position = p });
        else menu.ShowAt(target);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete && DeleteRequested != null)
        {
            var idx = SelectedIndices();
            if (idx.Count > 0) { e.Handled = true; DeleteRequested(idx); }
        }
    }

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) => Reordered?.Invoke(Items.ToList());
}

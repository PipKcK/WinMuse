using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinMuse.Helpers;

/// <summary>
/// Page headers with a title and a toolbar side by side (<c>helpers:HeaderWrap.IsEnabled="True"</c> on the header Grid):
/// when the window is too narrow for both, the toolbar (the header's last child) moves onto its own row under the title
/// instead of drawing over it. The title is the child that holds a <see cref="TextBlock"/> styled as a page title, or
/// the first child; anything else in the header (e.g. artwork) spans both rows.
/// </summary>
public static class HeaderWrap
{
    /// <summary>Room kept for the title before the toolbar wraps; longer titles are trimmed with an ellipsis.</summary>
    private const double TitleRoom = 260;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached("IsEnabled", typeof(bool), typeof(HeaderWrap), new PropertyMetadata(false, OnEnabled));

    /// <summary>A toolbar search box's own width, kept while it is narrowed to fit a wrapped toolbar.</summary>
    private static readonly DependencyProperty FullWidthProperty =
        DependencyProperty.RegisterAttached("FullWidth", typeof(double), typeof(HeaderWrap), new PropertyMetadata(double.NaN));

    public static bool GetIsEnabled(Grid g) => (bool)g.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Grid g, bool v) => g.SetValue(IsEnabledProperty, v);

    private static void OnEnabled(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid g || e.NewValue is not true) return;
        g.Loaded += (_, _) => Update(g);
        g.SizeChanged += (_, args) => { if (args.NewSize.Width != args.PreviousSize.Width) Update(g); };
    }

    private static void Update(Grid g)
    {
        if (g.Children.Count < 2 || g.Children[^1] is not FrameworkElement bar || bar.DesiredSize.Width == 0) return;
        var title = (FrameworkElement)g.Children[0];
        foreach (var c in g.Children)
            if (c is StackPanel { Children.Count: > 0 } sp && sp.Children[0] is TextBlock && c != bar) { title = sp; break; }

        // Width left for the title and toolbar: the header minus its padding and any fixed columns before the title.
        double other = 0;
        for (int i = 0; i < Grid.GetColumn(title) && i < g.ColumnDefinitions.Count; i++) other += g.ColumnDefinitions[i].ActualWidth + g.ColumnSpacing;
        var room = g.ActualWidth - g.Padding.Left - g.Padding.Right - other;
        var box = (bar as StackPanel)?.Children.OfType<TextBox>().FirstOrDefault();
        var full = box == null ? double.NaN : (double)box.GetValue(FullWidthProperty);
        if (box != null && double.IsNaN(full)) { full = box.Width; box.SetValue(FullWidthProperty, full); }
        // Decide on the toolbar at full width, so narrowing its search box never flips the decision back.
        var natural = NaturalWidth(bar);
        var barWidth = box == null || double.IsNaN(full) ? natural : natural - box.Width + full;
        var wrap = room < barWidth + TitleRoom;
        // On its own row the toolbar may still be too wide: the search box gives up width (down to 120 px) so the buttons stay on screen.
        if (box != null && !double.IsNaN(full))
        {
            var width = wrap ? Math.Clamp(room - (barWidth - full), 120, full) : full;
            if (box.Width != width) box.Width = width;
        }
        if (wrap == (Grid.GetRow(bar) == 1)) return;

        while (g.RowDefinitions.Count < 2) g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(bar, wrap ? 1 : 0);
        Grid.SetColumn(bar, wrap ? Grid.GetColumn(title) : g.ColumnDefinitions.Count > 0 ? g.ColumnDefinitions.Count - 1 : 0);
        bar.HorizontalAlignment = wrap ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        bar.Margin = wrap ? new Thickness(0, 12, 0, 0) : new Thickness(0);
        foreach (var c in g.Children)
            if (c != bar && c != title) Grid.SetRowSpan((FrameworkElement)c, wrap ? 2 : 1);
    }

    /// <summary>The toolbar's full width. Its DesiredSize is capped at the room it was given, but a horizontal
    /// StackPanel measures its items without a limit, so their sizes add up to what it really needs.</summary>
    private static double NaturalWidth(FrameworkElement bar)
    {
        if (bar is not StackPanel { Orientation: Orientation.Horizontal } sp) return bar.DesiredSize.Width;
        var items = sp.Children.Where(c => c.Visibility == Visibility.Visible).ToList();
        return items.Sum(c => c.DesiredSize.Width) + sp.Spacing * Math.Max(0, items.Count - 1) + sp.Padding.Left + sp.Padding.Right;
    }
}

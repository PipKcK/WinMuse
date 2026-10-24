using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinMuse.Controls;

/// <summary>
/// Tiles in equal-width columns that always fill the row: as many columns as fit at <see cref="MinItemWidth"/>
/// (between <see cref="MinColumns"/> and <see cref="MaxColumns"/>), stretched to the full width. Up to
/// <see cref="MaxItems"/> tiles show, in as many rows as that takes (a narrow window gets more rows, not fewer tiles).
/// </summary>
public sealed class QuickGrid : Panel
{
    public double MinItemWidth { get; set; } = 260;
    public int MinColumns { get; set; } = 2;
    public int MaxColumns { get; set; } = 5;
    public int MaxRows { get; set; } = 3; // kept for XAML compatibility; MaxItems decides
    public int MaxItems { get; set; } = 10;
    public double ItemHeight { get; set; } = 64;
    public double Spacing { get; set; } = 12;

    private int Columns(double width) => Math.Clamp((int)((width + Spacing) / (MinItemWidth + Spacing)), MinColumns, MaxColumns);
    private double ItemWidth(double width, int cols) => Math.Max(0, (width - Spacing * (cols - 1)) / cols);

    protected override Size MeasureOverride(Size available)
    {
        var width = double.IsInfinity(available.Width) ? MinItemWidth * MinColumns + Spacing : available.Width;
        int cols = Columns(width);
        var size = new Size(ItemWidth(width, cols), ItemHeight);
        int visible = Math.Min(Children.Count, MaxItems);
        for (int i = 0; i < Children.Count; i++) Children[i].Measure(i < visible ? size : new Size(0, 0));
        int rows = (visible + cols - 1) / cols;
        return new Size(width, rows == 0 ? 0 : rows * ItemHeight + (rows - 1) * Spacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int cols = Columns(finalSize.Width);
        var w = ItemWidth(finalSize.Width, cols);
        int visible = Math.Min(Children.Count, MaxItems);
        for (int i = 0; i < Children.Count; i++)
        {
            if (i >= visible) { Children[i].Arrange(new Rect(0, 0, 0, 0)); continue; }
            int row = i / cols, col = i % cols;
            Children[i].Arrange(new Rect(col * (w + Spacing), row * (ItemHeight + Spacing), w, ItemHeight));
        }
        return finalSize;
    }
}

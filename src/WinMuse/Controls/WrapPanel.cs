using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WinMuse.Controls;

/// <summary>Lays children out left to right, wrapping onto new lines (used for artist chips).</summary>
public sealed class WrapPanel : Panel
{
    public double Spacing { get; set; } = 6;
    public double LineSpacing { get; set; } = 6;

    protected override Size MeasureOverride(Size availableSize)
    {
        double x = 0, y = 0, line = 0, width = 0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            var s = child.DesiredSize;
            if (x > 0 && x + s.Width > availableSize.Width) { y += line + LineSpacing; x = 0; line = 0; }
            x += s.Width + Spacing;
            line = Math.Max(line, s.Height);
            width = Math.Max(width, x - Spacing);
        }
        return new Size(double.IsInfinity(availableSize.Width) ? width : Math.Min(width, availableSize.Width), y + line);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, line = 0;
        foreach (var child in Children)
        {
            var s = child.DesiredSize;
            if (x > 0 && x + s.Width > finalSize.Width) { y += line + LineSpacing; x = 0; line = 0; }
            child.Arrange(new Rect(x, y, Math.Min(s.Width, finalSize.Width), s.Height));
            x += s.Width + Spacing;
            line = Math.Max(line, s.Height);
        }
        return finalSize;
    }
}

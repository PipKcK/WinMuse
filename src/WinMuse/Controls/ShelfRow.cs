using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKeyModifiers = Windows.System.VirtualKeyModifiers;

namespace WinMuse.Controls;

/// <summary>
/// Wraps a horizontal shelf (a ListView of cards). The mouse wheel over the row scrolls the page; Shift + wheel and
/// tilt wheels slide the row. While the pointer is over the row, ‹ › arrows appear at its left and right ends; each
/// click glides the row by most of its width. An arrow only shows when there is more to see in that direction.
/// Touchpad swipes and touch scroll the row natively. All wheel and arrow movement is eased (Helpers/SmoothScroll).
/// </summary>
public sealed class ShelfRow : Grid
{
    private const double ArtCenter = 8 + 87; // the row's top padding plus half the card artwork: arrows sit on the art

    private readonly Border _left, _right;
    private ScrollViewer? _shelf;
    private bool _over;

    public ShelfRow()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // the whole row area counts as "over the row"
        _left = Arrow("", HorizontalAlignment.Left, "Scroll left", -1);
        _right = Arrow("", HorizontalAlignment.Right, "Scroll right", 1);
        Loaded += (_, _) =>
        {
            if (!Children.Contains(_left)) { Children.Add(_left); Children.Add(_right); } // on top of the row
            Hook();
            Update();
        };
        AddHandler(PointerEnteredEvent, new PointerEventHandler((_, e) =>
        {
            if (e.Pointer.PointerDeviceType != PointerDeviceType.Mouse) return;
            _over = true;
            Hook();
            Update();
        }), true);
        AddHandler(PointerExitedEvent, new PointerEventHandler((_, e) =>
        {
            // Exits between children inside the row also arrive here: only leaving the row counts.
            var p = e.GetCurrentPoint(this).Position;
            if (p.X >= 0 && p.Y >= 0 && p.X < ActualWidth && p.Y < ActualHeight) return;
            _over = false;
            Update();
        }), true);
    }

    /// <summary>
    /// A round arrow drawn by hand rather than with a Button, whose hover state turns it see-through. Solid in both
    /// themes, with a soft shadow; it grows a little on hover and dips when pressed.
    /// </summary>
    private Border Arrow(string glyph, HorizontalAlignment side, string name, int direction)
    {
        var icon = new FontIcon { Glyph = glyph, FontSize = 14 };
        var disc = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20), BorderThickness = new Thickness(1),
            Child = icon,
            BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(150) },
            ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(150) },
            CenterPoint = new System.Numerics.Vector3(20, 20, 0),
        };
        void Paint(bool hover)
        {
            disc.Background = Helpers.ThemeRes.Brush(hover ? "ShelfArrowHoverBrush" : "ShelfArrowBrush", this);
            disc.BorderBrush = Helpers.ThemeRes.Brush("ShelfArrowStrokeBrush", this);
            icon.Foreground = Helpers.ThemeRes.Brush("TextFillColorPrimaryBrush", this);
        }
        Paint(false);
        ActualThemeChanged += (_, _) => Paint(false);
        Loaded += (_, _) => Paint(false); // the theme can change while Home is cached off screen
        ToolTipService.SetToolTip(disc, name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(disc, name);
        disc.PointerEntered += (_, _) => { Paint(true); disc.Scale = new System.Numerics.Vector3(1.08f, 1.08f, 1); };
        disc.PointerExited += (_, _) => { Paint(false); disc.Scale = System.Numerics.Vector3.One; };
        disc.PointerPressed += (_, _) => disc.Scale = new System.Numerics.Vector3(0.94f, 0.94f, 1);
        disc.PointerReleased += (_, _) => disc.Scale = new System.Numerics.Vector3(1.08f, 1.08f, 1);
        disc.Tapped += (_, e) =>
        {
            e.Handled = true;
            if (_shelf != null) Helpers.SmoothScroll.For(_shelf, horizontal: true).Glide(direction * _shelf.ViewportWidth * 0.8);
        };
        // The fade runs on this wrapper so it doesn't clash with the disc's own hover animation.
        return new Border
        {
            Child = disc, HorizontalAlignment = side, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(side == HorizontalAlignment.Left ? 8 : 0, ArtCenter - 20, side == HorizontalAlignment.Right ? 8 : 0, 0),
            Opacity = 0, IsHitTestVisible = false,
            Shadow = new ThemeShadow(), Translation = new System.Numerics.Vector3(0, 0, 24),
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) },
        };
    }

    private void Hook()
    {
        if (_shelf != null) return;
        var list = Children.OfType<ListViewBase>().FirstOrDefault();
        _shelf = list != null ? Descendant<ScrollViewer>(list) : null;
        if (_shelf == null || VisualTreeHelper.GetChildrenCount(_shelf) == 0 || VisualTreeHelper.GetChild(_shelf, 0) is not UIElement frame) { _shelf = null; return; }
        // On the scroll viewer's template root, so it runs before the scroll viewer turns the wheel into sideways
        // scrolling, and covers both the content and the strip kept for the (hidden) scrollbar.
        frame.AddHandler(PointerWheelChangedEvent, new PointerEventHandler(OnWheel), false);
        _shelf.ViewChanged += (_, _) => Update();
        _shelf.SizeChanged += (_, _) => Update();
    }

    /// <summary>The plain wheel scrolls the page; Shift + wheel and tilt wheels slide the row. Both eased.</summary>
    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        if (_shelf == null) return;
        var props = e.GetCurrentPoint(this).Properties;
        bool sideways = props.IsHorizontalMouseWheel || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift);
        if (sideways)
        {
            // Tilt right is positive; wheel down with Shift (negative delta) moves right.
            double notches = (props.IsHorizontalMouseWheel ? props.MouseWheelDelta : -props.MouseWheelDelta) / 120.0;
            Helpers.SmoothScroll.For(_shelf, horizontal: true).By(notches * Helpers.SmoothScroll.NotchPixels * 2);
        }
        else if (Ancestor<ScrollViewer>(_shelf) is { } page)
            Helpers.SmoothScroll.For(page).By(-props.MouseWheelDelta / 120.0 * Helpers.SmoothScroll.NotchPixels);
        e.Handled = true;
    }

    private static T? Ancestor<T>(DependencyObject d) where T : DependencyObject
    {
        for (var p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p)) if (p is T t) return t;
        return null;
    }

    private void Update()
    {
        bool canScroll = _shelf is { ScrollableWidth: > 1 };
        Show(_left, _over && canScroll && _shelf!.HorizontalOffset > 1);
        Show(_right, _over && canScroll && _shelf!.HorizontalOffset < _shelf.ScrollableWidth - 1);
    }

    private static void Show(Border arrow, bool show)
    {
        arrow.Opacity = show ? 1 : 0;
        arrow.IsHitTestVisible = show;
    }

    private static T? Descendant<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (Descendant<T>(c) is { } found) return found;
        }
        return null;
    }
}

using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinMuse.Controls;

/// <summary>
/// <b>EdgeFade</b>, for horizontal shelves on Home (switched on from the shelf style): cards cut off by either end of
/// the shelf fade out and shrink slightly, so the row melts into the edges instead of ending in a hard cut.
/// </summary>
public static class ShelfEffects
{
    private static readonly DependencyProperty HookedProperty =
        DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(ShelfEffects), new PropertyMetadata(false));

    // ---------------- EdgeFade ----------------

    public static readonly DependencyProperty EdgeFadeProperty =
        DependencyProperty.RegisterAttached("EdgeFade", typeof(bool), typeof(ShelfEffects), new PropertyMetadata(false, OnEdgeFadeChanged));

    public static bool GetEdgeFade(DependencyObject o) => (bool)o.GetValue(EdgeFadeProperty);
    public static void SetEdgeFade(DependencyObject o, bool v) => o.SetValue(EdgeFadeProperty, v);

    private static void OnEdgeFadeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListViewBase list || e.NewValue is not true) return;
        list.Loaded += (_, _) =>
        {
            if (Find<ScrollViewer>(list) is not { } shelf || shelf.GetValue(HookedProperty) is true) return;
            shelf.SetValue(HookedProperty, true);
            shelf.ViewChanged += (_, _) => Fade(list, shelf);
            shelf.SizeChanged += (_, _) => Fade(list, shelf);
            list.ContainerContentChanging += (_, _) => list.DispatcherQueue.TryEnqueue(() => Fade(list, shelf));
            Fade(list, shelf);
        };
    }

    /// <summary>Fades and shrinks each card by how much of it is hidden past either end of the shelf.</summary>
    private static void Fade(ListViewBase list, ScrollViewer shelf)
    {
        if (list.ItemsPanelRoot is not Panel panel) return;
        double viewport = shelf.ViewportWidth;
        foreach (var child in panel.Children.OfType<FrameworkElement>())
        {
            double w = child.ActualWidth;
            if (w <= 0) continue;
            double x = child.TransformToVisual(shelf).TransformPoint(default).X;
            HookHover(child, list, shelf);
            double hidden = Math.Max(Math.Max(0, -x), Math.Max(0, x + w - viewport)) / w; // 0 = fully visible, 1 = fully hidden
            // Strong falloff: a card a third hidden is already faint, so the row melts into its ends.
            double shown = 1 - Math.Clamp(hidden * 2.6, 0, 1);
            double ease = shown * shown * (3 - 2 * shown); // smoothstep
            child.Opacity = 0.12 + 0.88 * ease;
            // Hover zooms the whole card (frame and edge included). Cards are laid out at their hover size and drawn
            // at RestScale when idle, so the zoom only fills space that is already theirs and can never be clipped.
            double state = child.GetValue(PressedProperty) is true ? RestScale * 0.965 : child.GetValue(HoveredProperty) is true ? 1 : RestScale;
            float s = (float)((0.9 + 0.1 * ease) * state);
            child.CenterPoint = new Vector3((float)w / 2, (float)child.ActualHeight / 2, 0);
            child.Scale = new Vector3(s, s, 1);
        }
    }

    /// <summary>Idle size relative to hover size (cards are laid out 174 px wide: 174 × 0.966 ≈ 168 px when idle).</summary>
    public const double RestScale = 0.966;

    private static readonly DependencyProperty HoveredProperty =
        DependencyProperty.RegisterAttached("Hovered", typeof(bool), typeof(ShelfEffects), new PropertyMetadata(false));

    private static void HookHover(FrameworkElement item, ListViewBase list, ScrollViewer shelf)
    {
        if (item.GetValue(HookedProperty) is true) return;
        item.SetValue(HookedProperty, true);
        item.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(220) };
        item.PointerEntered += (_, _) => { item.SetValue(HoveredProperty, true); Fade(list, shelf); };
        Microsoft.UI.Xaml.Input.PointerEventHandler off = (_, _) => { item.SetValue(HoveredProperty, false); item.SetValue(PressedProperty, false); Fade(list, shelf); };
        item.PointerExited += off; item.PointerCanceled += off; item.PointerCaptureLost += off;
        // Press: the card dips in. Release: it springs back and a soft accent glow pulses across it.
        // (Handled events too: the list item marks presses as handled.)
        item.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) =>
        {
            if (!e.GetCurrentPoint(item).Properties.IsLeftButtonPressed) return;
            item.SetValue(PressedProperty, true); Fade(list, shelf);
            if (FindTagged(item, "PressGlow") is FrameworkElement glow) glow.Opacity = 1;
        }), true);
        item.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) =>
        {
            if (item.GetValue(PressedProperty) is not true) return;
            item.SetValue(PressedProperty, false); Fade(list, shelf);
            if (FindTagged(item, "PressGlow") is FrameworkElement glow) glow.Opacity = 0; // fades out (its transition)
        }), true);
    }

    private static readonly DependencyProperty PressedProperty =
        DependencyProperty.RegisterAttached("Pressed", typeof(bool), typeof(ShelfEffects), new PropertyMetadata(false));

    /// <summary>Press feedback for something inside a card (its play button): pulses the card it belongs to.</summary>
    public static void PulseCardOf(DependencyObject? inside)
    {
        for (var d = inside; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is ListViewItem item) { Pulse(item); return; }
    }

    /// <summary>Flashes the card's "PressGlow" layer: up quickly, then a slower fade.</summary>
    private static void Pulse(DependencyObject root)
    {
        if (FindTagged(root, "PressGlow") is not FrameworkElement glow) return;
        glow.Opacity = 1;
        var t = glow.DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromMilliseconds(70);
        t.IsRepeating = false;
        t.Tick += (_, _) => glow.Opacity = 0;
        t.Start();
    }

    private static DependencyObject? FindTagged(DependencyObject d, string tag)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is FrameworkElement { Tag: string t } && t == tag) return c;
            if (FindTagged(c, tag) is { } found) return found;
        }
        return null;
    }

    // ---------------- Tree helpers ----------------

    private static T? Find<T>(DependencyObject d) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is T t) return t;
            if (Find<T>(c) is { } found) return found;
        }
        return null;
    }
}

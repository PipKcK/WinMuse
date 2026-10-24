using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace WinMuse.Controls;

/// <summary>Attached behaviours for subtle motion: hover lift/scale and entrance animations.</summary>
public static class Effects
{
    // ---------------- Hover scale + lift ----------------

    public static readonly DependencyProperty HoverScaleProperty =
        DependencyProperty.RegisterAttached("HoverScale", typeof(double), typeof(Effects), new PropertyMetadata(1.0, OnHoverScaleChanged));

    public static double GetHoverScale(DependencyObject o) => (double)o.GetValue(HoverScaleProperty);
    public static void SetHoverScale(DependencyObject o, double v) => o.SetValue(HoverScaleProperty, v);

    private static void OnHoverScaleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.PointerEntered -= OnEnter; el.PointerExited -= OnExit; el.PointerCanceled -= OnExit; el.PointerCaptureLost -= OnExit;
        if ((double)e.NewValue == 1.0) return;
        el.ScaleTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(260) };
        el.TranslationTransition ??= new Vector3Transition { Duration = TimeSpan.FromMilliseconds(260) };
        el.PointerEntered += OnEnter; el.PointerExited += OnExit; el.PointerCanceled += OnExit; el.PointerCaptureLost += OnExit;
        // A small dip while pressed, back to the hover size on release.
        el.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnPress), true);
        el.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(OnEnter), true);
    }

    private static void OnPress(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        el.CenterPoint = new Vector3((float)el.ActualWidth / 2, (float)el.ActualHeight / 2, 0);
        el.Scale = new Vector3(0.97f, 0.97f, 1);
        el.Translation = Vector3.Zero;
    }

    private static void OnEnter(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement el) return;
        var s = (float)GetHoverScale(el);
        el.CenterPoint = new Vector3((float)el.ActualWidth / 2, (float)el.ActualHeight / 2, 0);
        el.Scale = new Vector3(s, s, 1);
        el.Translation = new Vector3(0, -2, 0);
    }

    private static void OnExit(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is not UIElement el) return;
        el.Scale = Vector3.One;
        el.Translation = Vector3.Zero;
    }

    // ---------------- Underline a heading's title on hover ----------------

    /// <summary>
    /// True: the element behaves like a text link. Its first TextBlock (its title) is underlined while the pointer is
    /// over it, and the pointer becomes the hand.
    /// </summary>
    public static readonly DependencyProperty HoverUnderlineProperty =
        DependencyProperty.RegisterAttached("HoverUnderline", typeof(bool), typeof(Effects), new PropertyMetadata(false, OnHoverUnderlineChanged));

    public static bool GetHoverUnderline(DependencyObject o) => (bool)o.GetValue(HoverUnderlineProperty);
    public static void SetHoverUnderline(DependencyObject o, bool v) => o.SetValue(HoverUnderlineProperty, v);

    private static void OnHoverUnderlineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.PointerEntered -= OnUnderlineEnter; el.PointerExited -= OnUnderlineExit; el.PointerCanceled -= OnUnderlineExit; el.PointerCaptureLost -= OnUnderlineExit;
        if (!(bool)e.NewValue) return;
        SetHandCursor(el);
        el.PointerEntered += OnUnderlineEnter; el.PointerExited += OnUnderlineExit; el.PointerCanceled += OnUnderlineExit; el.PointerCaptureLost += OnUnderlineExit;
    }

    private static void OnUnderlineEnter(object sender, PointerRoutedEventArgs e) => Underline(sender as DependencyObject, true);
    private static void OnUnderlineExit(object sender, PointerRoutedEventArgs e) => Underline(sender as DependencyObject, false);

    private static void Underline(DependencyObject? root, bool on)
    {
        if (FirstText(root) is { } text) text.TextDecorations = on ? Windows.UI.Text.TextDecorations.Underline : Windows.UI.Text.TextDecorations.None;
    }

    // UIElement.ProtectedCursor is only settable from a subclass; links built from plain Buttons set it this way.
    private static readonly System.Reflection.PropertyInfo? CursorProperty =
        typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    private static void SetHandCursor(UIElement el)
    {
        if (el is Microsoft.UI.Xaml.Controls.HyperlinkButton) return; // has it already
        try { CursorProperty?.SetValue(el, InputSystemCursor.Create(InputSystemCursorShape.Hand)); }
        catch { /* the cursor is a nicety */ }
    }

    private static Microsoft.UI.Xaml.Controls.TextBlock? FirstText(DependencyObject? d)
    {
        if (d == null || d is Microsoft.UI.Xaml.Controls.IconElement) return null; // an icon's glyph is not the title
        if (d is Microsoft.UI.Xaml.Controls.TextBlock t) return t;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FirstText(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }

    // ---------------- Zoom an inner element on hover (e.g. card artwork; the card frame stays still) ----------------

    public static readonly DependencyProperty HoverZoomTargetProperty =
        DependencyProperty.RegisterAttached("HoverZoomTarget", typeof(UIElement), typeof(Effects), new PropertyMetadata(null, OnHoverZoomTargetChanged));

    public static UIElement GetHoverZoomTarget(DependencyObject o) => (UIElement)o.GetValue(HoverZoomTargetProperty);
    public static void SetHoverZoomTarget(DependencyObject o, UIElement v) => o.SetValue(HoverZoomTargetProperty, v);

    private static void OnHoverZoomTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement host || e.NewValue is not FrameworkElement target) return;
        target.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(320) };
        host.PointerEntered += (_, _) =>
        {
            target.CenterPoint = new Vector3((float)target.ActualWidth / 2, (float)target.ActualHeight / 2, 0);
            target.Scale = new Vector3(1.07f, 1.07f, 1);
        };
        PointerEventHandler reset = (_, _) => target.Scale = Vector3.One;
        host.PointerExited += reset; host.PointerCanceled += reset; host.PointerCaptureLost += reset;
    }

    // ---------------- Reveal-on-hover child (e.g. play button on a card) ----------------

    public static readonly DependencyProperty RevealTargetProperty =
        DependencyProperty.RegisterAttached("RevealTarget", typeof(UIElement), typeof(Effects), new PropertyMetadata(null, OnRevealTargetChanged));

    public static UIElement GetRevealTarget(DependencyObject o) => (UIElement)o.GetValue(RevealTargetProperty);
    public static void SetRevealTarget(DependencyObject o, UIElement v) => o.SetValue(RevealTargetProperty, v);

    private static void OnRevealTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement host || e.NewValue is not UIElement target) return;
        target.Opacity = 0;
        target.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) };
        target.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(220) };
        target.Translation = new Vector3(0, 6, 0);
        host.PointerEntered += (_, _) => { target.Opacity = 1; target.Translation = Vector3.Zero; };
        host.PointerExited += (_, _) => { target.Opacity = 0; target.Translation = new Vector3(0, 6, 0); };
    }

    // ---------------- Composition helpers ----------------

    /// <summary>Fades and slides an element in (used for page headers and overlays).</summary>
    public static void EntranceSlide(UIElement el, float fromY = 24, int delayMs = 0, int durationMs = 520)
    {
        var v = ElementCompositionPreview.GetElementVisual(el);
        var c = v.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f));

        ElementCompositionPreview.SetIsTranslationEnabled(el, true);
        var slide = c.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0, new Vector3(0, fromY, 0));
        slide.InsertKeyFrame(1, Vector3.Zero, ease);
        slide.Duration = TimeSpan.FromMilliseconds(durationMs);
        slide.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        slide.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = TimeSpan.FromMilliseconds(durationMs);
        fade.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        v.StartAnimation("Translation", slide);
        v.StartAnimation("Opacity", fade);
    }

    /// <summary>
    /// A subtle change in place: the element's new content eases in (a short fade from faint, rising a few pixels),
    /// e.g. a card that now shows another song.
    /// </summary>
    public static void SwapIn(UIElement el, int durationMs = 420)
    {
        var v = ElementCompositionPreview.GetElementVisual(el);
        var c = v.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));
        ElementCompositionPreview.SetIsTranslationEnabled(el, true);
        var slide = c.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0, new Vector3(0, 6, 0));
        slide.InsertKeyFrame(1, Vector3.Zero, ease);
        slide.Duration = TimeSpan.FromMilliseconds(durationMs);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, 0.15f);
        fade.InsertKeyFrame(1, 1, ease);
        fade.Duration = TimeSpan.FromMilliseconds(durationMs);
        v.StartAnimation("Translation", slide);
        v.StartAnimation("Opacity", fade);
    }

    /// <summary>Fades and slides an element out, then runs <paramref name="done"/> (e.g. to collapse it).</summary>
    public static void ExitSlide(UIElement el, Action done, float toY = 24, int durationMs = 220)
    {
        var v = ElementCompositionPreview.GetElementVisual(el);
        var c = v.Compositor;
        ElementCompositionPreview.SetIsTranslationEnabled(el, true);
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.7f, 0f), new Vector2(0.84f, 0f));
        var batch = c.CreateScopedBatch(CompositionBatchTypes.Animation);
        var slide = c.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(1, new Vector3(0, toY, 0), ease);
        slide.Duration = TimeSpan.FromMilliseconds(durationMs);
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, 0, ease);
        fade.Duration = TimeSpan.FromMilliseconds(durationMs);
        v.StartAnimation("Translation", slide);
        v.StartAnimation("Opacity", fade);
        batch.End();
        batch.Completed += (_, _) =>
        {
            done();
            v.Opacity = 1;
            v.Properties.InsertVector3("Translation", Vector3.Zero);
        };
    }

    /// <summary>Quick "pop" when an element changes (e.g. play/pause glyph).</summary>
    public static void Pop(UIElement el, float from = 0.85f)
    {
        var v = ElementCompositionPreview.GetElementVisual(el);
        var c = v.Compositor;
        if (el is FrameworkElement fe) v.CenterPoint = new Vector3((float)fe.ActualWidth / 2, (float)fe.ActualHeight / 2, 0);
        var a = c.CreateVector3KeyFrameAnimation();
        a.InsertKeyFrame(0, new Vector3(from, from, 1));
        a.InsertKeyFrame(1, Vector3.One, c.CreateCubicBezierEasingFunction(new Vector2(0.3f, 1.5f), new Vector2(0.5f, 1f)));
        a.Duration = TimeSpan.FromMilliseconds(320);
        v.StartAnimation("Scale", a);
    }

    /// <summary>Cross-fades content: fade out, run the swap, fade in.</summary>
    public static async Task CrossFadeAsync(UIElement el, Action swap, int ms = 160)
    {
        el.OpacityTransition ??= new ScalarTransition { Duration = TimeSpan.FromMilliseconds(ms) };
        el.Opacity = 0;
        await Task.Delay(ms);
        swap();
        el.Opacity = 1;
    }
}

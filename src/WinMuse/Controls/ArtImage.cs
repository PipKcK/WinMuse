using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinMuse.Helpers;

namespace WinMuse.Controls;

/// <summary>
/// Album art with a generated gradient placeholder and a soft fade-in once the image is decoded.
/// </summary>
public sealed class ArtImage : UserControl
{
    private readonly Border _frame;
    private readonly Border _placeholder;
    private readonly FontIcon _glyph;
    // The cover is painted by a centered ImageBrush: an Image element left-aligns oversized content, so wide or tall
    // covers showed their left/top part. The brush always center-crops (UniformToFill) to the box.
    private readonly Border _image;
    private readonly ImageBrush _brush;

    // Not exactly 0: WinUI skips decoding fully transparent images, so ImageOpened would never fire.
    private const double Hidden = 0.01;

    public static readonly DependencyProperty SourceProperty =
        DependencyProperty.Register(nameof(Source), typeof(string), typeof(ArtImage), new PropertyMetadata(null, (d, _) => ((ArtImage)d).Load()));

    public static readonly DependencyProperty SeedProperty =
        DependencyProperty.Register(nameof(Seed), typeof(string), typeof(ArtImage), new PropertyMetadata(null, (d, _) => ((ArtImage)d).UpdatePlaceholder()));

    public static readonly DependencyProperty DecodeSizeProperty =
        DependencyProperty.Register(nameof(DecodeSize), typeof(int), typeof(ArtImage), new PropertyMetadata(256, (d, _) => ((ArtImage)d).Load()));

    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(ArtImage), new PropertyMetadata("", (d, e) => ((ArtImage)d)._glyph.Glyph = (string)e.NewValue));

    public static readonly DependencyProperty StretchProperty =
        DependencyProperty.Register(nameof(Stretch), typeof(Stretch), typeof(ArtImage), new PropertyMetadata(Stretch.UniformToFill, (d, e) => ((ArtImage)d)._brush.Stretch = (Stretch)e.NewValue));

    public string? Source { get => (string?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public string? Seed { get => (string?)GetValue(SeedProperty); set => SetValue(SeedProperty, value); }
    public int DecodeSize { get => (int)GetValue(DecodeSizeProperty); set => SetValue(DecodeSizeProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public Stretch Stretch { get => (Stretch)GetValue(StretchProperty); set => SetValue(StretchProperty, value); }

    public ArtImage()
    {
        _glyph = new FontIcon
        {
            Glyph = "",
            FontSize = 28,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) { Opacity = 0.85 },
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var box = new Viewbox { Child = _glyph, MaxWidth = 72, MaxHeight = 72 };
        _placeholder = new Border { Child = box };
        // Keep the placeholder glyph proportional to the tile size.
        _placeholder.SizeChanged += (_, e) => box.Margin = new Thickness(Math.Max(6, Math.Min(e.NewSize.Width, e.NewSize.Height) * 0.3));
        _brush = new ImageBrush { Stretch = Stretch.UniformToFill, AlignmentX = AlignmentX.Center, AlignmentY = AlignmentY.Center };
        _image = new Border
        {
            Background = _brush,
            Opacity = Hidden,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(320) },
        };
        _brush.ImageOpened += (_, _) => _image.Opacity = 1;
        // A card taken off screen mid fade-in (rows moved, Home left and re-entered) can keep its picture stuck nearly
        // invisible; when it comes back with a decoded picture, show it fully again.
        Loaded += (_, _) => { if (IsDecoded) ForceShown(); };
        _brush.ImageFailed += (_, _) => { _image.Opacity = Hidden; _placeholder.Opacity = 1; };

        _frame = new Border
        {
            Child = new Grid { Children = { _placeholder, _image } },
        };
        Content = _frame;
        IsTabStop = false;

        RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) => _frame.CornerRadius = CornerRadius);
        // Non-square artwork is center-cropped to exactly this box (on screen only; files are never changed):
        // without the clip, a wide cover keeps its full width and spills over neighbouring cards and buttons.
        // Rounded frames already crop smoothly to their corners; an extra rectangular clip there makes the curve jagged.
        _frame.SizeChanged += (_, e) => UpdateClip(e.NewSize);
        RegisterPropertyChangedCallback(CornerRadiusProperty, (_, _) => UpdateClip(new Windows.Foundation.Size(_frame.ActualWidth, _frame.ActualHeight)));
        UpdatePlaceholder();
    }

    private bool IsDecoded => _brush.ImageSource is BitmapImage { PixelWidth: > 0 };

    /// <summary>Full opacity at once, without the fade (a re-applied fade could stall again).</summary>
    private void ForceShown()
    {
        var fade = _image.OpacityTransition;
        _image.OpacityTransition = null;
        _image.Opacity = 0.999; // a real change, so the rendered value is refreshed even if the property already says 1
        _image.Opacity = 1;
        _image.OpacityTransition = fade;
    }

    private void UpdateClip(Windows.Foundation.Size size)
    {
        var c = CornerRadius;
        bool square = c.TopLeft == 0 && c.TopRight == 0 && c.BottomLeft == 0 && c.BottomRight == 0;
        _frame.Clip = square ? new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, size.Width, size.Height) } : null;
    }

    /// <summary>Never asks for more room than it is given (a cropped cover must not widen its card).</summary>
    protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size available)
    {
        var desired = base.MeasureOverride(available);
        return new Windows.Foundation.Size(
            double.IsInfinity(available.Width) ? desired.Width : Math.Min(desired.Width, available.Width),
            double.IsInfinity(available.Height) ? desired.Height : Math.Min(desired.Height, available.Height));
    }

    private void UpdatePlaceholder()
    {
        var (a, b) = ColorKit.GradientFor(Seed ?? Source);
        _placeholder.Background = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops = { new GradientStop { Color = a, Offset = 0 }, new GradientStop { Color = b, Offset = 1 } },
        };
    }

    private void Load()
    {
        var path = Source;
        if (string.IsNullOrEmpty(path))
        {
            _brush.ImageSource = null;
            _image.Opacity = Hidden;
            _placeholder.Opacity = 1; // no cover: the generated gradient is the artwork
            return;
        }
        // A cover is coming: keep the colorful placeholder hidden so it never flashes while the image decodes
        // (e.g. when Home rebuilds its tiles on the way back from another page).
        _placeholder.Opacity = 0;

        if (_brush.ImageSource is BitmapImage { UriSource: { } existing } && existing.LocalPath.Equals(path, StringComparison.OrdinalIgnoreCase))
        {
            if (IsDecoded) ForceShown(); // same picture on a reused card: make sure it is visible
            return;
        }

        _image.Opacity = Hidden;
        try
        {
            _brush.ImageSource = new BitmapImage
            {
                DecodePixelWidth = DecodeSize,
                DecodePixelType = DecodePixelType.Logical,
                UriSource = new Uri(path),
            };
        }
        catch
        {
            _brush.ImageSource = null;
        }
        if (Seed == null) UpdatePlaceholder();
    }
}

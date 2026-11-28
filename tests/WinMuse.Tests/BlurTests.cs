using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;
using Xunit;

namespace WinMuse.Tests;

/// <summary>The background image's blur styles (Helpers/ImageBlur) and the Blur slider's scale (Helpers/WallpaperBlur).</summary>
public class BlurTests
{
    private const int W = 41, H = 41;

    private static byte[] Flat(byte v)
    {
        var px = new byte[W * H * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
        return px;
    }

    /// <summary>Black with one white pixel in the middle.</summary>
    private static byte[] Point()
    {
        var px = Flat(0);
        int c = (H / 2 * W + W / 2) * 4;
        px[c] = px[c + 1] = px[c + 2] = 255;
        return px;
    }

    private static byte At(byte[] px, int x, int y) => px[(y * W + x) * 4];

    [Theory]
    [InlineData(BlurStyle.Gaussian)]
    [InlineData(BlurStyle.Lens)]
    [InlineData(BlurStyle.Motion)]
    public void EvenColor_StaysTheSame(BlurStyle style)
    {
        var px = Flat(120);
        ImageBlur.Apply(style, px, W, H, 4);
        Assert.All(Enumerable.Range(0, W * H), i => Assert.InRange(px[i * 4], 119, 121));
    }

    [Fact]
    public void Frosted_EvenColor_GetsOnlyAFineGrain()
    {
        var px = Flat(120);
        ImageBlur.Apply(BlurStyle.Frosted, px, W, H, 4);
        Assert.All(Enumerable.Range(0, W * H), i => Assert.InRange(px[i * 4], 114, 126));
    }

    [Fact]
    public void Frosted_Grain_HasNoStreaks()
    {
        // Rows seeded one after another gave correlated grain: whole columns came out lighter or darker.
        const int w = 200, h = 200;
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = 120; px[i + 3] = 255; }
        ImageBlur.Frosted(px, w, h, 3);
        for (int x = 0; x < w; x++)
        {
            double mean = Enumerable.Range(0, h).Average(y => px[(y * w + x) * 4]);
            Assert.InRange(mean, 119, 121);
        }
    }

    [Fact]
    public void Frosted_SameImage_SameResult()
    {
        var a = Point(); var b = Point();
        ImageBlur.Apply(BlurStyle.Frosted, a, W, H, 3);
        ImageBlur.Apply(BlurStyle.Frosted, b, W, H, 3);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Gaussian_SpreadsEvenlyInEveryDirection()
    {
        var px = Point();
        ImageBlur.Gaussian(px, W, H, 2);
        int c = W / 2;
        Assert.True(At(px, c, c) < 255 && At(px, c, c) > 0);
        Assert.Equal(At(px, c - 3, c), At(px, c + 3, c));
        Assert.Equal(At(px, c, c - 3), At(px, c + 3, c));
        Assert.True(At(px, c + 1, c) > At(px, c + 3, c));
        Assert.Equal(0, At(px, 0, 0));
    }

    [Fact]
    public void Lens_SpreadsAPointIntoADisc()
    {
        var px = Point();
        ImageBlur.Lens(px, W, H, 5);
        int c = W / 2;
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                double d = Math.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                if (d <= 4.9) Assert.True(At(px, x, y) > 0, $"({x},{y}) inside the disc is dark");
                if (d >= 6) Assert.Equal(0, At(px, x, y));
            }
        // Even inside: a disc, not a soft Gaussian falloff.
        Assert.Equal(At(px, c, c), At(px, c + 3, c));
    }

    [Fact]
    public void Motion_StreaksSidewaysOnly()
    {
        var px = Point();
        ImageBlur.Motion(px, W, H, 2);
        int c = W / 2;
        Assert.True(At(px, c + 4, c) > 0);
        Assert.Equal(0, At(px, c, c + 1));
        Assert.Equal(0, At(px, c, c - 1));
    }

    [Fact]
    public void Scale_RisesSlowlyThenToTheMaximum()
    {
        Assert.Equal(0, WallpaperBlur.SigmaFor(0));
        Assert.Equal(WallpaperBlur.MaxSigma, WallpaperBlur.SigmaFor(100), 6);
        Assert.True(WallpaperBlur.SigmaFor(50) < WallpaperBlur.MaxSigma / 2);
        Assert.True(WallpaperBlur.SigmaFor(8) >= 0.3, "the first steps of the slider must already blur");
        for (int a = 1; a <= 100; a++) Assert.True(WallpaperBlur.SigmaFor(a) > WallpaperBlur.SigmaFor(a - 1));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(40, 100)]  // the old default: a full wash
    [InlineData(10, 88)]   // sigma 6
    [InlineData(5, 49)]    // sigma 3
    public void OldSliderValues_LookTheSameOnTheNewScale(double old, double expected) =>
        Assert.Equal(expected, WallpaperBlur.AmountFromOldScale(old));

    [Fact]
    public void SettingsFromVersion3_BlurIsMovedToTheNewScale()
    {
        var folder = Path.Combine(Path.GetTempPath(), "WinMuseUnitTests", "blur-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), """{ "SettingsVersion": 3, "WallpaperBlur": 10 }""");
        var settings = new SettingsService(new RecordingNotifier(), folder);
        Assert.Equal(88, settings.Current.WallpaperBlur);
        Assert.Equal(BlurStyle.Gaussian, settings.Current.WallpaperBlurStyle);
        Assert.Equal(4, settings.Current.SettingsVersion);
    }
}

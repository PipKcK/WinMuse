using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinMuse.Models;

namespace WinMuse.Helpers;

/// <summary>
/// Blurred copies of the background image (see <see cref="ImageBlur"/> for the styles), cached as PNG files in the app's
/// data folder, so moving the slider back to a value already seen is instant.
/// </summary>
public static class WallpaperBlur
{
    /// <summary>Width the image is blurred at: plenty for a soft background, and quick to compute.</summary>
    private const int WorkWidth = 1280;

    /// <summary>
    /// Blur at 100, as a Gaussian sigma in pixels at <see cref="WorkWidth"/>. Past about 7 the image is an even wash of
    /// color, so every step of the slider still changes something you can see.
    /// </summary>
    public const double MaxSigma = 7;

    /// <summary>
    /// Strength for a slider <paramref name="amount"/> (0–100). Rises a little slowly at first (amount^1.2), where small changes in
    /// blur are the easiest to see, so the low end of the slider is as useful as the top.
    /// </summary>
    public static double SigmaFor(double amount) => MaxSigma * Math.Pow(Math.Clamp(amount, 0, 100) / 100, Curve);

    /// <summary>The shape of the scale: 1 would be even steps; above 1 gives finer control at the light end.</summary>
    private const double Curve = 1.2;

    /// <summary>The amount on this scale that matches a value of the old Blur slider (settings from before version 4,
    /// where 100 meant a sigma of 60 px and anything above about 12 was already a full wash).</summary>
    public static double AmountFromOldScale(double old)
    {
        double sigma = Math.Round(Math.Clamp(old, 0, 100) / 100 * 60);
        if (sigma < 1) return 0;
        return Math.Round(100 * Math.Pow(Math.Min(sigma, MaxSigma) / MaxSigma, 1 / Curve));
    }

    /// <summary>The blurred image for <paramref name="amount"/> (0–100) in <paramref name="style"/>, made on first use.</summary>
    public static async Task<string> GetAsync(string source, double amount, BlurStyle style)
    {
        amount = Math.Round(Math.Clamp(amount, 0, 100));
        if (SigmaFor(amount) < 0.3) return source;
        var info = new FileInfo(source);
        var dir = Path.Combine(AppPaths.DataDir, "WallpaperCache");
        Directory.CreateDirectory(dir);
        var name = $"{Path.GetFileNameWithoutExtension(source)}-{info.Length}-{info.LastWriteTimeUtc.Ticks}-{style}-{amount}.png";
        var output = Path.Combine(dir, name);
        if (File.Exists(output)) return output;

        var file = await StorageFile.GetFileFromPathAsync(source);
        using var input = await file.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(input);
        uint w = Math.Min((uint)WorkWidth, decoder.PixelWidth);
        uint h = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * (double)w / decoder.PixelWidth));
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform { ScaledWidth = w, ScaledHeight = h, InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        var pixels = data.DetachPixelData();
        // Rotated by EXIF: the decoder reports the oriented size through the transform's result length.
        if (pixels.Length != w * h * 4) (w, h) = (h, w);

        // A smaller image gets a proportionally smaller blur, so it looks the same once stretched over the window.
        double sigma = SigmaFor(amount) * Math.Max(w, h) / WorkWidth;
        await Task.Run(() => ImageBlur.Apply(style, pixels, (int)w, (int)h, sigma));

        // Blurred copies of images no longer in use go (other amounts and styles of this image are kept, for the slider).
        var prefix = Path.GetFileNameWithoutExtension(source) + "-";
        foreach (var old in Directory.EnumerateFiles(dir, "*.png"))
            if (!Path.GetFileName(old).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) try { File.Delete(old); } catch { }
        using (var stream = new InMemoryRandomAccessStream())
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, w, h, 96, 96, pixels);
            await encoder.FlushAsync();
            stream.Seek(0);
            using var outFile = File.Create(output);
            await stream.AsStreamForRead().CopyToAsync(outFile);
        }
        return output;
    }
}

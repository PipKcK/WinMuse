using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WinMuse.Helpers;

/// <summary>
/// Smoothly blurred copies of the background image (a Gaussian approximated by three box-blur passes), cached as PNG
/// files in the app's data folder. Replaces decoding the image tiny and stretching it, which looked pixelated.
/// </summary>
public static class WallpaperBlur
{
    /// <summary>Width the image is blurred at: plenty for a soft background, and quick to compute.</summary>
    private const int WorkWidth = 1280;

    /// <summary>The blurred image for <paramref name="amount"/> (0–100), made on first use.</summary>
    public static async Task<string> GetAsync(string source, double amount)
    {
        int radius = (int)Math.Round(Math.Clamp(amount, 0, 100) / 100 * 60); // up to 60 px at 1280 wide
        if (radius < 1) return source;
        var info = new FileInfo(source);
        var dir = Path.Combine(AppPaths.DataDir, "WallpaperCache");
        Directory.CreateDirectory(dir);
        var name = $"{Path.GetFileNameWithoutExtension(source)}-{info.Length}-{info.LastWriteTimeUtc.Ticks}-r{radius}.png";
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

        await Task.Run(() => Blur(pixels, (int)w, (int)h, radius));

        // Blurred copies of images no longer in use go (other blur amounts of this image are kept, for the slider).
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

    /// <summary>Gaussian blur approximation: three box blurs, horizontal then vertical, on BGRA pixels.</summary>
    private static void Blur(byte[] px, int w, int h, int sigma)
    {
        var tmp = new byte[px.Length];
        foreach (var r in BoxSizes(sigma, 3))
        {
            BoxH(px, tmp, w, h, r);
            BoxV(tmp, px, w, h, r);
        }
    }

    // Box radii whose three passes match a Gaussian of the given sigma (Kovesi's method).
    private static int[] BoxSizes(double sigma, int n)
    {
        double wIdeal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int wl = (int)Math.Floor(wIdeal); if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        double mIdeal = (12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4 * wl - 4);
        int m = (int)Math.Round(mIdeal);
        var sizes = new int[n];
        for (int i = 0; i < n; i++) sizes[i] = ((i < m ? wl : wu) - 1) / 2;
        return sizes;
    }

    private static void BoxH(byte[] src, byte[] dst, int w, int h, int r)
    {
        if (r < 1) { System.Buffer.BlockCopy(src, 0, dst, 0, src.Length); return; }
        Parallel.For(0, h, y =>
        {
            int row = y * w * 4;
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int i = -r; i <= r; i++) sum += src[row + Math.Clamp(i, 0, w - 1) * 4 + c];
                for (int x = 0; x < w; x++)
                {
                    dst[row + x * 4 + c] = (byte)(sum / (2 * r + 1));
                    sum += src[row + Math.Min(x + r + 1, w - 1) * 4 + c] - src[row + Math.Max(x - r, 0) * 4 + c];
                }
            }
            for (int x = 0; x < w; x++) dst[row + x * 4 + 3] = 255;
        });
    }

    private static void BoxV(byte[] src, byte[] dst, int w, int h, int r)
    {
        if (r < 1) { System.Buffer.BlockCopy(src, 0, dst, 0, src.Length); return; }
        Parallel.For(0, w, x =>
        {
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int i = -r; i <= r; i++) sum += src[(Math.Clamp(i, 0, h - 1) * w + x) * 4 + c];
                for (int y = 0; y < h; y++)
                {
                    dst[(y * w + x) * 4 + c] = (byte)(sum / (2 * r + 1));
                    sum += src[(Math.Min(y + r + 1, h - 1) * w + x) * 4 + c] - src[(Math.Max(y - r, 0) * w + x) * 4 + c];
                }
            }
        });
    }
}

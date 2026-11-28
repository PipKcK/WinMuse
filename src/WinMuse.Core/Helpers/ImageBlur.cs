using WinMuse.Models;

namespace WinMuse.Helpers;

/// <summary>
/// The blur styles for the background image, on BGRA pixels (alpha is ignored and left opaque). Each style takes one
/// strength, <c>sigma</c>: the standard deviation in pixels of a Gaussian blur of similar softness, so every style
/// looks about as strong at the same slider position.
/// </summary>
public static class ImageBlur
{
    public static void Apply(BlurStyle style, byte[] px, int w, int h, double sigma)
    {
        if (sigma < 0.3 || w < 2 || h < 2) return;
        switch (style)
        {
            case BlurStyle.Lens: Lens(px, w, h, sigma * 2); break; // a disc of radius r spreads light like a Gaussian of sigma r / 2
            case BlurStyle.Frosted: Frosted(px, w, h, sigma); break;
            case BlurStyle.Motion: Motion(px, w, h, sigma); break;
            default: Gaussian(px, w, h, sigma); break;
        }
    }

    // ---------------- Gaussian ----------------

    /// <summary>A true Gaussian blur: one horizontal and one vertical pass with a sampled Gaussian kernel (radius 3 sigma).</summary>
    public static void Gaussian(byte[] px, int w, int h, double sigma)
    {
        if (sigma < 0.3) return;
        var kernel = GaussianKernel(sigma);
        var tmp = new byte[px.Length];
        Convolve(px, tmp, w, h, kernel, horizontal: true);
        Convolve(tmp, px, w, h, kernel, horizontal: false);
    }

    /// <summary>Normalized weights for offsets -r..r.</summary>
    internal static float[] GaussianKernel(double sigma)
    {
        int r = Math.Max(1, (int)Math.Ceiling(sigma * 3));
        var k = new float[2 * r + 1];
        double sum = 0;
        for (int i = -r; i <= r; i++) sum += k[i + r] = (float)Math.Exp(-(i * i) / (2 * sigma * sigma));
        for (int i = 0; i < k.Length; i++) k[i] = (float)(k[i] / sum);
        return k;
    }

    private static void Convolve(byte[] src, byte[] dst, int w, int h, float[] k, bool horizontal)
    {
        int r = k.Length / 2;
        int lines = horizontal ? h : w, length = horizontal ? w : h;
        Parallel.For(0, lines, line =>
        {
            // Pixel i of this line is at base + i * step.
            int step = horizontal ? 4 : w * 4, start = horizontal ? line * w * 4 : line * 4;
            for (int i = 0; i < length; i++)
            {
                float b = 0, g = 0, rr = 0;
                for (int j = -r; j <= r; j++)
                {
                    int p = start + Math.Clamp(i + j, 0, length - 1) * step;
                    float kw = k[j + r];
                    b += src[p] * kw; g += src[p + 1] * kw; rr += src[p + 2] * kw;
                }
                int o = start + i * step;
                dst[o] = (byte)(b + 0.5f); dst[o + 1] = (byte)(g + 0.5f); dst[o + 2] = (byte)(rr + 0.5f); dst[o + 3] = 255;
            }
        });
    }

    // ---------------- Lens ----------------

    /// <summary>
    /// Out of focus, like a camera lens: every point spreads into an even disc of <paramref name="radius"/> pixels.
    /// Averaged in linear light, so bright spots grow into soft round highlights (bokeh) instead of fading away.
    /// Each output row adds up one span per disc row from running sums, so the cost grows with the radius, not its square.
    /// </summary>
    public static void Lens(byte[] px, int w, int h, double radius)
    {
        int r = (int)Math.Round(radius);
        if (r < 1) return;
        var toLinear = new float[256];
        for (int i = 0; i < 256; i++) toLinear[i] = (float)Math.Pow(i / 255.0, 2.2);

        // Running sums per row and channel: sums[(y * 3 + c) * (w + 1) + x + 1] = total of pixels 0..x.
        var sums = new double[h * 3 * (w + 1)];
        Parallel.For(0, h, y =>
        {
            for (int c = 0; c < 3; c++)
            {
                int o = (y * 3 + c) * (w + 1);
                double s = 0;
                for (int x = 0; x < w; x++) sums[o + x + 1] = s += toLinear[px[(y * w + x) * 4 + c]];
            }
        });
        var half = new int[2 * r + 1]; // the disc's half-width on each of its rows
        for (int dy = -r; dy <= r; dy++) half[dy + r] = (int)Math.Floor(Math.Sqrt(r * (double)r - dy * dy));

        var src = (byte[])px.Clone();
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                double b = 0, g = 0, rr = 0;
                int count = 0;
                for (int dy = -r; dy <= r; dy++)
                {
                    int sy = Math.Clamp(y + dy, 0, h - 1), hw = half[dy + r];
                    int x0 = x - hw, x1 = x + hw;
                    // The part of the span inside the image from the running sums; past an edge, the edge pixel repeats.
                    int a = Math.Max(x0, 0), z = Math.Min(x1, w - 1);
                    int left = Math.Max(0, -x0), right = Math.Max(0, x1 - (w - 1));
                    for (int c = 0; c < 3; c++)
                    {
                        int o = (sy * 3 + c) * (w + 1);
                        double s = sums[o + z + 1] - sums[o + a]
                                   + left * toLinear[src[(sy * w) * 4 + c]] + right * toLinear[src[(sy * w + w - 1) * 4 + c]];
                        if (c == 0) b += s; else if (c == 1) g += s; else rr += s;
                    }
                    count += x1 - x0 + 1;
                }
                int p = (y * w + x) * 4;
                px[p] = ToByte(b / count); px[p + 1] = ToByte(g / count); px[p + 2] = ToByte(rr / count); px[p + 3] = 255;
            }
        });
    }

    private static byte ToByte(double linear) => (byte)Math.Clamp(Math.Pow(Math.Max(0, linear), 1 / 2.2) * 255 + 0.5, 0, 255);

    // ---------------- Frosted glass ----------------

    /// <summary>
    /// Like looking through frosted glass: each pixel is swapped with a random neighbor (the glass's grain), lightly
    /// blurred, then given a fine even texture. The pattern comes from each pixel's position, so the same image always
    /// looks the same.
    /// </summary>
    public static void Frosted(byte[] px, int w, int h, double sigma)
    {
        int spread = Math.Max(1, (int)Math.Round(sigma * 1.5)), span = 2 * spread + 1;
        var src = (byte[])px.Clone();
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Clamp(x + (int)(Noise(x, y, 1) % span) - spread, 0, w - 1);
                int sy = Math.Clamp(y + (int)(Noise(x, y, 2) % span) - spread, 0, h - 1);
                int d = (y * w + x) * 4, s = (sy * w + sx) * 4;
                px[d] = src[s]; px[d + 1] = src[s + 1]; px[d + 2] = src[s + 2]; px[d + 3] = 255;
            }
        });
        Gaussian(px, w, h, sigma * 0.6);
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                int n = (int)(Noise(x, y, 3) % 11) - 5, p = (y * w + x) * 4;
                for (int c = 0; c < 3; c++) px[p + c] = (byte)Math.Clamp(px[p + c] + n, 0, 255);
            }
        });
    }

    /// <summary>
    /// A well-mixed number for a pixel (an integer hash). Unlike a random generator seeded per row, neighboring rows
    /// share no pattern, which showed as faint vertical streaks.
    /// </summary>
    private static uint Noise(int x, int y, uint salt)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u + salt * 2246822519u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }

    // ---------------- Motion ----------------

    /// <summary>Horizontal streaks, as if the camera panned: two box passes along each row (a soft-ended streak).</summary>
    public static void Motion(byte[] px, int w, int h, double sigma)
    {
        int r = Math.Max(1, (int)Math.Round(sigma * 2.5));
        var tmp = new byte[px.Length];
        BoxRows(px, tmp, w, h, r);
        BoxRows(tmp, px, w, h, r);
    }

    private static void BoxRows(byte[] src, byte[] dst, int w, int h, int r)
    {
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
}

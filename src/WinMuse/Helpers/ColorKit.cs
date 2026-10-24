using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.UI;

namespace WinMuse.Helpers;

public static class ColorKit
{
    public static Color Parse(string? hex, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(hex)) return fallback;
        var h = hex.TrimStart('#');
        try
        {
            if (h.Length == 6) h = "FF" + h;
            if (h.Length != 8) return fallback;
            var v = Convert.ToUInt32(h, 16);
            return Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
        catch { return fallback; }
    }

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    public static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    public static (double h, double s, double l) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = 0, s, l = (max + min) / 2;
        if (max == min) { s = 0; }
        else
        {
            var d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
        }
        return (h * 360, s, l);
    }

    public static Color FromHsl(double h, double s, double l, byte a = 255)
    {
        h = ((h % 360) + 360) % 360 / 360.0;
        s = Math.Clamp(s, 0, 1); l = Math.Clamp(l, 0, 1);
        double r, g, b;
        if (s == 0) { r = g = b = l; }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = Hue(p, q, h + 1.0 / 3); g = Hue(p, q, h); b = Hue(p, q, h - 1.0 / 3);
        }
        return Color.FromArgb(a, (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));

        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
    }

    /// <summary>Shift lightness so the accent reads well on the given theme.</summary>
    public static Color AdjustForTheme(Color c, bool dark, double minSat = 0.35)
    {
        var (h, s, l) = ToHsl(c);
        s = Math.Max(s, minSat);
        l = dark ? Math.Clamp(l, 0.55, 0.72) : Math.Clamp(l, 0.36, 0.5);
        return FromHsl(h, s, l);
    }

    public static Color Lighten(Color c, double amount) { var (h, s, l) = ToHsl(c); return FromHsl(h, s, l + amount, c.A); }

    /// <summary>Deterministic pleasant gradient pair for a string (placeholder art).</summary>
    public static (Color a, Color b) GradientFor(string? key)
    {
        var hash = (uint)(key ?? "").GetHashCode(StringComparison.OrdinalIgnoreCase);
        var hue = hash % 360;
        return (FromHsl(hue, 0.62, 0.55), FromHsl(hue + 40, 0.7, 0.42));
    }

    private static readonly Dictionary<string, Color[]> PaletteCache = new();

    /// <summary>Extracts up to 4 dominant, vibrant colors from an image file.</summary>
    public static async Task<Color[]> ExtractPaletteAsync(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Array.Empty<Color>();
        lock (PaletteCache) if (PaletteCache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform { ScaledWidth = 48, ScaledHeight = 48, InterpolationMode = BitmapInterpolationMode.Fant };
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var px = data.DetachPixelData();
            var result = await Task.Run(() => Quantize(px));
            lock (PaletteCache) PaletteCache[path] = result;
            return result;
        }
        catch { return Array.Empty<Color>(); }
    }

    private static Color[] Quantize(byte[] bgra)
    {
        // 4 bits per channel histogram, weighted by saturation so vivid colors win over greys.
        var buckets = new Dictionary<int, (double w, double r, double g, double b)>();
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            byte b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            var (_, s, l) = ToHsl(Color.FromArgb(255, r, g, b));
            double w = 0.15 + s * (1 - Math.Abs(l - 0.5) * 1.6);
            if (w <= 0) w = 0.02;
            int key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
            buckets.TryGetValue(key, out var acc);
            buckets[key] = (acc.w + w, acc.r + r * w, acc.g + g * w, acc.b + b * w);
        }

        var ranked = buckets.Values.OrderByDescending(v => v.w)
            .Select(v => Color.FromArgb(255, (byte)(v.r / v.w), (byte)(v.g / v.w), (byte)(v.b / v.w)))
            .ToList();

        var chosen = new List<Color>();
        foreach (var c in ranked)
        {
            if (chosen.All(x => Distance(x, c) > 70)) chosen.Add(c);
            if (chosen.Count == 4) break;
        }
        while (chosen.Count < 4 && chosen.Count > 0) chosen.Add(Lighten(chosen[^1], 0.12));
        return chosen.ToArray();
    }

    private static double Distance(Color a, Color b)
    {
        int dr = a.R - b.R, dg = a.G - b.G, db = a.B - b.B;
        return Math.Sqrt(dr * dr * 0.3 + dg * dg * 0.59 + db * db * 0.11) * 1.6;
    }

    /// <summary>Picks the most vibrant color of a palette for use as an accent.</summary>
    public static Color? PickAccent(Color[] palette)
    {
        if (palette.Length == 0) return null;
        return palette.OrderByDescending(c => { var (_, s, l) = ToHsl(c); return s * (1 - Math.Abs(l - 0.5)); }).First();
    }
}

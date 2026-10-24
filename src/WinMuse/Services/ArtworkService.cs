using System.Collections.Concurrent;
using System.Security.Cryptography;
using WinMuse.Helpers;

namespace WinMuse.Services;

/// <summary>Content-addressed cache of cover images under %LocalAppData%\WinMuse\Artwork.</summary>
public sealed class ArtworkService
{
    private static readonly string[] FolderArtNames = { "cover", "folder", "front", "album", "albumart", "albumartsmall", "artwork" };
    private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".png", ".webp", ".bmp" };
    private readonly ConcurrentDictionary<string, string?> _folderArt = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stores image bytes and returns the cached file path (deduplicated by hash).</summary>
    public string? Save(byte[]? data)
    {
        if (data is not { Length: > 64 }) return null;
        try
        {
            var hash = Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
            var path = Path.Combine(AppPaths.ArtDir, hash + SniffExtension(data));
            if (File.Exists(path)) return path;
            // Parallel scans can produce the same image at once: write to a unique temp file, then move into place.
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(tmp, data);
            try { File.Move(tmp, path); }
            catch (IOException) { File.Delete(tmp); }
            return path;
        }
        catch (Exception ex)
        {
            AppPaths.Log("Art save failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Looks for cover.jpg / folder.jpg style images next to the audio file.</summary>
    public string? FindFolderArt(string audioPath)
    {
        var dir = Path.GetDirectoryName(audioPath);
        if (dir == null) return null;
        return _folderArt.GetOrAdd(dir, d =>
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(d))
                {
                    var ext = Path.GetExtension(f).ToLowerInvariant();
                    if (!ImageExts.Contains(ext)) continue;
                    var name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    if (FolderArtNames.Contains(name)) return f;
                }
            }
            catch { }
            return null;
        });
    }

    /// <summary>Centre-crops an image to a square (YouTube thumbnails are 16:9, sometimes letterboxed in 4:3).</summary>
    public static async Task<byte[]> CropToSquareAsync(byte[] data)
    {
        try
        {
            using var input = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await input.WriteAsync(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(data));
            input.Seek(0);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(input);
            uint w = decoder.PixelWidth, h = decoder.PixelHeight;
            uint contentH = Math.Min(h, w * 9 / 16);       // ignore letterbox bars
            uint side = Math.Min(w, contentH);
            using var output = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateForTranscodingAsync(output, decoder);
            encoder.BitmapTransform.Bounds = new Windows.Graphics.Imaging.BitmapBounds { X = (w - side) / 2, Y = (h - side) / 2, Width = side, Height = side };
            await encoder.FlushAsync();
            var bytes = new byte[output.Size];
            output.Seek(0);
            using var reader = new Windows.Storage.Streams.DataReader(output);
            await reader.LoadAsync((uint)output.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            AppPaths.Log("Square crop failed: " + ex.Message);
            return data;
        }
    }

    public void ForgetFolder(string dir) => _folderArt.TryRemove(dir, out _);

    public static string SniffExtension(byte[] d)
    {
        if (d.Length > 3 && d[0] == 0x89 && d[1] == 0x50) return ".png";
        if (d.Length > 12 && d[0] == 'R' && d[1] == 'I' && d[8] == 'W' && d[9] == 'E') return ".webp";
        if (d.Length > 2 && d[0] == 'B' && d[1] == 'M') return ".bmp";
        if (d.Length > 3 && d[0] == 'G' && d[1] == 'I') return ".gif";
        return ".jpg";
    }

    public static string MimeFor(byte[] d) => SniffExtension(d) switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".gif" => "image/gif",
        _ => "image/jpeg",
    };
}

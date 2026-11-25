using System.Text;

namespace WinMuse.Helpers;

/// <summary>
/// The app's log (log.txt in the data folder), kept the way most desktop apps keep theirs. Lines are written one at a
/// time under a lock, so lines from parallel work never collide or get lost, and each line is on disk before Write
/// returns, so the lines before a crash are there. The file isn't held open between lines: it can be opened, copied or
/// attached to a bug report while the app runs. At 1 MB it rolls over: log.txt becomes log.1.txt, log.1.txt becomes
/// log.2.txt, and so on, and the oldest goes. At most <see cref="Files"/> files, about 5 MB, are ever kept.
/// </summary>
public static class AppLog
{
    public const long MaxBytes = 1024 * 1024;
    /// <summary>log.txt plus log.1.txt to log.4.txt.</summary>
    public const int Files = 5;

    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8 = new(false);
    private static string? _path;

    /// <summary>The log file (tests point it elsewhere).</summary>
    public static string Path
    {
        get => _path ?? AppPaths.LogPath;
        set { lock (Gate) _path = value; }
    }

    public static void Write(string message)
    {
        var bytes = Utf8.GetBytes($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        lock (Gate)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    long length;
                    using (var fs = new FileStream(Path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                        fs.Write(bytes);
                        length = fs.Length;
                    }
                    if (length >= MaxBytes) Roll();
                    return;
                }
                catch (IOException) { Thread.Sleep(5); } // another program has the file open for a moment
                catch { return; } // the log must never take the app down
            }
        }
    }

    private static void Roll()
    {
        try
        {
            string Numbered(int n) => System.IO.Path.ChangeExtension(Path, null) + $".{n}" + System.IO.Path.GetExtension(Path);
            File.Delete(Numbered(Files - 1));
            for (int n = Files - 2; n >= 1; n--)
                if (File.Exists(Numbered(n))) File.Move(Numbered(n), Numbered(n + 1));
            File.Move(Path, Numbered(1));
        }
        catch { /* another program holds an old file: keep writing to the current one */ }
    }
}

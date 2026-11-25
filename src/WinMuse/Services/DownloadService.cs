using System.Diagnostics;
using System.Text;
using WinMuse.Helpers;
using WinMuse.Models;

namespace WinMuse.Services;

/// <summary>Outcome of one download. <see cref="Code"/> is mdown's exit code ("Exit code 3") or an app error code.</summary>
public sealed record DownloadResult(bool Success, string Code, string Message, List<string> Files, string Output)
{
    /// <summary>Everything a user needs to report the problem, as copied from the error box.</summary>
    public string Report => $"Error: {Code}\r\n{Message}" + (string.IsNullOrWhiteSpace(Output) ? "" : $"\r\n\r\nOutput:\r\n{Output.TrimEnd()}");
}

/// <summary>
/// Downloads songs with mdown.exe: <c>mdown.exe "&lt;url&gt;" --secret &lt;secret file&gt;</c>. mdown saves into its current
/// folder, so it runs inside the destination folder; audio files that appear there are added to the library and
/// checked right away. Downloading needs two files from the user, both kept in the app's data folder (so they survive
/// app updates): the downloader (mdown.exe) and the secret file, used for every download.
/// </summary>
public sealed class DownloadService
{
    public const string ToolName = "mdown.exe";
    private readonly SettingsService _settings;
    private readonly LibraryService _library;
    private readonly AutoFixService _autoFix;

    public DownloadService(SettingsService settings, LibraryService library, AutoFixService autoFix)
    {
        _settings = settings; _library = library; _autoFix = autoFix;
    }

    /// <summary>Where the downloader the user adds is kept: the Tools folder in the app's data folder.</summary>
    public static string ToolFolder => Path.Combine(AppPaths.DataDir, "Tools");
    private static string AddedToolPath => Path.Combine(ToolFolder, ToolName);

    /// <summary>
    /// The downloader: the one the user added, or one placed next to the app (a Tools folder beside WinMuse.exe, or the
    /// app folder itself). Null when there is none.
    /// </summary>
    public static string? ToolPath => new[] { AddedToolPath, Path.Combine(AppContext.BaseDirectory, "Tools", ToolName), Path.Combine(AppContext.BaseDirectory, ToolName) }
        .FirstOrDefault(File.Exists);
    public static bool HasTool => ToolPath != null;
    /// <summary>True when the downloader in use is the one added on the Download page (it can be removed there).</summary>
    public static bool ToolIsAdded => File.Exists(AddedToolPath);
    public static DateTime? ToolUpdated => ToolPath is { } p ? File.GetLastWriteTime(p) : null;

    /// <summary>Copies the chosen mdown.exe into the app's data folder; it replaces the previous one.</summary>
    public void SetTool(string sourceFile)
    {
        if (!string.Equals(Path.GetExtension(sourceFile), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The downloader must be a program (.exe).");
        Directory.CreateDirectory(ToolFolder);
        File.Copy(sourceFile, AddedToolPath, overwrite: true);
        File.SetLastWriteTime(AddedToolPath, DateTime.Now);
    }

    public void RemoveTool()
    {
        if (File.Exists(AddedToolPath)) File.Delete(AddedToolPath);
    }

    /// <summary>Both files are in place, so downloads can run.</summary>
    public bool IsReady => HasTool && HasSecret;

    /// <summary>The link as it can be passed to the downloader (http or https only), or null.</summary>
    public static string? CleanUrl(string text) => WebLink.Clean(text);

    public static string SecretPath => Path.Combine(AppPaths.DataDir, "download-secret.txt");
    public bool HasSecret => File.Exists(SecretPath);
    public DateTime? SecretUpdated => HasSecret ? File.GetLastWriteTime(SecretPath) : null;

    /// <summary>Copies the user's secret file into the app's data folder; it replaces the previous one.</summary>
    public void SetSecret(string sourceFile)
    {
        File.Copy(sourceFile, SecretPath, overwrite: true);
        File.SetLastWriteTime(SecretPath, DateTime.Now);
    }

    public void RemoveSecret()
    {
        if (File.Exists(SecretPath)) File.Delete(SecretPath);
    }

    public string DefaultFolder =>
        _settings.Current.DownloadFolder ?? _settings.Current.MusicFolders.FirstOrDefault() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);

    /// <summary>Runs mdown.exe for one URL. Lines it prints are reported through <paramref name="progress"/>.</summary>
    public async Task<DownloadResult> RunAsync(string url, string folder, IProgress<string>? progress, CancellationToken ct)
    {
        if (url.Trim().Length == 0) return Fail("NO_URL", "Paste the link of the song to download.");
        if (CleanUrl(url) is not { } link) return Fail("INVALID_URL", "The link isn't a web address. Paste a link that starts with https://.");
        url = link;
        if (ToolPath is not { } tool) return Fail("MDOWN_NOT_FOUND", $"The downloader isn't added. Add {ToolName} on the Download page.");
        if (!HasSecret) return Fail("NO_SECRET_FILE", "The secret file isn't added. Add it on the Download page.");
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) { return Fail("FOLDER_UNAVAILABLE", $"The folder {folder} can't be used. {ErrorText.Logged(ex)}"); }

        var before = Snapshot(folder);
        var psi = new ProcessStartInfo(tool)
        {
            WorkingDirectory = folder,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(url);
        psi.ArgumentList.Add("--secret");
        psi.ArgumentList.Add(SecretPath);

        var output = new StringBuilder();
        var errors = new StringBuilder();
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data == null) return; lock (output) output.AppendLine(e.Data); if (e.Data.Length > 0) progress?.Report(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data == null) return; lock (output) { output.AppendLine(e.Data); errors.AppendLine(e.Data); } };
        try
        {
            p.Start();
        }
        catch (Exception ex)
        {
            return Fail("MDOWN_START_FAILED", $"The downloader couldn't be started. {ErrorText.Logged(ex)}");
        }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        string log;
        lock (output) log = output.ToString();
        var files = NewFiles(before, Snapshot(folder));
        AppPaths.Log($"mdown exited with {p.ExitCode} for {url}; {files.Count} new file(s)");

        if (p.ExitCode != 0)
        {
            var reason = errors.ToString().Trim() is { Length: > 0 } err ? LastLines(err, 3) : LastLines(log, 3);
            return new DownloadResult(false, $"Exit code {p.ExitCode}", string.IsNullOrEmpty(reason) ? "The downloader reported an error." : reason, files, log);
        }
        if (files.Count == 0)
            return new DownloadResult(false, "NO_NEW_FILES", $"The downloader finished, but no new audio file appeared in {folder}.", files, log);
        return new DownloadResult(true, "OK", files.Count == 1 ? Path.GetFileName(files[0]) : $"{files.Count} songs", files, log);

        static DownloadResult Fail(string code, string message) => new(false, code, message, new List<string>(), "");
    }

    /// <summary>Adds downloaded files to the library (adding the folder if it isn't part of it) and checks them right away.</summary>
    public async Task<List<Track>> ImportAsync(string folder, IReadOnlyCollection<string> files)
    {
        while (_library.IsScanning) await Task.Delay(200);
        if (!_settings.Current.MusicFolders.Any(f => IsInside(folder, f))) await _library.AddFolderAsync(folder);
        else await _library.ScanAsync();

        var tracks = files.Select(_library.GetByPath).OfType<Track>().ToList();
        if (tracks.Count > 0) _ = _autoFix.SyncTracksAsync(tracks, replaceArt: true);
        return tracks;
    }

    private static bool IsInside(string path, string folder)
    {
        var p = Path.GetFullPath(path).TrimEnd('\\') + "\\";
        var f = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        return p.StartsWith(f, StringComparison.OrdinalIgnoreCase);
    }

    private static Dictionary<string, DateTime> Snapshot(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                .Where(AppPaths.IsAudio)
                .ToDictionary(f => f, File.GetLastWriteTimeUtc, StringComparer.OrdinalIgnoreCase);
        }
        catch { return new(StringComparer.OrdinalIgnoreCase); }
    }

    private static List<string> NewFiles(Dictionary<string, DateTime> before, Dictionary<string, DateTime> after) =>
        after.Where(kv => !before.TryGetValue(kv.Key, out var old) || old != kv.Value).Select(kv => kv.Key).ToList();

    private static string LastLines(string text, int count) =>
        string.Join("\r\n", text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).TakeLast(count));
}

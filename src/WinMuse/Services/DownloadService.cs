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
/// checked right away. The secret file the user uploads is kept in the app's data folder and used for every download.
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

    /// <summary>Where mdown.exe goes: the Tools folder next to WinMuse.exe (the app folder itself also works).</summary>
    public static string ToolFolder => Path.Combine(AppContext.BaseDirectory, "Tools");
    public static string? ToolPath => new[] { Path.Combine(ToolFolder, ToolName), Path.Combine(AppContext.BaseDirectory, ToolName) }.FirstOrDefault(File.Exists);

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
        url = url.Trim();
        if (url.Length == 0) return Fail("NO_URL", "Enter the URL of the song to download.");
        if (ToolPath is not { } tool) return Fail("MDOWN_NOT_FOUND", $"{ToolName} was not found. Place it in {ToolFolder}.");
        if (!HasSecret) return Fail("NO_SECRET_FILE", "Upload your secret file before downloading.");
        try { Directory.CreateDirectory(folder); }
        catch (Exception ex) { return Fail("FOLDER_UNAVAILABLE", $"The folder {folder} can't be used: {ex.Message}"); }

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
            return Fail("MDOWN_START_FAILED", $"{ToolName} could not be started: {ex.Message}");
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
            return new DownloadResult(false, $"Exit code {p.ExitCode}", string.IsNullOrEmpty(reason) ? $"{ToolName} reported an error." : reason, files, log);
        }
        if (files.Count == 0)
            return new DownloadResult(false, "NO_NEW_FILES", $"{ToolName} finished, but no new audio file appeared in {folder}.", files, log);
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

using System.Text.Json;
using System.Text.Json.Serialization;
using WinMuse.Helpers;
using WinMuse.Models;

namespace WinMuse.Services;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Debouncer _saveDebounce = new(TimeSpan.FromMilliseconds(600));

    public AppSettings Current { get; }
    private const int CurrentVersion = 3;

    /// <summary>Raised on the calling thread with the name of the setting that changed.</summary>
    public event Action<string>? Changed;

    public SettingsService()
    {
        Current = Load();
        if (Current.EqBands is not { Length: 10 }) Current.EqBands = new float[10];

        // First run: start with the user's Music folder.
        if (!File.Exists(AppPaths.SettingsPath) && Current.MusicFolders.Count == 0)
        {
            var music = AppPaths.MusicFolder;
            if (!string.IsNullOrEmpty(music) && Directory.Exists(music)) Current.MusicFolders.Add(music);
            Current.SettingsVersion = CurrentVersion;
            Save();
        }

        // Version 2: edits are kept in the library only, and the equalizer follows each song's genre.
        if (Current.SettingsVersion < 2)
        {
            Current.WriteMode = MetadataWriteMode.LibraryOnly;
            Current.EqMatchGenre = true;
        }
        // Version 3: changing music files is a setting of its own (off by default). Someone who had chosen to write
        // edits into file tags keeps doing so; everyone else keeps their files untouched.
        if (Current.SettingsVersion < 3)
            Current.AllowFileChanges = Current.WriteMode == MetadataWriteMode.WriteToFiles;
        if (Current.SettingsVersion < CurrentVersion)
        {
            Current.SettingsVersion = CurrentVersion;
            Save();
        }
    }

    /// <summary>
    /// Reads settings.json. A file that reads correctly is copied to settings.backup.json, the last working copy. A
    /// damaged file is kept as settings.damaged.json (for a bug report) and the backup is used instead; with no usable
    /// backup the defaults are. Each of the three files has one fixed name and is replaced each time, so they never pile up.
    /// </summary>
    private static AppSettings Load()
    {
        var path = AppPaths.SettingsPath;
        if (!File.Exists(path)) return new AppSettings();
        if (TryRead(path, out var current, out var error))
        {
            try { File.Copy(path, AppPaths.SettingsBackupPath, true); } catch (Exception ex) { AppPaths.Log("Settings backup failed: " + ex.Message); }
            return current!;
        }

        AppPaths.Log("Settings file damaged: " + error);
        try { File.Move(path, AppPaths.SettingsDamagedPath, true); } catch (Exception ex) { AppPaths.Log("Settings: " + ex.Message); }
        if (File.Exists(AppPaths.SettingsBackupPath) && TryRead(AppPaths.SettingsBackupPath, out var backup, out _))
        {
            try { File.Copy(AppPaths.SettingsBackupPath, path, true); } catch (Exception ex) { AppPaths.Log("Settings restore failed: " + ex.Message); }
            AppPaths.Log("Settings restored from the backup");
            Notifier.Show("Settings restored", "Your settings file was damaged, so the last working copy was used. Recent changes to settings may be missing.",
                Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning);
            return backup!;
        }
        Notifier.Show("Settings reset", "Your settings file was damaged and no working copy was found, so settings are back to their defaults. Add your music folders and API keys again in Settings: your songs come back with their playlists and plays.",
            Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning, Notifier.OpenLog);
        // The first-run API key dialog would cover this message: the keys are added again in Settings.
        return new AppSettings { LibraryFoldersLost = true, ApiKeysPrompted = true };
    }

    private static bool TryRead(string path, out AppSettings? settings, out string? error)
    {
        settings = null; error = null;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json);
            if (settings == null) error = "empty";
        }
        catch (Exception ex) { error = ex.Message; }
        return settings != null;
    }

    public void Set(string name, Action<AppSettings> mutate)
    {
        mutate(Current);
        _saveDebounce.Run(Save);
        Changed?.Invoke(name);
    }

    public void Save()
    {
        try
        {
            var tmp = AppPaths.SettingsPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
            File.Move(tmp, AppPaths.SettingsPath, true);
        }
        catch (Exception ex) { AppPaths.Log("Settings save failed: " + ex.Message); }
    }
}

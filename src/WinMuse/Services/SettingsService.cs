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
    private const int CurrentVersion = 2;

    /// <summary>Raised on the calling thread with the name of the setting that changed.</summary>
    public event Action<string>? Changed;

    public SettingsService()
    {
        try
        {
            Current = File.Exists(AppPaths.SettingsPath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsPath), Json) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception ex)
        {
            AppPaths.Log("Settings load failed: " + ex.Message);
            Current = new AppSettings();
        }
        if (Current.EqBands is not { Length: 10 }) Current.EqBands = new float[10];

        // First run: start with the user's Music folder.
        if (!File.Exists(AppPaths.SettingsPath) && Current.MusicFolders.Count == 0)
        {
            var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (!string.IsNullOrEmpty(music) && Directory.Exists(music)) Current.MusicFolders.Add(music);
            Current.SettingsVersion = CurrentVersion;
            Save();
        }

        // Version 2: edits are kept in the library only, and the equalizer follows each song's genre.
        if (Current.SettingsVersion < CurrentVersion)
        {
            Current.WriteMode = MetadataWriteMode.LibraryOnly;
            Current.EqMatchGenre = true;
            Current.SettingsVersion = CurrentVersion;
            Save();
        }
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

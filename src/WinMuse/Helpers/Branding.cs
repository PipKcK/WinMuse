using System.Text.Json;
using WinMuse.Models;

namespace WinMuse.Helpers;

/// <summary>
/// The app's name and default logo, from Assets\Branding.json (written by tools\Build-App.ps1). Without the file the
/// app is "Music" with the Rings logo.
/// </summary>
public static class Branding
{
    private sealed record Data(string? Name, string? Logo);

    private static readonly Data Loaded = Load();

    /// <summary>The name shown in the title bar, window titles and the mini player.</summary>
    public static string Name => string.IsNullOrWhiteSpace(Loaded.Name) ? "Music" : Loaded.Name!.Trim();

    /// <summary>The logo used until one is picked in Settings.</summary>
    public static AppLogoStyle DefaultLogo => Enum.TryParse<AppLogoStyle>(Loaded.Logo, true, out var s) ? s : AppLogoStyle.Rings;

    private static Data Load()
    {
        try
        {
            var path = Path.Combine(AppPaths.AssetsDir, "Branding.json");
            if (File.Exists(path)) return JsonSerializer.Deserialize<Data>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(null, null);
        }
        catch { /* defaults */ }
        return new(null, null);
    }
}

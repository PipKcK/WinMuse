namespace WinMuse.Helpers;

/// <summary>One entry in Settings &gt; About &gt; Credits: what it is, what the app uses it for, its license, and its page.</summary>
public sealed record Credit(string Name, string Use, string? License, string Url)
{
    public Uri Link => new(Url);
}

/// <summary>
/// Everything the app is built on or gets data from, shown in Settings &gt; Credits. Licenses are the ones the packages
/// declare (their NuGet metadata or license file). Keep this list in step with WinMuse.csproj, the online services and
/// THIRD-PARTY-NOTICES.txt in the repository root.
/// </summary>
public static class Credits
{
    public const string SourceUrl = "https://github.com/PipKcK/WinMuse";
    public const string IssuesUrl = "https://github.com/PipKcK/WinMuse/issues";
    public const string LicenseUrl = "https://github.com/PipKcK/WinMuse/blob/main/LICENSE";

    /// <summary>The license texts of every component below, installed next to the app.</summary>
    public static string NoticesPath => Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");

    public static readonly IReadOnlyList<Credit> Components = new Credit[]
    {
        new("FFmpeg", "Plays almost every audio format", "LGPL 3.0", "https://ffmpeg.org"),
        new("FFmpeg.AutoGen", "Connects the app to FFmpeg", "MIT", "https://github.com/Ruslan-B/FFmpeg.AutoGen"),
        new("NAudio", "Sends audio to your speakers", "MIT", "https://github.com/naudio/NAudio"),
        new("TagLib#", "Reads and writes song details in files", "LGPL 2.1", "https://github.com/mono/taglib-sharp"),
        new("AcoustID.NET", "Audio fingerprints (a port of Chromaprint)", "LGPL 2.1", "https://github.com/wo80/AcoustID.NET"),
        new("SQLite", "Stores your library", "Public domain", "https://sqlite.org"),
        new("Microsoft.Data.Sqlite", "Opens the library database", "MIT", "https://github.com/dotnet/efcore"),
        new("Dapper", "Reads and writes the library database", "Apache 2.0", "https://github.com/DapperLib/Dapper"),
        new("Windows App SDK and WinUI 3", "The app's windows and controls", "Microsoft Software License", "https://github.com/microsoft/WindowsAppSDK"),
        new("Windows Community Toolkit", "Settings cards", "MIT", "https://github.com/CommunityToolkit/Windows"),
        new(".NET Community Toolkit", "App structure (MVVM)", "MIT", "https://github.com/CommunityToolkit/dotnet"),
        new(".NET", "Runs the app (included in the setup files)", "MIT", "https://github.com/dotnet/runtime"),
        new("WiX Toolset", "Builds the Windows Installer setup (.msi)", "MS-RL", "https://wixtoolset.org"),
    };

    /// <summary>Online services the app asks for song details, covers, photos and lyrics.</summary>
    public static readonly IReadOnlyList<Credit> Services = new Credit[]
    {
        new("MusicBrainz", "Song and album details", null, "https://musicbrainz.org"),
        new("Cover Art Archive", "Album covers", null, "https://coverartarchive.org"),
        new("AcoustID", "Identifies songs by their sound", null, "https://acoustid.org"),
        new("LRCLIB", "Synchronized lyrics", null, "https://lrclib.net"),
        new("iTunes Search", "Song details and covers", null, "https://performance-partners.apple.com/search-api"),
        new("Deezer", "Song details, covers and artist photos", null, "https://www.deezer.com"),
        new("Wikipedia, Wikidata and Wikimedia Commons", "Artist photos", null, "https://www.wikidata.org"),
        new("TheAudioDB", "Artist photos", null, "https://www.theaudiodb.com"),
        new("Fanart.tv", "Artist photos (with your API key)", null, "https://fanart.tv"),
        new("Last.fm", "Genres (with your API key)", null, "https://www.last.fm"),
        new("Spotify", "Artist photos (with your API key)", null, "https://developer.spotify.com"),
        new("YouTube", "Song details and covers when nothing else finds them (with your API key)", null, "https://www.youtube.com"),
    };
}

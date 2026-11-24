using System.Reflection;

namespace WinMuse.Helpers;

/// <summary>
/// The app's version, read from the build. &lt;Version&gt; in WinMuse.csproj is the single source: it stamps the
/// exe, the About page shows it, and tools\Build-App.ps1 and tools\Make-Installer.ps1 read it for Settings &gt; Apps.
/// </summary>
public static class AppInfo
{
    private static readonly string Informational =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>The release version, e.g. "1.0.0".</summary>
    public static string Version { get; } = Informational.Split('+')[0];

    /// <summary>
    /// The commit the build came from (first 7 characters), when the SDK recorded it ("1.0.0+&lt;commit&gt;"); null otherwise.
    /// </summary>
    public static string? Commit { get; } =
        Informational.Split('+') is [_, var rev, ..] && rev.Length >= 7 ? rev[..7] : null;

    /// <summary>"Version 1.0.0", plus the commit when known: what About shows and bug reports should quote.</summary>
    public static string DisplayVersion => Commit is null ? $"Version {Version}" : $"Version {Version} ({Commit})";
}

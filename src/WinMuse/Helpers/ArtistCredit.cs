using System.Text.RegularExpressions;

namespace WinMuse.Helpers;

/// <summary>
/// An artist credit as written in a song's tags ("A & B", "A, B", "A x B", "A feat. B", "A; B") and the separate
/// artists in it. Every credited artist gets the song on their own page.
/// </summary>
public static partial class ArtistCredit
{
    // Captured, so a split keeps the separators and a renamed credit keeps its original punctuation.
    [GeneratedRegex(@"(\s*;\s*|\s*,\s*|\s*&\s*|\s+[x×]\s+|\s*[(\[]?\s*\b(?:feat|ft|featuring)\b\.?\s*)", RegexOptions.IgnoreCase)]
    private static partial Regex Separators();

    private static string Core(string part) => part.Trim().Trim('(', ')', '[', ']').Trim();

    /// <summary>The individual artists, in order, without duplicates.</summary>
    public static List<string> Split(string? credit)
    {
        var names = new List<string>();
        if (string.IsNullOrWhiteSpace(credit)) return names;
        var parts = Separators().Split(credit);
        for (int i = 0; i < parts.Length; i += 2) // odd indexes are the separators
        {
            var name = Core(parts[i]);
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }
        return names;
    }

    public static bool Contains(string? credit, string name) => Split(credit).Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The credit with one artist renamed; the other artists and the separators stay as written.</summary>
    public static string? Rename(string? credit, string oldName, string newName)
    {
        if (string.IsNullOrWhiteSpace(credit) || !Contains(credit, oldName)) return credit;
        var parts = Separators().Split(credit);
        for (int i = 0; i < parts.Length; i += 2)
        {
            var core = Core(parts[i]);
            if (core.Equals(oldName, StringComparison.OrdinalIgnoreCase)) parts[i] = parts[i].Replace(core, newName);
        }
        var renamed = string.Concat(parts);
        // Renamed to an artist already in the credit: list each artist once.
        var names = Split(renamed);
        int written = parts.Where((p, i) => i % 2 == 0 && Core(p).Length > 0).Count();
        return names.Count < written ? string.Join("; ", names) : renamed;
    }

    /// <summary>The credit with one more artist (stored as a separate artist value: "A & B; C").</summary>
    public static string Add(string? credit, string name) =>
        string.IsNullOrWhiteSpace(credit) ? name : Contains(credit, name) ? credit : credit.TrimEnd() + "; " + name;
}

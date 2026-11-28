using System.Globalization;
using System.Text.RegularExpressions;
using WinMuse.Models;

namespace WinMuse.Helpers;

public static partial class LrcParser
{
    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    public static bool LooksSynced(string? text) => !string.IsNullOrEmpty(text) && TimeTag().IsMatch(text);

    /// <summary>
    /// Every kind of line break as <paramref name="newline"/>. Text typed or pasted in a WinUI TextBox separates lines
    /// with a lone CR, files use LF or CRLF; treating only LF as a break ran edited lyrics together into one line.
    /// </summary>
    public static string NormalizeLineBreaks(string text, string newline = "\n")
    {
        var lf = text.Replace("\r\n", "\n").Replace('\r', '\n');
        return newline == "\n" ? lf : lf.Replace("\n", newline);
    }

    public static LyricsResult Parse(string text, string? source = null)
    {
        var lines = new List<LyricLine>();
        long offsetMs = 0;
        text = NormalizeLineBreaks(text);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var om = OffsetTag().Match(line);
            if (om.Success) { offsetMs = long.TryParse(om.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var o) ? o : 0; continue; }

            var matches = TimeTag().Matches(line);
            if (matches.Count == 0) continue;
            var content = TimeTag().Replace(line, "").Trim();
            // strip word-level <mm:ss.xx> tags from enhanced LRC
            content = Regex.Replace(content, @"<\d+:\d+(?:\.\d+)?>", "").Trim();
            foreach (Match m in matches)
            {
                var min = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var sec = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                var frac = m.Groups[3].Success ? m.Groups[3].Value : "0";
                var ms = frac.Length switch { 1 => int.Parse(frac, CultureInfo.InvariantCulture) * 100, 2 => int.Parse(frac, CultureInfo.InvariantCulture) * 10, _ => int.Parse(frac[..3], CultureInfo.InvariantCulture) };
                var t = TimeSpan.FromMilliseconds(Math.Max(0, (min * 60 + sec) * 1000L + ms - offsetMs));
                lines.Add(new LyricLine(t, content));
            }
        }

        if (lines.Count == 0)
        {
            var plain = text.Trim();
            return new LyricsResult
            {
                IsSynced = false,
                PlainText = plain,
                Lines = plain.Split('\n').Select(l => new LyricLine(TimeSpan.Zero, l.Trim())).ToList(),
                Source = source,
            };
        }

        lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return new LyricsResult
        {
            IsSynced = true,
            Lines = lines,
            PlainText = string.Join("\n", lines.Select(l => l.Text)),
            Source = source,
        };
    }
}

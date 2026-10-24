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

    public static LyricsResult Parse(string text, string? source = null)
    {
        var lines = new List<LyricLine>();
        long offsetMs = 0;
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            var om = OffsetTag().Match(line);
            if (om.Success) { long.TryParse(om.Groups[1].Value, out offsetMs); continue; }

            var matches = TimeTag().Matches(line);
            if (matches.Count == 0) continue;
            var content = TimeTag().Replace(line, "").Trim();
            // strip word-level <mm:ss.xx> tags from enhanced LRC
            content = Regex.Replace(content, @"<\d+:\d+(?:\.\d+)?>", "").Trim();
            foreach (Match m in matches)
            {
                var min = int.Parse(m.Groups[1].Value);
                var sec = int.Parse(m.Groups[2].Value);
                var frac = m.Groups[3].Success ? m.Groups[3].Value : "0";
                var ms = frac.Length switch { 1 => int.Parse(frac) * 100, 2 => int.Parse(frac) * 10, _ => int.Parse(frac[..3]) };
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
                Lines = plain.Replace("\r", "").Split('\n').Select(l => new LyricLine(TimeSpan.Zero, l.Trim())).ToList(),
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

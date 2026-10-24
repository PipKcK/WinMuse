using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace WinMuse.Services;

/// <summary>Cleans up file names / video titles and scores how well an online result matches a song.</summary>
public static partial class Matching
{
    [GeneratedRegex(@"\s*\[([A-Za-z0-9_-]{11})\]\s*$")]
    private static partial Regex YouTubeIdSuffix();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*\b(official|video|audio|lyrics?|visuali[sz]er|hd|hq|4k|m/?v|explicit|remaster(ed)?|copyright|ncs|free download|full song|out now|premiere|stem)\b[^\)\]]*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex NoiseBrackets();

    [GeneratedRegex(@"\s*\[[^\]]*\]")]
    private static partial Regex SquareBrackets();

    [GeneratedRegex(@"\s*[\(\[]?\s*\b(feat\.?|ft\.?|featuring)\s+[^\)\]]*[\)\]]?", RegexOptions.IgnoreCase)]
    private static partial Regex Featuring();

    [GeneratedRegex(@"^\s*\d{1,3}\s*[-._)]\s*")]
    private static partial Regex LeadingTrackNumber();

    /// <summary>The YouTube video ID that yt-dlp appends to file names ("Song [dQw4w9WgXcQ]").</summary>
    public static string? YouTubeId(string path)
    {
        var m = YouTubeIdSuffix().Match(Path.GetFileNameWithoutExtension(path));
        return m.Success ? m.Groups[1].Value : null;
    }

    [GeneratedRegex(@"\s*[\(\[]\s*\d{2,4}\s*k(?:bps|b/s|bit)?\s*[\)\]]", RegexOptions.IgnoreCase)]
    private static partial Regex BitrateTag();          // (128K) [320kbps]

    [GeneratedRegex(@"(\s*\(\d{1,2}\)|\s+-\s+Copy(\s*\(\d+\))?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CopySuffix();          // "Song (1)", "Song - Copy (2)"

    [GeneratedRegex(@"^\s*(nightcore|sped\s*up|slowed(\s*(and|\+|&)\s*reverb)?)\s*[-:_]?\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SpeedPrefix();

    [GeneratedRegex(@"\b(nightcore|sped\s*up|speed\s*up|slowed)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SpeedWords();

    [GeneratedRegex(@"[「『](.+?)[」』]")]
    private static partial Regex JapaneseQuote();       // 「Artist」

    [GeneratedRegex(@"【[^】]*】")]
    private static partial Regex JapaneseTag();         // 【MV】

    /// <summary>Removes video ids, "(Official Video)", "(Lyrics)", "(128K)", "(1)", "| NCS Release" and similar noise.</summary>
    public static string Clean(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = YouTubeIdSuffix().Replace(s, "");
        // Names like "Nightcore_Song_Name" use underscores for spaces.
        if (s.Count(c => c == '_') >= 2 && s.Count(c => c == ' ') < s.Count(c => c == '_')) s = s.Replace('_', ' ');
        var cut = s.IndexOfAny(new[] { '|', '｜' });
        if (cut > 0) s = s[..cut];
        s = CopySuffix().Replace(s, "");
        s = BitrateTag().Replace(s, "");
        s = CopySuffix().Replace(s, "");
        s = NoiseBrackets().Replace(s, "");
        s = SquareBrackets().Replace(s, "");
        s = JapaneseTag().Replace(s, "");
        s = LeadingTrackNumber().Replace(s, "");
        s = Regex.Replace(s, @"\(\s+", "(");
        s = Regex.Replace(s, @"\s+\)", ")");
        s = Regex.Replace(s, @"\s+", " ");
        return s.Trim(' ', '-', '–', '—', '_');
    }

    /// <summary>Nightcore / sped-up / slowed versions: their length never matches the original release.</summary>
    public static bool IsSpeedVariant(string? s) => !string.IsNullOrEmpty(s) && SpeedWords().IsMatch(s);

    /// <summary>
    /// Possible (artist, title) readings of a cleaned name, most likely first:
    /// "Artist - Title", "Title 「Artist」", "Title By Artist", and the whole text as a title.
    /// </summary>
    public static List<(string? artist, string title)> Readings(string cleaned, string? knownArtist)
    {
        var list = new List<(string? artist, string title)>();
        var text = SpeedPrefix().Replace(cleaned, "").Trim();

        var jq = JapaneseQuote().Match(text);
        if (jq.Success)
        {
            var artist = jq.Groups[1].Value.Trim(' ', '_');
            var title = JapaneseQuote().Replace(text, "").Trim();
            if (artist.Length > 0 && title.Length > 0) list.Add((artist, title));
            text = title;
        }

        if (SplitDash(text) is { } p)
        {
            if (knownArtist != null && ArtistMatches(p.b, knownArtist)) list.Add((p.b, p.a));
            else list.Add((p.a, p.b));
        }
        else
        {
            // "Sleepless Night By Pill Low" — offered as a reading; scoring/verification decides ("Stand By Me" stays a title).
            var by = Regex.Match(text, @"^(?<title>.+?)\s+by\s+(?<artist>[^()]+?)$", RegexOptions.IgnoreCase);
            if (by.Success && by.Groups["artist"].Value.Split(' ').Length <= 4) list.Add((by.Groups["artist"].Value.Trim(), by.Groups["title"].Value.Trim()));
            list.Add((knownArtist, text));
        }
        // "夢と葉桜 Yume To Hazakura": catalogs list either the Japanese or the romanised title, so offer both.
        foreach (var (artist, title) in list.ToList())
        {
            if (ScriptSplit(title) is not { } t) continue;
            (string? cjk, string? latin) a = artist != null && ScriptSplit(artist) is { } sa ? sa : (artist, artist);
            list.Add((a.cjk, t.cjk!));
            list.Add((a.latin, t.latin!));
        }
        return list.Where(r => !string.IsNullOrWhiteSpace(r.title)).Distinct().Take(5).ToList();
    }

    /// <summary>Splits text that mixes CJK and Latin words into its two halves.</summary>
    public static (string? cjk, string? latin)? ScriptSplit(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        static bool IsCjk(char c) => c >= 0x3040 && c <= 0x30FF || c >= 0x4E00 && c <= 0x9FFF || c >= 0xAC00 && c <= 0xD7AF;
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cjk = string.Join(" ", words.Where(w => w.Any(IsCjk)));
        var latin = string.Join(" ", words.Where(w => !w.Any(IsCjk)));
        return cjk.Length > 0 && latin.Length > 0 ? (cjk, latin) : null;
    }

    public static string StripFeat(string? s) => string.IsNullOrEmpty(s) ? "" : Featuring().Replace(s, "").Trim();

    /// <summary>Splits "A - B" (also en/em dashes) into two parts.</summary>
    public static (string a, string b)? SplitDash(string s)
    {
        foreach (var sep in new[] { " - ", " – ", " — " })
        {
            var i = s.IndexOf(sep, StringComparison.Ordinal);
            if (i > 0) return (s[..i].Trim(), s[(i + sep.Length)..].Trim());
        }
        return null;
    }

    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder();
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    public static bool Similar(string? a, string? b)
    {
        var x = Normalize(a); var y = Normalize(b);
        if (x.Length == 0 || y.Length == 0) return false;
        if (x == y) return true;
        var (shorter, longer) = x.Length < y.Length ? (x, y) : (y, x);
        return longer.Contains(shorter) && shorter.Length >= Math.Max(3, longer.Length * 0.6);
    }

    /// <summary>Individual names in "A feat. B", "A & B", "A, B", "A x B".</summary>
    public static IEnumerable<string> ArtistNames(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) yield break;
        foreach (var part in Regex.Split(s, @"\s*(?:,|&|;|\bx\b|\band\b|\bfeat\.?|\bft\.?|\bfeaturing\b|\bwith\b)\s*", RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(part)) yield return part.Trim(' ', '(', ')');
    }

    public static bool ArtistMatches(string? candidate, string? query) =>
        ArtistNames(candidate).Any(c => ArtistNames(query).Any(q => Similar(c, q))) || Similar(candidate, query);

    /// <summary>
    /// 0–100 confidence that a text-search result (iTunes, Deezer) is the local song. Tries both "Artist - Title"
    /// orientations because file names are inconsistent, and uses the duration as a strong tie-breaker.
    /// </summary>
    public static double Score(string? cArtist, string? cTitle, long cMs, string? qArtist, string? qTitle, string freeText, long localMs)
    {
        double best = 0;
        foreach (var (a, t) in new[] { (qArtist, qTitle), (qTitle, qArtist) })
        {
            if (string.IsNullOrWhiteSpace(t)) continue;
            double s = TitleScore(cTitle, t) + (string.IsNullOrWhiteSpace(a) ? 15 : ArtistMatches(cArtist, a) ? 35 : 0);
            best = Math.Max(best, s);
        }
        // Free text such as a cleaned file name containing both the artist and the title.
        var ft = Normalize(StripFeat(freeText));
        var ct = Normalize(StripFeat(cTitle));
        if (ft.Length > 0 && ct.Length >= 3 && ft.Contains(ct) && ArtistNames(cArtist).Any(n => Normalize(n).Length >= 2 && ft.Contains(Normalize(n))))
            best = Math.Max(best, 85);

        if (localMs > 0 && cMs > 0)
        {
            var diff = Math.Abs(localMs - cMs) / 1000.0;
            best += diff <= 3 ? 10 : diff <= 8 ? 5 : diff <= 20 ? 0 : -30;
        }
        return Math.Clamp(best, 0, 100);
    }

    private static double TitleScore(string? candidate, string query)
    {
        var c = Normalize(StripFeat(candidate)); var q = Normalize(StripFeat(Clean(query)));
        if (c.Length == 0 || q.Length == 0) return 0;
        if (c == q) return 55;
        return Similar(c, q) ? 45 : 0;
    }
}

/// <summary>The genre vocabulary: only these values are written to the Genre field.</summary>
public static class Genres
{
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "acoustic", "afrobeat", "afrobeats", "alternative", "alternative rock", "alternative metal", "ambient", "americana", "bass house", "big band",
        "big room", "bluegrass", "blues", "blues rock", "bossa nova", "breakbeat", "britpop", "chillout", "chillstep", "chillwave", "christian",
        "classic rock", "classical", "country", "country pop", "dance", "dance-pop", "dancehall", "deep house", "disco", "drum and bass", "dub",
        "dubstep", "edm", "electro", "electro house", "electronic", "electronica", "electropop", "emo", "folk", "folk rock", "funk", "future bass",
        "future house", "garage", "glitch hop", "gospel", "grime", "grunge", "hard rock", "hardcore", "hardstyle", "heavy metal", "hip hop", "hip-hop",
        "house", "idm", "indie", "indie folk", "indie pop", "indie rock", "industrial", "j-pop", "jazz", "jazz fusion", "k-pop", "latin", "latin pop",
        "lo-fi", "lofi", "lounge", "melodic dubstep", "melodic house", "metal", "metalcore", "moombahton", "neo soul", "new age", "new wave",
        "nu disco", "opera", "piano", "pop", "pop punk", "pop rock", "post-punk", "post-rock", "progressive house", "progressive rock", "psychedelic",
        "psychedelic rock", "punk", "punk rock", "r&b", "rap", "reggae", "reggaeton", "rock", "rock and roll", "salsa", "shoegaze", "ska",
        "singer-songwriter", "soft rock", "soul", "soundtrack", "synth-pop", "synthpop", "synthwave", "techno", "trance", "trap", "trip hop",
        "tropical house", "tropical", "vaporwave", "world",
    };

    // iTunes uses its own broad categories.
    private static readonly Dictionary<string, string> ITunesMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hip-Hop/Rap"] = "Hip-Hop", ["R&B/Soul"] = "R&B", ["Singer/Songwriter"] = "Singer-Songwriter", ["Electronic"] = "Electronic",
        ["Dance"] = "Dance", ["Alternative"] = "Alternative", ["Pop"] = "Pop", ["Rock"] = "Rock", ["Country"] = "Country", ["Jazz"] = "Jazz",
        ["Classical"] = "Classical", ["Metal"] = "Metal", ["Hard Rock"] = "Hard Rock", ["Soundtrack"] = "Soundtrack", ["Latin"] = "Latin",
        ["Reggae"] = "Reggae", ["Blues"] = "Blues", ["Folk"] = "Folk", ["Indie Pop"] = "Indie Pop", ["K-Pop"] = "K-Pop", ["J-Pop"] = "J-Pop",
        ["House"] = "House", ["Techno"] = "Techno", ["Trance"] = "Trance", ["Dubstep"] = "Dubstep", ["Ambient"] = "Ambient", ["Soul"] = "Soul",
        ["Funk"] = "Funk", ["Punk"] = "Punk", ["Christian"] = "Christian", ["Gospel"] = "Gospel", ["Reggaeton"] = "Reggaeton",
    };

    public static bool IsKnown(string? name) => !string.IsNullOrWhiteSpace(name) && Known.Contains(name.Trim());

    /// <summary>Returns a display-cased genre, or null if the tag isn't a recognised genre.</summary>
    public static string? FromTag(string? tag)
    {
        if (!IsKnown(tag)) return null;
        var t = tag!.Trim().ToLowerInvariant();
        return t switch
        {
            "edm" => "EDM", "idm" => "IDM", "r&b" => "R&B", "hip hop" or "hip-hop" => "Hip-Hop", "lofi" or "lo-fi" => "Lo-Fi",
            "k-pop" => "K-Pop", "j-pop" => "J-Pop", "synthpop" or "synth-pop" => "Synth-Pop", "drum and bass" => "Drum and Bass",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(t),
        };
    }

    public static string? FromITunes(string? primaryGenre) =>
        primaryGenre != null && ITunesMap.TryGetValue(primaryGenre, out var g) ? g : FromTag(primaryGenre);
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace WinMuse.Services;

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

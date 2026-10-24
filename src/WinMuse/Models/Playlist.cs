using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace WinMuse.Models;

public sealed class Playlist : ObservableObject
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public long CreatedUtc { get; set; }
    public bool IsSmart { get; set; }
    public string? RulesJson { get; set; }
    public string? CoverPath { get; set; }

    /// <summary>
    /// Automatic playlists (Fresh in Your Library, On Repeat, Long Time No Play) have negative ids: the app
    /// keeps them up to date, they are never saved and they are not listed with your playlists.
    /// </summary>
    public bool IsSystem => Id < 0;

    /// <summary>Resolved track list (filled by the library service).</summary>
    public List<Track> Tracks { get; set; } = new();

    public string? Art1 => CoverPath ?? DistinctArt.ElementAtOrDefault(0);
    public string? Art2 => CoverPath != null ? null : DistinctArt.ElementAtOrDefault(1);
    public string? Art3 => CoverPath != null ? null : DistinctArt.ElementAtOrDefault(2);
    public string? Art4 => CoverPath != null ? null : DistinctArt.ElementAtOrDefault(3);
    public bool IsMosaic => CoverPath == null && DistinctArt.Count() >= 4;
    private IEnumerable<string> DistinctArt => Tracks.Select(t => t.ArtPath).Where(p => p != null).Distinct()!;

    public string Summary
    {
        get
        {
            var n = Tracks.Count;
            var kind = IsSystem ? "Automatic playlist" : IsSmart ? "Smart playlist" : "Playlist";
            return $"{kind} · {n} {(n == 1 ? "song" : "songs")} · {Helpers.Format.LongTime(TimeSpan.FromMilliseconds(Tracks.Sum(t => t.DurationMs)))}";
        }
    }

    public SmartPlaylistDefinition? Rules
    {
        get => string.IsNullOrEmpty(RulesJson) ? null : JsonSerializer.Deserialize<SmartPlaylistDefinition>(RulesJson);
        set => RulesJson = value == null ? null : JsonSerializer.Serialize(value);
    }

    public void Refresh() => OnPropertyChanged(string.Empty);

    private bool _canMove;
    /// <summary>UI only: Home shows the move up / down buttons (Custom order).</summary>
    public bool CanMove { get => _canMove; set => SetProperty(ref _canMove, value); }
}

public enum SmartField { Title, Artist, Album, AlbumArtist, Genre, Year, PlayCount, Rating, AddedDaysAgo, LastPlayedDaysAgo, DurationSeconds, Codec, Path }
public enum SmartOperator { Contains, NotContains, Is, IsNot, StartsWith, GreaterThan, LessThan }

public sealed class SmartRule
{
    public SmartField Field { get; set; } = SmartField.Genre;
    public SmartOperator Operator { get; set; } = SmartOperator.Contains;
    public string Value { get; set; } = "";
}

public sealed class SmartPlaylistDefinition
{
    public bool MatchAll { get; set; } = true;
    public List<SmartRule> Rules { get; set; } = new();
    public int Limit { get; set; }
    public string SortBy { get; set; } = "Random";

    public bool Matches(Track t)
    {
        if (Rules.Count == 0) return true;
        return MatchAll ? Rules.All(r => Eval(r, t)) : Rules.Any(r => Eval(r, t));
    }

    public IEnumerable<Track> Apply(IEnumerable<Track> all)
    {
        var q = all.Where(Matches);
        q = SortBy switch
        {
            "Title" => q.OrderBy(t => t.DisplayTitle),
            "Artist" => q.OrderBy(t => t.DisplayArtist).ThenBy(t => t.DisplayAlbum).ThenBy(t => t.DiscNumber).ThenBy(t => t.TrackNumber),
            "Most played" => q.OrderByDescending(t => t.PlayCount),
            "Recently added" => q.OrderByDescending(t => t.AddedUtc),
            "Recently played" => q.OrderByDescending(t => t.LastPlayedUtc),
            "Rating" => q.OrderByDescending(t => t.Rating),
            _ => q.OrderBy(_ => Random.Shared.Next()),
        };
        return Limit > 0 ? q.Take(Limit) : q;
    }

    private static bool Eval(SmartRule r, Track t)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        object? v = r.Field switch
        {
            SmartField.Title => t.DisplayTitle,
            SmartField.Artist => t.DisplayArtist,
            SmartField.Album => t.DisplayAlbum,
            SmartField.AlbumArtist => t.DisplayAlbumArtist,
            SmartField.Genre => t.Genre ?? "",
            SmartField.Codec => t.Codec ?? t.Extension,
            SmartField.Path => t.Path,
            SmartField.Year => (double)t.Year,
            SmartField.PlayCount => (double)t.PlayCount,
            SmartField.Rating => (double)t.Rating,
            SmartField.AddedDaysAgo => (now - t.AddedUtc) / 86400.0,
            SmartField.LastPlayedDaysAgo => t.LastPlayedUtc == 0 ? double.MaxValue : (now - t.LastPlayedUtc) / 86400.0,
            SmartField.DurationSeconds => t.DurationMs / 1000.0,
            _ => "",
        };

        if (v is double d)
        {
            if (!double.TryParse(r.Value, out var x)) return false;
            return r.Operator switch
            {
                SmartOperator.GreaterThan => d > x,
                SmartOperator.LessThan => d < x,
                SmartOperator.IsNot => Math.Abs(d - x) > 0.0001,
                _ => Math.Abs(d - x) < 0.0001,
            };
        }

        var s = (string)v!;
        var cmp = StringComparison.OrdinalIgnoreCase;
        return r.Operator switch
        {
            SmartOperator.Contains => s.Contains(r.Value, cmp),
            SmartOperator.NotContains => !s.Contains(r.Value, cmp),
            SmartOperator.Is => s.Equals(r.Value, cmp),
            SmartOperator.IsNot => !s.Equals(r.Value, cmp),
            SmartOperator.StartsWith => s.StartsWith(r.Value, cmp),
            SmartOperator.GreaterThan => string.Compare(s, r.Value, cmp) > 0,
            SmartOperator.LessThan => string.Compare(s, r.Value, cmp) < 0,
            _ => false,
        };
    }
}

using System.Collections.ObjectModel;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;
using Xunit;

namespace WinMuse.Tests;

/// <summary>Artist credits: splitting "A & B", "A feat. B", "A; B"; renaming one artist; adding one.</summary>
public class ArtistCreditTests
{
    [Theory]
    [InlineData("Tom Frane & RJ Pasin", new[] { "Tom Frane", "RJ Pasin" })]
    [InlineData("A, B & C", new[] { "A", "B", "C" })]
    [InlineData("A feat. B", new[] { "A", "B" })]
    [InlineData("A ft. B", new[] { "A", "B" })]
    [InlineData("A featuring B", new[] { "A", "B" })]
    [InlineData("A (feat. B)", new[] { "A", "B" })]
    [InlineData("A x B", new[] { "A", "B" })]
    [InlineData("A; B; A", new[] { "A", "B" })]
    [InlineData("Solo", new[] { "Solo" })]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    public void Split(string credit, string[] expected) => Assert.Equal(expected, ArtistCredit.Split(credit));

    [Fact] public void SplitNull() => Assert.Empty(ArtistCredit.Split(null));
    [Fact] public void Contains_IgnoresCase() => Assert.True(ArtistCredit.Contains("Tom Frane & RJ Pasin", "rj pasin"));
    [Fact] public void Xavier_IsNotSplitOnX() => Assert.Equal(new[] { "Xavier" }, ArtistCredit.Split("Xavier"));

    [Theory]
    [InlineData("A & B", "A", "Z", "Z & B")]
    [InlineData("A feat. B", "B", "Z", "A feat. Z")]
    [InlineData("A; B", "C", "Z", "A; B")]         // not credited: unchanged
    [InlineData("A & B", "A", "B", "B")]            // renamed onto the other artist: listed once
    public void Rename(string credit, string oldName, string newName, string expected) => Assert.Equal(expected, ArtistCredit.Rename(credit, oldName, newName));

    [Theory]
    [InlineData(null, "A", "A")]
    [InlineData("A", "B", "A; B")]
    [InlineData("A & B", "b", "A & B")]
    public void Add(string? credit, string name, string expected) => Assert.Equal(expected, ArtistCredit.Add(credit, name));
}

/// <summary>Which artists a song belongs to (artist pages, Continue Listening, fetch).</summary>
public class TrackArtistTests
{
    [Fact] public void Unknown_WhenNoArtist() => Assert.Equal(new[] { "Unknown Artist" }, TestSupport.T(null).ArtistNames);
    [Fact] public void VariousArtists_IsNotAnArtist() => Assert.Equal(new[] { "A" }, TestSupport.T("A", "Various Artists").ArtistNames);
    [Fact] public void ArtistAndAlbumArtist_Merged() => Assert.Equal(new[] { "A", "B", "C" }, TestSupport.T("A & B", "C").ArtistNames);
    [Fact] public void PrimaryArtist_PrefersAlbumArtist() => Assert.Equal("C", TestSupport.T("A & B", "C").PrimaryArtist);
    [Fact] public void PrimaryArtist_FallsBackToArtist() => Assert.Equal("A", TestSupport.T("A & B").PrimaryArtist);
    [Fact] public void AlbumKey_IsCaseInsensitive()
    {
        var a = TestSupport.T("X"); a.Album = "Night";
        var b = TestSupport.T("x"); b.Album = "NIGHT";
        Assert.Equal(a.AlbumKey, b.AlbumKey);
    }
    [Fact] public void DisplayTitle_FromFileName()
    {
        var t = new Track { Path = @"C:\m\Some Song.mp3" };
        Assert.Equal("Some Song", t.DisplayTitle);
    }
}

/// <summary>Song identification helpers: cleaning names, comparing, scoring candidates.</summary>
public class MatchingTests
{
    [Theory]
    [InlineData("Song (Official Video)", "Song")]
    [InlineData("Song [Lyrics]", "Song")]
    [InlineData("Song (Official Music Video) [dQw4w9WgXcQ]", "Song")]
    public void Clean_RemovesVideoNoise(string input, string expected) => Assert.Equal(expected, Matching.Clean(input).Trim());

    [Fact] public void YouTubeId_FromFileName() => Assert.Equal("dQw4w9WgXcQ", Matching.YouTubeId(@"C:\m\Song [dQw4w9WgXcQ].mp3"));
    [Fact] public void YouTubeId_NoneInPlainName() => Assert.Null(Matching.YouTubeId(@"C:\m\Song.mp3"));
    [Fact] public void Similar_IgnoresCaseAndPunctuation() => Assert.True(Matching.Similar("Don't Stop Me Now", "dont stop me now"));
    [Fact] public void Similar_DifferentSongs() => Assert.False(Matching.Similar("Yesterday", "Tomorrow Never Knows"));
    [Fact] public void ArtistMatches_FeaturedArtist() => Assert.True(Matching.ArtistMatches("Calvin Harris feat. Rihanna", "Rihanna"));
    [Fact] public void StripFeat() => Assert.Equal("Song", Matching.StripFeat("Song (feat. Someone)"));
    [Fact] public void SplitDash() => Assert.Equal(("Artist", "Title"), Matching.SplitDash("Artist - Title"));

    [Fact]
    public void Score_RightSongBeatsWrongSong()
    {
        double right = Matching.Score("Queen", "Bohemian Rhapsody", 354000, "Queen", "Bohemian Rhapsody", "Queen - Bohemian Rhapsody", 355000);
        double wrongTitle = Matching.Score("Queen", "Radio Ga Ga", 343000, "Queen", "Bohemian Rhapsody", "Queen - Bohemian Rhapsody", 355000);
        double wrongLength = Matching.Score("Queen", "Bohemian Rhapsody", 120000, "Queen", "Bohemian Rhapsody", "Queen - Bohemian Rhapsody", 355000);
        Assert.True(right > wrongTitle, $"{right} vs {wrongTitle}");
        Assert.True(right > wrongLength, $"{right} vs {wrongLength}");
        Assert.InRange(right, 0, 100);
    }

    [Fact]
    public void Score_SwappedArtistAndTitleStillMatches()
    {
        double s = Matching.Score("Queen", "Bohemian Rhapsody", 354000, "Bohemian Rhapsody", "Queen", "Bohemian Rhapsody - Queen", 355000);
        Assert.True(s >= 60, s.ToString());
    }

    [Theory]
    [InlineData("Rock", true)]
    [InlineData("Pop", true)]
    [InlineData("seen live", false)]
    [InlineData("", false)]
    public void Genres_IsKnown(string g, bool known) => Assert.Equal(known, Genres.IsKnown(g));
}

public class LrcParserTests
{
    [Fact]
    public void Synced_LinesInOrder()
    {
        var r = LrcParser.Parse("[00:01.00]One\n[00:02.50]Two\n[01:00.123]Three");
        Assert.True(r.IsSynced);
        Assert.Equal(new[] { 1000.0, 2500.0, 60123.0 }, r.Lines.Select(l => l.Time.TotalMilliseconds));
        Assert.Equal("Two", r.Lines[1].Text);
    }

    [Fact]
    public void Offset_ShiftsEarlier()
    {
        var r = LrcParser.Parse("[offset:+500]\n[00:02.00]Line");
        Assert.Equal(1500, r.Lines[0].Time.TotalMilliseconds);
    }

    [Fact]
    public void RepeatedTags_ExpandToSeveralLines()
    {
        var r = LrcParser.Parse("[00:01.00][00:05.00]Chorus");
        Assert.Equal(2, r.Lines.Count);
    }

    [Fact]
    public void Plain_IsNotSynced()
    {
        var r = LrcParser.Parse("just some words\nmore words");
        Assert.False(r.IsSynced);
        Assert.Contains("more words", r.PlainText);
    }

    [Fact] public void WordTags_Removed() => Assert.Equal("Hello world", LrcParser.Parse("[00:01.00]<00:01.00>Hello <00:01.50>world").Lines[0].Text);
}

public class FormatTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(59.9, "0:59")]
    [InlineData(61, "1:01")]
    [InlineData(3661, "1:01:01")]
    public void Time(double seconds, string expected) => Assert.Equal(expected, Format.Time(seconds));

    [Fact] public void Time_NaN_IsZero() => Assert.Equal("0:00", Format.Time(double.NaN));
    [Fact] public void Greeting_IsTitleCase() => Assert.Matches("^Good (Morning|Afternoon|Evening)$", Format.Greeting());
}

/// <summary>In-place list updates (Home rows, grids): any change must end with exactly the source list.</summary>
public class CollectionSyncTests
{
    [Fact]
    public void RandomEdits_EndEqualToSource()
    {
        var rng = new Random(1234);
        for (int round = 0; round < 2000; round++)
        {
            var before = Enumerable.Range(0, rng.Next(0, 30)).Select(_ => rng.Next(0, 40)).Distinct().ToList();
            var after = Enumerable.Range(0, rng.Next(0, 30)).Select(_ => rng.Next(0, 40)).Distinct().ToList();
            var target = new ObservableCollection<int>(before);
            var bound = CollectionSync.SyncOrReplace(target, after, x => x);
            Assert.Equal(after, bound);
        }
    }

    [Fact]
    public void ChangedItem_ReplacedInPlace()
    {
        var target = new ObservableCollection<(int id, string v)>(new[] { (1, "a"), (2, "b") });
        CollectionSync.Sync(target, new (int id, string v)[] { (1, "a"), (2, "B") }, x => x.id, (a, b) => a.v == b.v);
        Assert.Equal("B", target[1].v);
    }
}

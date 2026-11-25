using WinMuse.Models;
using WinMuse.Services;
using Xunit;

namespace WinMuse.Tests;

/// <summary>Songs that shared an album label end up on one album after matching (see AlbumAgreement).</summary>
public class AlbumAgreementTests
{
    private static MetadataCandidate C(string title, string album, string albumArtist, int year, int track = 0, string? rec = null, double score = 95) =>
        new() { Title = title, Artist = "Boogie Belgique", Album = album, AlbumArtist = albumArtist, Year = year, TrackNumber = track, RecordingId = rec, Score = score, DurationMs = 200_000 };

    /// <summary>A song after matching applied <paramref name="match"/> to it.</summary>
    private static AlbumMember Matched(int n, MetadataCandidate match, params MetadataCandidate[] others)
    {
        var t = new Track { Id = n, Path = $@"C:\Music\Nightwalker\{n:00}.mp3", Title = match.Title, Artist = match.Artist, Album = match.Album, AlbumArtist = match.AlbumArtist, Year = match.Year, TrackNumber = match.TrackNumber };
        return new AlbumMember(t, match, others.Prepend(match).ToList(), true);
    }

    private const string Original = "Nightwalker, Volume I";

    [Fact]
    public void OneSongOnACompilation_JoinsTheAlbum()
    {
        // Song 1 matched a compilation; one of its other results is the same recording on the original album.
        var members = new List<AlbumMember>
        {
            Matched(1, C("Forever and Ever", "Swing-Hop Don't Stop: Speakeasy vol.3", "Speakeasy Electro Swing", 2016, 5, "r1"),
                       C("Forever & Ever", Original, "Boogie Belgique", 2015, 1, "r1", 88)),
        };
        for (int i = 2; i <= 8; i++) members.Add(Matched(i, C($"Song {i}", Original, "Boogie Belgique", 2015, i)));

        var d = AlbumAgreement.Decide(members, "Nightwalker (Vol. 1) (LP)")!;
        Assert.Equal(Original, d.Album);
        Assert.Equal("Boogie Belgique", d.AlbumArtist);
        Assert.Equal(2015, d.Year);
        Assert.Equal(8, d.Songs.Count);
        Assert.Equal(1, d.Releases[members[0].Song].TrackNumber); // its place on the album comes from that result
    }

    [Fact]
    public void AmpersandAndAnd_AreTheSameRecording()
    {
        // No shared recording ID: the titles differ only by "&" / "and".
        var members = new List<AlbumMember>
        {
            Matched(1, C("Forever and Ever", "Speakeasy vol.3", "Various Artists", 2016), C("Forever & Ever", Original, "Boogie Belgique", 2015, 1)),
            Matched(2, C("Song 2", Original, "Boogie Belgique", 2015, 2)),
            Matched(3, C("Song 3", Original, "Boogie Belgique", 2015, 3)),
        };
        Assert.Equal(3, AlbumAgreement.Decide(members, "Nightwalker")!.Songs.Count);
    }

    [Fact]
    public void Reissue_DoesNotChangeTheAlbumYear()
    {
        var members = Enumerable.Range(1, 8).Select(i => Matched(i, C($"Song {i}", "Curses From Past Times", "Bunkur", i == 3 ? 2018 : 2011, i))).ToList();
        Assert.Equal(2011, AlbumAgreement.Decide(members, "Curses From Past Times")!.Year);
    }

    [Fact]
    public void OneGenrePerAlbum()
    {
        var members = Enumerable.Range(1, 4).Select(i => Matched(i, C($"Song {i}", "Cookin' Session", "X", 2010, i))).ToList();
        members[0].Song.Genre = "Jazz"; members[1].Song.Genre = "Funk"; members[2].Song.Genre = "Jazz"; members[3].Song.Genre = "Soul";
        Assert.Equal("Jazz", AlbumAgreement.Decide(members, "Cookin' Session")!.Genre);
    }

    [Fact]
    public void SongNotFoundOnline_KeepsTheSharedLabel_JoinsTheAlbum()
    {
        var members = Enumerable.Range(1, 3).Select(i => Matched(i, C($"Song {i}", Original, "Boogie Belgique", 2015, i))).ToList();
        var unmatched = new Track { Id = 9, Path = @"C:\Music\Nightwalker\09.mp3", Album = "Nightwalker (Vol. 1) (LP)" };
        members.Add(new AlbumMember(unmatched, null, Array.Empty<MetadataCandidate>(), false));
        var d = AlbumAgreement.Decide(members, "Nightwalker (Vol. 1) (LP)")!;
        Assert.Contains(unmatched, d.Songs);
        Assert.Equal(Original, d.Album);
    }

    [Fact]
    public void FolderOfSingles_IsLeftAsMatched()
    {
        // A downloader labelled every song "YouTube": each one really is on a different album.
        var members = Enumerable.Range(1, 4).Select(i => Matched(i, C($"Song {i}", $"Album {i}", $"Artist {i}", 2000 + i))).ToList();
        Assert.Null(AlbumAgreement.Decide(members, "YouTube"));
    }

    [Fact]
    public void SongOnAnotherAlbum_WithNoLinkToTheWinner_StaysThere()
    {
        var members = Enumerable.Range(1, 4).Select(i => Matched(i, C($"Song {i}", Original, "Boogie Belgique", 2015, i))).ToList();
        members.Add(Matched(5, C("Other", "Something Else Entirely", "Someone", 1999)));
        members[4].Song.Artist = "Someone"; // a song by someone else that happened to carry the label

        var d = AlbumAgreement.Decide(members, "Nightwalker")!;
        Assert.Equal(4, d.Songs.Count);
        Assert.DoesNotContain(members[4].Song, d.Songs);
    }

    [Fact]
    public void Deluxe_BonusTrack_JoinsTheAlbum()
    {
        var members = Enumerable.Range(1, 3).Select(i => Matched(i, C($"Song {i}", "First Light", "A", 2015, i))).ToList();
        members.Add(Matched(4, C("Bonus", "First Light (Deluxe Edition)", "A", 2016, 12)));
        Assert.Equal(4, AlbumAgreement.Decide(members, "First Light")!.Songs.Count);
    }

    [Fact]
    public void SingleByTheAlbumArtist_JoinsTheAlbum()
    {
        // "Talk to Me EP": three songs not found online keep the label; the fourth matched a single by the same artist.
        var ep = Enumerable.Range(1, 3).Select(i => new AlbumMember(
            new Track { Id = i, Path = $@"C:\Music\EP\{i}.mp3", Title = $"Song {i}", Artist = "Miranda Shvangiradze", Album = "Talk to Me EP", TrackNumber = i }, null, Array.Empty<MetadataCandidate>(), false)).ToList();
        var single = C("Ready for the Trip?", "Ready for the Trip?", "Miranda Shvangiradze", 2012, 1);
        single.Artist = "Miranda Shvangiradze";
        ep.Add(Matched(4, single));
        var d = AlbumAgreement.Decide(ep, "Talk to Me EP")!;
        Assert.Equal("Talk to Me EP", d.Album);
        Assert.Equal(4, d.Songs.Count);
    }

    [Fact] public void SingleSong_NothingToDecide() => Assert.Null(AlbumAgreement.Decide(new[] { Matched(1, C("Song", "Album", "A", 2015)) }, "Album"));

    [Fact] public void Normalize_TreatsAmpersandAsAnd() => Assert.Equal(Matching.Normalize("Forever and Ever"), Matching.Normalize("Forever & Ever"));
}

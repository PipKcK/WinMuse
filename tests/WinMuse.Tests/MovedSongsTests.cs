using WinMuse.Models;
using WinMuse.Services;
using Xunit;

namespace WinMuse.Tests;

/// <summary>A song whose file was moved or renamed keeps its history (see MovedSongs).</summary>
public class MovedSongsTests
{
    private const long Date = 1_795_540_681;

    private static Track Song(long id, string path, long size = 13_849_925, long date = Date, string? title = "The Werewolf", int ms = 342_014) =>
        new() { Id = id, Path = path, Title = title, FileSize = size, ModifiedUtc = date, DurationMs = ms, Codec = "MP3", SampleRate = 44_100, Channels = 2 };

    private static MovedSongs.NewFile File(string path, long size = 13_849_925, long date = Date) => new(path, size, date);

    [Fact]
    public void Renamed_IsTheSameSong()
    {
        var old = Song(2, @"C:\Music\Kova\Kova_-_03_-_The_Werewolf.mp3");
        var m = MovedSongs.MatchUnchanged([old], [File(@"C:\Music\Kova\03 The Werewolf.mp3")]);
        Assert.Same(old, m[@"C:\Music\Kova\03 The Werewolf.mp3"]);
    }

    [Fact]
    public void MovedAndRenamed_IsTheSameSong()
    {
        var old = Song(2, @"C:\Music\Kova\Kova_-_03_-_The_Werewolf.mp3");
        var m = MovedSongs.MatchUnchanged([old], [File(@"C:\Music\Kova\Cookin' Session\03 The Werewolf.mp3")]);
        Assert.Single(m);
    }

    [Fact]
    public void MovedUnderTheSameName_StillMatches()
    {
        var old = Song(2, @"C:\Music\A\song.mp3");
        var m = MovedSongs.MatchUnchanged([old], [File(@"C:\Music\B\song.mp3")]);
        Assert.Same(old, m[@"C:\Music\B\song.mp3"]);
    }

    [Fact]
    public void DifferentContent_IsANewSong()
    {
        var old = Song(2, @"C:\Music\Kova\a.mp3");
        Assert.Empty(MovedSongs.MatchUnchanged([old], [File(@"C:\Music\Kova\b.mp3", size: 9_000_000)]));
        Assert.Empty(MovedSongs.MatchUnchanged([old], [File(@"C:\Music\Kova\b.mp3", date: Date + 60)]));
    }

    [Fact]
    public void TwoFilesThatFitOneSong_NeitherIsMatched()
    {
        // The song was renamed and also copied: either file could carry its history, so neither is guessed.
        var old = Song(2, @"C:\Music\a.mp3");
        Assert.Empty(MovedSongs.MatchUnchanged([old], [File(@"C:\Music\b.mp3"), File(@"C:\Music\c.mp3")]));
    }

    [Fact]
    public void TwoSongsThatFitOneFile_NeitherIsMatched()
    {
        var a = Song(2, @"C:\Music\a.mp3");
        var b = Song(3, @"C:\Music\Copy\b.mp3");
        Assert.Empty(MovedSongs.MatchUnchanged([a, b], [File(@"C:\Music\c.mp3")]));
    }

    [Fact]
    public void SameNameIsPreferred_WhenSizeAndDateRepeat()
    {
        // Two songs of identical size and date: the one whose name matches is the safe pair.
        var a = Song(2, @"C:\Music\A\one.mp3");
        var b = Song(3, @"C:\Music\A\two.mp3");
        var m = MovedSongs.MatchUnchanged([a, b], [File(@"C:\Music\B\one.mp3")]);
        Assert.Same(a, m[@"C:\Music\B\one.mp3"]);
    }

    [Fact]
    public void RetaggedAndRenamed_MatchesByAudioAndTitle()
    {
        // A tag editor rewrote the tags (new size and date) and renamed the file from them.
        var old = Song(2, @"C:\Music\Kova\Kova_-_03_-_The_Werewolf.mp3", title: "the werewolf");
        var now = Song(0, @"C:\Music\Kova\Kova - The Werewolf.mp3", size: 13_900_000, date: Date + 3600, title: "The Werewolf", ms: 342_300);
        var m = MovedSongs.MatchChanged([old], [now]);
        Assert.Same(old, m[now.Path]);
    }

    [Fact]
    public void Retagged_DifferentTitleOrLength_IsANewSong()
    {
        var old = Song(2, @"C:\Music\a.mp3");
        Assert.Empty(MovedSongs.MatchChanged([old], [Song(0, @"C:\Music\b.mp3", title: "Jungle Boogie")]));
        Assert.Empty(MovedSongs.MatchChanged([old], [Song(0, @"C:\Music\b.mp3", ms: 262_957)]));
    }

    [Fact]
    public void Retagged_Untitled_MatchesByFileName()
    {
        var old = Song(2, @"C:\Music\x\01_hiliv003_hypnagogic.mp3", title: null);
        var now = Song(0, @"C:\Music\y\01_hiliv003_hypnagogic.mp3", size: 1, date: 1, title: "Hypnagogic");
        Assert.Single(MovedSongs.MatchChanged([old], [now]));
    }

    [Fact]
    public void Retagged_TwoCopiesOfOneSong_NeitherIsMatched()
    {
        var old = Song(2, @"C:\Music\a.mp3");
        var m = MovedSongs.MatchChanged([old], [Song(0, @"C:\Music\b.mp3", size: 1), Song(0, @"C:\Music\c.mp3", size: 2)]);
        Assert.Empty(m);
    }
}

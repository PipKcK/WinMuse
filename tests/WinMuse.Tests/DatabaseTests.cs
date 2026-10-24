using WinMuse.Models;
using WinMuse.Services;
using Xunit;

namespace WinMuse.Tests;

/// <summary>The library database (a fresh one per test run, in the temp test data folder).</summary>
public class DatabaseTests
{
    private static readonly LibraryDatabase Db = new();

    private static Track NewTrack(string name) => new()
    {
        Path = $@"C:\db-test\{Guid.NewGuid():N}-{name}.mp3", Title = name, Artist = "Artist", Album = "Album", Genre = "Pop",
        Year = 2020, TrackNumber = 3, DurationMs = 200_000, PlayCount = 2, LastPlayedUtc = 1_700_000_000,
        MetaState = ItemState.Downloaded, ArtState = ItemState.NotFound, LyricsState = ItemState.EditedByUser, EditedLocally = true,
    };

    [Fact]
    public void Tracks_RoundTrip()
    {
        var t = NewTrack("Round trip");
        Db.UpsertTracks(new[] { t });
        Assert.True(t.Id > 0);
        var back = Db.LoadTracks().Single(x => x.Id == t.Id);
        Assert.Equal(t.Title, back.Title);
        Assert.Equal(t.Year, back.Year);
        Assert.Equal(t.PlayCount, back.PlayCount);
        Assert.Equal(t.MetaState, back.MetaState);
        Assert.Equal(t.ArtState, back.ArtState);
        Assert.Equal(t.LyricsState, back.LyricsState);
        Assert.True(back.EditedLocally);
    }

    [Fact]
    public void Upsert_SamePathUpdates()
    {
        var t = NewTrack("Update");
        Db.UpsertTracks(new[] { t });
        t.Title = "Updated";
        Db.UpsertTracks(new[] { t });
        Assert.Single(Db.LoadTracks(), x => x.Path == t.Path);
        Assert.Equal("Updated", Db.LoadTracks().Single(x => x.Path == t.Path).Title);
    }

    [Fact]
    public void Playlist_ItemsKeepOrder()
    {
        var a = NewTrack("A"); var b = NewTrack("B"); var c = NewTrack("C");
        Db.UpsertTracks(new[] { a, b, c });
        var p = new Playlist { Name = "Order", CreatedUtc = 1 };
        Db.SavePlaylist(p);
        Db.SetPlaylistItems(p.Id, new[] { c.Id, a.Id, b.Id });
        var (_, ids) = Db.LoadPlaylists().Single(x => x.playlist.Id == p.Id);
        Assert.Equal(new[] { c.Id, a.Id, b.Id }, ids);
    }

    [Fact]
    public void Playlist_Delete()
    {
        var p = new Playlist { Name = "Gone", CreatedUtc = 1 };
        Db.SavePlaylist(p);
        Db.DeletePlaylist(p.Id);
        Assert.DoesNotContain(Db.LoadPlaylists(), x => x.playlist.Id == p.Id);
    }

    [Fact]
    public void SmartRules_RoundTrip()
    {
        var p = new Playlist { Name = "Smart", IsSmart = true, CreatedUtc = 1 };
        p.Rules = new SmartPlaylistDefinition { Rules = { new SmartRule { Field = SmartField.Genre, Operator = SmartOperator.Is, Value = "Rock" } } };
        Db.SavePlaylist(p);
        var back = Db.LoadPlaylists().Single(x => x.playlist.Id == p.Id).playlist;
        Assert.True(back.IsSmart);
        Assert.Equal("Rock", back.Rules!.Rules[0].Value);
    }

    [Fact]
    public void ArtistImage_RoundTrip()
    {
        Db.SaveArtistImage("Test Artist", @"C:\a.jpg", tried: true, byUser: true);
        var row = Db.LoadArtistImages().Single(x => x.Name == "Test Artist");
        Assert.Equal(@"C:\a.jpg", row.Path);
        Assert.True(row.ByUser);
    }

    [Fact]
    public void Excluded_AddAndClear()
    {
        Db.AddExcluded(new[] { @"C:\x\removed.mp3" });
        Assert.Contains(@"C:\x\removed.mp3", Db.LoadExcluded());
        Db.ClearExcluded();
        Assert.Empty(Db.LoadExcluded());
    }

    [Fact]
    public void ManyTracks_UpsertIsFast()
    {
        var many = Enumerable.Range(0, 5000).Select(i => NewTrack("bulk" + i)).ToList();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Db.UpsertTracks(many);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"5000 tracks took {sw.Elapsed.TotalSeconds:0.0} s");
        Assert.True(Db.LoadTracks().Count >= 5000);
    }
}

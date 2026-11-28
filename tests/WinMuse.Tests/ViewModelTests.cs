using WinMuse.Models;
using WinMuse.Services;
using WinMuse.ViewModels;
using Xunit;

namespace WinMuse.Tests;

/// <summary>The Settings page's logic, through its view model, without a window.</summary>
public class SettingsViewModelTests
{
    private static (TestApp app, SettingsViewModel vm, List<string> changed) Make()
    {
        var app = new TestApp();
        var vm = new SettingsViewModel(app.Settings, app.Library, app.Player, app.AutoFix);
        var changed = new List<string>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName ?? "");
        return (app, vm, changed);
    }

    [Fact]
    public void Oled_HasNoBackground()
    {
        var (app, vm, _) = Make();
        vm.BackgroundModeIndex = (int)BackgroundMode.Wallpaper;
        Assert.True(vm.ShowImageOptions);
        vm.ThemeIndex = (int)AppTheme.Oled;
        Assert.False(vm.IsBackgroundAvailable);
        Assert.False(vm.ShowImageOptions);
        Assert.Contains("OLED", vm.BackgroundDescription);
        Assert.Equal(AppTheme.Oled, app.Settings.Current.Theme);
    }

    [Theory]
    [InlineData(BackgroundMode.None, false, false, false)]
    [InlineData(BackgroundMode.AlbumArt, true, false, false)]
    [InlineData(BackgroundMode.Gradient, true, true, false)]
    [InlineData(BackgroundMode.Wallpaper, false, false, true)]
    public void BackgroundMode_ShowsItsOwnOptions(BackgroundMode mode, bool intensity, bool gradient, bool image)
    {
        var (_, vm, _) = Make();
        vm.BackgroundModeIndex = (int)mode;
        Assert.Equal(intensity, vm.ShowIntensity);
        Assert.Equal(gradient, vm.ShowGradientColors);
        Assert.Equal(image, vm.ShowImageOptions);
    }

    [Fact]
    public void WriteMode_BoxOrderMapsToTheSavedMode()
    {
        var (app, vm, changed) = Make();
        vm.WriteModeIndex = 1;
        Assert.Equal(MetadataWriteMode.WriteToFiles, app.Settings.Current.WriteMode);
        Assert.Contains(nameof(vm.WriteModeDescription), changed);
        Assert.Contains("written into the files", vm.WriteModeDescription);
        vm.WriteModeIndex = 2;
        Assert.Equal(MetadataWriteMode.Ask, app.Settings.Current.WriteMode);
        vm.WriteModeIndex = 9; // out of range: ignored
        Assert.Equal(MetadataWriteMode.Ask, app.Settings.Current.WriteMode);
    }

    [Fact]
    public void CardStyle_RetiredValue_ReadsAsTheFirstChoice()
    {
        var (app, vm, _) = Make();
        app.Settings.Current.ShelfCardColor = ShelfCardColor.Accent;
        Assert.Equal(0, vm.CardColorIndex);
        vm.CardColorIndex = 2;
        Assert.Equal(ShelfCardColor.Smoke, app.Settings.Current.ShelfCardColor);
    }

    [Fact]
    public void SameValue_IsNotSavedAgain()
    {
        var (app, vm, _) = Make();
        var saved = new List<string>();
        app.Settings.Changed += saved.Add;
        vm.Gapless = app.Settings.Current.Gapless;
        vm.WallpaperDim = app.Settings.Current.WallpaperDim;
        Assert.Empty(saved);
    }

    [Fact]
    public void LogoStyle_ClickingTheChosenOneShowsItChosenAgain()
    {
        var (app, vm, changed) = Make();
        vm.LogoStyle = AppLogoStyle.Simple;
        Assert.True(vm.IsLogoSimple);
        changed.Clear();
        vm.LogoStyle = AppLogoStyle.Simple;
        Assert.Contains(nameof(vm.IsLogoSimple), changed);
        Assert.Equal(AppLogoStyle.Simple, app.Settings.Current.AppLogoStyle);
    }

    [Fact]
    public void BlurStyle_IsSavedAndDescribed()
    {
        var (app, vm, _) = Make();
        vm.BlurStyleIndex = (int)BlurStyle.Lens;
        Assert.Equal(BlurStyle.Lens, app.Settings.Current.WallpaperBlurStyle);
        Assert.Contains("lens", vm.BlurStyleDescription);
    }

    [Fact]
    public void Crossfade_Text()
    {
        var (_, vm, _) = Make();
        vm.CrossfadeSeconds = 2.5;
        Assert.Equal("2.5 s", vm.CrossfadeText);
        vm.CrossfadeSeconds = 0;
        Assert.Equal("Off", vm.CrossfadeText);
    }

    [Fact]
    public void ChangedElsewhere_PageReadsEverythingAgain()
    {
        var (app, vm, changed) = Make();
        vm.Attach();
        app.Settings.Set(nameof(AppSettings.GreetingStyle), s => s.GreetingStyle = GreetingStyle.None);
        Assert.Contains("", changed);
        Assert.Equal((int)GreetingStyle.None, vm.GreetingStyleIndex);
        vm.Detach();
        changed.Clear();
        app.Settings.Set(nameof(AppSettings.GreetingStyle), s => s.GreetingStyle = GreetingStyle.Banner);
        Assert.Empty(changed);
    }

    [Fact]
    public async Task FetchPlan_CountsIncompleteAndEditedSongs()
    {
        var (app, vm, _) = Make();
        var edited = TestApp.Song("Edited"); edited.MetaState = ItemState.EditedByUser;
        var tooShort = TestApp.Song("Jingle"); tooShort.DurationMs = 10_000; // not checked online
        await app.LoadSongsAsync(TestApp.Song("New"), edited, tooShort);
        var (pending, editedCount) = vm.FetchPlan();
        Assert.Equal(2, pending);
        Assert.Equal(1, editedCount);
    }

    [Fact]
    public void Summaries_UseTheRightPlural()
    {
        var (_, vm, _) = Make();
        Assert.Equal("0 songs", vm.ScanStatus);
        Assert.Equal("None", vm.RemovedText);
    }

    [Fact]
    public void Wallpaper_IsCopiedIntoTheDataFolderAndShown()
    {
        var (app, vm, _) = Make();
        var picked = Path.Combine(app.Folder, "picked.png");
        File.WriteAllBytes(picked, new byte[] { 1, 2, 3 });
        vm.UseWallpaper(picked);
        Assert.Equal(BackgroundMode.Wallpaper, app.Settings.Current.BackgroundMode);
        Assert.NotEqual(picked, app.Settings.Current.WallpaperPath);
        Assert.True(File.Exists(app.Settings.Current.WallpaperPath));
        Assert.Equal(app.Settings.Current.WallpaperPath, vm.WallpaperPath);
    }
}

/// <summary>Home's logic, through its view model.</summary>
public class HomeViewModelTests
{
    private static HomeViewModel Make(TestApp app, int hour = 9) =>
        new(app.Library, app.Settings, app.Favorites, app.Continue, app.Player, () => new DateTime(2026, 1, 1, hour, 0, 0));

    [Fact]
    public void EmptyLibrary_Welcomes()
    {
        var vm = Make(new TestApp());
        vm.Refresh();
        Assert.False(vm.HasMusic);
        Assert.True(vm.ShowWelcome);
        Assert.Equal("Your library is empty", vm.Stats);
        Assert.Equal("No Songs Found", vm.WelcomeTitle); // the (test) Music folder is in the library already
        Assert.Empty(vm.Quick);
    }

    [Theory]
    [InlineData(9, "Good Morning", 0xE706)]
    [InlineData(14, "Good Afternoon", 0xE706)]
    [InlineData(17, "Good Evening", 0xE708)]
    [InlineData(21, "Good Evening", 0xE708)]
    [InlineData(4, "Good Evening", 0xE708)]
    public void Greeting_FollowsTheTimeOfDay(int hour, string greeting, int glyph)
    {
        var vm = Make(new TestApp(), hour);
        Assert.Equal(greeting, vm.Greeting);
        Assert.Equal(((char)glyph).ToString(), vm.GreetingGlyph);
    }

    [Fact]
    public async Task Stats_OneAlbumAndArtist_AreSingular()
    {
        var app = new TestApp();
        await app.LoadSongsAsync(TestApp.Song("A"), TestApp.Song("B"));
        var vm = Make(app);
        vm.Refresh();
        Assert.StartsWith("2 songs · 1 album · 1 artist · ", vm.Stats.Replace(' ', ' '));
    }

    [Fact]
    public async Task QuickAccess_PinnedFirst_AtMostTen()
    {
        var app = new TestApp();
        var songs = Enumerable.Range(1, 15).Select(i => TestApp.Song($"Song {i}", artist: $"Artist {i % 4}")).ToArray();
        var loaded = await app.LoadSongsAsync(songs);
        var pinned = loaded.Single(t => t.Title == "Song 7");
        app.Favorites.SetPinned(FavoritesService.Key(pinned), true);
        var vm = Make(app);
        vm.Refresh();
        Assert.InRange(vm.Quick.Count, 1, HomeViewModel.QuickAccessSize);
        Assert.Same(pinned, vm.Quick[0].Track);
        Assert.True(vm.Quick[0].IsPinned);
        Assert.Equal(vm.Quick.Count, vm.Quick.Select(q => q.Key).Distinct().Count());
        // Random picks after the pin: 2 artists and 3 songs.
        Assert.Equal(2, vm.Quick.Count(q => q.IsArtist));
        Assert.Equal(6, vm.Quick.Count);
    }

    [Fact]
    public async Task QuickAccess_KeepsItsPicksBetweenRefreshes()
    {
        var app = new TestApp();
        await app.LoadSongsAsync(Enumerable.Range(1, 30).Select(i => TestApp.Song($"S{i}", artist: $"A{i}")).ToArray());
        var vm = Make(app);
        vm.Refresh();
        var first = vm.Quick.Select(q => q.Key).ToList();
        vm.Refresh();
        Assert.Equal(first, vm.Quick.Select(q => q.Key).ToList());
    }

    [Fact]
    public async Task Playlists_SortedLikeFileExplorer_EmptyOnesHidden()
    {
        var app = new TestApp();
        var songs = await app.LoadSongsAsync(TestApp.Song("x"), TestApp.Song("y"));
        app.Library.CreatePlaylist("Mix 10", tracks: songs);
        app.Library.CreatePlaylist("Mix 2", tracks: songs);
        app.Library.CreatePlaylist("Empty");
        var vm = Make(app);
        vm.PlaylistSortIndex = (int)PlaylistSort.Alphabetical;
        vm.Refresh();
        Assert.Equal(new[] { "Mix 2", "Mix 10" }, vm.Playlists.Select(p => p.Name));
        Assert.Equal("2 playlists", vm.PlaylistCountText);
        Assert.All(vm.Playlists, p => Assert.False(p.CanMove));
    }

    [Fact]
    public async Task CustomOrder_StartsFromWhatIsShown_AndMoves()
    {
        var app = new TestApp();
        var songs = await app.LoadSongsAsync(TestApp.Song("x"));
        foreach (var name in new[] { "A", "B", "C" }) app.Library.CreatePlaylist(name, tracks: songs);
        var vm = Make(app);
        vm.PlaylistSortIndex = (int)PlaylistSort.Alphabetical;
        vm.Refresh();
        vm.PlaylistSortIndex = (int)PlaylistSort.Custom;
        Assert.Equal(new[] { "A", "B", "C" }, vm.Playlists.Select(p => p.Name));
        Assert.All(vm.Playlists, p => Assert.True(p.CanMove));

        vm.MovePlaylist(vm.Playlists[2], -1);
        Assert.Equal(new[] { "A", "C", "B" }, vm.Playlists.Select(p => p.Name));
        vm.MovePlaylist(vm.Playlists[0], -1); // already first: stays
        Assert.Equal(new[] { "A", "C", "B" }, vm.Playlists.Select(p => p.Name));
    }

    [Fact]
    public async Task OnePlaylist_IsSingular()
    {
        var app = new TestApp();
        var songs = await app.LoadSongsAsync(TestApp.Song("x"));
        app.Library.CreatePlaylist("Only", tracks: songs);
        var vm = Make(app);
        vm.Refresh();
        Assert.Equal("1 playlist", vm.PlaylistCountText);
    }

    [Fact]
    public async Task Refresh_UpdatesRowsInPlace()
    {
        var app = new TestApp();
        await app.LoadSongsAsync(TestApp.Song("x"), TestApp.Song("y"));
        var vm = Make(app);
        vm.Refresh();
        var row = vm.RecentlyAdded;
        Assert.Equal(2, row.Count);
        vm.Refresh();
        Assert.Same(row, vm.RecentlyAdded);
    }
}

/// <summary>Now Playing's lyrics, timing and details, through its view model.</summary>
public class NowPlayingViewModelTests
{
    private static readonly List<LyricLine> Lines = new()
    {
        new(TimeSpan.FromSeconds(1), "one"), new(TimeSpan.FromSeconds(3), "two"), new(TimeSpan.FromSeconds(5), "three"),
    };

    [Theory]
    [InlineData(0.5, -1)]
    [InlineData(1, 0)]
    [InlineData(2.9, 0)]
    [InlineData(3, 1)]
    [InlineData(60, 2)]
    public void LineAt_LastLineStarted(double seconds, int expected) =>
        Assert.Equal(expected, NowPlayingViewModel.LineAt(Lines, TimeSpan.FromSeconds(seconds)));

    private static async Task<NowPlayingViewModel> WithSyncedLyrics()
    {
        var app = new TestApp();
        var song = TestApp.Song("Synced");
        song.Lyrics = "[00:01.00]one\n[00:03.00]two\n[00:05.00]three";
        var vm = new NowPlayingViewModel(app.Lyrics, app.Library, app.Ui);
        await vm.ShowAsync(song);
        return vm;
    }

    [Fact]
    public async Task ActiveLine_LightsUpJustBeforeItsTime()
    {
        var vm = await WithSyncedLyrics();
        Assert.False(vm.UpdateActiveLine(TimeSpan.FromSeconds(0.5)));
        Assert.True(vm.UpdateActiveLine(TimeSpan.FromSeconds(3) - NowPlayingViewModel.Lead));
        Assert.Equal(1, vm.ActiveLine);
        Assert.False(vm.UpdateActiveLine(TimeSpan.FromSeconds(4))); // same line: nothing to move
    }

    [Fact]
    public async Task Offset_ShowsLinesLater_AndIsClamped()
    {
        var vm = await WithSyncedLyrics();
        vm.SetOffset(1000); // a second later
        vm.UpdateActiveLine(TimeSpan.FromSeconds(3.5));
        Assert.Equal(0, vm.ActiveLine);
        Assert.Equal("Timing +1.00 s", vm.TimingText);
        Assert.Equal(4.0, vm.SeekSecondsFor(1));

        vm.SetOffset(99_000);
        Assert.Equal(NowPlayingViewModel.MaxOffsetMs, vm.OffsetMs);
        vm.NudgeOffset(-NowPlayingViewModel.MaxOffsetMs);
        Assert.Equal("Timing", vm.TimingText);
    }

    [Fact]
    public async Task NoLyrics_SaysSo()
    {
        var app = new TestApp();
        var vm = new NowPlayingViewModel(app.Lyrics, app.Library, app.Ui);
        await vm.ShowAsync(TestApp.Song("Quiet"));
        Assert.False(vm.HasLyrics);
        Assert.Equal("Lyrics unavailable", vm.Message);
        Assert.Equal("", vm.SourceText);
    }

    [Fact]
    public void Details_HaveEveryRow()
    {
        Assert.Empty(NowPlayingViewModel.DetailsOf(null));
        var rows = NowPlayingViewModel.DetailsOf(TestApp.Song("Title here"));
        Assert.Equal("Title here", rows.Single(r => r.Label == "Title").Value);
        Assert.Equal("—", rows.Single(r => r.Label == "Year").Value);
        Assert.Equal(15, rows.Count);
    }
}

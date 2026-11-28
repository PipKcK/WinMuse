using WinMuse.Helpers;
using WinMuse.ViewModels;
using Xunit;

namespace WinMuse.Tests;

/// <summary>
/// Lyrics typed or pasted in the editor ran together into one line: the text box separates lines with a lone CR,
/// which the parser didn't treat as a line break.
/// </summary>
public class LyricsLineBreakTests
{
    [Theory]
    [InlineData("one\rtwo\rthree")]
    [InlineData("one\r\ntwo\r\nthree")]
    [InlineData("one\ntwo\nthree")]
    [InlineData("one\r\ntwo\rthree")]
    public void PlainLyrics_AnyLineBreak_GiveOneLineEach(string text)
    {
        var r = LrcParser.Parse(text);
        Assert.False(r.IsSynced);
        Assert.Equal(new[] { "one", "two", "three" }, r.Lines.Select(l => l.Text));
    }

    [Fact]
    public void SyncedLyrics_FromTheTextBox_GiveOneLineEach()
    {
        var r = LrcParser.Parse("[00:01.00]one\r[00:02.50]two\r[00:04.00]three");
        Assert.True(r.IsSynced);
        Assert.Equal(new[] { "one", "two", "three" }, r.Lines.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromSeconds(2.5), r.Lines[1].Time);
    }

    [Fact]
    public void NormalizeLineBreaks_ToWindowsBreaks()
    {
        Assert.Equal("a\r\nb\r\nc\r\nd", LrcParser.NormalizeLineBreaks("a\rb\nc\r\nd", "\r\n"));
        Assert.Equal("a\nb\nc", LrcParser.NormalizeLineBreaks("a\r\nb\rc"));
    }

    [Fact]
    public async Task NowPlaying_ShowsEditedLyrics_LineByLine()
    {
        var app = new TestApp();
        var song = TestApp.Song("Edited");
        song.Lyrics = "[00:01.00]first\r[00:02.00]second\r[00:03.00]third";
        var vm = new NowPlayingViewModel(app.Lyrics, app.Library, app.Ui);
        int rebuilt = 0;
        vm.LinesChanged += () => rebuilt++;

        await vm.ShowAsync(song);

        Assert.True(vm.IsSynced);
        Assert.Equal(3, vm.Lyrics!.Lines.Count);
        Assert.Equal("Synced lyrics · Embedded", vm.SourceText);
        Assert.True(rebuilt >= 1);
    }

    [Fact]
    public async Task NowPlaying_ShowsLyricsEditedElsewhere_AndIgnoresUnrelatedChanges()
    {
        var app = new TestApp();
        var song = TestApp.Song("Edited elsewhere");
        song.Lyrics = "[00:01.00]only line";
        var vm = new NowPlayingViewModel(app.Lyrics, app.Library, app.Ui);
        await vm.ShowAsync(song);
        int rebuilt = 0;
        vm.LinesChanged += () => rebuilt++;

        await vm.RefreshIfChangedAsync(); // nothing changed
        Assert.Equal(0, rebuilt);

        app.Lyrics.Save(song, LrcParser.NormalizeLineBreaks("[00:01.00]one\r[00:02.00]two\r[00:03.00]three", "\r\n"));
        try
        {
            await vm.RefreshIfChangedAsync();
            Assert.Equal(1, rebuilt);
            Assert.Equal(3, vm.Lyrics!.Lines.Count);
        }
        finally { File.Delete(WinMuse.Services.LyricsService.AppLyricsPath(song.Path)); }
    }
}

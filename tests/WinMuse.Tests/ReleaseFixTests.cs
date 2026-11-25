using System.Net.Http;
using Windows.UI;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;
using Xunit;

namespace WinMuse.Tests;

/// <summary>Missing songs keep their playlist places when the playlist is edited while they are hidden.</summary>
public class PlaylistOrderTests
{
    private static List<long> Keep(long[] saved, long[] shown, params long[] missing) =>
        PlaylistOrder.KeepMissingPlaces(saved, shown, missing.ToHashSet());

    [Fact] public void NothingMissing_IsTheShownList() => Assert.Equal(new long[] { 3, 1, 2 }, Keep(new long[] { 1, 2, 3 }, new long[] { 3, 1, 2 }));
    [Fact] public void Unchanged_ListIsRestored() => Assert.Equal(new long[] { 1, 9, 2, 3 }, Keep(new long[] { 1, 9, 2, 3 }, new long[] { 1, 2, 3 }, 9));
    [Fact] public void FirstSongRemoved_MissingSongStaysFirst() => Assert.Equal(new long[] { 9, 2, 3 }, Keep(new long[] { 1, 9, 2, 3 }, new long[] { 2, 3 }, 9));
    [Fact] public void MissingAtStart_StaysAtStartInOrder() => Assert.Equal(new long[] { 8, 9, 1, 2 }, Keep(new long[] { 8, 9, 1, 2 }, new long[] { 1, 2 }, 8, 9));
    [Fact] public void FollowsItsNeighbourWhenReordered() => Assert.Equal(new long[] { 2, 1, 9 }, Keep(new long[] { 1, 9, 2 }, new long[] { 2, 1 }, 9));
    [Fact] public void RunAfterSameSong_KeepsOrder() => Assert.Equal(new long[] { 1, 7, 8, 2 }, Keep(new long[] { 1, 7, 8, 2 }, new long[] { 1, 2 }, 7, 8));
    [Fact] public void MissingAtEnd_StaysAtEnd() => Assert.Equal(new long[] { 1, 2, 9 }, Keep(new long[] { 1, 2, 9 }, new long[] { 1, 2 }, 9));
    [Fact] public void AllShownRemoved_MissingRemain() => Assert.Equal(new long[] { 9 }, Keep(new long[] { 1, 9 }, Array.Empty<long>(), 9));
}

/// <summary>Only web links reach the downloader.</summary>
public class WebLinkTests
{
    [Theory]
    [InlineData("https://example.com/watch?v=1", "https://example.com/watch?v=1")]
    [InlineData("  http://example.com/a  ", "http://example.com/a")]
    [InlineData("--exec calc", null)]
    [InlineData("-o C:\\x", null)]
    [InlineData("file:///C:/Windows/notepad.exe", null)]
    [InlineData("example.com/song", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Clean(string? text, string? expected) => Assert.Equal(expected, WebLink.Clean(text));
}

/// <summary>Errors are described in plain words, never with the exception's own message.</summary>
public class ErrorTextTests
{
    [Fact] public void Network_IsPlain() => Assert.Contains("internet connection", ErrorText.Describe(new HttpRequestException("No such host is known. (api.example:443)")));
    [Fact] public void FileInUse_IsPlain() => Assert.Contains("open in another app", ErrorText.Describe(new IOException("x", unchecked((int)0x80070020))));
    [Fact] public void Unknown_DoesNotLeakMessage() => Assert.DoesNotContain("Object reference", ErrorText.Describe(new NullReferenceException("Object reference not set")));
    [Fact] public void Aggregate_IsUnwrapped() => Assert.Contains("Access was denied", ErrorText.Describe(new AggregateException(new UnauthorizedAccessException())));
    [Fact] public void Bug_IsNotRecoverable() => Assert.False(ErrorText.IsRecoverable(new NullReferenceException()));
    [Fact] public void FileError_IsRecoverable() => Assert.True(ErrorText.IsRecoverable(new FileNotFoundException()));
}

/// <summary>Genre tiles: the text color with the higher contrast wins (WCAG formula).</summary>
public class ContrastTests
{
    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    [Fact] public void BlackOnWhite_Is21() => Assert.Equal(21, ColorKit.Contrast(Rgb(0, 0, 0), Rgb(255, 255, 255)), 1);
    [Fact] public void YellowGreen_GetsDarkText() => Assert.True(ColorKit.PrefersDarkText(Rgb(0xB5, 0xD9, 0x4C)));
    [Fact] public void DeepBlue_GetsWhiteText() => Assert.False(ColorKit.PrefersDarkText(Rgb(0x2B, 0x3F, 0xC0)));

    [Fact]
    public void EveryGenreHue_ReachesLargeTextContrast()
    {
        // Genre names are 20 px bold (WCAG "large text": 3:1). Whichever text color is picked must reach it on every hue.
        for (int hue = 0; hue < 360; hue += 5)
        {
            var bg = ColorKit.FromHsl(hue, 0.62, 0.55);
            var text = ColorKit.PrefersDarkText(bg) ? ColorKit.DarkText : Rgb(255, 255, 255);
            Assert.True(ColorKit.Contrast(text, bg) >= 3, $"hue {hue}: {ColorKit.Contrast(text, bg):F2}");
        }
    }
}

/// <summary>The log rolls over at 1 MB and never keeps more than five files.</summary>
public class AppLogTests
{
    [Fact]
    public void RollsOver_AndKeepsFiveFiles()
    {
        var dir = Directory.CreateTempSubdirectory("winmuse-log").FullName;
        var before = AppLog.Path;
        try
        {
            AppLog.Path = Path.Combine(dir, "log.txt");
            var line = new string('x', 1000);
            for (int i = 0; i < 7_000; i++) AppLog.Write(line); // about 7 MB
            var files = Directory.GetFiles(dir).Select(Path.GetFileName).Order().ToArray();
            Assert.Equal(new[] { "log.1.txt", "log.2.txt", "log.3.txt", "log.4.txt", "log.txt" }, files);
            Assert.All(Directory.GetFiles(dir), f => Assert.True(new FileInfo(f).Length <= AppLog.MaxBytes + 2048));
        }
        finally { AppLog.Path = before; Directory.Delete(dir, true); }
    }

    [Fact]
    public void ParallelWrites_LoseNoLines()
    {
        var dir = Directory.CreateTempSubdirectory("winmuse-log").FullName;
        var before = AppLog.Path;
        try
        {
            AppLog.Path = Path.Combine(dir, "log.txt");
            Parallel.For(0, 2000, i => AppLog.Write($"line {i}"));
            Assert.Equal(2000, File.ReadAllLines(AppLog.Path).Count(l => l.Contains("] line ")));
        }
        finally { AppLog.Path = before; Directory.Delete(dir, true); }
    }
}

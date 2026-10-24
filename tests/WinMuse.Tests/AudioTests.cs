using System.Diagnostics;
using WinMuse.Services.Audio;
using Xunit;
using Xunit.Abstractions;

namespace WinMuse.Tests;

/// <summary>
/// Decoding real songs (MUSIC_TEST_DIR): every file opens and decodes, decode-ahead gives exactly the same audio as
/// direct decoding, seeking lands where asked, and the gapless pipeline joins songs without losing samples.
/// </summary>
public class AudioTests
{
    private const int Rate = 48000;
    private readonly ITestOutputHelper _out;
    public AudioTests(ITestOutputHelper output) => _out = output;

    private static float[] ReadAll(FFmpegDecoder d, int maxSamples)
    {
        var all = new List<float>();
        var buf = new float[4800];
        int n;
        while (all.Count < maxSamples && (n = d.Read(buf, 0, buf.Length)) > 0) all.AddRange(buf.Take(n));
        return all.Take(maxSamples).ToArray();
    }

    [Fact]
    public void EverySong_OpensAndDecodes()
    {
        var songs = TestSupport.Songs();
        if (songs.Count == 0) return; // no MUSIC_TEST_DIR
        var failed = new List<string>();
        var sw = Stopwatch.StartNew();
        double audioSeconds = 0;
        foreach (var path in songs)
        {
            try
            {
                using var d = new FFmpegDecoder(path, Rate);
                var samples = ReadAll(d, Rate * 2 * 3); // first 3 s
                if (samples.Length == 0 || d.Duration <= TimeSpan.Zero) failed.Add($"{Path.GetFileName(path)}: no audio");
                audioSeconds += samples.Length / 2.0 / Rate;
            }
            catch (Exception ex) { failed.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        _out.WriteLine($"{songs.Count} songs, {failed.Count} failed, decode speed {audioSeconds / sw.Elapsed.TotalSeconds:0}x real time");
        foreach (var f in failed) _out.WriteLine("FAILED " + f);
        Assert.Empty(failed);
    }

    [Fact]
    public void DecodeAhead_IsBitIdentical()
    {
        foreach (var path in TestSupport.Songs(8))
        {
            using var direct = new FFmpegDecoder(path, Rate);
            using var ahead = new FFmpegDecoder(path, Rate);
            ahead.StartPrefetch();
            Thread.Sleep(50); // let the ring fill partly
            var a = ReadAll(direct, Rate * 2 * 20);
            var b = ReadAll(ahead, Rate * 2 * 20);
            Assert.Equal(a.Length, b.Length);
            Assert.True(a.AsSpan().SequenceEqual(b), Path.GetFileName(path));
        }
    }

    [Fact]
    public void Seek_LandsOnTarget_WithDecodeAhead()
    {
        foreach (var path in TestSupport.Songs(5))
        {
            using var d = new FFmpegDecoder(path, Rate);
            if (d.Duration < TimeSpan.FromSeconds(40)) continue;
            d.StartPrefetch();
            ReadAll(d, Rate * 2 * 2);
            d.Seek(TimeSpan.FromSeconds(30));
            Assert.InRange(d.Position.TotalSeconds, 29.99, 30.01);
            var after = ReadAll(d, Rate * 2);
            Assert.Equal(Rate * 2, after.Length);
            Assert.InRange(d.Position.TotalSeconds, 30.99, 31.01);

            // Same audio as a fresh decoder seeking to the same place (no leftovers from before the seek).
            using var fresh = new FFmpegDecoder(path, Rate);
            fresh.Seek(TimeSpan.FromSeconds(30));
            var expected = ReadAll(fresh, Rate * 2);
            Assert.True(expected.AsSpan().SequenceEqual(after), Path.GetFileName(path));
        }
    }

    [Fact]
    public void Seek_WhileReading_FromAnotherThread_DoesNotThrow()
    {
        var path = TestSupport.Songs(1).FirstOrDefault();
        if (path == null) return;
        using var d = new FFmpegDecoder(path, Rate);
        d.StartPrefetch();
        var stop = DateTime.UtcNow.AddSeconds(3);
        var reader = Task.Run(() => { var buf = new float[4096]; while (DateTime.UtcNow < stop) if (d.Read(buf, 0, buf.Length) == 0) d.Seek(TimeSpan.Zero); });
        var rng = new Random(7);
        while (DateTime.UtcNow < stop) { d.Seek(TimeSpan.FromSeconds(rng.NextDouble() * Math.Max(1, d.Duration.TotalSeconds - 5))); Thread.Sleep(5); }
        reader.Wait();
    }

    [Fact]
    public void Dispose_WhilePrefetching_IsSafe()
    {
        foreach (var path in TestSupport.Songs(3))
            for (int i = 0; i < 20; i++)
            {
                var d = new FFmpegDecoder(path, Rate);
                d.StartPrefetch();
                Thread.Sleep(i % 5);
                d.Dispose();
            }
    }

    [Fact]
    public void Pipeline_GaplessJoin_KeepsEverySample()
    {
        var songs = TestSupport.Songs(2);
        if (songs.Count < 2) return;
        var p = new PlaybackPipeline(Rate);
        p.Volume = 1;
        var a = new FFmpegDecoder(songs[0], Rate); a.Seek(a.Duration - TimeSpan.FromSeconds(2));
        var b = new FFmpegDecoder(songs[1], Rate);
        long expected = 0;
        using (var probe = new FFmpegDecoder(songs[0], Rate)) { probe.Seek(probe.Duration - TimeSpan.FromSeconds(2)); expected = ReadAll(probe, int.MaxValue).Length; }
        p.SetCurrent(a); p.SetNext(b);
        bool advanced = false;
        p.Advanced += _ => advanced = true;
        var buf = new float[960];
        long total = 0;
        while (!advanced) total += p.Read(buf, 0, buf.Length);
        // The buffer that crossed into song B holds the end of A and the start of B together.
        Assert.True(advanced);
        Assert.InRange(total - expected, 0, buf.Length);
        Assert.Same(b, p.Current);
        b.Dispose();
    }

    [Fact]
    public void Prefetch_CpuIsModest()
    {
        var path = TestSupport.Songs(1).FirstOrDefault();
        if (path == null) return;
        var proc = Process.GetCurrentProcess();
        var cpu0 = proc.TotalProcessorTime;
        var sw = Stopwatch.StartNew();
        using var d = new FFmpegDecoder(path, Rate);
        d.StartPrefetch();
        // Consume in real time for 3 s, like the sound card would.
        var buf = new float[Rate * 2 / 10];
        for (int i = 0; i < 30; i++) { d.Read(buf, 0, buf.Length); Thread.Sleep(100); }
        proc.Refresh();
        var cpuPct = (proc.TotalProcessorTime - cpu0).TotalMilliseconds / sw.Elapsed.TotalMilliseconds / Environment.ProcessorCount * 100;
        _out.WriteLine($"decode-ahead CPU: {cpuPct:0.00}% of the machine");
        Assert.True(cpuPct < 2, $"{cpuPct:0.00}%");
    }
}

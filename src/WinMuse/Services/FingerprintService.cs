using System.Text;
using AcoustID;
using WinMuse.Models;
using WinMuse.Services.Audio;

namespace WinMuse.Services;

/// <summary>Computes Chromaprint fingerprints by decoding audio through FFmpeg.</summary>
public sealed class FingerprintService
{
    public Task<(string fingerprint, int durationSec)> ComputeAsync(string path, CancellationToken ct = default) => Task.Run(() =>
    {
        const int rate = 11025;
        // Background work: playback (decode-ahead and audio threads) comes first.
        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        thread.Priority = ThreadPriority.BelowNormal;
        try { return Compute(path, rate, ct); }
        finally { thread.Priority = priority; }
    }, ct);

    private static (string fingerprint, int durationSec) Compute(string path, int rate, CancellationToken ct)
    {
        using var dec = new FFmpegDecoder(path, rate, 1);
        var ctx = new ChromaContext();
        ctx.Start(rate, 1);
        var f = new float[rate];
        var s = new short[rate];
        int total = 0, max = rate * 120;
        while (total < max)
        {
            ct.ThrowIfCancellationRequested();
            int n = dec.Read(f, 0, f.Length);
            if (n <= 0) break;
            for (int i = 0; i < n; i++) s[i] = (short)Math.Clamp(f[i] * 32767f, short.MinValue, short.MaxValue);
            ctx.Feed(s, n);
            total += n;
        }
        ctx.Finish();
        var dur = (int)Math.Round(dec.Duration.TotalSeconds > 0 ? dec.Duration.TotalSeconds : total / (double)rate);
        return (ctx.GetFingerprint(), dur);
    }
}

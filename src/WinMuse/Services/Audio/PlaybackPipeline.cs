using NAudio.Wave;

namespace WinMuse.Services.Audio;

/// <summary>
/// The sample source fed to WASAPI. Holds the current and the pre-opened next decoder so tracks join gaplessly,
/// optionally crossfades between them, and applies EQ, volume and spectrum capture.
/// </summary>
public sealed class PlaybackPipeline : ISampleProvider
{
    private readonly object _lock = new();
    private volatile FFmpegDecoder? _current;
    private FFmpegDecoder? _next;
    private float[] _mix = new float[8192];
    private float _gain;
    private bool _fading;
#if DEBUG
    private long _lastRead; private int _lastGcs;
#endif

    public WaveFormat WaveFormat { get; }
    public Equalizer Equalizer { get; }
    public SpectrumAnalyzer Spectrum { get; }

    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }
    public double CrossfadeSeconds { get; set; }

    /// <summary>Raised on the audio thread when playback moved on to the queued next decoder.</summary>
    public event Action<FFmpegDecoder>? Advanced;
    /// <summary>Raised on the audio thread when the current decoder ended and nothing was queued.</summary>
    public event Action<FFmpegDecoder>? Ended;

    public PlaybackPipeline(int sampleRate, int channels = 2)
    {
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        Equalizer = new Equalizer(sampleRate, channels);
        Spectrum = new SpectrumAnalyzer(sampleRate, channels);
    }

    // Lock-free so the UI never waits on the audio thread while it decodes.
    public FFmpegDecoder? Current => _current;

    public TimeSpan Position => _current?.Position ?? TimeSpan.Zero;

    public void SetCurrent(FFmpegDecoder? decoder)
    {
        FFmpegDecoder? oldCur = null, oldNext = null;
        // Called on the UI thread: wait without message pumping (see Helpers/UiLock).
        Helpers.UiLock.Run(_lock, () =>
        {
            oldCur = _current; oldNext = _next;
            _current = decoder; _next = null; _fading = false;
        });
        oldCur?.Dispose();
        if (oldNext != decoder) oldNext?.Dispose();
        Spectrum.Clear();
    }

    public void SetNext(FFmpegDecoder? decoder)
    {
        FFmpegDecoder? old = null;
        Helpers.UiLock.Run(_lock, () => { old = _next; _next = decoder; });
        if (old != null && old != decoder) old.Dispose();
    }

    public void Seek(TimeSpan position)
    {
        Helpers.UiLock.Run(_lock, () =>
        {
            _current?.Seek(position);
            if (_fading) { _fading = false; _next?.Seek(TimeSpan.Zero); }
        });
    }

    public int Read(float[] buffer, int offset, int count)
    {
        AudioThread.Register(); // the device's render thread: once
#if DEBUG
        // Test aid: a long gap between the device's requests means this thread was held up (audible as a stutter).
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        int gcs = GC.CollectionCount(0) + GC.CollectionCount(1) * 100 + GC.CollectionCount(2) * 10000;
        if (_lastRead != 0)
        {
            double ms = (now - _lastRead) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (ms > 50)
            {
                var full = GC.GetGCMemoryInfo(GCKind.FullBlocking);
                var any = GC.GetGCMemoryInfo(GCKind.Any);
                double askedMs = count / (double)WaveFormat.Channels / WaveFormat.SampleRate * 1000;
                Helpers.AppPaths.Log($"Render gap {ms:0} ms, refill {askedMs:0} ms of audio (gen0/1/2 since last: {(gcs - _lastGcs) % 100}/{(gcs - _lastGcs) / 100 % 100}/{(gcs - _lastGcs) / 10000}); " +
                    $"last GC #{any.Index} gen{any.Generation} pause {any.PauseDurations[0].TotalMilliseconds:0}ms compacted={any.Compacted} concurrent={any.Concurrent}; " +
                    $"last full blocking #{full.Index} pause {full.PauseDurations[0].TotalMilliseconds:0}ms; heap {any.HeapSizeBytes / 1048576} MB, LOH alloc'd {GC.GetTotalAllocatedBytes() / 1048576} MB total");
            }
        }
        _lastRead = now; _lastGcs = gcs;
#endif
        FFmpegDecoder? advancedTo = null, endedFrom = null;
        int channels = WaveFormat.Channels;

        lock (_lock)
        {
            if (_current == null)
            {
                Array.Clear(buffer, offset, count);
            }
            else
            {
                int got = ReadCurrent(buffer, offset, count, channels, ref advancedTo);
                if (got < count)
                {
                    // Current track finished inside this buffer.
                    if (_next != null)
                    {
                        var finished = _current;
                        _current = _next; _next = null; _fading = false;
                        advancedTo = _current;
                        finished.Dispose();
                        int more = _current.Read(buffer, offset + got, count - got);
                        got += more;
                    }
                    else if (_current.IsFinished)
                    {
                        endedFrom = _current;
                        _current = null;
                    }
                    if (got < count) Array.Clear(buffer, offset + got, count - got);
                }
            }
        }

        Equalizer.Process(buffer, offset, count);

        // Smooth volume changes per sample to avoid zipper noise.
        float target = Muted ? 0f : Volume * Volume; // perceptual curve
        float g = _gain;
        float step = (target - g) / Math.Max(1, count / channels);
        for (int i = 0; i < count; i += channels)
        {
            g += step;
            for (int c = 0; c < channels && i + c < count; c++) buffer[offset + i + c] *= g;
        }
        _gain = target;

        Spectrum.Write(buffer, offset, count);

        if (advancedTo != null) Advanced?.Invoke(advancedTo);
        if (endedFrom != null) { Ended?.Invoke(endedFrom); endedFrom.Dispose(); }
        return count;
    }

    /// <summary>Reads from the current decoder, mixing in the next one during a crossfade window.</summary>
    private int ReadCurrent(float[] buffer, int offset, int count, int channels, ref FFmpegDecoder? advancedTo)
    {
        var cur = _current!;
        double cf = CrossfadeSeconds;
        bool canFade = cf > 0.05 && _next != null && cur.Duration.TotalSeconds > cf * 2 && _next.Duration.TotalSeconds > cf * 2;

        if (!canFade) return cur.Read(buffer, offset, count);

        double remaining = (cur.Duration - cur.Position).TotalSeconds;
        if (!_fading && remaining > cf) return cur.Read(buffer, offset, count);

        _fading = true;
        if (_mix.Length < count) _mix = new float[count];
        int a = cur.Read(buffer, offset, count);
        if (a < count) Array.Clear(buffer, offset + a, count - a);
        int b = _next!.Read(_mix, 0, count);
        if (b < count) Array.Clear(_mix, b, count - b);

        double rate = WaveFormat.SampleRate;
        double fadePos = cf - remaining; // seconds into the fade
        for (int i = 0; i < count; i += channels)
        {
            double p = Math.Clamp((fadePos + (i / channels) / rate) / cf, 0, 1);
            float gOut = (float)Math.Cos(p * Math.PI / 2);
            float gIn = (float)Math.Sin(p * Math.PI / 2);
            for (int c = 0; c < channels; c++)
                buffer[offset + i + c] = buffer[offset + i + c] * gOut + _mix[i + c] * gIn;
        }

        if (a < count || cur.IsFinished || remaining <= 0.01)
        {
            // Fade complete: the next track becomes current, already advanced by what we mixed.
            var finished = cur;
            _current = _next; _next = null; _fading = false;
            advancedTo = _current;
            finished.Dispose();
        }
        return count;
    }
}

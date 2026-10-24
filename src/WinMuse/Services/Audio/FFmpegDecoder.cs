using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using WinMuse.Helpers;

namespace WinMuse.Services.Audio;

public static class FFmpegLoader
{
    private static bool _loaded;
    private static readonly object Gate = new();

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            ffmpeg.RootPath = AppPaths.FFmpegDir;
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
            _loaded = true;
        }
    }

    public static string Version
    {
        get { try { EnsureLoaded(); return ffmpeg.av_version_info(); } catch { return "unavailable"; } }
    }
}

/// <summary>Technical stream info and container tags read with FFmpeg.</summary>
public sealed class FFmpegProbe
{
    public TimeSpan Duration { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public int BitsPerSample { get; set; }
    public int Bitrate { get; set; }
    public string Codec { get; set; } = "";
    public Dictionary<string, string> Tags { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[]? AttachedPicture { get; set; }

    public string? Tag(params string[] keys)
    {
        foreach (var k in keys) if (Tags.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v.Trim();
        return null;
    }
}

/// <summary>
/// Decodes any FFmpeg-supported audio file into interleaved 32-bit float PCM at a fixed output rate / channel count.
/// Not thread-safe: the playback pipeline serialises access. For playback, <see cref="StartPrefetch"/> keeps a few
/// seconds decoded ahead on a dedicated thread, so the audio callback only copies samples.
/// </summary>
public sealed unsafe class FFmpegDecoder : IDisposable
{
    private AVFormatContext* _fmt;
    private AVCodecContext* _codec;
    private SwrContext* _swr;
    private AVPacket* _pkt;
    private AVFrame* _frame;
    private readonly int _stream;
    private readonly AVRational _timeBase;
    private readonly long _startTime;

    private int _swrInFormat = -1, _swrInRate = -1, _swrInChannels = -1;
    private float[] _buf = new float[16384];
    private int _bufStart, _bufCount;
    private bool _inputEof, _done, _flushSent, _packetPending;
    private long _framesOut; // read lock-free from the UI thread (64-bit reads are atomic on x64/ARM64)
    private double _seekTarget = -1;

    public string Path { get; }
    public int OutputRate { get; }
    public int OutputChannels { get; }
    public TimeSpan Duration { get; }
    public int SourceSampleRate { get; }
    public int SourceChannels { get; }
    public string CodecName { get; }
    public object? Tag { get; set; }

    public TimeSpan Position => TimeSpan.FromSeconds((double)_framesOut / OutputRate);
    public bool IsFinished => _done && _bufCount == 0 && _ringCount == 0;

    public FFmpegDecoder(string path, int outputRate, int outputChannels = 2)
    {
        FFmpegLoader.EnsureLoaded();
        Path = path;
        OutputRate = outputRate;
        OutputChannels = outputChannels;

        AVFormatContext* fmt = null;
        Check(ffmpeg.avformat_open_input(&fmt, path, null, null), "open");
        _fmt = fmt;
        try
        {
            Check(ffmpeg.avformat_find_stream_info(_fmt, null), "stream info");
            AVCodec* codec = null;
            _stream = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
            if (_stream < 0 || codec == null) throw new InvalidDataException("No audio stream found.");

            for (int i = 0; i < _fmt->nb_streams; i++)
                if (i != _stream) _fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;

            var st = _fmt->streams[_stream];
            _timeBase = st->time_base;
            _startTime = st->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : st->start_time;

            _codec = ffmpeg.avcodec_alloc_context3(codec);
            Check(ffmpeg.avcodec_parameters_to_context(_codec, st->codecpar), "codec params");
            _codec->pkt_timebase = st->time_base;
            Check(ffmpeg.avcodec_open2(_codec, codec, null), "open codec");

            if (st->duration != ffmpeg.AV_NOPTS_VALUE && st->duration > 0)
                Duration = TimeSpan.FromSeconds(st->duration * ffmpeg.av_q2d(st->time_base));
            else if (_fmt->duration > 0)
                Duration = TimeSpan.FromSeconds(_fmt->duration / (double)ffmpeg.AV_TIME_BASE);

            SourceSampleRate = _codec->sample_rate;
            SourceChannels = _codec->ch_layout.nb_channels;
            CodecName = ffmpeg.avcodec_get_name(_codec->codec_id) ?? "";

            _pkt = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // ---------------- Decode-ahead ----------------
    // The audio device asks for the next ~90 ms on a deadline. Decoding inside that request missed it now and then
    // when the CPU was busy (a library-wide fetch fingerprints several songs at once), which was heard as small gaps.
    // A dedicated thread keeps the ring filled; Read only copies, and decodes inline only if the ring ran dry.
    // Lock order: _decodeLock, then _ringLock. Samples are only appended to the ring while holding _decodeLock,
    // so a seek (which clears the ring under _decodeLock) can never be followed by stale samples.

    private readonly object _decodeLock = new(), _ringLock = new();
    private float[]? _ring;
    private int _ringStart, _ringCount;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _disposed;

    /// <summary>Starts keeping <paramref name="seconds"/> of audio decoded ahead (playback decoders only).</summary>
    public void StartPrefetch(double seconds = 3)
    {
        if (_ring != null) return;
#if DEBUG
        if (Environment.GetEnvironmentVariable("WINMUSE_NO_PREFETCH") == "1") return; // test switch
#endif
        _ring = new float[Math.Max(8192, (int)(OutputRate * OutputChannels * seconds))];
        new Thread(PrefetchLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "Decode ahead" }.Start();
    }

    private void PrefetchLoop()
    {
        AudioThread.Register();
        var chunk = new float[4096 * OutputChannels];
        while (!_disposed)
        {
            bool filled = false;
            lock (_decodeLock)
            {
                if (_disposed) break;
                int free;
                lock (_ringLock) free = _ring!.Length - _ringCount;
                if (free >= chunk.Length && !(_done && _bufCount == 0))
                {
                    int n = ReadDecoded(chunk, 0, chunk.Length);
                    if (n > 0) { lock (_ringLock) RingWrite(chunk, n); filled = true; }
                }
            }
            if (!filled) _wake.WaitOne(25);
        }
    }

    private void RingWrite(float[] src, int n)
    {
        var ring = _ring!;
        int end = (_ringStart + _ringCount) % ring.Length;
        int first = Math.Min(n, ring.Length - end);
        Array.Copy(src, 0, ring, end, first);
        if (n > first) Array.Copy(src, first, ring, 0, n - first);
        _ringCount += n;
        _primed = true;
    }

    private bool _primed; // the ring has been filled at least once (running dry after that is an underrun)

    private int RingRead(float[] dst, int offset, int count)
    {
        var ring = _ring!;
        int n = Math.Min(count, _ringCount);
        int first = Math.Min(n, ring.Length - _ringStart);
        Array.Copy(ring, _ringStart, dst, offset, first);
        if (n > first) Array.Copy(ring, 0, dst, offset + first, n - first);
        _ringStart = (_ringStart + n) % ring.Length;
        _ringCount -= n;
        return n;
    }

    /// <summary>Reads up to <paramref name="count"/> interleaved samples. Returns fewer only at end of stream.</summary>
    public int Read(float[] buffer, int offset, int count)
    {
        int total;
        if (_ring == null)
        {
            total = ReadDecoded(buffer, offset, count);
        }
        else
        {
            lock (_ringLock) total = RingRead(buffer, offset, count);
            if (total < count && !_disposed)
            {
                // Ran dry (just after opening or seeking): take what was appended meanwhile, then decode inline.
#if DEBUG
                if (_primed && !_done) AppPaths.Log($"Decode-ahead ran dry ({total}/{count}) in {System.IO.Path.GetFileName(Path)}");
#endif
                lock (_decodeLock)
                {
                    lock (_ringLock) total += RingRead(buffer, offset + total, count - total);
                    if (total < count && !_disposed) total += ReadDecoded(buffer, offset + total, count - total);
                }
            }
            _wake.Set();
        }
        _framesOut += total / OutputChannels;
        return total;
    }

    private int ReadDecoded(float[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            if (_bufCount == 0)
            {
                if (_done || !DecodeMore()) break;
                continue;
            }
            int n = Math.Min(_bufCount, count - total);
            Array.Copy(_buf, _bufStart, buffer, offset + total, n);
            _bufStart += n; _bufCount -= n; total += n;
            if (_bufCount == 0) _bufStart = 0;
        }
        return total;
    }

    public void Seek(TimeSpan position)
    {
        lock (_decodeLock)
        {
            if (_disposed) return;
            SeekDecoder(position);
            lock (_ringLock) { _ringStart = _ringCount = 0; _primed = false; }
        }
        _wake.Set();
    }

    private void SeekDecoder(TimeSpan position)
    {
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        long ts = (long)(position.TotalSeconds / ffmpeg.av_q2d(_timeBase)) + _startTime;
        ffmpeg.av_seek_frame(_fmt, _stream, ts, ffmpeg.AVSEEK_FLAG_BACKWARD);
        ffmpeg.avcodec_flush_buffers(_codec);
        FreeSwr();
        _bufStart = _bufCount = 0;
        if (_packetPending) { ffmpeg.av_packet_unref(_pkt); _packetPending = false; }
        _inputEof = _done = _flushSent = false;
        _seekTarget = position.TotalSeconds;
        _framesOut = (long)(position.TotalSeconds * OutputRate);
    }

    private bool DecodeMore()
    {
        // Send/receive state machine. Every path either produces samples, feeds the decoder, or finishes;
        // the iteration cap is a last-resort guard against decoders that never signal EOF.
        for (int guard = 0; guard < 100_000; guard++)
        {
            int r = ffmpeg.avcodec_receive_frame(_codec, _frame);
            if (r == 0)
            {
                ConvertFrame(_frame);
                ffmpeg.av_frame_unref(_frame);
                if (_bufCount > 0) return true;
                continue;
            }
            if (r == ffmpeg.AVERROR_EOF) return Finish();
            if (r != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                // Corrupt data: skip ahead, or stop if we're already draining.
                if (_flushSent) return Finish();
            }

            // The decoder needs input.
            if (_flushSent) return Finish();
            if (_packetPending)
            {
                if (ffmpeg.avcodec_send_packet(_codec, _pkt) == ffmpeg.AVERROR(ffmpeg.EAGAIN)) continue;
                _packetPending = false;
                ffmpeg.av_packet_unref(_pkt);
                continue;
            }
            if (_inputEof)
            {
                if (ffmpeg.avcodec_send_packet(_codec, null) != ffmpeg.AVERROR(ffmpeg.EAGAIN)) _flushSent = true;
                continue;
            }

            r = ffmpeg.av_read_frame(_fmt, _pkt);
            if (r < 0) { _inputEof = true; continue; }
            if (_pkt->stream_index != _stream) { ffmpeg.av_packet_unref(_pkt); continue; }

            int s = ffmpeg.avcodec_send_packet(_codec, _pkt);
            if (s == ffmpeg.AVERROR(ffmpeg.EAGAIN)) { _packetPending = true; continue; } // retry after draining output
            ffmpeg.av_packet_unref(_pkt); // sent, or rejected as corrupt (skipped)
        }
        AppPaths.Log($"Decoder gave up on {Path} (no progress)");
        return Finish();
    }

    private bool Finish()
    {
        FlushResampler();
        _done = true;
        return _bufCount > 0;
    }

    private void ConvertFrame(AVFrame* f)
    {
        int skip = 0;
        if (_seekTarget >= 0)
        {
            long pts = f->best_effort_timestamp;
            if (pts != ffmpeg.AV_NOPTS_VALUE)
            {
                double start = (pts - _startTime) * ffmpeg.av_q2d(_timeBase);
                double end = start + (double)f->nb_samples / Math.Max(1, f->sample_rate);
                if (end <= _seekTarget) return; // whole frame before target
                if (start < _seekTarget) skip = (int)((_seekTarget - start) * OutputRate) * OutputChannels;
            }
            _seekTarget = -1;
        }

        EnsureResampler(f);
        int maxOut = ffmpeg.swr_get_out_samples(_swr, f->nb_samples);
        if (maxOut <= 0) return;
        Reserve(maxOut * OutputChannels);
        int got;
        fixed (float* p = &_buf[_bufStart + _bufCount])
        {
            byte* outPtr = (byte*)p;
            got = ffmpeg.swr_convert(_swr, &outPtr, maxOut, f->extended_data, f->nb_samples);
        }
        if (got > 0)
        {
            int samples = got * OutputChannels;
            if (skip > 0)
            {
                skip = Math.Min(skip, samples);
                _bufStart += skip; samples -= skip;
            }
            _bufCount += samples;
        }
    }

    private void FlushResampler()
    {
        if (_swr == null) return;
        int maxOut = ffmpeg.swr_get_out_samples(_swr, 0);
        if (maxOut <= 0) return;
        Reserve(maxOut * OutputChannels);
        fixed (float* p = &_buf[_bufStart + _bufCount])
        {
            byte* outPtr = (byte*)p;
            int got = ffmpeg.swr_convert(_swr, &outPtr, maxOut, null, 0);
            if (got > 0) _bufCount += got * OutputChannels;
        }
    }

    private void Reserve(int extra)
    {
        if (_bufStart > 0 && _bufCount > 0) Array.Copy(_buf, _bufStart, _buf, 0, _bufCount);
        _bufStart = 0;
        if (_bufCount + extra > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _bufCount + extra));
    }

    private void EnsureResampler(AVFrame* f)
    {
        if (_swr != null && f->format == _swrInFormat && f->sample_rate == _swrInRate && f->ch_layout.nb_channels == _swrInChannels) return;
        FreeSwr();

        AVChannelLayout outLayout, inLayout;
        ffmpeg.av_channel_layout_default(&outLayout, OutputChannels);
        if (f->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC || f->ch_layout.nb_channels == 0)
            ffmpeg.av_channel_layout_default(&inLayout, Math.Max(1, f->ch_layout.nb_channels));
        else
            ffmpeg.av_channel_layout_copy(&inLayout, &f->ch_layout);

        SwrContext* swr = null;
        Check(ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, OutputRate,
            &inLayout, (AVSampleFormat)f->format, f->sample_rate, 0, null), "swr alloc");
        ffmpeg.av_opt_set_int(swr, "dither_method", 0, 0);
        Check(ffmpeg.swr_init(swr), "swr init");
        ffmpeg.av_channel_layout_uninit(&inLayout);
        ffmpeg.av_channel_layout_uninit(&outLayout);

        _swr = swr;
        _swrInFormat = f->format;
        _swrInRate = f->sample_rate;
        _swrInChannels = f->ch_layout.nb_channels;
    }

    private void FreeSwr()
    {
        if (_swr == null) return;
        var s = _swr;
        ffmpeg.swr_free(&s);
        _swr = null;
        _swrInFormat = _swrInRate = _swrInChannels = -1;
    }

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
        lock (_decodeLock) Free(); // waits for a chunk being decoded ahead
    }

    private void Free()
    {
        FreeSwr();
        if (_frame != null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_pkt != null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_codec != null) { var c = _codec; ffmpeg.avcodec_free_context(&c); _codec = null; }
        if (_fmt != null) { var fm = _fmt; ffmpeg.avformat_close_input(&fm); _fmt = null; }
    }

    private static void Check(int result, string what)
    {
        if (result >= 0) return;
        var buf = stackalloc byte[256];
        ffmpeg.av_strerror(result, buf, 256);
        throw new InvalidDataException($"FFmpeg {what} failed: {Marshal.PtrToStringAnsi((IntPtr)buf)}");
    }

    /// <summary>Reads container tags, stream info and any attached cover picture without decoding audio.</summary>
    public static FFmpegProbe? Probe(string path, bool includePicture = false)
    {
        try
        {
            FFmpegLoader.EnsureLoaded();
            AVFormatContext* fmt = null;
            if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0) return null;
            try
            {
                ffmpeg.avformat_find_stream_info(fmt, null);
                var probe = new FFmpegProbe();
                ReadDict(fmt->metadata, probe.Tags);
                AVCodec* codec = null;
                int idx = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
                if (idx >= 0)
                {
                    var st = fmt->streams[idx];
                    ReadDict(st->metadata, probe.Tags);
                    var par = st->codecpar;
                    probe.SampleRate = par->sample_rate;
                    probe.Channels = par->ch_layout.nb_channels;
                    probe.BitsPerSample = par->bits_per_raw_sample > 0 ? par->bits_per_raw_sample : 0;
                    probe.Codec = ffmpeg.avcodec_get_name(par->codec_id) ?? "";
                    probe.Bitrate = (int)((par->bit_rate > 0 ? par->bit_rate : fmt->bit_rate) / 1000);
                    if (st->duration != ffmpeg.AV_NOPTS_VALUE && st->duration > 0)
                        probe.Duration = TimeSpan.FromSeconds(st->duration * ffmpeg.av_q2d(st->time_base));
                }
                if (probe.Duration == TimeSpan.Zero && fmt->duration > 0)
                    probe.Duration = TimeSpan.FromSeconds(fmt->duration / (double)ffmpeg.AV_TIME_BASE);

                if (includePicture)
                {
                    for (int i = 0; i < fmt->nb_streams; i++)
                    {
                        var st = fmt->streams[i];
                        if ((st->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0 && st->attached_pic.size > 0)
                        {
                            var bytes = new byte[st->attached_pic.size];
                            Marshal.Copy((IntPtr)st->attached_pic.data, bytes, 0, bytes.Length);
                            probe.AttachedPicture = bytes;
                            break;
                        }
                    }
                }
                return probe;
            }
            finally { ffmpeg.avformat_close_input(&fmt); }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"Probe failed for {path}: {ex.Message}");
            return null;
        }
    }

    private static void ReadDict(AVDictionary* dict, Dictionary<string, string> into)
    {
        AVDictionaryEntry* e = null;
        while ((e = ffmpeg.av_dict_get(dict, "", e, ffmpeg.AV_DICT_IGNORE_SUFFIX)) != null)
        {
            var key = Marshal.PtrToStringUTF8((IntPtr)e->key);
            var val = Marshal.PtrToStringUTF8((IntPtr)e->value);
            if (key != null && val != null && !into.ContainsKey(key)) into[key] = val;
        }
    }
}

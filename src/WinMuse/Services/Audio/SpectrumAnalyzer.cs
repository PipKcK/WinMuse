using NAudio.Dsp;

namespace WinMuse.Services.Audio;

/// <summary>Collects the most recent output samples and turns them into smoothed, log-spaced band levels (0..1).</summary>
public sealed class SpectrumAnalyzer
{
    private const int FftSize = 2048;
    private const int FftM = 11;
    private readonly float[] _ring = new float[FftSize];
    private int _write;
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _window = new float[FftSize];
    private float[] _smoothed = Array.Empty<float>();
    private readonly int _channels;
    public int SampleRate { get; }

    public SpectrumAnalyzer(int sampleRate, int channels)
    {
        SampleRate = sampleRate;
        _channels = channels;
        for (int i = 0; i < FftSize; i++) _window[i] = (float)FastFourierTransform.HannWindow(i, FftSize);
    }

    /// <summary>Called on the audio thread.</summary>
    public void Write(float[] buffer, int offset, int count)
    {
        lock (_ring)
        {
            if (_clear) { Array.Clear(_ring); _clear = false; }
            for (int i = 0; i + _channels - 1 < count; i += _channels)
            {
                float m = 0;
                for (int c = 0; c < _channels; c++) m += buffer[offset + i + c];
                _ring[_write] = m / _channels;
                _write = (_write + 1) % FftSize;
            }
        }
    }

    private volatile bool _clear;

    /// <summary>Called from the UI thread: the audio thread (or the next GetBands) clears the buffer.</summary>
    public void Clear() => _clear = true;

    /// <summary>
    /// Fills <paramref name="bands"/> with levels. Called on the UI thread (~60 fps). Never waits for the audio thread:
    /// if it is writing right now, this frame repeats the previous levels (waiting here crashed WinUI, see UiLock).
    /// </summary>
    public void GetBands(float[] bands, bool playing)
    {
        if (_smoothed.Length != bands.Length) _smoothed = new float[bands.Length];

        if (!Monitor.TryEnter(_ring))
        {
            Array.Copy(_smoothed, bands, bands.Length);
            return;
        }
        try
        {
            if (_clear) { Array.Clear(_ring); _clear = false; }
            for (int i = 0; i < FftSize; i++)
            {
                _fft[i].X = _ring[(_write + i) % FftSize] * _window[i];
                _fft[i].Y = 0;
            }
        }
        finally { Monitor.Exit(_ring); }
        FastFourierTransform.FFT(true, FftM, _fft);

        double minF = 40, maxF = Math.Min(16000, SampleRate / 2.0);
        double binHz = (double)SampleRate / FftSize;
        for (int b = 0; b < bands.Length; b++)
        {
            double f0 = minF * Math.Pow(maxF / minF, (double)b / bands.Length);
            double f1 = minF * Math.Pow(maxF / minF, (double)(b + 1) / bands.Length);
            int i0 = Math.Max(1, (int)(f0 / binHz));
            int i1 = Math.Max(i0 + 1, (int)(f1 / binHz));
            double peak = 0;
            for (int i = i0; i < i1 && i < FftSize / 2; i++)
            {
                double mag = Math.Sqrt(_fft[i].X * _fft[i].X + _fft[i].Y * _fft[i].Y);
                if (mag > peak) peak = mag;
            }
            // dB scale normalised to roughly 0..1, slight tilt so highs are visible
            double db = 20 * Math.Log10(peak + 1e-9) + 6 * Math.Log2(1 + b / (double)bands.Length * 4);
            float target = playing ? (float)Math.Clamp((db + 70) / 60, 0, 1) : 0;
            float cur = _smoothed[b];
            cur = target > cur ? cur + (target - cur) * 0.55f : cur + (target - cur) * 0.12f;
            _smoothed[b] = cur;
            bands[b] = cur;
        }
    }
}

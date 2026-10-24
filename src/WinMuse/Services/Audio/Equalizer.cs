using NAudio.Dsp;

namespace WinMuse.Services.Audio;

/// <summary>10-band peaking equalizer with preamp and a soft limiter, operating on interleaved stereo float.</summary>
public sealed class Equalizer
{
    public static readonly float[] Frequencies = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    public static readonly string[] Labels = { "31", "62", "125", "250", "500", "1K", "2K", "4K", "8K", "16K" };

    public static readonly Dictionary<string, float[]> Presets = new()
    {
        ["Flat"] = new float[10],
        ["Bass boost"] = new float[] { 6, 5, 4, 2, 0, 0, 0, 0, 0, 0 },
        ["Bass reducer"] = new float[] { -6, -5, -4, -2, 0, 0, 0, 0, 0, 0 },
        ["Treble boost"] = new float[] { 0, 0, 0, 0, 0, 1, 2, 4, 5, 6 },
        ["Vocal"] = new float[] { -2, -2, -1, 1, 3, 4, 3, 1, 0, -1 },
        ["Rock"] = new float[] { 5, 4, 2, -1, -2, -1, 1, 3, 4, 5 },
        ["Pop"] = new float[] { -1, 1, 3, 4, 3, 0, -1, -1, 1, 2 },
        ["Jazz"] = new float[] { 3, 2, 1, 2, -1, -1, 0, 1, 2, 3 },
        ["Classical"] = new float[] { 4, 3, 2, 1, -1, -1, 0, 2, 3, 4 },
        ["Electronic"] = new float[] { 5, 4, 1, 0, -2, 2, 1, 1, 4, 5 },
        ["Hip-hop"] = new float[] { 5, 4, 1, 3, -1, -1, 1, -1, 2, 3 },
        ["Acoustic"] = new float[] { 4, 3, 2, 1, 2, 2, 3, 3, 3, 2 },
        ["Loudness"] = new float[] { 6, 4, 0, 0, -2, 0, -1, -4, 4, 2 },
    };

    /// <summary>Equalizer preset that suits a genre ("Tropical House" -> Electronic, "Country Pop" -> Acoustic...).</summary>
    public static string PresetForGenre(string? genre)
    {
        var g = (genre ?? "").ToLowerInvariant();
        bool Has(params string[] words) => words.Any(w => g.Contains(w));
        if (g.Length == 0) return "Flat";
        if (Has("hip-hop", "hip hop", "rap", "trap", "r&b", "grime", "drill")) return "Hip-hop";
        if (Has("metal", "rock", "punk", "grunge", "emo", "hardcore")) return "Rock";
        if (Has("house", "edm", "electro", "techno", "trance", "dubstep", "drum and bass", "future bass", "dance", "big room", "hardstyle", "moombahton", "disco", "synthwave", "tropical")) return "Electronic";
        if (Has("classical", "opera", "soundtrack", "piano", "orchestra")) return "Classical";
        if (Has("jazz", "blues", "soul", "funk", "swing", "bossa")) return "Jazz";
        if (Has("acoustic", "folk", "country", "singer-songwriter", "americana", "bluegrass")) return "Acoustic";
        if (Has("pop", "k-pop", "j-pop")) return "Pop";
        if (Has("vocal", "gospel", "christian")) return "Vocal";
        return "Flat";
    }

    private readonly BiQuadFilter[,] _filters;
    private readonly float[] _gains = new float[10];
    private readonly int _rate;
    private readonly int _channels;
    private float _preamp = 1f;
    private volatile bool _dirty;

    public bool Enabled { get; set; }

    public Equalizer(int sampleRate, int channels)
    {
        _rate = sampleRate;
        _channels = channels;
        _filters = new BiQuadFilter[channels, Frequencies.Length];
        for (int c = 0; c < channels; c++)
            for (int b = 0; b < Frequencies.Length; b++)
                _filters[c, b] = BiQuadFilter.PeakingEQ(sampleRate, Frequencies[b], 1.1f, 0);
    }

    public void SetBands(float[] gainsDb, float preampDb)
    {
        // Called on the UI thread: wait without message pumping (see Helpers/UiLock).
        Helpers.UiLock.Run(_gains, () =>
        {
            Array.Copy(gainsDb, _gains, Math.Min(gainsDb.Length, _gains.Length));
            _preamp = (float)Math.Pow(10, preampDb / 20.0);
            _dirty = true;
        });
    }

    public void Process(float[] buffer, int offset, int count)
    {
        if (!Enabled) return;
        if (_dirty)
        {
            lock (_gains)
            {
                for (int c = 0; c < _channels; c++)
                    for (int b = 0; b < Frequencies.Length; b++)
                        _filters[c, b].SetPeakingEq(_rate, Frequencies[b], 1.1f, _gains[b]);
                _dirty = false;
            }
        }

        for (int i = 0; i < count; i++)
        {
            int ch = i % _channels;
            float s = buffer[offset + i] * _preamp;
            for (int b = 0; b < Frequencies.Length; b++)
                if (_gains[b] != 0) s = _filters[ch, b].Transform(s);
            // soft knee limiter to avoid harsh clipping when boosting
            if (s > 0.9f) s = 0.9f + 0.1f * MathF.Tanh((s - 0.9f) / 0.1f);
            else if (s < -0.9f) s = -0.9f + 0.1f * MathF.Tanh((s + 0.9f) / 0.1f);
            buffer[offset + i] = s;
        }
    }
}

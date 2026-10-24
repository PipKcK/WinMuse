using System.Runtime.InteropServices;
using NAudio.Wave;

namespace WinMuse.Services.Audio;

/// <summary>
/// Converts the float pipeline to the byte stream WASAPI wants (32-bit float, or 24/16-bit PCM for exclusive mode).
/// Replaces NAudio's SampleToWaveProvider*, whose WaveBuffer union trick hands out a byte[] typed as float[] and breaks Array.Copy.
/// </summary>
public sealed class PcmOutputProvider : IWaveProvider
{
    private readonly ISampleProvider _source;
    private readonly int _bits;
    private float[] _temp = new float[4096];

    public WaveFormat WaveFormat { get; }

    public PcmOutputProvider(ISampleProvider source, int bits)
    {
        _source = source;
        _bits = bits;
        var sf = source.WaveFormat;
        WaveFormat = bits == 32 ? WaveFormat.CreateIeeeFloatWaveFormat(sf.SampleRate, sf.Channels) : new WaveFormat(sf.SampleRate, bits, sf.Channels);
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        int bytesPerSample = _bits / 8;
        int samples = count / bytesPerSample;
        if (_temp.Length < samples) _temp = new float[samples];
        int read = _source.Read(_temp, 0, samples);
        var dst = buffer.AsSpan(offset, read * bytesPerSample);

        switch (_bits)
        {
            case 32:
                MemoryMarshal.AsBytes(_temp.AsSpan(0, read)).CopyTo(dst);
                break;
            case 24:
                for (int i = 0, j = 0; i < read; i++, j += 3)
                {
                    int v = (int)(Math.Clamp(_temp[i], -1f, 1f) * 8388607f);
                    dst[j] = (byte)v; dst[j + 1] = (byte)(v >> 8); dst[j + 2] = (byte)(v >> 16);
                }
                break;
            default:
                for (int i = 0, j = 0; i < read; i++, j += 2)
                {
                    short v = (short)(Math.Clamp(_temp[i], -1f, 1f) * 32767f);
                    dst[j] = (byte)v; dst[j + 1] = (byte)(v >> 8);
                }
                break;
        }
        return read * bytesPerSample;
    }
}

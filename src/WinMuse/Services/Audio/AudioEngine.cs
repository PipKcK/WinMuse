using NAudio.CoreAudioApi;
using NAudio.Wave;
using WinMuse.Helpers;

namespace WinMuse.Services.Audio;

public sealed record AudioDevice(string? Id, string Name);

/// <summary>Owns the WASAPI output and the playback pipeline.</summary>
public sealed class AudioEngine : IDisposable
{
    private WasapiOut? _out;
    private bool _wantPlaying;

    public PlaybackPipeline Pipeline { get; private set; } = new(48000);
    public int SampleRate => Pipeline.WaveFormat.SampleRate;
    public bool IsExclusive { get; private set; }

    // Audio buffered ahead of the speakers. Windows UI objects (cover pictures) make .NET run full garbage collections
    // that pause every managed thread, the audio thread too, for up to ~100 ms; a 90 ms buffer ran dry then (heard as
    // a stutter when switching pages). 200 ms rides those out.
    private const int SharedLatencyMs = 200, ExclusiveLatencyMs = 100;

    /// <summary>How far the decoder runs ahead of what is heard (subtracted from the shown position).</summary>
    public TimeSpan OutputLatency => TimeSpan.FromMilliseconds(IsExclusive ? ExclusiveLatencyMs : SharedLatencyMs);
    public string OutputDescription { get; private set; } = "";

    /// <summary>Raised (on any thread) when the device was lost; the engine has already re-initialised.</summary>
    public event Action? DeviceReset;
    /// <summary>Raised when the output keeps failing and the engine stopped retrying.</summary>
    public event Action<string>? Fatal;

    public static List<AudioDevice> GetDevices()
    {
        var list = new List<AudioDevice> { new(null, "System default") };
        try
        {
            using var en = new MMDeviceEnumerator();
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                list.Add(new AudioDevice(d.ID, d.FriendlyName));
        }
        catch { }
        return list;
    }

    /// <summary>(Re)creates the output. Existing decoders are carried over to the new pipeline when the rate matches.</summary>
    public void Initialize(string? deviceId, bool exclusive, PlaybackPipeline? carry = null)
    {
        var wasPlaying = _wantPlaying;
        DisposeOutput();

        using var en = new MMDeviceEnumerator();
        MMDevice device;
        try { device = deviceId == null ? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : en.GetDevice(deviceId); }
        catch { device = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); }

        int rate = device.AudioClient.MixFormat.SampleRate;
        IWaveProvider? provider = null;
        bool useExclusive = false;

        if (exclusive)
        {
            // Bypass the system mixer: find a PCM format the device accepts natively.
            foreach (var bits in new[] { 32, 24, 16 })
            {
                var fmt = bits == 32 ? WaveFormat.CreateIeeeFloatWaveFormat(rate, 2) : new WaveFormat(rate, bits, 2);
                try
                {
                    if (device.AudioClient.IsFormatSupported(AudioClientShareMode.Exclusive, fmt))
                    {
                        Pipeline = MakePipeline(rate, carry);
                        provider = new PcmOutputProvider(Pipeline, bits);
                        useExclusive = true;
                        OutputDescription = $"Exclusive · {bits}-bit · {rate / 1000.0:0.#} kHz · {device.FriendlyName}";
                        break;
                    }
                }
                catch { }
            }
        }

        if (provider == null)
        {
            Pipeline = MakePipeline(rate, carry);
            provider = new PcmOutputProvider(Pipeline, 32);
            OutputDescription = $"Shared · {rate / 1000.0:0.#} kHz · {device.FriendlyName}";
        }

        try
        {
            _out = new WasapiOut(device, useExclusive ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared, true, useExclusive ? ExclusiveLatencyMs : SharedLatencyMs);
            _out.Init(provider);
        }
        catch (Exception ex) when (useExclusive)
        {
            AppPaths.Log("Exclusive mode init failed, falling back to shared: " + ex.Message);
            _out?.Dispose();
            Pipeline = MakePipeline(rate, carry);
            _out = new WasapiOut(device, AudioClientShareMode.Shared, true, SharedLatencyMs);
            _out.Init(new PcmOutputProvider(Pipeline, 32));
            useExclusive = false;
            OutputDescription = $"Shared · {rate / 1000.0:0.#} kHz · {device.FriendlyName}";
        }

        IsExclusive = useExclusive;
        _out.PlaybackStopped += OnPlaybackStopped;
        if (wasPlaying) Play();
    }

    private PlaybackPipeline MakePipeline(int rate, PlaybackPipeline? carry)
    {
        var p = carry != null && carry.WaveFormat.SampleRate == rate ? carry : new PlaybackPipeline(rate);
        return p;
    }

    private readonly Queue<DateTime> _recentErrors = new();

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception == null || sender != _out) return;
        AppPaths.Log("Audio device error: " + e.Exception);
        // Give up after repeated failures instead of spinning (e.g. a bug in the pipeline, not a device change).
        var now = DateTime.UtcNow;
        _recentErrors.Enqueue(now);
        while (_recentErrors.Count > 0 && now - _recentErrors.Peek() > TimeSpan.FromSeconds(10)) _recentErrors.Dequeue();
        if (_recentErrors.Count > 3) { Fatal?.Invoke(e.Exception.Message); return; }
        // Device unplugged / default changed: rebuild on the default device on a worker thread.
        Task.Run(() =>
        {
            try { DeviceReset?.Invoke(); }
            catch (Exception ex) { AppPaths.Log("Device reset failed: " + ex.Message); }
        });
    }

    public void Play()
    {
        _wantPlaying = true;
        try { _out?.Play(); } catch (Exception ex) { AppPaths.Log("Play failed: " + ex.Message); }
    }

    public void Pause()
    {
        _wantPlaying = false;
        try { _out?.Pause(); } catch { }
    }

    public bool IsOutputPlaying => _out?.PlaybackState == PlaybackState.Playing;

    private void DisposeOutput()
    {
        if (_out == null) return;
        _out.PlaybackStopped -= OnPlaybackStopped;
        try { _out.Stop(); } catch { }
        _out.Dispose();
        _out = null;
    }

    public void Dispose()
    {
        DisposeOutput();
        Pipeline.SetCurrent(null);
    }
}

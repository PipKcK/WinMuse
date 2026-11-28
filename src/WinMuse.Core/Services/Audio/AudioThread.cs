using System.Runtime.InteropServices;

namespace WinMuse.Services.Audio;

/// <summary>
/// Registers the calling thread with Windows' multimedia scheduler (MMCSS "Audio" task), so audio work is served
/// ahead of background work such as a library-wide fetch.
/// </summary>
public static class AudioThread
{
    [ThreadStatic] private static bool _registered;

    [DllImport("avrt.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr AvSetMmThreadCharacteristicsW(string task, ref uint taskIndex);

    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        try { uint index = 0; AvSetMmThreadCharacteristicsW("Audio", ref index); }
        catch { /* not available: normal priority */ }
    }
}

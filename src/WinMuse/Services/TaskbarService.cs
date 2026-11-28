using System.Runtime.InteropServices;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using WinMuse.Helpers;

namespace WinMuse.Services;

/// <summary>Previous / play-pause / next buttons on the taskbar thumbnail preview.</summary>
public sealed class TaskbarService
{
    private const int WM_COMMAND = 0x0111;
    private const int THBN_CLICKED = 0x1800;
    private const uint THB_ICON = 0x2, THB_TOOLTIP = 0x4, THB_FLAGS = 0x8;
    private const uint IdPrev = 100, IdPlay = 101, IdNext = 102;

    private readonly PlaybackService _player;
    private IntPtr _hwnd;
    private ITaskbarList3? _taskbar;
    private SubclassProc? _proc; // keep delegate alive
    private uint _taskbarCreatedMsg;
    private bool _added;
    private IntPtr _icoPrev, _icoPlay, _icoPause, _icoNext;

    public TaskbarService(PlaybackService player) => _player = player;

    public void Initialize(IntPtr hwnd)
    {
        try
        {
            _hwnd = hwnd;
            _taskbarCreatedMsg = RegisterWindowMessage("TaskbarButtonCreated");
            int size = GetSystemMetrics(49 /*SM_CXSMICON*/);
            _icoPrev = LoadIcon("ThumbPrevious.ico", size);
            _icoPlay = LoadIcon("ThumbPlay.ico", size);
            _icoPause = LoadIcon("ThumbPause.ico", size);
            _icoNext = LoadIcon("ThumbNext.ico", size);
            _proc = WndProc;
            SetWindowSubclass(hwnd, _proc, 1, IntPtr.Zero);
            _player.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlaybackService.IsPlaying)) Update(); };
            AddButtons();
        }
        catch (Exception ex) { AppPaths.Log("Taskbar init failed: " + ex.Message); }
    }

    private static IntPtr LoadIcon(string name, int size) =>
        LoadImage(IntPtr.Zero, Path.Combine(AppPaths.AssetsDir, name), 1 /*IMAGE_ICON*/, size, size, 0x10 /*LR_LOADFROMFILE*/);

    private THUMBBUTTON[] Buttons() => new[]
    {
        new THUMBBUTTON { dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS, iId = IdPrev, hIcon = _icoPrev, szTip = "Previous" },
        new THUMBBUTTON { dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS, iId = IdPlay, hIcon = _player.IsPlaying ? _icoPause : _icoPlay, szTip = _player.IsPlaying ? "Pause" : "Play" },
        new THUMBBUTTON { dwMask = THB_ICON | THB_TOOLTIP | THB_FLAGS, iId = IdNext, hIcon = _icoNext, szTip = "Next" },
    };

    private void AddButtons()
    {
        try
        {
            _taskbar ??= (ITaskbarList3)new TaskbarList();
            _taskbar.HrInit();
            _added = _taskbar.ThumbBarAddButtons(_hwnd, 3, Buttons()) >= 0;
        }
        catch (Exception ex) { AppPaths.Log("Thumb buttons failed: " + ex.Message); }
    }

    private void Update()
    {
        if (!_added || _taskbar == null) return;
        try { _taskbar.ThumbBarUpdateButtons(_hwnd, 3, Buttons()); } catch { }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data)
    {
        if (msg == _taskbarCreatedMsg && _taskbarCreatedMsg != 0)
        {
            _added = false;
            AddButtons();
        }
        else if (msg == WM_COMMAND && ((long)wParam >> 16 & 0xFFFF) == THBN_CLICKED)
        {
            var btn = (uint)((long)wParam & 0xFFFF);
            App.UI.TryEnqueue(async () =>
            {
                if (btn == IdPrev) await _player.PreviousAsync();
                else if (btn == IdPlay) _player.TogglePlayPause();
                else if (btn == IdNext) await _player.NextAsync();
            });
            return IntPtr.Zero;
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    // ---- interop ----
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, IntPtr id, IntPtr data);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, nuint id, IntPtr data);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImage(IntPtr hInst, string name, uint type, int cx, int cy, uint load);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct THUMBBUTTON
    {
        public uint dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szTip;
        public uint dwFlags;
    }

    [ComImport, Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr hwnd, ulong completed, ulong total);
        void SetProgressState(IntPtr hwnd, int flags);
        void RegisterTab(IntPtr tab, IntPtr mdi);
        void UnregisterTab(IntPtr tab);
        void SetTabOrder(IntPtr tab, IntPtr insertBefore);
        void SetTabActive(IntPtr tab, IntPtr mdi, uint reserved);
        [PreserveSig] int ThumbBarAddButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
        [PreserveSig] int ThumbBarUpdateButtons(IntPtr hwnd, uint count, [MarshalAs(UnmanagedType.LPArray)] THUMBBUTTON[] buttons);
        void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
        void SetOverlayIcon(IntPtr hwnd, IntPtr icon, [MarshalAs(UnmanagedType.LPWStr)] string description);
        void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string tip);
        void SetThumbnailClip(IntPtr hwnd, IntPtr clip);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList { }
}

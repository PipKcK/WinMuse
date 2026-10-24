// Stand-ins for app services the linked sources refer to (the real ones need the UI).
namespace WinMuse.Services
{
    public static class SelectionService
    {
        internal static void OnTrackChecked(WinMuse.Models.Track t, bool isChecked) { }
    }
}

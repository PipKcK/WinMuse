using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Helpers;

/// <summary>Opens the Windows share window for song files, the same one File Explorer shows for an .mp3.</summary>
public static class ShareFiles
{
    public static async Task ShareAsync(IList<Track> tracks)
    {
        var files = new List<IStorageItem>();
        foreach (var t in tracks)
        {
            try { files.Add(await StorageFile.GetFileFromPathAsync(t.Path)); }
            catch { /* missing file: skipped */ }
        }
        if (files.Count == 0) { Notifier.Error("Unable to share", "The song file was not found."); return; }

        var hwnd = App.MainWindow.Hwnd;
        var manager = DataTransferManagerInterop.GetForWindow(hwnd);
        void OnRequested(DataTransferManager sender, DataRequestedEventArgs e)
        {
            sender.DataRequested -= OnRequested;
#if DEBUG
            AppPaths.Log($"Share: data requested ({files.Count} files)");
#endif
            var data = e.Request.Data;
            data.Properties.Title = tracks.Count == 1 ? tracks[0].DisplayTitle : $"{tracks.Count} songs";
            data.Properties.Description = tracks.Count == 1 ? tracks[0].DisplayArtist : string.Join(", ", tracks.Take(3).Select(t => t.DisplayTitle));
            data.SetStorageItems(files);
        }
        manager.DataRequested += OnRequested;
        DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
#if DEBUG
        AppPaths.Log("Share: window requested");
#endif
    }
}

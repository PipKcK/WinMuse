using Windows.Storage.Pickers;

namespace WinMuse.Helpers;

/// <summary>File / folder pickers bound to the main window (required for unpackaged WinUI apps).</summary>
public static class Pickers
{
    public static async Task<string?> PickFolderAsync()
    {
        var p = new FolderPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        p.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(p, App.MainWindow.Hwnd);
        var f = await p.PickSingleFolderAsync();
        return f?.Path;
    }

    public static async Task<string?> PickImageAsync()
    {
        var p = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, ViewMode = PickerViewMode.Thumbnail };
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif" }) p.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(p, App.MainWindow.Hwnd);
        var f = await p.PickSingleFileAsync();
        return f?.Path;
    }

    public static async Task<string?> PickFileAsync(params string[] extensions)
    {
        var p = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.MusicLibrary };
        foreach (var ext in extensions) p.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(p, App.MainWindow.Hwnd);
        var f = await p.PickSingleFileAsync();
        return f?.Path;
    }

    public static async Task<string?> PickSaveAsync(string suggestedName, string label, string extension)
    {
        var p = new FileSavePicker { SuggestedStartLocation = PickerLocationId.MusicLibrary, SuggestedFileName = suggestedName };
        p.FileTypeChoices.Add(label, new List<string> { extension });
        WinRT.Interop.InitializeWithWindow.Initialize(p, App.MainWindow.Hwnd);
        var f = await p.PickSaveFileAsync();
        return f?.Path;
    }
}

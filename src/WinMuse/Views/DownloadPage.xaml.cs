using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
using WinMuse.Controls;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Views;

/// <summary>One download in this session's list.</summary>
public sealed partial class DownloadEntry : ObservableObject
{
    public string Url { get; init; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "Downloading…";
    [ObservableProperty] public partial string Glyph { get; set; } = "";
    [ObservableProperty] public partial Brush Brush { get; set; } = ThemeRes.Brush("TextFillColorSecondaryBrush");
}

/// <summary>Downloads songs with mdown.exe, adds them to the library and runs the online checks on them.</summary>
public sealed partial class DownloadPage : Page
{
    private static readonly ObservableCollection<DownloadEntry> s_history = new();
    private CancellationTokenSource? _cts;
    private DownloadResult? _lastError;

    public DownloadPage()
    {
        InitializeComponent();
        History.ItemsSource = s_history;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        FolderBox.Text = FolderRun.Text = Svc.Download.DefaultFolder;
        RefreshTool();
        RefreshSecret();
        RefreshButton();
        HistoryHeader.Visibility = s_history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Effects.EntranceSlide(Root, 20);
    }

    private bool IsRunning => _cts != null;

    private void RefreshTool()
    {
        ToolMissingBar.IsOpen = DownloadService.ToolPath == null;
        ToolMissingBar.Message = $"Place {DownloadService.ToolName} in {DownloadService.ToolFolder}.";
    }

    private void RefreshSecret()
    {
        var updated = Svc.Download.SecretUpdated;
        SecretText.Text = updated is { } d
            ? $"Uploaded {d:d MMM yyyy, HH:mm}. Used for every download until you replace it."
            : "Required for downloading. Upload the secret file you were given; it is used for every download.";
        SecretButtonText.Text = updated == null ? "Upload" : "Replace";
        RemoveSecretButton.Visibility = updated == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void RefreshButton()
    {
        DownloadButton.IsEnabled = !IsRunning && UrlBox.Text.Trim().Length > 0;
        CancelButton.Visibility = IsRunning ? Visibility.Visible : Visibility.Collapsed;
        HintText.Visibility = IsRunning ? Visibility.Collapsed : Visibility.Visible;
        ProgressPanel.Visibility = IsRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUrlChanged(object sender, TextChangedEventArgs e) => RefreshButton();

    private void OnUrlKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter && DownloadButton.IsEnabled) { e.Handled = true; OnDownload(sender, e); }
    }

    private async void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        if (await Pickers.PickFolderAsync() is not { } folder) return;
        FolderBox.Text = FolderRun.Text = folder;
        Svc.Settings.Set(nameof(AppSettings.DownloadFolder), s => s.DownloadFolder = folder);
    }

    private async void OnUploadSecret(object sender, RoutedEventArgs e)
    {
        if (await Pickers.PickFileAsync("*") is not { } file) return;
        try
        {
            Svc.Download.SetSecret(file);
            Notifier.Show("Secret file uploaded", "It will be used for every download.");
        }
        catch (Exception ex) { ShowError(new DownloadResult(false, "SECRET_COPY_FAILED", $"The secret file could not be saved: {ex.Message}", new(), "")); }
        RefreshSecret();
    }

    private async void OnRemoveSecret(object sender, RoutedEventArgs e)
    {
        if (!await TrackActions.Confirm("Remove secret file?", "Downloads won't work until you upload it again.", "Remove")) return;
        try { Svc.Download.RemoveSecret(); } catch (Exception ex) { Notifier.Error("Unable to remove the secret file", ex.Message); }
        RefreshSecret();
    }

    private void OnOpenToolFolder(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DownloadService.ToolFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(DownloadService.ToolFolder) { UseShellExecute = true });
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (IsRunning) return;
        RefreshTool();
        var url = UrlBox.Text.Trim();
        var folder = FolderBox.Text;
        HideError();

        var entry = new DownloadEntry { Url = url };
        s_history.Insert(0, entry);
        HistoryHeader.Visibility = Visibility.Visible;

        _cts = new CancellationTokenSource();
        ProgressText.Text = "Starting…";
        RefreshButton();
        try
        {
            var progress = new Progress<string>(line => ProgressText.Text = line);
            var result = await Svc.Download.RunAsync(url, folder, progress, _cts.Token);
            if (!result.Success)
            {
                Mark(entry, "", "SystemFillColorCriticalBrush", $"Failed · {result.Code}");
                ShowError(result);
                // A failed run can still leave complete files behind; add them.
                if (result.Files.Count > 0) await Svc.Download.ImportAsync(folder, result.Files);
                return;
            }

            ProgressText.Text = "Adding to your library…";
            var tracks = await Svc.Download.ImportAsync(folder, result.Files);
            var name = tracks.Count == 1 ? $"{tracks[0].DisplayTitle} · {tracks[0].DisplayArtist}" : result.Message;
            Mark(entry, "", "SystemFillColorSuccessBrush", $"Downloaded · {name}");
            Notifier.Show("Download complete", tracks.Count == 1 ? $"{tracks[0].DisplayTitle} was added to your library." : $"{tracks.Count} songs were added to your library.");
            UrlBox.Text = "";
        }
        catch (OperationCanceledException)
        {
            Mark(entry, "", "TextFillColorSecondaryBrush", "Canceled");
        }
        catch (Exception ex)
        {
            AppPaths.Log("Download failed: " + ex);
            var result = new DownloadResult(false, "UNEXPECTED_ERROR", ex.Message, new(), ex.ToString());
            Mark(entry, "", "SystemFillColorCriticalBrush", $"Failed · {result.Code}");
            ShowError(result);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            RefreshButton();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private static void Mark(DownloadEntry entry, string glyph, string brush, string status)
    {
        entry.Glyph = glyph;
        entry.Brush = ThemeRes.Brush(brush);
        entry.Status = status;
    }

    // ---------------- Error box ----------------

    private void ShowError(DownloadResult result)
    {
        _lastError = result;
        ErrorBox.Text = result.Report;
        CopyText.Text = "Copy";
        ErrorCard.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        _lastError = null;
        ErrorCard.Visibility = Visibility.Collapsed;
    }

    private void OnDismissError(object sender, RoutedEventArgs e) => HideError();

    private void OnCopyError(object sender, RoutedEventArgs e)
    {
        if (_lastError == null) return;
        var data = new DataPackage();
        data.SetText(_lastError.Report);
        Clipboard.SetContent(data);
        CopyText.Text = "Copied";
    }
}

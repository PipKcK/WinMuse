using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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

    /// <summary>What screen readers announce for the entry (WinUI names list items from ToString).</summary>
    public override string ToString() => $"{Url}, {Status}";
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
        RefreshSetup();
        RefreshButton();
        HistoryHeader.Visibility = s_history.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Effects.EntranceSlide(Root, 20);
    }

    private bool IsRunning => _cts != null;

    /// <summary>The two files downloading needs: each row shows whether it is there, with Add, Replace and Remove.</summary>
    private void RefreshSetup()
    {
        var tool = DownloadService.ToolUpdated;
        ToolText.Text = tool is { } t
            ? (DownloadService.ToolIsAdded ? $"{DownloadService.ToolName} · added {t:d MMM yyyy, HH:mm}" : $"{DownloadService.ToolName} · installed with the app")
            : $"Choose the {DownloadService.ToolName} file you were given.";
        ToolButtonText.Text = tool == null ? "Add" : "Replace";
        AutomationProperties.SetName(ToolButton, tool == null ? "Add downloader" : "Replace downloader");
        RemoveToolButton.Visibility = DownloadService.ToolIsAdded ? Visibility.Visible : Visibility.Collapsed;
        SetBadge(ToolBadge, ToolBadgeIcon, ToolBadgeText, tool != null);

        var secret = Svc.Download.SecretUpdated;
        SecretText.Text = secret is { } d
            ? $"Added {d:d MMM yyyy, HH:mm}. Used for every download."
            : "Choose the secret file you were given.";
        SecretButtonText.Text = secret == null ? "Add" : "Replace";
        AutomationProperties.SetName(SecretButton, secret == null ? "Add secret file" : "Replace secret file");
        RemoveSecretButton.Visibility = secret == null ? Visibility.Collapsed : Visibility.Visible;
        SetBadge(SecretBadge, SecretBadgeIcon, SecretBadgeText, secret != null);

        SetupText.Text = Svc.Download.IsReady
            ? "Both files are in place. They are kept in the app's data folder."
            : "Downloading needs both files below. They are kept in the app's data folder.";
        RefreshButton();
    }

    private static void SetBadge(Border badge, FontIcon icon, TextBlock text, bool ready)
    {
        badge.Background = ThemeRes.Brush(ready ? "SystemFillColorSuccessBackgroundBrush" : "SubtleFillColorSecondaryBrush");
        icon.Glyph = ready ? "" : "";
        icon.Foreground = ThemeRes.Brush(ready ? "SystemFillColorSuccessBrush" : "SystemFillColorCautionBrush");
        text.Text = ready ? "Ready" : "Required";
        text.Foreground = ThemeRes.Brush("TextFillColorPrimaryBrush");
    }

    private void RefreshButton()
    {
        var text = UrlBox.Text.Trim();
        bool valid = DownloadService.CleanUrl(text) != null;
        DownloadButton.IsEnabled = !IsRunning && valid && Svc.Download.IsReady;
        CancelButton.Visibility = IsRunning ? Visibility.Visible : Visibility.Collapsed;
        HintText.Visibility = IsRunning ? Visibility.Collapsed : Visibility.Visible;
        ProgressPanel.Visibility = IsRunning ? Visibility.Visible : Visibility.Collapsed;

        bool warn = !Svc.Download.IsReady || (text.Length > 0 && !valid);
        HintIcon.Glyph = warn ? "" : "";
        HintIcon.Foreground = ThemeRes.Brush(warn ? "SystemFillColorCautionBrush" : "TextFillColorSecondaryBrush");
        HintLabel.Text = !Svc.Download.IsReady
            ? (DownloadService.HasTool || Svc.Download.HasSecret ? "Add the remaining file under Setup to start downloading." : "Add the downloader and the secret file under Setup to start downloading.")
            : text.Length > 0 && !valid ? "This isn't a web link. Paste a link that starts with https://."
            : "Downloaded songs join your library, and their details, album art and lyrics are fetched automatically.";
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

    private async void OnChooseTool(object sender, RoutedEventArgs e)
    {
        if (await Pickers.PickFileAsync(".exe") is not { } file) return;
        try
        {
            Svc.Download.SetTool(file);
            Notifier.Success("Downloader added", Svc.Download.IsReady ? "You can download songs now." : "Add the secret file to start downloading.");
        }
        catch (Exception ex) { Notifier.Error("Unable to add the downloader", ex); }
        RefreshSetup();
    }

    private async void OnRemoveTool(object sender, RoutedEventArgs e)
    {
        if (!await TrackActions.Confirm("Remove the downloader?", "Downloads won't work until you add it again.", "Remove")) return;
        try { Svc.Download.RemoveTool(); } catch (Exception ex) { Notifier.Error("Unable to remove the downloader", ex); }
        RefreshSetup();
    }

    private async void OnUploadSecret(object sender, RoutedEventArgs e)
    {
        if (await Pickers.PickFileAsync("*") is not { } file) return;
        try
        {
            Svc.Download.SetSecret(file);
            Notifier.Success("Secret file added", Svc.Download.IsReady ? "You can download songs now." : "Add the downloader to start downloading.");
        }
        catch (Exception ex) { Notifier.Error("Unable to add the secret file", ex); }
        RefreshSetup();
    }

    private async void OnRemoveSecret(object sender, RoutedEventArgs e)
    {
        if (!await TrackActions.Confirm("Remove the secret file?", "Downloads won't work until you add it again.", "Remove")) return;
        try { Svc.Download.RemoveSecret(); } catch (Exception ex) { Notifier.Error("Unable to remove the secret file", ex); }
        RefreshSetup();
    }

    private async void OnDownload(object sender, RoutedEventArgs e)
    {
        if (IsRunning) return;
        RefreshSetup();
        if (!DownloadButton.IsEnabled) return;
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
                Mark(entry, "", "SystemFillColorCriticalBrush", "Failed · " + FirstLine(result.Message));
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
            var result = new DownloadResult(false, "UNEXPECTED_ERROR", ErrorText.Describe(ex), new(), ex.ToString());
            Mark(entry, "", "SystemFillColorCriticalBrush", "Failed · " + FirstLine(result.Message));
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

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

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

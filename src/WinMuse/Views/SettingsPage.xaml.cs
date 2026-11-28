using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using WinMuse.Controls;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;
using WinMuse.Services.Audio;
using WinMuse.ViewModels;

namespace WinMuse.Views;

/// <summary>
/// Settings. The options and their rules live in <see cref="SettingsViewModel"/>; this page adds what needs a window:
/// pickers, the color flyouts, the Fetch confirmation and opening files and folders.
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsViewModel Vm { get; } = new(Svc.Settings, Svc.Library, Svc.Player, Svc.AutoFix);

    private bool _loadingAccent = true; // the color picker reports its first color as a change; that one is not the user's

    public SettingsPage()
    {
        InitializeComponent();
        ComponentCredits.ItemsSource = Credits.Components;
        ServiceCredits.ItemsSource = Credits.Services;
        SourceLink.NavigateUri = new Uri(Credits.SourceUrl);
        LicenseLink.NavigateUri = new Uri(Credits.LicenseUrl);
        IssuesLink.NavigateUri = new Uri(Credits.IssuesUrl);
        foreach (var (name, color) in SettingsViewModel.AccentSwatches)
        {
            var b = new Button
            {
                Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Padding = new Thickness(0), BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(color),
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(b, name);
            ToolTipService.SetToolTip(b, name);
            Effects.SetHoverScale(b, 1.15);
            b.Click += (_, _) => { _loadingAccent = true; AccentPicker.Color = color; _loadingAccent = false; Vm.SetCustomAccent(color); };
            AccentSwatches.Children.Add(b);
        }

        AcoustIdKey.Validator = k => Svc.Online.ValidateAcoustIdKeyAsync(k);
        AcoustIdKey.Save = k => Vm.AcoustIdApiKey = k;
        YouTubeKey.Validator = k => Svc.Online.ValidateYouTubeKeyAsync(k);
        YouTubeKey.Save = k => Vm.YouTubeApiKey = k;
        LastFmKey.Validator = k => Svc.Online.ValidateLastFmKeyAsync(k);
        LastFmKey.Save = k => Vm.LastFmApiKey = k;
        FanartKey.Validator = k => Svc.Online.ValidateFanartKeyAsync(k);
        FanartKey.Save = k => Vm.FanartApiKey = k;
        SpotifyKey.Placeholder = "Client ID";
        SpotifyKey.SecretPlaceholder = "Client secret";
        SpotifyKey.PairValidator = (id, secret) => Svc.Online.ValidateSpotifyAsync(id, secret);
        SpotifyKey.SavePair = Vm.SetSpotify;

        this.WhileLoaded(Vm.Attach, Vm.Detach);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Vm.Refresh();
        _loadingAccent = true;
        AccentPicker.Color = Vm.CustomAccent;
        _loadingAccent = false;
        AcoustIdKey.Load(Vm.AcoustIdApiKey);
        YouTubeKey.Load(Vm.YouTubeApiKey);
        LastFmKey.Load(Vm.LastFmApiKey);
        FanartKey.Load(Vm.FanartApiKey);
        SpotifyKey.Load(Vm.SpotifyClientId, Vm.SpotifyClientSecret);
        DeviceBox.ItemsSource = Vm.Devices;
        DeviceBox.SelectedItem = Vm.SelectedDevice;
        Effects.EntranceSlide(Root, 20);
    }

    // ---------------- Library ----------------

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        if (await Pickers.PickFolderAsync() is { } folder) await Vm.AddFolderAsync(folder);
    }

    private async void OnRemoveFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string folder) await Vm.RemoveFolderAsync(folder);
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await Vm.ScanAsync();
    private async void OnRestoreRemoved(object sender, RoutedEventArgs e) => await Vm.RestoreRemovedAsync();

    // ---------------- Appearance ----------------

    private void OnLogoStyle(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && Enum.TryParse<AppLogoStyle>(tag, out var style)) Vm.LogoStyle = style;
    }

    private readonly Debouncer _accentDebounce = new(TimeSpan.FromMilliseconds(120));

    private void OnAccentPicked(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loadingAccent) return;
        var c = args.NewColor;
        _accentDebounce.Run(() => App.UI.TryEnqueue(() => Vm.SetCustomAccent(c)));
    }

    private void OnGradientColor(object sender, RoutedEventArgs e)
    {
        var btn = (Button)sender;
        int index = int.Parse((string)btn.Tag, System.Globalization.CultureInfo.InvariantCulture);
        var picker = new ColorPicker
        {
            Color = index switch { 1 => Vm.GradientColor1, 2 => Vm.GradientColor2, _ => Vm.GradientColor3 },
            IsAlphaEnabled = false,
            ColorSpectrumShape = ColorSpectrumShape.Ring,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
        };
        var flyout = new Flyout { Content = picker };
        flyout.Closed += (_, _) => Vm.SetGradientColor(index, picker.Color);
        flyout.ShowAt(btn);
    }

    private async void OnPickWallpaper(object sender, RoutedEventArgs e)
    {
        if (await Pickers.PickImageAsync() is { } path) Vm.UseWallpaper(path);
    }

    // ---------------- Playback ----------------

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceBox.SelectedItem is AudioDevice d) Vm.SelectedDevice = d;
    }

    // ---------------- Metadata ----------------

    private async void OnFixAll(object sender, RoutedEventArgs e)
    {
        if (Vm.IsFetching) { Vm.StopFetch(); return; }
        var (pending, edited) = Vm.FetchPlan();
        if (pending == 0) { Vm.ShowNothingToFetch(); return; }
        if (!await TrackActions.Confirm("Fetch for the whole library?",
                $"{pending:N0} songs will be updated with online metadata, album art, artist images and lyrics." +
                (edited > 0 ? $" This includes {edited:N0} songs you edited; your changes to them will be replaced." : ""),
                "Fetch")) return;
        await Vm.FetchAllAsync();
    }

    // ---------------- Your music files ----------------

    /// <summary>Turned on by the user: open the card, so the choice of where edits go is right there.</summary>
    private void OnFileChangesToggled(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return; // set from the saved value as the page opens
        FileChangesCard.IsExpanded = ((ToggleSwitch)sender).IsOn;
    }

    // ---------------- Debug ----------------

    private void OnCreateTestPlaylists(object sender, RoutedEventArgs e)
    {
        var count = Svc.Library.Tracks.Count;
        if (!Vm.CreateTestPlaylists()) { Notifier.Show("No songs in the library"); return; }
        Notifier.Show("Created 10 test playlists", count < 20 ? $"Each has all {count} songs in random order." : "Each has 20 random songs.");
    }

    // ---------------- About ----------------

    private void OnOpenLog(object sender, RoutedEventArgs e) => Notifier.OpenLog.Run();

    /// <summary>Opens THIRD-PARTY-NOTICES.txt (installed next to the app) in the default text editor.</summary>
    private void OnOpenNotices(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Credits.NoticesPath) { UseShellExecute = true }); }
        catch (Exception ex) { AppPaths.Log("Open third-party notices: " + ex.Message); }
    }

    private void OnOpenData(object sender, RoutedEventArgs e)
    {
        try { Process.Start("explorer.exe", AppPaths.DataDir); } catch { }
    }
}

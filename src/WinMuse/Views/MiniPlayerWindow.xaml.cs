using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinMuse.Helpers;
using WinMuse.Services;

namespace WinMuse.Views;

/// <summary>Compact always-on-top player.</summary>
public sealed partial class MiniPlayerWindow : Window
{
    public PlaybackService Player => Svc.Player;

    public MiniPlayerWindow()
    {
        InitializeComponent();
        SpaceKey.Attach(Root);
        Title = Branding.Name;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragArea);
        var icon = Path.Combine(AppPaths.AssetsDir, "AppIcon.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        SystemBackdrop = new DesktopAcrylicBackdrop();

        AppWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);
        var scale = (App.MainWindow.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32((int)(400 * scale), (int)(170 * scale)));

        var main = (FrameworkElement)App.MainWindow.Content;
        Root.RequestedTheme = main.ActualTheme;
        var tb = AppWindow.TitleBar;
        tb.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        tb.ButtonForegroundColor = main.ActualTheme == ElementTheme.Light ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
    }

    private void OnPlayPause(object sender, RoutedEventArgs e) => Player.TogglePlayPause();
    private async void OnNext(object sender, RoutedEventArgs e) => await Player.NextAsync();
    private async void OnPrevious(object sender, RoutedEventArgs e) => await Player.PreviousAsync();

    private void OnExpand(object sender, RoutedEventArgs e)
    {
        App.MainWindow.BringToFront();
        Close();
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using WinMuse.Controls;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Dialogs;

/// <summary>
/// First launch: asks for the optional free API keys before the library is synced for the first time. Keys are only
/// saved with "Save and continue"; "Skip for now" saves nothing. Either way, syncing starts when the dialog closes.
/// </summary>
public sealed class ApiKeysDialog : ContentDialog
{
    private string? _acoustId, _youTube, _lastFm, _fanart, _spotifyId, _spotifySecret;

    private ApiKeysDialog()
    {
        XamlRoot = App.MainWindow.Content.XamlRoot;
        RequestedTheme = ((FrameworkElement)App.MainWindow.Content).ActualTheme;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 760.0;
        Title = "Improve song identification";
        PrimaryButtonText = "Save and continue";
        CloseButtonText = "Skip for now";
        DefaultButton = ContentDialogButton.Primary;

        var acoustId = new ApiKeyField { Validator = k => Svc.Online.ValidateAcoustIdKeyAsync(k), Save = k => _acoustId = k };
        var lastFm = new ApiKeyField { Validator = k => Svc.Online.ValidateLastFmKeyAsync(k), Save = k => _lastFm = k };
        var fanart = new ApiKeyField { Validator = k => Svc.Online.ValidateFanartKeyAsync(k), Save = k => _fanart = k };
        var youTube = new ApiKeyField { Validator = k => Svc.Online.ValidateYouTubeKeyAsync(k), Save = k => _youTube = k };
        var spotify = new ApiKeyField
        {
            Placeholder = "Client ID", SecretPlaceholder = "Client secret",
            PairValidator = (id, secret) => Svc.Online.ValidateSpotifyAsync(id, secret),
            SavePair = (id, secret) => { _spotifyId = id; _spotifySecret = secret; },
        };

        var intro = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "These keys are free and optional. They help Music identify your songs and find album art, artist photos and genres. " +
                   "Each key is verified as you paste it. You can add or change them later in Settings > API keys.",
        };

        var list = new StackPanel { Spacing = 10 };
        list.Children.Add(intro);
        list.Children.Add(Row("AcoustID (recommended)", "Identifies songs by their audio fingerprint, even when file names and tags are wrong.", "https://acoustid.org/new-application", acoustId));
        list.Children.Add(Row("Last.fm", "Detailed genres such as Tropical House, EDM or Metal.", "https://www.last.fm/api/account/create", lastFm));
        list.Children.Add(Row("Fanart.tv", "High-quality artist photos.", "https://fanart.tv/get-an-api-key/", fanart));
        list.Children.Add(Row("Spotify", "Artist photos for smaller and independent artists.", "https://developer.spotify.com/dashboard", spotify));
        list.Children.Add(Row("YouTube Data API", "Last-resort source for song details and images.", "https://console.cloud.google.com/apis/library/youtube.googleapis.com", youTube));

        Content = new ScrollViewer { Content = list, MaxHeight = 560, Padding = new Thickness(0, 0, 16, 0) };
        PrimaryButtonClick += (_, _) => Commit();
    }

    private static Grid Row(string name, string description, string url, ApiKeyField field)
    {
        var g = new Grid
        {
            Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 16, CornerRadius = new CornerRadius(8),
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var link = new Hyperlink { NavigateUri = new Uri(url) };
        link.Inlines.Add(new Run { Text = "Get a free key" });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        var desc = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Opacity = 0.8 };
        desc.Inlines.Add(new Run { Text = description + " " });
        desc.Inlines.Add(link);
        text.Children.Add(desc);
        g.Children.Add(text);
        Grid.SetColumn(field, 1);
        field.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(field);
        return g;
    }

    private void Commit() => Svc.Settings.Set(nameof(AppSettings.AcoustIdApiKey), s =>
    {
        s.AcoustIdApiKey = _acoustId;
        s.YouTubeApiKey = _youTube;
        s.LastFmApiKey = _lastFm;
        s.FanartApiKey = _fanart;
        s.SpotifyClientId = _spotifyId;
        s.SpotifyClientSecret = _spotifySecret;
    });

    /// <summary>True on the first launch when no key has been entered yet.</summary>
    public static bool ShouldShow(AppSettings s) =>
        !s.ApiKeysPrompted && new[] { s.AcoustIdApiKey, s.YouTubeApiKey, s.LastFmApiKey, s.FanartApiKey, s.SpotifyClientId }.All(string.IsNullOrWhiteSpace);

    public static async Task ShowOnceAsync()
    {
        try { await new ApiKeysDialog().ShowAsync(); }
        finally { Svc.Settings.Set(nameof(AppSettings.ApiKeysPrompted), s => s.ApiKeysPrompted = true); }
    }
}

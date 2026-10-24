using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinMuse.Controls;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Dialogs;

/// <summary>
/// Edits an album as a whole: name, album artist, cover and year. Saving applies them to every song on the album
/// (song titles, artists and the rest stay as they are). Nothing changes until Save.
/// </summary>
public sealed class EditAlbumDialog : ContentDialog
{
    private readonly Album _album;
    private readonly TagPicker _name = new() { Header = "Album name", Kind = TagKind.Album, Multiple = false };
    private readonly TagPicker _artist = new() { Header = "Album artist", Kind = TagKind.Artist, Multiple = false };
    private readonly TextBox _year = new()
    {
        Header = "Year", PlaceholderText = "e.g. 2019", Width = 140, HorizontalAlignment = HorizontalAlignment.Left,
        InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.Number) } },
    };
    private readonly ArtImage _cover = new() { Width = 180, Height = 180, CornerRadius = new CornerRadius(12), DecodeSize = 220, Glyph = "" };
    private readonly ProgressRing _busy = new() { Width = 32, Height = 32, IsActive = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _coverNote = new() { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly string _initialName, _initialArtist, _initialYear;
    private byte[]? _art; // new cover; empty = remove

    private EditAlbumDialog(Album album)
    {
        _album = album;
        XamlRoot = App.MainWindow.Content.XamlRoot;
        RequestedTheme = ((FrameworkElement)App.MainWindow.Content).ActualTheme;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 900.0;
        Title = "Edit album";
        PrimaryButtonText = "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        // Values the songs share (an album normally has one of each).
        var first = album.Tracks[0];
        _initialName = first.Album ?? "";
        _initialArtist = first.AlbumArtist ?? first.Artist ?? "";
        _initialYear = album.Year > 0 ? album.Year.ToString() : "";
        _name.SetCredit(_initialName);
        _artist.SetCredit(_initialArtist);
        _year.Text = _initialYear;
        _cover.Source = album.ArtPath;
        _cover.Seed = album.Key;

        // ---- Cover ----
        var choose = new Button { Content = Label("", "Choose image…"), HorizontalAlignment = HorizontalAlignment.Stretch };
        choose.Click += async (_, _) =>
        {
            var path = await Pickers.PickImageAsync();
            if (path != null) SetCover(await File.ReadAllBytesAsync(path));
        };
        var fetch = new Button { Content = Label("", "Fetch image"), HorizontalAlignment = HorizontalAlignment.Stretch };
        fetch.Click += async (_, _) =>
        {
            fetch.IsEnabled = false; _busy.IsActive = true; _coverNote.Visibility = Visibility.Collapsed;
            try
            {
                var artist = Value(_artist) ?? first.Artist;
                var (bytes, fromYouTube) = await Svc.Online.FetchCoverAsync(null, artist, Value(_name) ?? first.Album);
                if (bytes != null && fromYouTube) bytes = await ArtworkService.CropToSquareAsync(bytes);
                if (bytes != null) SetCover(bytes);
                else Note("No cover was found online. Check the album and artist names.");
            }
            catch (Exception ex) { Note("Fetch failed: " + ex.Message); }
            finally { fetch.IsEnabled = true; _busy.IsActive = false; }
        };
        var remove = new Button { Content = Label("", "Remove"), HorizontalAlignment = HorizontalAlignment.Stretch };
        remove.Click += (_, _) => { _art = Array.Empty<byte>(); _cover.Source = null; Note("The cover will be removed when you save."); };

        var coverBox = new Grid { Width = 180, Height = 180, HorizontalAlignment = HorizontalAlignment.Center, Children = { _cover, _busy } };
        var left = new StackPanel { Spacing = 8, Width = 190, Children = { coverBox, choose, fetch, remove, _coverNote } };

        // ---- Details ----
        var count = album.TrackCount;
        var note = new TextBlock
        {
            Text = $"Saving updates all {count} {(count == 1 ? "song" : "songs")} on this album. Song titles and artists stay as they are.",
            Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        var right = new StackPanel { Spacing = 14, Children = { _name, _artist, _year, note } };

        var body = new Grid { ColumnSpacing = 24 };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(right, 1);
        body.Children.Add(left); body.Children.Add(right);

        Content = new StackPanel { Width = 640, Children = { DialogChrome.Card("Album", "", body) } };
        DialogChrome.AddCloseButton(this);
        PrimaryButtonClick += OnSave;
    }

    public static async Task ShowAsync(Album album)
    {
        if (album.Tracks.Count == 0) return;
        await new EditAlbumDialog(album).ShowAsync();
    }

    private static StackPanel Label(string glyph, string text) => new()
    {
        Orientation = Orientation.Horizontal, Spacing = 8,
        Children = { new FontIcon { Glyph = glyph, FontSize = 14 }, new TextBlock { Text = text } },
    };

    private static string? Value(TagPicker p) => p.Credit.Trim() is { Length: > 0 } v ? v : null;

    private void Note(string text) { _coverNote.Text = text; _coverNote.Visibility = Visibility.Visible; }

    private void SetCover(byte[] bytes)
    {
        _art = bytes;
        _cover.Source = Svc.Art.Save(bytes);
        _coverNote.Visibility = Visibility.Collapsed;
    }

    private async void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var edit = new TagEdit();
            var name = _name.Credit.Trim();
            var artist = _artist.Credit.Trim();
            var year = _year.Text.Trim();
            if (name != _initialName) edit.Album = name;
            if (artist != _initialArtist) edit.AlbumArtist = artist;
            if (year != _initialYear) edit.Year = int.TryParse(year, out var y) ? y : 0;
            edit.Artwork = _art;
            if (!edit.HasMetadataFields && edit.Artwork == null) return; // nothing changed
            var errors = await Svc.Metadata.ApplyAsync(_album.Tracks.ToList(), edit, byUser: true);
            if (errors.Count > 0) Notifier.Error("Some files could not be updated", errors[0]);
            else Notifier.Success("Album updated", name.Length > 0 ? name : _album.Title);
        }
        catch (Exception ex) { Notifier.Error("Unable to update the album", ex.Message); }
        finally { deferral.Complete(); }
    }
}

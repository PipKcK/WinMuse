using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinMuse.Controls;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Dialogs;

/// <summary>
/// Edits an artist: their name (changed on every song that credits them; the songs keep their other artists), their
/// photo, and songs from the library to credit them on. Nothing changes until Save.
/// </summary>
public sealed class EditArtistDialog : ContentDialog
{
    private readonly string _oldName;
    private readonly TextBox _name = new() { Header = "Artist name" };
    private readonly TextBlock _nameNote = new() { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ArtImage _photo = new() { Width = 132, Height = 132, CornerRadius = new CornerRadius(66), DecodeSize = 160, Glyph = "" };
    private readonly TextBlock _photoNote = new() { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly ProgressRing _photoBusy = new() { Width = 28, Height = 28, IsActive = false };
    private readonly TextBox _filter = new() { PlaceholderText = "Search your songs by title, artist or album" };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Multiple, Height = 260 };
    private readonly TextBlock _count = new() { Opacity = 0.7 };
    private readonly List<Track> _selected = new();
    private byte[]? _image;
    private bool _imageChosen, _syncing;

    /// <summary>The artist's name after Save (the old name if it wasn't changed or the dialog was cancelled).</summary>
    public string ResultName { get; private set; }

    private EditArtistDialog(Artist artist)
    {
        _oldName = ResultName = artist.Name;
        XamlRoot = App.MainWindow.Content.XamlRoot;
        RequestedTheme = ((FrameworkElement)App.MainWindow.Content).ActualTheme;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 900.0;
        bool creating = artist.Name.Length == 0;
        Title = creating ? "New artist" : "Edit artist";
        PrimaryButtonText = creating ? "Create" : "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        _photo.Source = artist.ArtPath;
        _photo.Seed = artist.Name;
        _name.Text = artist.Name;
        int songs = artist.Tracks.Count;

        // ---- Photo ----
        var choose = new Button { Content = "Choose image…", HorizontalAlignment = HorizontalAlignment.Stretch };
        choose.Click += async (_, _) =>
        {
            var path = await Pickers.PickImageAsync();
            if (path == null) return;
            SetImage(await File.ReadAllBytesAsync(path), chosen: true);
        };
        var download = new Button { Content = "Fetch image", HorizontalAlignment = HorizontalAlignment.Stretch };
        download.Click += async (_, _) =>
        {
            download.IsEnabled = false; _photoBusy.IsActive = true; _photoNote.Visibility = Visibility.Collapsed;
            try
            {
                var name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : _oldName;
                if (await Svc.Metadata.DownloadArtistImageAsync(name, sample: artist.Tracks.FirstOrDefault()) is { } bytes) SetImage(bytes, chosen: false);
                else { _photoNote.Text = "No image was found online."; _photoNote.Visibility = Visibility.Visible; }
            }
            catch (Exception ex) { _photoNote.Text = "Download failed: " + ex.Message; _photoNote.Visibility = Visibility.Visible; }
            finally { download.IsEnabled = true; _photoBusy.IsActive = false; }
        };
        var photoBox = new Grid { Width = 132, Height = 132, Children = { _photo, _photoBusy } };
        _photoBusy.HorizontalAlignment = HorizontalAlignment.Center;
        _photoBusy.VerticalAlignment = VerticalAlignment.Center;
        var left = new StackPanel { Spacing = 8, Width = 150, Children = { photoBox, choose, download, _photoNote } };
        photoBox.HorizontalAlignment = HorizontalAlignment.Center;

        // ---- Name ----
        void UpdateNameNote()
        {
            var n = _name.Text.Trim();
            var existing = n.Length > 0 && !n.Equals(_oldName, StringComparison.OrdinalIgnoreCase) ? Svc.Library.FindArtist(n) : null;
            _nameNote.Text = creating
                ? existing != null ? $"{existing.Name} is already in your library: the songs you choose are added to them." : "Choose the songs this artist is credited on below."
                : existing != null ? $"{existing.Name} is already in your library: the {Songs(songs)} will be combined with theirs."
                : $"Renaming changes the artist on all {Songs(songs)} credited to {_oldName}. Other artists on those songs stay.";
            IsPrimaryButtonEnabled = n.Length > 0 && (!creating || _selected.Count > 0);
        }
        _name.TextChanged += (_, _) => { _photo.Seed = _name.Text; UpdateNameNote(); };
        _list.SelectionChanged += (_, _) => UpdateNameNote();
        UpdateNameNote();
        var right = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, Children = { _name, _nameNote } };

        var top = new Grid { ColumnSpacing = 24 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(right, 1);
        top.Children.Add(left); top.Children.Add(right);

        // ---- Add songs ----
        _list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <StackPanel Padding="0,6">
                    <TextBlock Text="{Binding DisplayTitle}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                    <TextBlock Text="{Binding DisplayArtist}" Opacity="0.7" FontSize="12" TextTrimming="CharacterEllipsis" />
                </StackPanel>
            </DataTemplate>
            """);
        _list.SelectionChanged += OnListSelection;
        _filter.TextChanged += (_, _) => Fill();

        var root = new StackPanel { Spacing = 12, Width = 660 };
        root.Children.Add(DialogChrome.Card("Artist", "", top));
        if (artist.Name != "Unknown Artist") // "Unknown Artist" just collects songs without one
        {
            var add = new StackPanel { Spacing = 10 };
            add.Children.Add(new TextBlock
            {
                Text = "Choose songs to credit to this artist. They keep their other artists.",
                Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            });
            add.Children.Add(_filter);
            add.Children.Add(_list);
            add.Children.Add(_count);
            root.Children.Add(DialogChrome.Card("Add songs", "", add));
        }
        Content = root;
        DialogChrome.AddCloseButton(this);

        Fill();
        PrimaryButtonClick += OnSave;
    }

    /// <summary>Creates an artist: a name, a photo, and the songs to credit them on.</summary>
    public static Task<string> ShowCreateAsync() => ShowAsync(new Artist { Name = "" });

    /// <summary>Shows the editor; returns the artist's name afterwards (the new name if it was renamed).</summary>
    public static async Task<string> ShowAsync(Artist artist)
    {
        var dlg = new EditArtistDialog(artist);
        await dlg.ShowAsync();
        return dlg.ResultName;
    }

    private static string Songs(int n) => n == 1 ? "1 song" : $"{n} songs";

    private void SetImage(byte[] bytes, bool chosen)
    {
        _image = bytes;
        _imageChosen = chosen;
        _photo.Source = Svc.Art.Save(bytes);
        _photoNote.Visibility = Visibility.Collapsed;
    }

    /// <summary>Songs not credited to the artist yet; selected ones stay listed while searching.</summary>
    private void Fill()
    {
        var q = _filter.Text.Trim();
        bool Credits(Track t) => t.ArtistNames.Contains(_oldName, StringComparer.OrdinalIgnoreCase);
        bool Hit(Track t) => t.DisplayTitle.Contains(q, StringComparison.CurrentCultureIgnoreCase) || t.DisplayArtist.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                             || t.DisplayAlbum.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        var others = Svc.Library.Tracks.Where(t => !Credits(t) && !_selected.Contains(t));
        var items = _selected.Concat(q.Length == 0
                ? others.OrderBy(t => t.DisplayTitle, StringComparer.CurrentCultureIgnoreCase).Take(300)
                : others.Where(Hit).Take(300))
            .ToList();
        _syncing = true;
        _list.ItemsSource = items;
        _list.UpdateLayout();
        foreach (var t in _selected) _list.SelectedItems.Add(t);
        _syncing = false;
        UpdateCount();
    }

    private void OnListSelection(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        foreach (Track t in e.AddedItems) if (!_selected.Contains(t)) _selected.Add(t);
        foreach (Track t in e.RemovedItems) _selected.Remove(t);
        UpdateCount();
    }

    private void UpdateCount() => _count.Text = _selected.Count == 0 ? "No songs selected" : $"{Songs(_selected.Count)} will be added";

    private async void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var name = _name.Text.Trim();
            var errors = new List<string>();
            if (_oldName.Length > 0 && name != _oldName)
            {
                errors.AddRange(await Svc.Metadata.RenameArtistAsync(_oldName, name));
                Svc.Favorites.RenameArtist(_oldName, name);
            }
            if (_image != null)
            {
                if (_imageChosen) Svc.Metadata.SetArtistImageByUser(name, _image);
                else if (Svc.Art.Save(_image) is { } path) Svc.Library.SetArtistImage(name, path);
            }
            if (_selected.Count > 0) errors.AddRange(await Svc.Metadata.AddArtistToTracksAsync(name, _selected));
            ResultName = name;
            if (errors.Count > 0) Notifier.Error("Some files could not be updated", errors[0]);
            else Notifier.Success("Artist updated", name);
        }
        catch (Exception ex) { Notifier.Error("Unable to update the artist", ex.Message); }
        finally { deferral.Complete(); }
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinMuse.Controls;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Dialogs;

/// <summary>
/// Builds an album out of songs in the library: sets album / album artist / year / genre / cover (and optionally track
/// numbers) on the chosen songs, written according to the metadata save mode.
/// </summary>
public sealed class CreateAlbumDialog : ContentDialog
{
    private readonly TextBox _title = new() { Header = "Album name", PlaceholderText = "New album" };
    private readonly TagPicker _artist = new() { Header = "Album artist", Kind = TagKind.Artist, Multiple = false };
    private readonly TextBox _year = new() { Header = "Year", InputScope = new Microsoft.UI.Xaml.Input.InputScope { Names = { new Microsoft.UI.Xaml.Input.InputScopeName(Microsoft.UI.Xaml.Input.InputScopeNameValue.Number) } } };
    private readonly TagPicker _genre = new() { Header = "Genres", Kind = TagKind.Genre };
    private readonly CheckBox _numbers = new() { Content = "Number tracks in the order selected", IsChecked = true };
    private readonly TextBox _filter = new() { PlaceholderText = "Search songs" };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Multiple, Height = 280 };
    private readonly TextBlock _count = new() { Opacity = 0.7 };
    private readonly ArtImage _cover = new() { Width = 132, Height = 132, CornerRadius = new CornerRadius(12), DecodeSize = 160, Seed = "new album" };
    private readonly List<Track> _selected = new();
    private byte[]? _art;
    private bool _syncing;

    private CreateAlbumDialog(IList<Track>? preselected)
    {
        XamlRoot = App.MainWindow.Content.XamlRoot;
        RequestedTheme = ((FrameworkElement)App.MainWindow.Content).ActualTheme;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 900.0;
        Title = "Create album";
        PrimaryButtonText = "Create";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        if (preselected != null) _selected.AddRange(preselected);

        var coverButton = new Button { Content = "Choose cover…", HorizontalAlignment = HorizontalAlignment.Stretch };
        coverButton.Click += async (_, _) =>
        {
            var path = await Pickers.PickImageAsync();
            if (path == null) return;
            _art = await File.ReadAllBytesAsync(path);
            _cover.Source = Svc.Art.Save(_art);
        };

        var left = new StackPanel { Spacing = 10, Width = 132, Children = { _cover, coverButton } };
        var fields = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
        fields.ColumnDefinitions.Add(new ColumnDefinition());
        fields.ColumnDefinitions.Add(new ColumnDefinition());
        fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        fields.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumnSpan(_title, 2);
        // Album artist and genres are pickers (library matches or new); year sits beside the album name.
        fields.ColumnDefinitions[1].Width = new GridLength(120);
        Grid.SetColumnSpan(_title, 1);
        Grid.SetColumn(_year, 1);
        Grid.SetRow(_artist, 1); Grid.SetColumnSpan(_artist, 2);
        Grid.SetRow(_genre, 2); Grid.SetColumnSpan(_genre, 2);
        _year.VerticalAlignment = VerticalAlignment.Top;
        fields.Children.Add(_title); fields.Children.Add(_artist); fields.Children.Add(_year); fields.Children.Add(_genre);

        var top = new Grid { ColumnSpacing = 20 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(fields, 1);
        top.Children.Add(left); top.Children.Add(fields);

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

        var bottom = new Grid();
        bottom.Children.Add(_count);
        _numbers.HorizontalAlignment = HorizontalAlignment.Right;
        bottom.Children.Add(_numbers);
        var songs = new StackPanel { Spacing = 10, Children = { _filter, _list, bottom } };
        var root = new StackPanel { Spacing = 12, Width = 680 };
        root.Children.Add(DialogChrome.Card("Album", "", top));
        root.Children.Add(DialogChrome.Card("Songs", "", songs));
        Content = root;
        DialogChrome.AddCloseButton(this);

        if (_selected.Count > 0)
        {
            _title.Text = _selected[0].Album ?? "";
            _artist.SetCredit(_selected[0].AlbumArtist ?? _selected[0].Artist ?? "");
            _genre.SetCredit(_selected[0].Genre);
        }
        _title.TextChanged += (_, _) => { _cover.Seed = _title.Text; Validate(); };
        Fill();
        Validate();
        PrimaryButtonClick += OnCreate;
    }

    public static async Task ShowAsync(IList<Track>? preselected)
    {
        var dlg = new CreateAlbumDialog(preselected);
        await dlg.ShowAsync();
    }

    private void Fill()
    {
        var q = _filter.Text.Trim();
        var items = (q.Length == 0
                ? _selected.Concat(Svc.Library.Tracks.Where(t => !_selected.Contains(t)).OrderBy(t => t.DisplayArtist).ThenBy(t => t.DisplayTitle).Take(300))
                : Svc.Library.Tracks.Where(t => t.DisplayTitle.Contains(q, StringComparison.CurrentCultureIgnoreCase) || t.DisplayArtist.Contains(q, StringComparison.CurrentCultureIgnoreCase) || t.DisplayAlbum.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(300))
            .ToList();
        _syncing = true;
        _list.ItemsSource = items;
        _list.UpdateLayout();
        foreach (var t in _selected) if (items.Contains(t)) _list.SelectedItems.Add(t);
        _syncing = false;
        UpdateCount();
    }

    private void OnListSelection(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        foreach (Track t in e.AddedItems) if (!_selected.Contains(t)) _selected.Add(t);
        foreach (Track t in e.RemovedItems) _selected.Remove(t);
        UpdateCount();
        Validate();
    }

    private void UpdateCount() => _count.Text = $"{_selected.Count} songs selected";

    private void Validate() => IsPrimaryButtonEnabled = _title.Text.Trim().Length > 0 && _selected.Count > 0;

    private async void OnCreate(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var common = new TagEdit
            {
                Album = _title.Text.Trim(),
                AlbumArtist = _artist.Credit.Trim().Length > 0 ? _artist.Credit.Trim() : "Various Artists",
                Year = int.TryParse(_year.Text, out var y) ? y : null,
                Genre = _genre.Credit.Trim().Length > 0 ? _genre.Credit.Trim() : null,
                Artwork = _art,
                TrackCount = _numbers.IsChecked == true ? _selected.Count : null,
            };
            var errors = new List<string>();
            if (_numbers.IsChecked == true)
            {
                for (int i = 0; i < _selected.Count; i++)
                {
                    var e = new TagEdit
                    {
                        Album = common.Album, AlbumArtist = common.AlbumArtist, Year = common.Year, Genre = common.Genre,
                        Artwork = common.Artwork, TrackCount = common.TrackCount, TrackNumber = i + 1, DiscNumber = 1,
                    };
                    errors.AddRange(await Svc.Metadata.ApplyAsync(new[] { _selected[i] }, e, byUser: true));
                }
            }
            else
            {
                errors.AddRange(await Svc.Metadata.ApplyAsync(_selected, common, byUser: true));
            }
            if (errors.Count > 0) Notifier.Error("Some files could not be updated", errors[0]);
            else Notifier.Success($"Album created: {common.Album}");
            App.MainWindow.Navigate(typeof(Views.AlbumDetailPage), _selected[0].AlbumKey);
        }
        catch (Exception ex) { Notifier.Error("Unable to create album", ex.Message); }
        finally { deferral.Complete(); }
    }
}

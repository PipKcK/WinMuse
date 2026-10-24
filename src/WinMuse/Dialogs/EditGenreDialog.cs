using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Dialogs;

/// <summary>
/// Edits a genre (its name, changed on every song that has it; their other genres stay) and adds songs to it. With no
/// genre given it creates a new one from the chosen songs. Nothing changes until Save.
/// </summary>
public sealed class EditGenreDialog : ContentDialog
{
    private readonly string _oldName;
    private readonly bool _creating;
    private readonly TextBox _name = new() { Header = "Genre name" };
    private readonly TextBlock _nameNote = new() { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox _filter = new() { PlaceholderText = "Search your songs by title, artist or album" };
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Multiple, Height = 280 };
    private readonly TextBlock _count = new() { Opacity = 0.7 };
    private readonly List<Track> _selected = new();
    private bool _syncing;

    private EditGenreDialog(string? genre)
    {
        _oldName = genre ?? "";
        _creating = genre == null;
        XamlRoot = App.MainWindow.Content.XamlRoot;
        RequestedTheme = ((FrameworkElement)App.MainWindow.Content).ActualTheme;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 900.0;
        Title = _creating ? "New genre" : "Edit genre";
        PrimaryButtonText = _creating ? "Create" : "Save";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        _name.Text = _oldName;
        int songs = Svc.Library.Tracks.Count(t => MetadataEditService.GenresOf(t.Genre).Contains(_oldName, StringComparer.OrdinalIgnoreCase));
        void Validate()
        {
            var n = _name.Text.Trim();
            var existing = n.Length > 0 && !n.Equals(_oldName, StringComparison.OrdinalIgnoreCase)
                ? Svc.Library.Genres.FirstOrDefault(g => g.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) : null;
            _nameNote.Text = _creating
                ? existing != null ? $"{existing.Name} already exists: the songs you choose are added to it." : "Choose the songs that belong to this genre below."
                : existing != null ? $"{existing.Name} already exists: the {Songs(songs)} will be combined with it."
                : $"Renaming changes the genre on all {Songs(songs)} that have it. Their other genres stay.";
            IsPrimaryButtonEnabled = n.Length > 0 && (!_creating || _selected.Count > 0);
        }
        _name.TextChanged += (_, _) => Validate();

        _list.ItemTemplate = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load(
            """
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <StackPanel Padding="0,6">
                    <TextBlock Text="{Binding DisplayTitle}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
                    <TextBlock Text="{Binding DisplayArtist}" Opacity="0.7" FontSize="12" TextTrimming="CharacterEllipsis" />
                </StackPanel>
            </DataTemplate>
            """);
        _list.SelectionChanged += (_, e) =>
        {
            if (_syncing) return;
            foreach (Track t in e.AddedItems) if (!_selected.Contains(t)) _selected.Add(t);
            foreach (Track t in e.RemovedItems) _selected.Remove(t);
            UpdateCount();
            Validate();
        };
        _filter.TextChanged += (_, _) => Fill();

        var add = new StackPanel { Spacing = 10 };
        add.Children.Add(new TextBlock { Text = "Choose songs to add to this genre. They keep their other genres.", Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        add.Children.Add(_filter);
        add.Children.Add(_list);
        add.Children.Add(_count);

        Content = new StackPanel
        {
            Spacing = 12, Width = 620,
            Children =
            {
                DialogChrome.Card("Genre", "", new StackPanel { Spacing = 8, Children = { _name, _nameNote } }),
                DialogChrome.Card("Add songs", "", add),
            },
        };
        DialogChrome.AddCloseButton(this);
        Fill();
        Validate();
        PrimaryButtonClick += OnSave;
    }

    /// <summary>Edits <paramref name="genre"/>, or creates a new genre when it is null.</summary>
    public static async Task ShowAsync(string? genre) => await new EditGenreDialog(genre).ShowAsync();

    private static string Songs(int n) => n == 1 ? "1 song" : $"{n} songs";

    /// <summary>Songs without the genre yet; selected ones stay listed while searching.</summary>
    private void Fill()
    {
        var q = _filter.Text.Trim();
        bool Has(Track t) => !_creating && MetadataEditService.GenresOf(t.Genre).Contains(_oldName, StringComparer.OrdinalIgnoreCase);
        bool Hit(Track t) => t.DisplayTitle.Contains(q, StringComparison.CurrentCultureIgnoreCase) || t.DisplayArtist.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                             || t.DisplayAlbum.Contains(q, StringComparison.CurrentCultureIgnoreCase);
        var others = Svc.Library.Tracks.Where(t => !Has(t) && !_selected.Contains(t));
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

    private void UpdateCount() => _count.Text = _selected.Count == 0 ? "No songs selected" : $"{Songs(_selected.Count)} will be added";

    private async void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var name = _name.Text.Trim();
            var errors = new List<string>();
            if (!_creating && name != _oldName) errors.AddRange(await Svc.Metadata.RenameGenreAsync(_oldName, name));
            if (_selected.Count > 0) errors.AddRange(await Svc.Metadata.AddGenreToTracksAsync(name, _selected));
            if (errors.Count > 0) Notifier.Error("Some files could not be updated", errors[0]);
            else Notifier.Success(_creating ? "Genre created" : "Genre updated", name);
        }
        catch (Exception ex) { Notifier.Error("Unable to update the genre", ex.Message); }
        finally { deferral.Complete(); }
    }
}

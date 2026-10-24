using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinMuse.Helpers;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Dialogs;

/// <summary>Create / edit a playlist. Smart playlists get a rule builder whose results preview live.</summary>
public sealed class PlaylistDialog : ContentDialog
{
    private readonly TextBox _name = new() { Header = "Name", PlaceholderText = "My playlist" };
    private readonly TextBox _desc = new() { Header = "Description", PlaceholderText = "Optional", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 64 };
    private readonly StackPanel _rules = new() { Spacing = 8 };
    private readonly ComboBox _match = new() { Items = { "all", "any" }, SelectedIndex = 0, MinWidth = 80 };
    private readonly NumberBox _limit = new() { Header = "Limit (0 = no limit)", Minimum = 0, Maximum = 10000, Value = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ComboBox _sort = new() { Header = "Order", MinWidth = 180 };
    private readonly TextBlock _preview = new() { Opacity = 0.7 };
    private readonly bool _smart;

    private static readonly (SmartField field, string label)[] Fields =
    {
        (SmartField.Genre, "Genre"), (SmartField.Artist, "Artist"), (SmartField.Album, "Album"), (SmartField.AlbumArtist, "Album artist"),
        (SmartField.Title, "Title"), (SmartField.Year, "Year"), (SmartField.PlayCount, "Play count"), (SmartField.Rating, "Rating"),
        (SmartField.AddedDaysAgo, "Added (days ago)"), (SmartField.LastPlayedDaysAgo, "Last played (days ago)"),
        (SmartField.DurationSeconds, "Length (seconds)"), (SmartField.Codec, "Format"), (SmartField.Path, "File path"),
    };

    private static readonly (SmartOperator op, string label)[] Operators =
    {
        (SmartOperator.Contains, "contains"), (SmartOperator.NotContains, "doesn't contain"), (SmartOperator.Is, "is"), (SmartOperator.IsNot, "is not"),
        (SmartOperator.StartsWith, "starts with"), (SmartOperator.GreaterThan, "is greater than"), (SmartOperator.LessThan, "is less than"),
    };

    private PlaylistDialog(bool smart, Playlist? existing)
    {
        _smart = smart;
        XamlRoot = App.MainWindow.Content.XamlRoot;
        RequestedTheme = ((FrameworkElement)App.MainWindow.Content).ActualTheme;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        Resources["ContentDialogMaxWidth"] = 760.0;
        Title = existing != null ? "Edit playlist" : smart ? "New smart playlist" : "New playlist";
        PrimaryButtonText = existing != null ? "Save" : "Create";
        CloseButtonText = "Cancel";
        DefaultButton = ContentDialogButton.Primary;

        var root = new StackPanel { Spacing = 14, Width = smart ? 640 : 420 };
        root.Children.Add(_name);
        root.Children.Add(_desc);

        if (smart)
        {
            foreach (var s in new[] { "Random", "Title", "Artist", "Most played", "Recently added", "Recently played", "Rating" }) _sort.Items.Add(s);
            _sort.SelectedIndex = 0;

            var matchRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            matchRow.Children.Add(new TextBlock { Text = "Include songs that match", VerticalAlignment = VerticalAlignment.Center });
            matchRow.Children.Add(_match);
            matchRow.Children.Add(new TextBlock { Text = "of these rules:", VerticalAlignment = VerticalAlignment.Center });
            root.Children.Add(matchRow);
            root.Children.Add(new ScrollViewer { Content = _rules, MaxHeight = 260 });

            var add = new HyperlinkButton { Content = "Add rule" };
            add.Click += (_, _) => { AddRule(new SmartRule()); UpdatePreview(); };
            root.Children.Add(add);

            var opts = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            opts.Children.Add(_sort);
            opts.Children.Add(_limit);
            root.Children.Add(opts);
            root.Children.Add(_preview);

            _match.SelectionChanged += (_, _) => UpdatePreview();
            _limit.ValueChanged += (_, _) => UpdatePreview();
        }

        if (existing != null)
        {
            _name.Text = existing.Name;
            _desc.Text = existing.Description ?? "";
            if (existing.Rules is { } r)
            {
                _match.SelectedIndex = r.MatchAll ? 0 : 1;
                _limit.Value = r.Limit;
                _sort.SelectedItem = r.SortBy;
                foreach (var rule in r.Rules) AddRule(rule);
            }
        }
        else if (smart)
        {
            AddRule(new SmartRule { Field = SmartField.PlayCount, Operator = SmartOperator.GreaterThan, Value = "3" });
        }

        _name.TextChanged += (_, _) => IsPrimaryButtonEnabled = _name.Text.Trim().Length > 0;
        IsPrimaryButtonEnabled = _name.Text.Trim().Length > 0;
        Content = root;
        DialogChrome.AddCloseButton(this);
        UpdatePreview();
    }

    private void AddRule(SmartRule rule)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var field = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var f in Fields) field.Items.Add(f.label);
        field.SelectedIndex = Math.Max(0, Array.FindIndex(Fields, f => f.field == rule.Field));
        var op = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var o in Operators) op.Items.Add(o.label);
        op.SelectedIndex = Math.Max(0, Array.FindIndex(Operators, o => o.op == rule.Operator));
        var value = new TextBox { Text = rule.Value, PlaceholderText = "Value" };
        var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(8) };

        Grid.SetColumn(op, 1); Grid.SetColumn(value, 2); Grid.SetColumn(remove, 3);
        row.Children.Add(field); row.Children.Add(op); row.Children.Add(value); row.Children.Add(remove);
        row.Tag = (field, op, value);
        remove.Click += (_, _) => { _rules.Children.Remove(row); UpdatePreview(); };
        field.SelectionChanged += (_, _) => UpdatePreview();
        op.SelectionChanged += (_, _) => UpdatePreview();
        value.TextChanged += (_, _) => UpdatePreview();
        _rules.Children.Add(row);
    }

    private SmartPlaylistDefinition ReadRules()
    {
        var def = new SmartPlaylistDefinition
        {
            MatchAll = _match.SelectedIndex == 0,
            Limit = double.IsNaN(_limit.Value) ? 0 : (int)_limit.Value,
            SortBy = _sort.SelectedItem as string ?? "Random",
        };
        foreach (var row in _rules.Children.OfType<Grid>())
        {
            var (field, op, value) = ((ComboBox, ComboBox, TextBox))row.Tag;
            def.Rules.Add(new SmartRule
            {
                Field = Fields[Math.Max(0, field.SelectedIndex)].field,
                Operator = Operators[Math.Max(0, op.SelectedIndex)].op,
                Value = value.Text.Trim(),
            });
        }
        return def;
    }

    private void UpdatePreview()
    {
        if (!_smart) return;
        var n = ReadRules().Apply(Svc.Library.Tracks).Count();
        _preview.Text = $"{n:N0} matching songs";
    }

    public static Task<Playlist?> ShowCreateAsync(IEnumerable<Track>? tracks) => ShowNewAsync(false, tracks);
    public static Task<Playlist?> ShowCreateSmartAsync() => ShowNewAsync(true, null);

    private static async Task<Playlist?> ShowNewAsync(bool smart, IEnumerable<Track>? tracks)
    {
        var dlg = new PlaylistDialog(smart, null);
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;
        return Svc.Library.CreatePlaylist(dlg._name.Text.Trim(), NullIfEmpty(dlg._desc.Text), smart ? dlg.ReadRules() : null, tracks);
    }

    public static async Task ShowEditAsync(Playlist p)
    {
        var dlg = new PlaylistDialog(p.IsSmart, p);
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        p.Name = dlg._name.Text.Trim();
        p.Description = NullIfEmpty(dlg._desc.Text);
        if (p.IsSmart) p.Rules = dlg.ReadRules();
        Svc.Library.UpdatePlaylist(p);
    }

    private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinMuse.Helpers;
using WinMuse.Services;

namespace WinMuse.Controls;

public enum TagKind { Artist, Album, Genre }

/// <summary>
/// A tag field as chips: Artist (several), Album artist (one), Album (one) or Genre (several). Typing searches the
/// library: each match shows a check box and choosing it adds or removes it (with one allowed, it replaces the
/// current value); when nothing matches exactly, the typed text can be added as a new one (Enter, "," or ";").
/// Backspace in the empty box removes the last chip. Several values are written as "A; B".
/// With one value allowed (album, album artist) the value is the field's own text instead of a chip: the field's
/// clear button removes it, and choosing another replaces it.
/// </summary>
public sealed class TagPicker : UserControl
{
    private readonly TextBlock _header = new();
    private readonly WrapPanel _chips = new() { Spacing = 6, LineSpacing = 6 };
    private readonly AutoSuggestBox _box = new()
    {
        QueryIcon = new FontIcon { Glyph = "", FontSize = 12 },
        UpdateTextOnSelect = false,
    };
    private readonly List<string> _values = new();
    private string _original = "";
    private List<string> _originalValues = new();
    private TagKind _kind = TagKind.Artist;

    public TagPicker()
    {
        // Same header spacing as a TextBox's Header, so pickers and text boxes line up in a row.
        var root = new StackPanel { Spacing = 8 };
        root.Children.Add(_header);
        root.Children.Add(_box);
        root.Children.Add(_chips);
        Content = root;
        IsTabStop = false;
        _chips.Visibility = Visibility.Collapsed;
        UpdatePlaceholder();
        // Chip colors are picked in code: pick them again once the dialog's theme is known, and when it changes.
        Loaded += (_, _) => RenderChips();
        ActualThemeChanged += (_, _) => RenderChips();

        _box.TextChanged += (_, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
            if (!Multiple)
            {
                // The text is the value (typing a new name, or clearing the field, changes it).
                _values.Clear();
                if (_box.Text.Trim().Length > 0) _values.Add(_box.Text.Trim());
                Suggest();
                return;
            }
            // "," or ";" finishes a value, like Enter (album names may contain commas, so not for albums).
            var text = _box.Text;
            int cut = _kind == TagKind.Album ? text.IndexOf(';') : text.IndexOfAny(new[] { ',', ';' });
            if (cut >= 0)
            {
                Commit(text[..cut]);
                _box.Text = text[(cut + 1)..].TrimStart();
            }
            Suggest();
        };
        _box.QuerySubmitted += (_, e) =>
        {
            if (!Multiple)
            {
                if (e.ChosenSuggestion is FrameworkElement { Tag: Suggestion chosen }) Add(chosen.Value);
                else Commit(_box.Text);
                _box.IsSuggestionListOpen = false;
                return;
            }
            if (e.ChosenSuggestion is FrameworkElement { Tag: Suggestion s }) Toggle(s.Value);
            else Commit(_box.Text);
            _box.Text = "";
            Suggest();
        };
        _box.AddHandler(KeyDownEvent, new KeyEventHandler((_, e) =>
        {
            if (Multiple && e.Key == Windows.System.VirtualKey.Back && _box.Text.Length == 0 && _values.Count > 0)
            {
                _values.RemoveAt(_values.Count - 1);
                RenderChips();
            }
        }), true);
    }

    public string Header { get => _header.Text; set => _header.Text = value; }

    /// <summary>What the field holds (where suggestions come from).</summary>
    public TagKind Kind { get => _kind; set { _kind = value; UpdatePlaceholder(); } }

    /// <summary>False: one value only; choosing another replaces it.</summary>
    public bool Multiple { get; set; } = true;

    public string PlaceholderText { get => _box.PlaceholderText; set => _box.PlaceholderText = value; }

    private string Noun => _kind switch { TagKind.Album => "album", TagKind.Genre => "genre", _ => "artist" };

    private void UpdatePlaceholder() => _box.PlaceholderText = $"Search or add {(_kind == TagKind.Genre ? "a" : "an")} {Noun}";

    /// <summary>The text to edit (e.g. artist "A & B" shows two chips).</summary>
    public void SetCredit(string? credit)
    {
        _original = credit ?? "";
        _originalValues = Split(credit);
        _values.Clear();
        _values.AddRange(_originalValues);
        RenderChips();
    }

    /// <summary>
    /// The text to save: unchanged values keep their original text ("A & B"); otherwise the values as separate
    /// entries ("A; B"). Empty when everything was removed.
    /// </summary>
    public string Credit => _values.SequenceEqual(_originalValues) ? _original : string.Join("; ", _values);

    private List<string> Split(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        if (!Multiple) return new List<string> { text.Trim() };
        return _kind == TagKind.Genre
            ? text.Split(new[] { ';', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : ArtistCredit.Split(text);
    }

    private void Commit(string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return;
        // An existing value keeps its spelling.
        Add(Library().FirstOrDefault(o => o.Value.Equals(value, StringComparison.OrdinalIgnoreCase))?.Value ?? value);
    }

    private void Toggle(string value)
    {
        int i = _values.FindIndex(v => v.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) { _values.RemoveAt(i); RenderChips(); }
        else Add(value);
    }

    private void Add(string value)
    {
        if (!Multiple) { _values.Clear(); _values.Add(value); RenderChips(); return; }
        if (_values.Contains(value, StringComparer.OrdinalIgnoreCase)) return;
        _values.Add(value);
        RenderChips();
    }

    // ---------------- Library values ----------------

    private sealed record Option(string Value, string Detail, string? Art, int Weight);

    private IEnumerable<Option> Library() => _kind switch
    {
        TagKind.Album => Svc.Library.Albums
            .Where(a => a.Title != "Unknown Album")
            .Select(a => new Option(a.Title, $"{a.Artist} · {Count(a.TrackCount)}", a.ArtPath, a.TrackCount)),
        TagKind.Genre => Svc.Library.Genres
            .Where(g => g.Name != "Unknown")
            .Select(g => new Option(g.Name, Count(g.Tracks.Count), g.Art1, g.Tracks.Count)),
        _ => Svc.Library.Artists
            .Where(a => a.Name is not ("Unknown Artist" or "Various Artists"))
            .Select(a => new Option(a.Name, Count(a.Tracks.Count), a.ArtPath, a.Tracks.Count)),
    };

    private static string Count(int n) => n == 1 ? "1 song" : $"{n} songs";

    private Option? Find(string value) => Library().FirstOrDefault(o => o.Value.Equals(value, StringComparison.OrdinalIgnoreCase));

    private string Glyph => _kind switch { TagKind.Album => "", TagKind.Genre => "", _ => "" };

    // ---------------- Suggestions ----------------

    private sealed record Suggestion(string Value, bool IsNew);

    private void Suggest()
    {
        var q = _box.Text.Trim();
        if (q.Length == 0) { _box.ItemsSource = null; _box.IsSuggestionListOpen = false; return; }
        bool exists = Library().Any(o => o.Value.Equals(q, StringComparison.OrdinalIgnoreCase));
        var matches = Library()
            .Where(o => o.Value.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(o => o.Value.StartsWith(q, StringComparison.CurrentCultureIgnoreCase))
            .ThenByDescending(o => o.Weight)
            .Take(8)
            .ToList();
        var items = new List<FrameworkElement>();
        foreach (var o in matches)
            items.Add(Row(new Suggestion(o.Value, false), o.Art, o.Detail, _values.Contains(o.Value, StringComparer.OrdinalIgnoreCase)));
        if (!exists && (!Multiple || !_values.Contains(q, StringComparer.OrdinalIgnoreCase)))
            items.Add(Row(new Suggestion(q, true), null, $"Add as a new {Noun}", false));
        _box.ItemsSource = items;
        _box.IsSuggestionListOpen = items.Count > 0;
    }

    private FrameworkElement Row(Suggestion s, string? art, string detail, bool on)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 4, 0, 4), Tag = s };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        FrameworkElement mark = s.IsNew
            ? new Grid
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(4),
                Background = (Brush)Application.Current.Resources["AccentGradientBrush"],
                Children = { new FontIcon { Glyph = "", FontSize = 10, Foreground = new SolidColorBrush(Microsoft.UI.Colors.White) } },
            }
            : Multiple
                ? new CheckBox { IsChecked = on, IsHitTestVisible = false, IsTabStop = false, MinWidth = 0, Padding = new Thickness(0) }
                : new RadioButton { IsChecked = on, IsHitTestVisible = false, IsTabStop = false, MinWidth = 0, Padding = new Thickness(0) };
        mark.VerticalAlignment = VerticalAlignment.Center;
        var avatar = Avatar(s.Value, art, 28);
        Grid.SetColumn(avatar, 1);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = s.Value, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Children.Add(new TextBlock { Text = detail, FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(text, 2);
        grid.Children.Add(mark); grid.Children.Add(avatar); grid.Children.Add(text);
        return grid;
    }

    /// <summary>Round for artists, rounded square for albums and genres.</summary>
    private ArtImage Avatar(string value, string? art, double size) => new()
    {
        Width = size, Height = size, DecodeSize = (int)(size * 1.4),
        CornerRadius = new CornerRadius(_kind == TagKind.Artist ? size / 2 : 5),
        Source = art, Seed = value, Glyph = Glyph,
    };

    // ---------------- Chips ----------------

    private void RenderChips()
    {
        if (!Multiple)
        {
            // One value: shown as the field's text (programmatic, so no suggestions open).
            var v = _values.FirstOrDefault() ?? "";
            if (_box.Text != v) _box.Text = v;
            _chips.Visibility = Visibility.Collapsed;
            return;
        }
        _chips.Children.Clear();
        foreach (var v in _values) _chips.Children.Add(Chip(v));
        _chips.Visibility = _values.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private Border Chip(string value)
    {
        var known = Find(value);
        var remove = new Button
        {
            Style = (Style)Application.Current.Resources["IconButtonStyle"],
            Width = 22, Height = 22, CornerRadius = new CornerRadius(11), FontSize = 9, Content = "",
        };
        ToolTipService.SetToolTip(remove, $"Remove {value}");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, $"Remove {value}");
        remove.Click += (_, _) => Toggle(value);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(Avatar(value, known?.Art, 22));
        row.Children.Add(new TextBlock { Text = value, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis });
        if (known == null) // new to the library
            row.Children.Add(new TextBlock { Text = "new", FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(remove);
        return new Border
        {
            Child = row,
            Padding = new Thickness(3),
            CornerRadius = new CornerRadius(15),
            Background = ThemeRes.Brush("SubtleFillColorSecondaryBrush", this),
            BorderBrush = ThemeRes.Brush("ControlStrokeColorDefaultBrush", this),
            BorderThickness = new Thickness(1),
        };
    }
}

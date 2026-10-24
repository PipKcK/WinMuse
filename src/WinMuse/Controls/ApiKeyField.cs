using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinMuse.Helpers;

namespace WinMuse.Controls;

/// <summary>
/// API key input with automatic verification: the key is saved and checked shortly after typing stops,
/// when the page opens with a saved key, and on demand with the Verify button.
/// </summary>
public sealed class ApiKeyField : UserControl
{
    private readonly PasswordBox _box = new() { Width = 260, PlaceholderText = "API key" };
    private readonly PasswordBox _secret = new() { Width = 260, PlaceholderText = "Client secret", Visibility = Visibility.Collapsed };
    private readonly Button _verify = new() { Content = "Verify" };
    private readonly ProgressRing _ring = new() { Width = 14, Height = 14, IsActive = false };
    private readonly FontIcon _icon = new() { FontSize = 14, Visibility = Visibility.Collapsed };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 340 };
    private readonly StackPanel _statusRow;
    private readonly Debouncer _debounce = new(TimeSpan.FromMilliseconds(900));
    private bool _loading;
    private int _version;

    /// <summary>Checks a key online and returns (valid, message).</summary>
    public Func<string, Task<(bool ok, string message)>>? Validator { get; set; }
    /// <summary>Persists the key (null when cleared).</summary>
    public Action<string?>? Save { get; set; }

    /// <summary>Two-part credentials (e.g. Spotify client ID + secret): checked together, saved together.</summary>
    public Func<string, string, Task<(bool ok, string message)>>? PairValidator { get; set; }
    public Action<string?, string?>? SavePair { get; set; }

    public string Placeholder { set => _box.PlaceholderText = value; }

    /// <summary>Shows a second box for a client secret.</summary>
    public string? SecretPlaceholder
    {
        set { _secret.PlaceholderText = value ?? ""; _secret.Visibility = value == null ? Visibility.Collapsed : Visibility.Visible; }
    }

    public ApiKeyField()
    {
        _status.Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"];
        _statusRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed, Children = { _ring, _icon, _status } };
        Content = new StackPanel
        {
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _box, _verify } }, _secret, _statusRow },
        };
        _box.PasswordChanged += (_, _) => OnChanged();
        _secret.PasswordChanged += (_, _) => OnChanged();
        _secret.HorizontalAlignment = HorizontalAlignment.Left;
        _verify.Click += async (_, _) => await VerifyAsync();
    }

    /// <summary>Shows the saved key and verifies it in the background.</summary>
    public void Load(string? key, string? secret = null)
    {
        _loading = true;
        _box.Password = key ?? "";
        _secret.Password = secret ?? "";
        _loading = false;
        _statusRow.Visibility = Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(key)) _ = VerifyAsync();
    }

    private void OnChanged()
    {
        if (_loading) return;
        var key = _box.Password.Trim();
        var secret = _secret.Password.Trim();
        Save?.Invoke(key.Length == 0 ? null : key);
        SavePair?.Invoke(key.Length == 0 ? null : key, secret.Length == 0 ? null : secret);
        _statusRow.Visibility = Visibility.Collapsed;
        bool ready = key.Length > 0 && (_secret.Visibility != Visibility.Visible || secret.Length > 0);
        if (ready) _debounce.Run(() => App.UI.TryEnqueue(async () => { if (_box.Password.Trim() == key && _secret.Password.Trim() == secret) await VerifyAsync(); }));
    }

    public async Task VerifyAsync()
    {
        if (Validator == null && PairValidator == null) return;
        var key = _box.Password.Trim();
        var secret = _secret.Password.Trim();
        var version = ++_version;
        _statusRow.Visibility = Visibility.Visible;
        _ring.IsActive = true;
        _icon.Visibility = Visibility.Collapsed;
        _status.Text = "Verifying…";
        _verify.IsEnabled = false;
        (bool ok, string message) result;
        try { result = PairValidator != null ? await PairValidator(key, secret) : await Validator!(key); }
        catch (Exception ex) { result = (false, ex.Message); }
        if (version != _version) return; // a newer check started
        _ring.IsActive = false;
        _verify.IsEnabled = true;
        _icon.Visibility = Visibility.Visible;
        _icon.Glyph = result.ok ? "" : "";
        _icon.Foreground = ThemeRes.Brush(result.ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush", this);
        _status.Text = result.message;
    }
}

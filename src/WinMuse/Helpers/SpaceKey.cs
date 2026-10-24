using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using WinMuse.Services;

namespace WinMuse.Helpers;

/// <summary>
/// Space plays or pauses wherever you are (song lists, buttons, dialogs, the mini player), except while typing in a
/// text field. Attached to a root element's PreviewKeyDown, so it runs before a focused list row or button would
/// treat Space as a click.
/// </summary>
public static class SpaceKey
{
    public static void Attach(UIElement root) => root.PreviewKeyDown += OnPreviewKeyDown;

    private static void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Space || sender is not UIElement root || IsTyping(root.XamlRoot)) return;
        e.Handled = true;
        if (e.KeyStatus.WasKeyDown) return; // held down: toggle once
        Svc.Player.TogglePlayPause();
    }

    public static bool IsTyping(XamlRoot? root) =>
        root != null && FocusManager.GetFocusedElement(root) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox;
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinMuse.Models;
using WinMuse.Services;

namespace WinMuse.Helpers;

/// <summary>
/// How edit dialogs (song editor, album, artist, genre, new album) save, following Settings &gt; Your music files:
/// <list type="bullet">
/// <item>Changing files off, or App only: one Save button; edits stay in Music's library.</item>
/// <item>Files only: one Save button; edits are written into the files' tags.</item>
/// <item>Ask each time: two buttons, "Save to library" and "Save to files".</item>
/// </list>
/// A short note in the dialog always says where the edits will go.
/// </summary>
public static class SaveDestinationUi
{
    /// <summary>
    /// Wires the dialog's save button(s) to <paramref name="save"/>, which receives where to save (null: the default from
    /// Settings). In Ask each time, the primary and secondary buttons become <paramref name="toLibrary"/> and
    /// <paramref name="toFiles"/>.
    /// </summary>
    public static void Attach(ContentDialog dialog, string toLibrary, string toFiles,
        Action<ContentDialog, ContentDialogButtonClickEventArgs, EditDestination?> save)
    {
        if (!Svc.Metadata.AsksForDestination)
        {
            dialog.PrimaryButtonClick += (s, a) => save(s, a, null);
            return;
        }
        dialog.PrimaryButtonText = toLibrary;
        dialog.SecondaryButtonText = toFiles;
        dialog.DefaultButton = ContentDialogButton.Primary;
        dialog.PrimaryButtonClick += (s, a) => save(s, a, EditDestination.Library);
        dialog.SecondaryButtonClick += (s, a) => save(s, a, EditDestination.Files);
        // Dialogs enable Save as their fields become valid: "Save to files" follows it.
        dialog.IsSecondaryButtonEnabled = dialog.IsPrimaryButtonEnabled;
        dialog.RegisterPropertyChangedCallback(ContentDialog.IsPrimaryButtonEnabledProperty,
            (d, _) => ((ContentDialog)d).IsSecondaryButtonEnabled = ((ContentDialog)d).IsPrimaryButtonEnabled);
    }

    /// <summary>Where an edit sent to <paramref name="destination"/> actually goes (files only while changing them is allowed).</summary>
    public static EditDestination Resolve(EditDestination? destination) =>
        Svc.Metadata.FileChangesAllowed && (destination ?? Svc.Metadata.DefaultDestination) == EditDestination.Files
            ? EditDestination.Files : EditDestination.Library;

    /// <summary>The second line of the "saved" notification.</summary>
    public static string SavedText(EditDestination? destination) =>
        Resolve(destination) == EditDestination.Files ? "Written into the files' tags." : "Saved in your library. Your music files weren't changed.";

    /// <summary>The note shown in edit dialogs: where edits will go.</summary>
    public static FrameworkElement CreateNote()
    {
        var (glyph, text) = Svc.Metadata.AsksForDestination
            ? ("", "Choose where to save: your library, or the files' own tags.")
            : Svc.Metadata.DefaultDestination == EditDestination.Files
                ? ("", "Edits are written into the files' tags.")
                : ("", "Edits are saved in your library. Your music files aren't changed.");
        // The dialog's own text color, softened: follows light/dark like everything else in the dialog.
        var icon = new FontIcon { Glyph = glyph, FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var label = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Opacity = 0.75, Children = { icon, label } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, text);
        return row;
    }
}

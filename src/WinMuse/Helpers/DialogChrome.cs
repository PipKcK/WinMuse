using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinMuse.Helpers;

public static class DialogChrome
{
    /// <summary>
    /// Puts a ✕ button at the top right of a dialog, next to its title; it closes the dialog like Cancel.
    /// Call after the title and content are set. The title area is left-aligned in the dialog template, so it is
    /// stretched to the content's width.
    /// </summary>
    /// <summary>A section card for edit dialogs (same look as the song editor): icon, title, then the content.</summary>
    public static Border Card(string title, string glyph, UIElement content)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
        head.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["EditorSectionTitleStyle"] });
        return new Border
        {
            Style = (Style)Application.Current.Resources["EditorCardStyle"],
            Child = new StackPanel { Spacing = 14, Children = { head, content } },
        };
    }

    public static void AddCloseButton(ContentDialog dialog)
    {
        SpaceKey.Attach(dialog); // Space plays/pauses in dialogs too (except in text fields)
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = dialog.Title as string ?? "", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });

        var close = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 },
            Width = 32, Height = 32, Padding = new Thickness(0), Margin = new Thickness(0, -4, -6, 0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTipService.SetToolTip(close, "Close");
        AutomationProperties.SetName(close, "Close dialog");
        close.Click += (_, _) => dialog.Hide();
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);

        dialog.Title = grid;
        // Follow the content's width: at Opened the content may not have been measured yet.
        void Fit() { if (dialog.Content is FrameworkElement c && c.ActualWidth > 0) grid.Width = c.ActualWidth; }
        if (dialog.Content is FrameworkElement content) content.SizeChanged += (_, _) => Fit();
        dialog.Opened += (_, _) => Fit();
    }
}

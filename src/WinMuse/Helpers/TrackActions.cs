using System.Diagnostics;
using Microsoft.UI.Xaml.Controls;
using WinMuse.Dialogs;
using WinMuse.Models;
using WinMuse.Services;
using WinMuse.Views;

namespace WinMuse.Helpers;

/// <summary>An entry for a menu: text, icon glyph and what it does.</summary>
public sealed record MenuAction(string Text, string Glyph, Func<Task>? Action)
{
    public MenuAction(string text, string glyph, Action action) : this(text, glyph, () => { action(); return Task.CompletedTask; }) { }
}

/// <summary>Builds the shared context menus for songs, artists, albums and genres.</summary>
public static class TrackActions
{
    /// <summary>
    /// The song menu: a row of icon actions on top (play next, add to queue, add to playlist, edit, fetch, pin), then
    /// a short list (go to album/artist, page-specific actions, remove, delete, properties). Favorite is the heart
    /// column in song lists, so it is not repeated here.
    /// </summary>
    /// <param name="leading">Actions listed first (e.g. Select).</param>
    /// <param name="extra">Page-specific actions (e.g. Remove from playlist).</param>
    public static CommandBarFlyout BuildMenu(IList<Track> tracks, IEnumerable<MenuAction>? extra = null, IEnumerable<MenuAction>? leading = null)
    {
        var menu = new CommandBarFlyout { AlwaysExpanded = true, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft };
        if (tracks.Count == 0) return menu;
        var single = tracks.Count == 1 ? tracks[0] : null;

        // ---- Icon row ----
        menu.PrimaryCommands.Add(Icon("Play next", "\uE893", () => { Svc.Player.PlayNext(tracks); Notifier.Show("Playing next: " + Count(tracks)); }));
        menu.PrimaryCommands.Add(Icon("Add to queue", "\uE8FD", () => { Svc.Player.AddToQueue(tracks); Notifier.Show("Added to queue: " + Count(tracks)); }));
        var playlistMenu = new MenuFlyout();
        FillPlaylistItems(playlistMenu.Items, tracks);
        var toPlaylist = Icon("Add to playlist", "\uE90B", (Func<Task>?)null);
        toPlaylist.Flyout = playlistMenu; // opens as a drop-down from the icon
        menu.PrimaryCommands.Add(toPlaylist);
        menu.PrimaryCommands.Add(Icon(single != null ? "Edit" : $"Edit {tracks.Count} songs", "\uE70F", async () => await MetadataEditorDialog.ShowAsync(tracks)));
        menu.PrimaryCommands.Add(Icon("Share", "", async () => await ShareFiles.ShareAsync(tracks)));

        // ---- List ----
        if (leading != null) foreach (var l in leading) menu.SecondaryCommands.Add(Row(l));
        // Fetch: metadata, album art, artist photo, lyrics and genre in one place.
        menu.SecondaryCommands.Add(Row(new MenuAction(single != null ? "Fetch\u2026" : $"Fetch {tracks.Count} songs\u2026", "\uEBD3", async () => await FetchDialog.ShowAsync(tracks))));
        if (single != null)
        {
            var key = FavoritesService.Key(single);
            bool pinned = Svc.Favorites.IsPinned(key);
            menu.SecondaryCommands.Add(Row(new MenuAction(pinned ? "Unpin from Quick Access" : "Pin to Quick Access", pinned ? "\uE77A" : "\uE718", () => Svc.Favorites.SetPinned(key, !pinned))));

            // Go to: a drop-down with the album and the artist(s).
            var go = new MenuFlyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.RightEdgeAlignedTop };
            go.Items.Add(Item("Album", "\uE93C", () => App.MainWindow.Navigate(typeof(AlbumDetailPage), single.AlbumKey)));
            var names = single.ArtistNames;
            if (names.Count == 1) go.Items.Add(Item("Artist", "\uE77B", () => GoToArtist(names[0])));
            else foreach (var n in names) go.Items.Add(Item($"Artist: {n}", "\uE77B", () => GoToArtist(n)));
            var goTo = Row(new MenuAction("Go to", "\uE8AD", (Func<Task>?)null));
            goTo.Flyout = go;
            menu.SecondaryCommands.Add(goTo);
        }
        if (extra != null)
        {
            var list = extra.ToList();
            if (list.Count > 0) menu.SecondaryCommands.Add(new AppBarSeparator());
            foreach (var e in list) menu.SecondaryCommands.Add(Row(e));
        }
        menu.SecondaryCommands.Add(new AppBarSeparator());
        menu.SecondaryCommands.Add(Row(new MenuAction("Remove from library", "\uE738", async () =>
        {
            if (await Confirm($"Remove from library: {Count(tracks)}?",
                    "The songs are hidden from Music only. The files stay on your PC. You can bring them back in Settings > Library > Removed songs."))
                Svc.Library.RemoveTracks(tracks);
        })));
        menu.SecondaryCommands.Add(Row(new MenuAction("Delete file", "\uE74D", async () =>
        {
            if (!await Confirm(tracks.Count == 1 ? $"Delete \"{tracks[0].DisplayTitle}\"?" : $"Delete {tracks.Count} songs?",
                    "The files are moved to the Recycle Bin and removed from your library.", "Delete")) return;
            // Release the files first: stop if one is playing and drop them from the queue.
            var set = tracks.ToHashSet();
            if (Svc.Player.CurrentTrack is { } cur && set.Contains(cur)) Svc.Player.Stop();
            Svc.Player.RemoveFromQueue(Svc.Player.Queue.Select((t, i) => (t, i)).Where(x => set.Contains(x.t)).Select(x => x.i).ToList());
            await Task.Delay(150);
            var errors = Svc.Library.DeleteFiles(tracks);
            if (errors.Count > 0) Notifier.Error("Some files could not be deleted", string.Join(Environment.NewLine, errors.Take(3)));
            else Notifier.Show(tracks.Count == 1 ? "Moved to Recycle Bin" : $"{tracks.Count} songs moved to Recycle Bin");
        })));
        if (single != null)
        {
            menu.SecondaryCommands.Add(new AppBarSeparator());
            menu.SecondaryCommands.Add(Row(new MenuAction("Properties", "\uE946", async () => await PropertiesDialog.ShowAsync(single))));
        }
        return menu;
    }

    /// <summary>An icon in the menu's top row; its name shows as a tooltip.</summary>
    private static AppBarButton Icon(string label, string glyph, Func<Task>? action)
    {
        // Icon only: the name is the tooltip (and what screen readers say).
        var b = new AppBarButton { Label = label, Icon = new FontIcon { Glyph = glyph }, LabelPosition = CommandBarLabelPosition.Collapsed, Width = 45 }; // five across, same menu width as before
        ToolTipService.SetToolTip(b, label);
        if (action != null) b.Click += async (_, _) => await Run(action);
        return b;
    }

    private static AppBarButton Icon(string label, string glyph, Action action) => Icon(label, glyph, () => { action(); return Task.CompletedTask; });

    /// <summary>A row in the menu's list (a little wider than the default menu, so it reads less cramped).</summary>
    private static AppBarButton Row(MenuAction a)
    {
        var b = new AppBarButton { Label = a.Text, Icon = new FontIcon { Glyph = a.Glyph }, MinWidth = 220 };
        if (a.Action != null) b.Click += async (_, _) => await Run(a.Action);
        return b;
    }

    private static async Task Run(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Notifier.Error("Unable to complete the action", ex.Message); }
    }

    /// <summary>Menu for an album (Albums page): play, queue, playlist, edit, fetch, go to artist.</summary>
    public static MenuFlyout BuildAlbumMenu(Album album)
    {
        var tracks = album.Tracks;
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Play", "\uE768", async () => await Svc.Player.PlayAsync(tracks, context: PlayContext.Of(album))));
        menu.Items.Add(Item("Shuffle", "\uE8B1", async () => await Svc.Player.PlayAsync(tracks, 0, shuffle: true, context: PlayContext.Of(album))));
        menu.Items.Add(Item("Play next", "\uE893", () => { Svc.Player.PlayNext(tracks); Notifier.Show($"Playing next: {album.Title}"); }));
        menu.Items.Add(Item("Add to queue", "\uE8FD", () => { Svc.Player.AddToQueue(tracks); Notifier.Show($"Added to queue: {album.Title}"); }));
        menu.Items.Add(AddToPlaylistMenu(tracks));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Edit album\u2026", "\uE70F", async () => await EditAlbumDialog.ShowAsync(album)));
        menu.Items.Add(Item("Fetch album\u2026", "\uEBD3", async () => await FetchDialog.ShowAsync(tracks)));
        menu.Items.Add(new MenuFlyoutSeparator());
        var names = ArtistCredit.Split(album.Artist);
        if (names.Count <= 1) menu.Items.Add(Item("Go to artist", "\uE77B", () => GoToArtist(names.FirstOrDefault() ?? album.Artist)));
        else
        {
            var sub = new MenuFlyoutSubItem { Text = "Go to artist", Icon = new FontIcon { Glyph = "\uE77B" } };
            foreach (var n in names) sub.Items.Add(Item(n, "\uE77B", () => GoToArtist(n)));
            menu.Items.Add(sub);
        }
        return menu;
    }

    /// <summary>Menu for a genre (Genres page): play, queue, playlist, edit.</summary>
    public static MenuFlyout BuildGenreMenu(Genre genre)
    {
        var tracks = genre.Tracks;
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Play", "\uE768", async () => await Svc.Player.PlayAsync(tracks)));
        menu.Items.Add(Item("Shuffle", "\uE8B1", async () => await Svc.Player.PlayAsync(tracks, 0, shuffle: true)));
        menu.Items.Add(Item("Play next", "\uE893", () => { Svc.Player.PlayNext(tracks); Notifier.Show($"Playing next: {genre.Name}"); }));
        menu.Items.Add(Item("Add to queue", "\uE8FD", () => { Svc.Player.AddToQueue(tracks); Notifier.Show($"Added to queue: {genre.Name}"); }));
        menu.Items.Add(AddToPlaylistMenu(tracks));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Edit genre\u2026", "\uE70F", async () => await EditGenreDialog.ShowAsync(genre.Name)));
        return menu;
    }

    public static void GoToArtist(string name) => App.MainWindow.Navigate(typeof(ArtistDetailPage), name);

    /// <summary>Opens the artist's page; with several artists, a menu at <paramref name="anchor"/> lets you pick one.</summary>
    public static void GoToArtist(IReadOnlyList<string> names, Microsoft.UI.Xaml.FrameworkElement anchor)
    {
        if (names.Count == 0) return;
        if (names.Count == 1) { GoToArtist(names[0]); return; }
        var menu = new MenuFlyout();
        foreach (var n in names) menu.Items.Add(Item(n, "", () => GoToArtist(n)));
        menu.ShowAt(anchor);
    }

    /// <summary>Menu for an artist (Artists page, Quick access): play, favorite, pin and edit.</summary>
    public static MenuFlyout BuildArtistMenu(Artist artist)
    {
        var menu = new MenuFlyout();
        menu.Items.Add(Item("Play", "\uE768", async () => await Svc.Player.PlayAsync(artist.Tracks, context: PlayContext.Of(artist))));
        menu.Items.Add(Item("Shuffle", "\uE8B1", async () => await Svc.Player.PlayAsync(artist.Tracks, 0, shuffle: true, context: PlayContext.Of(artist))));
        menu.Items.Add(Item("Add to queue", "\uE8FD", () => { Svc.Player.AddToQueue(artist.Tracks); Notifier.Show("Added to queue: " + Count(artist.Tracks)); }));
        menu.Items.Add(new MenuFlyoutSeparator());
        bool favorite = Svc.Favorites.IsFavoriteArtist(artist.Name);
        menu.Items.Add(Item(favorite ? "Remove from Favorite" : "Add to Favorite", favorite ? "" : "", () => Svc.Favorites.SetFavoriteArtist(artist.Name, !favorite)));
        var key = FavoritesService.Key(artist);
        bool pinned = Svc.Favorites.IsPinned(key);
        menu.Items.Add(Item(pinned ? "Unpin from Quick access" : "Pin to Quick access", pinned ? "\uE77A" : "\uE718", () => Svc.Favorites.SetPinned(key, !pinned)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Edit\u2026", "\uE70F", async () => await EditArtistDialog.ShowAsync(artist)));
        return menu;
    }

    public static MenuFlyoutSubItem AddToPlaylistMenu(IList<Track> tracks)
    {
        var sub = new MenuFlyoutSubItem { Text = "Add to playlist", Icon = new FontIcon { Glyph = "\uE90B" } };
        FillPlaylistItems(sub.Items, tracks);
        return sub;
    }

    /// <summary>"New playlist…" plus one entry per playlist. <paramref name="done"/> runs after a choice is made.</summary>
    public static void FillPlaylistItems(IList<MenuFlyoutItemBase> items, IList<Track> tracks, Action? done = null)
    {
        items.Add(Item("New playlist…", "\uE710", async () =>
        {
            var p = await PlaylistDialog.ShowCreateAsync(tracks);
            if (p != null) { Notifier.Success($"Playlist created: {p.Name}"); done?.Invoke(); }
        }));
        var lists = Svc.Library.Playlists.Where(p => !p.IsSmart).ToList();
        if (lists.Count > 0) items.Add(new MenuFlyoutSeparator());
        foreach (var p in lists)
            items.Add(Item(p.Name, "\uE90B", () => { Svc.Library.AddToPlaylist(p, tracks); Notifier.Show($"Added to {p.Name}: {Count(tracks)}"); done?.Invoke(); }));
    }

    public static MenuFlyoutItem Item(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    public static MenuFlyoutItem Item(string text, string glyph, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { Notifier.Error("Unable to complete the action", ex.Message); }
        };
        return item;
    }

    public static string Count(ICollection<Track> t) => t.Count == 1 ? $"\"{t.First().DisplayTitle}\"" : $"{t.Count} songs";

    public static void ShowInExplorer(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }

    public static async Task<bool> Confirm(string title, string message, string primary = "Remove")
    {
        var dlg = new ContentDialog
        {
            Title = title,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = App.MainWindow.Content.XamlRoot,
            RequestedTheme = ((Microsoft.UI.Xaml.FrameworkElement)App.MainWindow.Content).ActualTheme,
        };
        // Plain-text content: wrap it in a fixed-width block so the title row (with its ✕) has a width to stretch to.
        dlg.Content = new TextBlock { Text = message, TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap, Width = 420 };
        DialogChrome.AddCloseButton(dlg);
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }
}

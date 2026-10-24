# Music (WinMuse)

A Fluent-design music player for local files, built with WinUI 3 (Windows App SDK 1.8) and .NET 10.
The app shows itself as **Music**. The project, namespaces and exe are **WinMuse**.

## Build & run

Requirements: Windows 11, .NET 10 SDK. Visual Studio 2022+ with the WinUI workload is optional.

```powershell
dotnet build src/WinMuse/WinMuse.csproj -c Release -p:Platform=x64
src/WinMuse/bin/x64/Release/net10.0-windows10.0.22621.0/win-x64/WinMuse.exe
```

Or open `WinMuse.sln` and press F5 with the **x64** platform selected.

- The first build downloads the LGPL FFmpeg 8.1 shared build into `external/ffmpeg` (`tools/Get-FFmpeg.ps1`).
  Only `avcodec`, `avformat`, `avutil` and `swresample` are copied to the output.
- The app is unpackaged and self-contained: no MSIX install and no separate Windows App Runtime are needed.
- On first launch the library starts with your Music folder (`C:\Users\<you>\Music`). Add more folders in Settings.

### Build tool (name, icon, install)

`tools/Build-App.ps1` opens a window that builds the app for you (right-click > Run with PowerShell, or
`powershell -ExecutionPolicy Bypass -File tools\Build-App.ps1`):

- **Name**: shown in the title bar, window titles, Start menu and Settings > Apps (default "Music"). The exe stays
  `WinMuse.exe`, and your library and settings stay in `%LocalAppData%\WinMuse`.
- **Icon**: one of the designs in `tools/icons` (default Purple), any color (the design is recolored to it), or your own
  SVG/PNG/JPG/ICO, with a live preview. SVGs are drawn with Microsoft Edge in headless mode.
- **Title-bar logo**: follows the accent color, or shows the icon exactly as it is.
- **Architecture**: Auto (this PC), x64 or ARM64.
- **Install as a Windows app** (optional): copies the build to `%LocalAppData%\Programs\<name>` and adds a Start menu
  shortcut, plus optionally a desktop shortcut, an entry in Settings > Apps with an uninstaller, and "Open with" for
  audio files. **Uninstall…** in the tool removes it again.

The name and title-bar logo are written to `src/WinMuse/Assets/Branding.json`.

### Using your own icon

Pick either option:

1. **Build property.** Point the build at any square PNG (256×256 or larger):
   ```powershell
   dotnet build src/WinMuse/WinMuse.csproj -c Release -p:Platform=x64 -p:AppIconPng=C:\path\to\logo.png
   ```
   To make it permanent, uncomment the `AppIconPng` property in `src/WinMuse/WinMuse.csproj`.
2. **Manual.** Run `tools/Make-Icons.ps1 -FromPng C:\path\to\logo.png`, or replace `src/WinMuse/Assets/AppIcon.ico`
   (multi-size) and `AppIcon.png` (256×256) yourself.

Either way, the icon is used for the exe, the window, the taskbar and the About page. Running `tools/Make-Icons.ps1`
with no arguments restores the default icon.

## Features

| Area | What's there |
|---|---|
| Playback | FFmpeg decoding of any audio format FFmpeg reads (MP3, FLAC, ALAC, AAC, Opus, Vorbis, WAV, AIFF, WMA, APE, WavPack, Musepack, TTA, TAK, DSD, AC-3/E-AC-3, DTS, TrueHD, …) → WASAPI output. Gapless playback, crossfade (0–12 s), an always-on 10-band EQ with presets and preamp, smooth volume changes. The EQ follows each song's genre (**Match genre**, on by default) and can be saved for a single song (**Save for this song**), which then always wins. |
| Library | Folder scanning with TagLib# (falls back to FFmpeg for missing tags), a SQLite database, automatic monitoring of every library folder, and embedded or folder cover art. Browse by songs, albums, artists and genres, plus search. |
| Queue & playlists | A collapsible **Up next** panel (bottom right, toggled from the player bar) shows the current song, highlights the next one, and lists a few after it. Drag-to-reorder queue, shuffle, repeat all/one. The queue is restored when you reopen the app. Normal playlists (reorder, custom cover, M3U import/export) and smart playlists that fill themselves from rules (genre, play count, added/last-played dates, rating, format…). |
| Favorites and Quick access | Star a song (in any song list, the player bar or the right-click menu) to add it to the **Favorite** playlist; star an artist to list it first on the Artists page. Right-click a song or artist and choose **Pin to Quick access** to keep it on Home. Quick access shows up to 10 tiles in as many columns as fit (2–5) and up to 2 rows: pinned items first, then favorites, then five random tiles (2 artists and 3 songs, preferring ones with artwork); within each group artists come before songs. Artists have round photos and an artist icon, songs square art and a note icon. |
| Playlists on Home | Under a large **Playlists** heading at the bottom of Home (select it to open the Playlists page), each playlist has its own section, laid out like "Recently added": its name (select it to open the playlist) and a row of its songs; selecting a song plays the playlist from there. Sorted Alphabetically, by Recent activity (played, opened or changed; default), by Date created, or in a Custom order set with each playlist's move buttons. |
| Home rows | Albums and songs appear as cards: artwork the full width of the card, then title, artist and year. The mouse wheel always scrolls the page, with a gentle eased glide. Pointing at a row shows ‹ › arrows at its ends that slide it (Shift + wheel, tilt wheels, touchpad swipes and touch also scroll rows). Cards cut off at either end of a row fade and shrink. Accent colors blend into each other when the song changes. |
| Albums | **New album** builds an album from songs you choose: name, album artist, year, genre, cover, and optional track numbering. |
| Automatic metadata & Sync | New songs are identified once when they are added. Only songs from 30 seconds to 15 minutes are checked online. The audio fingerprint decides first; the file name and tags confirm it or choose between recordings that share the same audio. Without a fingerprint match, a name match is applied only when the name or tags give both artist and title and the length agrees within 3 seconds; otherwise the song is left untouched. Each song tracks where its metadata, album art, artist image and lyrics came from, giving a Sync flag: **1** complete (everything downloaded or already in the file), **0** incomplete, **-1** edited by you. **Sync** (Settings or the Source page) re-downloads everything for songs flagged 0 or -1 and skips complete ones. |
| Source page | Lists every song with the state of its metadata, album art, artist image and lyrics (Downloaded, From file, Edited, Not found, Pending, Skipped) and its Sync flag, with filters for incomplete, edited and complete songs. Each row has a Sync button that redoes the checks for that song. |
| Removing and deleting | **Remove from library** hides a song but keeps the file (restore it in Settings > Library > Removed songs). **Delete file** moves the file and its .lrc lyrics to the Recycle Bin. |
| Metadata | **Edit** opens the editor for one song or many at once (details, artwork, lyrics, file info). A setting chooses whether edits are **kept in the library only** (default) or **written into the files**. **Properties** shows read-only details: tags, audio format, file, listening history and where each item came from. |
| Sync (right-click a song) | Identifies the song as soon as it opens, preselects the match the automatic rules would choose, and applies it together with album art, artist photo, lyrics and genre (each can be unticked). You can pick another match or search manually; several songs are handled one after another. Results are ranked by how well title, artist and length agree with the song, not by each service's search relevance. Sources, in order: AcoustID audio fingerprint (free key), MusicBrainz, iTunes Search, Deezer, and YouTube as the last resort (free key). File names from video downloads are cleaned first ("(Official Video)", "(Lyrics)", "[video ID]" are removed). YouTube results are used only when the uploader matches the artist in the video title (for example "Beautiful - Giulio Cercato" uploaded by Giulio Cercato); otherwise the song stays Unknown Artist. |
| Online (download only, nothing is streamed) | Album art: the matched source, Cover Art Archive, iTunes, Deezer, then the YouTube video thumbnail (cropped to a square) as the last resort. Artist photos: found through the artist's MusicBrainz entry and the profiles it links to (Wikidata/Wikimedia Commons, Fanart.tv, TheAudioDB, Spotify, Deezer, the official YouTube channel, Bandcamp), then by exact-name search (TheAudioDB, Deezer, Spotify), the YouTube uploader of the file's video ID, and finally Wikipedia. Each credited artist is tried in turn. Genres: MusicBrainz, Last.fm (free key), then iTunes; only recognised genres are written. Synced lyrics from LRCLIB, saved as a `.lrc` file next to the song. |
| Song lists | Title (with the duration underneath), Artist, Year, Album and Genre columns; an unknown album shows "-", an unknown year or genre stays blank. Click a song to play it; the playing song gets an animated border (Rainbow, Accent color or Off in Settings). Right-click a song and choose **Select** to show checkboxes in every song list; checked songs can then be played, queued or added to a playlist from the selection bar. Esc or **Cancel** clears the selection. |
| Now Playing | Animated "aurora" background in the album's colors, synced lyrics with highlighting and auto-scroll (click a line to seek) and a per-song timing offset, up-next queue, details with a rating, a real-time spectrum visualizer, and artwork that pulses gently with the bass. A second spectrum plays behind the player bar, whose acrylic diffuses it. |
| Appearance | Light, dark, system or OLED (true black) theme on a Mica Alt window. Accent color from Windows, a custom pick, or the current album art; it applies to every control, including toggles, sliders, check boxes and text boxes. Background: album aurora, custom 3-color gradient, or a wallpaper image with blur and dim. |
| Windows integration | Media keys and the Windows media overlay, play/pause and skip buttons on the taskbar thumbnail, a compact always-on-top mini player, a single running instance ("Open with" sends files to the open window), and keyboard shortcuts. |
| Download | Downloads a song from a URL with `mdown.exe "<url>" --secret <secret file>`, run inside the **Save to** folder. Upload the secret file once; it is kept in the app's data folder and used for every download until replaced. New songs are added to the library and checked right away. Failures show an error box with the error code and mdown's output, ready to copy. Put `mdown.exe` in `src/WinMuse/Tools` (copied to `Tools\mdown.exe` next to `WinMuse.exe`). |

### Keyboard shortcuts

Space / Ctrl+P play-pause · Ctrl+→ / Ctrl+← next / previous · Ctrl+↑ / Ctrl+↓ volume · Ctrl+S shuffle · Ctrl+R repeat ·
Ctrl+F search · Ctrl+M mini player · F11 Now Playing · Esc close Now Playing.

## Where things live

```
src/WinMuse/
  Services/Audio/    FFmpegDecoder, PlaybackPipeline (gapless, crossfade, EQ, spectrum), AudioEngine (WASAPI)
  Services/          Library (SQLite + scanner + watcher), Tags, Artwork, Playback (queue), Theme,
                     OnlineMetadata (MusicBrainz / CAA / AcoustID / LRCLIB), MetadataServices (lyrics, fingerprint,
                     edit/identify), DownloadService (mdown.exe), SystemIntegration (media overlay + taskbar)
  Controls/          PlayerBar, TrackList, ArtImage, AmbientBackground (aurora), Visualizer, Effects (hover/entrance motion)
  Views/             Pages, NowPlayingView, MiniPlayerWindow
  Dialogs/           Metadata editor, Identify & fix, Playlist (incl. smart rules), Create album
  Themes/            Styles (Fluent control styles, accent brushes) and card templates
```

User data is stored in `%LocalAppData%\WinMuse`: `settings.json`, `library.db`, an artwork cache, downloaded lyrics, the download secret file (`download-secret.txt`), and `log.txt`.

## Notes

- **API keys** (Settings → API keys, and a dialog on first launch when none are set; the first sync waits for it) are optional and free: AcoustID (audio fingerprints), YouTube Data API (last-resort artist/title and images; 10,000 free quota units per day) and Last.fm (detailed genres), Fanart.tv (artist photos) and Spotify (artist photos; client ID and secret). Each card has step-by-step instructions for getting a key, and keys are verified automatically when pasted and when Settings opens.
- **Library-only edits** are kept in the database and survive rescans. Edits written to files are read back on the next scan.
- **Automatic metadata** processes up to four songs at a time; MusicBrainz (1 request/second) and iTunes (about 20/minute) rate limits are always respected.

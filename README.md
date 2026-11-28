<div align="center">

<img src="src/WinMuse/Assets/AppIcon.png" width="96" alt="" />

# WinMuse

**A beautiful, native Windows 11 music player for the music you already own.**

Plays almost any audio format, makes messy libraries tidy without breaking them, and paints the whole app in the
colors of whatever is playing.

![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?logo=windows11&logoColor=white)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![WinUI 3](https://img.shields.io/badge/WinUI-3-6E4AFF)
![FFmpeg](https://img.shields.io/badge/decoding-FFmpeg_8-007808?logo=ffmpeg&logoColor=white)
![x64 and ARM64](https://img.shields.io/badge/arch-x64%20%7C%20ARM64-555)

<img src="docs/screenshots/home.jpg" alt="Home: greeting, Quick Access, Fresh in Your Library and the Up next panel, tinted by the album that is playing" />

</div>

---

Streaming apps have taken over, but a lot of us still have folders full of FLACs, rips, old MP3s and downloads with names
like `Artist - Song (Official Video) [dQw4w9WgXcQ].mp3`. WinMuse is a player built for that library. It looks and feels
like a first-party Windows 11 app, it never streams or uploads your music, and it quietly fixes titles, covers, artist
photos and lyrics for you, but only when it is sure.

> The app shows itself as **Music** by default, and the built-in build tool lets you give it any name and icon you like.
> The project, namespaces and executable are **WinMuse**.

## Highlights

- 🎧 **Plays practically everything.** MP3, FLAC, ALAC, AAC, Opus, Vorbis, WAV, AIFF, WMA, APE, WavPack, Musepack, TTA,
  TAK, DSD, AC-3, DTS, TrueHD, and anything else FFmpeg can read. Output goes straight to WASAPI.
- ⏭️ **Gapless by default.** The next song is opened and decoded ahead of time. Add a crossfade of up to 12 seconds if you prefer.
- 🎨 **Your music sets the colors.** The accent color, the animated "aurora" background, the visualizer and every toggle,
  slider and check box follow the album art, blending smoothly from song to song.
- 🎤 **Synced lyrics.** Lyrics come from LRCLIB and scroll with the song. Click a line to jump to it, and adjust the timing per song.
- 🪄 **A library that fixes itself, carefully.** New songs are identified by audio fingerprint and by name. A match is
  applied only when the evidence agrees, and your own edits are never overwritten.
- 🎚️ **A 10-band equalizer that follows the genre.** Rock gets the Rock curve, jazz gets the Jazz curve. Save a custom
  EQ for a single song and it always wins.
- 🛡️ **Your music files stay untouched.** Edits, downloaded covers and lyrics live in Music's own library. Writing tags
  into the files or deleting files are separate opt-in settings, and you can choose to be asked on every save.
- 🔒 **Private by design.** No account, no telemetry, no streaming. API keys are optional and free, and every online
  source is listed below.
- 🪟 **Feels native.** Mica, acrylic, Fluent icons, media keys, the Windows media overlay, taskbar buttons, a mini player
  and keyboard shortcuts.

## Tour

### Now Playing

Album art that pulses gently with the bass, a live spectrum, and lyrics that follow along. Switch between Artwork,
Lyrics, Up next and Details without leaving the screen.

<img src="docs/screenshots/now-playing-lyrics.jpg" alt="Now Playing with synced lyrics, album art and the spectrum visualizer" />

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/now-playing-queue.jpg" alt="Now Playing with the Up next list; the playing song has an animated rainbow border" /></td>
    <td width="50%"><img src="docs/screenshots/now-playing-artwork.jpg" alt="Now Playing, artwork view" /></td>
  </tr>
  <tr>
    <td><sub><b>Up next</b>: drag to reorder. The playing song gets an animated border (Rainbow, Accent or Off).</sub></td>
    <td><sub><b>Artwork view</b>: just the cover, the song and the controls.</sub></td>
  </tr>
</table>

### Browse your library your way

Songs, albums, artists and genres, each with its own search and sort. Album and artist pages take their color from
the artwork. A song credited to "A & B" or "A feat. B" appears on both artists' pages.

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/albums.jpg" alt="Albums grid" /></td>
    <td width="50%"><img src="docs/screenshots/artist.jpg" alt="Artist page with photo, popular songs and albums" /></td>
  </tr>
  <tr>
    <td width="50%"><img src="docs/screenshots/album.jpg" alt="Album page tinted red by its cover" /></td>
    <td width="50%"><img src="docs/screenshots/genres.jpg" alt="Genres as colorful tiles with stacked covers" /></td>
  </tr>
</table>

<img src="docs/screenshots/songs.jpg" alt="Songs list with favorites, columns and the playing song highlighted" />

### Home that keeps up with you

Home brings together **Quick Access** (pinned and favorite songs and artists), **Continue Listening** (every album,
artist or playlist you played, with its position, ready to resume), automatic playlists (**Fresh in Your Library**,
**On Repeat**, **Long Time No Play**) and a row for each of your playlists. Rows glide with the mouse wheel and
fade softly at their edges.

<img src="docs/screenshots/home-full.jpg" alt="Home with Quick Access full of favorites and Continue Listening with resume progress" />

### Playlists, including ones that fill themselves

Make normal playlists (reorder them, give them a custom cover, import or export M3U), or **smart playlists** built from
rules: genre, play count, rating, dates added and played, format and more. Smart playlists update by themselves as your
library changes. Tap the heart on any song to add it to **Favorites**.

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/playlists.jpg" alt="Playlists page with a smart playlist, Favorites and a custom playlist" /></td>
    <td width="50%"><img src="docs/screenshots/smart-playlist.jpg" alt="Smart playlist editor with a Genre contains Hop rule" /></td>
  </tr>
</table>

### Metadata that fixes itself, but never behind your back

When a song joins the library, WinMuse tries to identify it once:

1. **Audio fingerprint first** (AcoustID). This works even when the file is called `track01.mp3`.
2. **Then the name and tags.** It searches MusicBrainz, iTunes and Deezer, and ranks results by how well the title,
   artist *and* length agree, not by the service's own relevance. Video-download noise such as "(Official Video)",
   "[video ID]" or "(Lyrics)" is stripped first.
3. **Only confident matches are applied.** Without a fingerprint match, a name match needs both artist and title, and
   a length within 3 seconds. Otherwise the song is left exactly as it was.

Album art, artist photos, genres and synced lyrics are downloaded alongside. The **Fetch** page shows where every
piece came from (downloaded, from the file, edited by you, not found) and lets you redo any song with one click.
The **editor** handles one song or many at once. Your edits stay in Music's library and your files are never changed,
unless you allow it in **Settings → Your music files**: then choose **App only**, **Files only** (edits are written into
the tags), or **Ask each time** (every edit window offers *Save to library* and *Save to files*).

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/fetch.jpg" alt="Fetch page listing the source of each song's metadata, art, artist image and lyrics" /></td>
    <td width="50%"><img src="docs/screenshots/editor.jpg" alt="Metadata editor with artist chips and album fields" /></td>
  </tr>
</table>

### Sound

An always-on 10-band EQ with presets and a preamp. **Match genre** picks the right curve for each song, and **Save for
this song** pins a custom one. The player also offers gapless playback, crossfade, smooth volume changes with no
clicks, and a choice of output device.

<img src="docs/screenshots/equalizer.jpg" alt="Equalizer flyout matched to Electronic" />

### Make it yours

Light, dark, follow Windows, or **OLED true black**. The accent comes from Windows, a color you pick, or the album
art. The background can be the album "aurora", a three-color gradient, or your own wallpaper with real blur and dim.
Choose the card style for Home and the highlight for the playing song. A compact, always-on-top **mini player** is one
click (or <kbd>Ctrl</kbd>+<kbd>M</kbd>) away, and the layout adapts down to narrow windows.

<table>
  <tr>
    <td width="50%"><img src="docs/screenshots/light-theme.jpg" alt="Albums in the light theme" /></td>
    <td width="50%"><img src="docs/screenshots/settings.jpg" alt="Appearance and playback settings" /></td>
  </tr>
  <tr>
    <td width="50%" align="center"><img src="docs/screenshots/mini-player.jpg" alt="Mini player" /><br /><sub>Mini player</sub></td>
    <td width="50%"><img src="docs/screenshots/narrow.jpg" alt="Now Playing in a narrow window" /><br /><sub>Narrow window</sub></td>
  </tr>
</table>

## Everything it does

| Area | Features |
|---|---|
| **Playback** | FFmpeg decoding (decoded ahead on its own thread) → WASAPI. Gapless, crossfade (0–12 s), 10-band EQ with presets, preamp and soft limiter, genre-matched and per-song EQ, smooth volume, output device choice. The queue and position are restored when you reopen the app. |
| **Library** | Scans any number of folders (TagLib#, with FFmpeg filling the gaps), stores everything in SQLite, watches folders for changes, and reads embedded or folder art (`cover.jpg`, `folder.jpg`…). A song whose file is moved or renamed, even by a tag editor that rewrites its tags, keeps its plays, Favorites and playlist places. Browse by songs, albums, artists and genres, with search everywhere. |
| **Queue** | Collapsible **Up next** panel, a full Queue page, drag to reorder, play next or add to queue, shuffle, repeat all or one. |
| **Playlists** | Normal playlists (reorder, custom cover, M3U import/export), smart playlists from rules, automatic playlists on Home, and Favorites (the heart). |
| **Home** | Time-of-day greeting with Shuffle All, Quick Access (pins, favorites and a few discoveries), Continue Listening, automatic playlists, and one row per playlist with the sort order of your choice. |
| **Metadata** | Automatic identification of new songs, manual **Fetch** for any song (pick another match or search yourself), the Fetch overview page, a single- or multi-song editor (details, artwork, lyrics, file info), a read-only Properties view, artist and genre renaming, and **New album** to build an album from songs you choose. |
| **Lyrics** | Synced lyrics from LRCLIB, kept in Music's data folder (an `.lrc` you already have next to a song is read too). Click a line to seek, adjust timing per song, or add your own. |
| **Files** | **Remove from library** hides a song but keeps the file (restore it in Settings). **Delete file** (opt-in: Settings → Your music files) sends the song to the Recycle Bin. |
| **Windows** | Media keys and the media overlay, taskbar thumbnail buttons, mini player, a single running instance ("Open with" sends files to the open window), and Start menu and "Open with" registration through the build tool. |
| **Appearance** | Light, dark, system or OLED theme on Mica Alt; accent from Windows, a custom color or the album; album aurora, gradient or wallpaper background; card styles; playing-song highlight; visualizer styles. |

### Keyboard shortcuts

| Keys | Action |
|---|---|
| <kbd>Space</kbd> / <kbd>Ctrl</kbd>+<kbd>P</kbd> | Play / pause |
| <kbd>Ctrl</kbd>+<kbd>→</kbd> / <kbd>Ctrl</kbd>+<kbd>←</kbd> | Next / previous |
| <kbd>Ctrl</kbd>+<kbd>↑</kbd> / <kbd>Ctrl</kbd>+<kbd>↓</kbd> | Volume up / down |
| <kbd>Ctrl</kbd>+<kbd>S</kbd> / <kbd>Ctrl</kbd>+<kbd>R</kbd> | Shuffle / repeat |
| <kbd>Ctrl</kbd>+<kbd>F</kbd> | Search |
| <kbd>Ctrl</kbd>+<kbd>M</kbd> | Mini player |
| <kbd>F11</kbd> / <kbd>Esc</kbd> | Open / close Now Playing |

## Getting started

### Requirements

- Windows 11 (x64 or ARM64)
- [.NET 10 SDK](https://dotnet.microsoft.com/download) to build from source. Visual Studio 2022+ with the WinUI workload is optional.

### Build and run

```powershell
git clone <this repository>
cd WinMuse
dotnet build src/WinMuse/WinMuse.csproj -c Release -p:Platform=x64
src/WinMuse/bin/x64/Release/net10.0-windows10.0.22621.0/win-x64/WinMuse.exe
```

Or open `WinMuse.sln` and press <kbd>F5</kbd> with the **x64** (or **ARM64**) platform selected.

- The first build downloads the LGPL FFmpeg 8.1 shared libraries for your CPU into `external/` (`tools/Get-FFmpeg.ps1`).
  Only `avcodec`, `avformat`, `avutil` and `swresample` ship with the app.
- The app is unpackaged: no MSIX and no separate Windows App Runtime install. A plain `dotnet build` uses the
  .NET 10 runtime installed with the SDK. To run it on a PC without .NET, use a self-contained build (next section).
- On first launch the library starts with your Music folder. Add more folders in **Settings → Library**.

### Build tool, installer and your own branding

`tools/Build-App.ps1` opens a small window that builds the app for you (right-click → *Run with PowerShell*, or
`powershell -ExecutionPolicy Bypass -File tools\Build-App.ps1`):

- **Current build**: when the app has been built before, the window opens with that build's choices and shows its
  version, where it is, and whether the source has changed since. **Rebuild and update** builds the current source
  again with them and replaces the installed copy, so trying a change takes one click.
- **Name**: shown in the title bar, Start menu and Settings → Apps (default "Music"). The exe stays `WinMuse.exe` and
  your library stays in `%LocalAppData%\WinMuse`.
- **Icon**: the current icon, one of the designs in `tools/icons`, a custom color (the design is recolored for you),
  or your own SVG, PNG, JPG or ICO, with a live preview.
- **Title bar logo**: follows the accent color, or shows your icon exactly as it is.
- **Architecture**: Automatic, x64 or ARM64.
- **Installation** (optional): Start menu shortcut, desktop shortcut, an entry in Settings → Apps with an
  uninstaller, and "Open with" for audio files.
- **Setup file**: a single self-contained file you can share with anyone (`tools/Make-Installer.ps1`), in either format:
  - **Setup program (.exe)**: `<Name> Setup.exe`, a small setup window; `/silent` installs without it.
  - **Windows Installer package (.msi)**: `<Name> Setup.msi`, the standard Windows setup wizard. It also works with
    `msiexec`, Group Policy and Intune (`msiexec /i "<Name> Setup.msi" /qn DESKTOPSHORTCUT=1`). Built with
    [WiX Toolset](https://wixtoolset.org) 5 (`tools/InstallerMsi`), which the build downloads from NuGet.

  Both install for the current user without administrator rights. A newer setup file with the same name replaces the
  copy an older one installed.
- **Developer hints**: explains every option. Set `$DeveloperHints = $false` at the top of the script to remove the
  button for a release.

The name and logo choice are written to `src/WinMuse/Assets/Branding.json`. The last build's choices are kept in
`%LocalAppData%\WinMuse-Build\last-build.json`.

<details>
<summary>Using your own icon without the build tool</summary>

1. **Build property.** Point the build at any square PNG (256×256 or larger):
   ```powershell
   dotnet build src/WinMuse/WinMuse.csproj -c Release -p:Platform=x64 -p:AppIconPng=C:\path\to\logo.png
   ```
   To make it permanent, uncomment the `AppIconPng` property in `src/WinMuse/WinMuse.csproj`.
2. **Manually.** Run `tools/Make-Icons.ps1 -FromPng C:\path\to\logo.png`, or replace `src/WinMuse/Assets/AppIcon.ico`
   (multi-size) and `AppIcon.png` (256×256) yourself. `tools/Make-Icons.ps1` with no arguments restores the default.

</details>

### Optional API keys

Everything works without keys. These free keys add more (Settings → API keys, also offered on first launch). Each one
has step-by-step instructions in the app and is checked as soon as you paste it.

| Key | What it adds |
|---|---|
| **AcoustID** (recommended) | Identifies songs by their audio fingerprint, even with wrong names and tags |
| **Last.fm** | More detailed genres |
| **Fanart.tv** | High-quality artist photos |
| **Spotify** (client ID + secret) | Photos for smaller and independent artists |
| **YouTube Data API** | Last-resort song details and images for video downloads (10,000 free units per day) |

## Privacy: what goes online

WinMuse never streams, uploads or shares your audio. It only *downloads* information about songs:

- **Identification:** AcoustID (a fingerprint, if you set a key), MusicBrainz, iTunes Search and Deezer, plus YouTube as a last resort if you set a key.
- **Album art:** the matched source, Cover Art Archive, iTunes, Deezer, then a YouTube thumbnail cropped to a square.
- **Artist photos:** via the artist's MusicBrainz links (Wikidata/Wikimedia Commons, Fanart.tv, TheAudioDB, Spotify,
  Deezer, YouTube, Bandcamp), then exact-name searches, then Wikipedia.
- **Genres:** MusicBrainz, Last.fm, then iTunes. Only recognised genres are written.
- **Lyrics:** LRCLIB.

Rate limits are respected (MusicBrainz 1 request per second, iTunes about 20 per minute), and at most four songs are
processed at a time. Turn any automatic download off in **Settings → Metadata**.

## Under the hood

WinMuse is a single WinUI 3 app with a small set of app-wide services (`Services/Svc.cs`) and pages that talk to them
directly.

```
FFmpegDecoder ──► PlaybackPipeline ──► PcmOutputProvider ──► WASAPI (NAudio)
 (decode-ahead     (gapless / crossfade,
  thread)           EQ, volume, spectrum)
```

A few decisions worth knowing about:

- **The audio never waits on the UI.** Decoding runs ahead on a dedicated thread registered with Windows' multimedia
  scheduler (MMCSS), the output keeps a 200 ms buffer, garbage collection runs in low-latency mode, and the UI thread
  never blocks on an audio lock (see `Helpers/UiLock.cs`).
- **The accent really is everywhere.** `ThemeService` points WinUI's own control brushes at app-owned accent brushes and
  recolors them in place, so every control follows the album without restyling anything.
- **Your files are never changed behind your back.** Every write to a music file goes through one place
  (`MetadataEditService.ApplyAsync`), which refuses unless *Change music files* is on. When it is, tag writes to the
  song that is playing (or queued next) wait until it closes, so playback never jumps.

```
src/WinMuse/
  Services/Audio/   FFmpegDecoder, PlaybackPipeline, Equalizer, SpectrumAnalyzer, AudioEngine (WASAPI)
  Services/         Library (SQLite + scanner + watcher), Tags, Artwork, Playback (queue), Theme, Settings,
                    OnlineMetadata (+ .MusicSources, .ArtistImages), MetadataEdit (identify, edit), Lyrics,
                    Fingerprint, AutoFix, Favorites, ContinueListening, Smtc (media overlay), Taskbar
  Controls/         PlayerBar, TrackList, QueuePanel, ArtImage, AmbientBackground (aurora), Visualizer, shelf rows
  Views/            Pages, NowPlayingView, MiniPlayerWindow
  Dialogs/          Metadata editor, Fetch, playlists (incl. smart rules), album/artist/genre editors, API keys
  Themes/           Fluent control styles, accent brushes and card templates
tools/              Build tool, installer, icon and FFmpeg scripts
tests/              Unit tests (WinMuse.Tests) and UI stress/visual scripts (ui/)
```

**Your data** lives in `%LocalAppData%\WinMuse`: `settings.json`, `library.db`, the artwork cache, downloaded
lyrics and `log.txt`. Set `WINMUSE_DATA_DIR` to run a fully separate copy (handy for testing).

## Testing

```powershell
dotnet test tests\WinMuse.Tests -p:Platform=x64
```

The unit tests cover artist credits, matching, lyrics parsing, formatting, the database, and the audio engine (every
song decodes, decode-ahead is bit-identical, seeking, gapless joins). `tests/ui` adds a scripted stress session that
tracks CPU and memory, a screenshot pass over every page in both themes, and a report generator. See
[tests/README.md](tests/README.md).

## Contributing

Issues and pull requests are welcome. If you're changing something visible, a before/after screenshot helps a lot.
For anything touching playback, please run the audio tests: they catch gaps and glitches you can't always hear.
Code style (naming, layout, one class per file) is in `.editorconfig`, which Visual Studio, VS Code and Rider apply
automatically; a Release build has no warnings, and it should stay that way.

## Notes

- **Download page (optional):** a page for running a separate downloader tool (`mdown.exe`, not included). Without the
  tool the page simply says it isn't installed.

## Credits

- Decoding by [FFmpeg](https://ffmpeg.org) (LGPL, shared libraries), tags by [TagLib#](https://github.com/mono/taglib-sharp),
  audio output by [NAudio](https://github.com/naudio/NAudio), fingerprints with [AcoustID.NET](https://github.com/wo80/AcoustID.NET),
  storage by [Microsoft.Data.Sqlite](https://learn.microsoft.com/dotnet/standard/data/sqlite/) and [Dapper](https://github.com/DapperLib/Dapper),
  UI by the [Windows App SDK](https://github.com/microsoft/WindowsAppSDK) and the [Windows Community Toolkit](https://github.com/CommunityToolkit/Windows).
- Song data from [MusicBrainz](https://musicbrainz.org), [Cover Art Archive](https://coverartarchive.org),
  [AcoustID](https://acoustid.org) and [LRCLIB](https://lrclib.net).
- **Music in the screenshots** is Creative Commons–licensed, from the [Internet Archive netlabels collection](https://archive.org/details/netlabels):
  ProleteR · Boogie Belgique · Riding Alone for Thousands of Miles · Miranda Shvangiradze · Candlegravity · mccandless ·
  Echo Lali · The Underscore Orkestra · Poodleplay Arkestra · Intoxicated Piano · The Transisters · Kova
  (CC BY-NC-ND / BY-NC-SA / BY-NC). Thank you for sharing your music.

## License

No license has been chosen yet. Until one is added, all rights are reserved by the author. FFmpeg is distributed under
the LGPL. See `external/ffmpeg/LICENSE.txt`.

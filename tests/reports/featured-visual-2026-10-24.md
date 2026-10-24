# Music (WinMuse): Featured test report, run 2 (internal + visual)

24 October 2026. Build: "Music Test 1", built and installed by `tools\Build-App.ps1`. Light and Dark themes at 1600×1000, 1100×720 and 720×900.

## Results

- Internal tests: 72 of 72 passed (11 s).
- Build script: the fix is confirmed. After a successful build, the tool closes once OK is pressed on "Build complete". Install (Start menu, desktop, Apps, "Open with") and uninstall both worked.
- Visual: 76 captures, none blank; all reviewed by eye. They are saved as the baseline in `%LOCALAPPDATA%\WinMuseTests\Baseline\visual`.
- Stress: not run (out of time).

## Issues found (not fixed)

- **High**: At 720 px wide, Now Playing shows only the Lyrics tab. There is no artwork, title, seek bar or play/pause, and the player bar is hidden.
- **Medium**: The "Up next" panel floats over page content at every size. At 1600 px it covers the right quarter (Settings' Theme and App icon controls, the 4th column of Albums, Queue's columns), at 1100 px about half, and at 720 px nearly everything.
- **Medium**: Page titles collide with their toolbars at 720 px (Albums, Artists, Genres, Fetch, Playlists, Queue). At 1100 px, "Songs" breaks into "Son / gs".
- **Medium**: At 720 px the navigation pane stays fully open (about 40% of the width), and the player bar's right side is cut off.
- **Medium**: At 720 px, Home's greeting card is an empty colored block: no greeting, stats or Shuffle All.
- **Low**: The greeting's stats line wraps before "music" at the opening window size.
- **Low**: Artists with a blank tag show as an empty or "–" name, and their page has a dash as its heading.

## Script gaps (to fix before the next run)

- Search results were not captured: the search box's button wasn't found through UI Automation.
- The mini player was not captured: its button is hidden at 720 px, the last size tested.

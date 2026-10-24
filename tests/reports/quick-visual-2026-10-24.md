# Music (WinMuse): Quick visual test after the layout fixes

24 October 2026. Builds: "Music Test 2", "Music Test 3" and "Music Test 4", each made and installed by `tools\Build-App.ps1`. Each build closed itself after success, and each uninstall removed everything.

## Results

- Internal tests: 72 of 72 passed.
- Visual: 78 captures (Light and Dark at 1600×1000, 1100×720 and 720×900), plus two narrow re-checks (28 and 26 captures). None were blank; all were reviewed by eye.
- At 1600×1000 the app looks the same as before the fixes.

## Fixed and confirmed

| Issue | Fix |
|---|---|
| Narrow Now Playing had no controls on the Lyrics, Up next and Details tabs | A compact strip (cover, title, previous/play/next, seek bar) shows under those tabs when the window is narrow; tapping the cover opens Artwork |
| Up next panel covered page content | It steps aside below 1200 px wide; turning it on from the player bar while narrow still shows it |
| Navigation pane stayed fully open in a narrow window | It shrinks to icons below 1000 px; the menu button still opens it |
| Player bar cut off at 720 px | The seek area narrows (down to 280 px) before anything leaves the window |
| Page titles collided with their toolbars | The toolbar moves onto its own row when both don't fit, and its search box narrows if needed (`Helpers/HeaderWrap.cs`); titles no longer break mid-word |
| Home greeting card empty at 720 px | The hidden fanned covers give back their column |
| Greeting stats wrapped before "music" | Each part stays together; the line breaks only between parts |
| Artists named "_" or "�" | Names without a letter or digit count as Unknown Artist (display only; tags untouched) |

## Still open (minor)

- Artist page at 720 px: the button row is a little wide, so "All songs" is cut off at the edge.
- Fetch page at 720 px: the table's columns are cramped ("Metadat / a").
- Not captured: search results (the search box's button isn't exposed to UI Automation). The mini player wasn't captured in the narrow-only re-checks, because its button is hidden at that size.

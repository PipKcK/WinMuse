# Tests

Everything runs against an isolated copy of the app: `WINMUSE_DATA_DIR` points it at its own data folder under
`%LOCALAPPDATA%\WinMuseTests`, so the real library (`%LOCALAPPDATA%\WinMuse`) and a running copy of the app are never touched.
Music files are only read (settings use `WriteMode = LibraryOnly`). No mouse or keyboard input is sent.

## Internal tests (`WinMuse.Tests`)

```
$env:MUSIC_TEST_DIR = "$env:LOCALAPPDATA\WinMuseTests\Music"   # a copy of real songs; tests that need it skip without it
dotnet test tests\WinMuse.Tests -p:Platform=x64 --logger "trx;LogFileName=unit.trx" --results-directory "$env:LOCALAPPDATA\WinMuseTests\Results"
```

Covers artist credits, matching, lyrics parsing, formatting, collection sync, the database, and audio (every song decodes,
decode-ahead is bit-identical, seeking, thread safety, gapless joins, prefetch CPU).

## Build script and stress (`ui`)

1. `ui\Drive-BuildApp.ps1 -Name "Music Test" -Color "#1DB954" -InstallDir "$env:LOCALAPPDATA\WinMuseTests\Install\MusicTest" -Register`
   builds and installs a copy through `tools\Build-App.ps1`. Afterwards, run its uninstaller, check that the real
   install's "Open with" entry is intact, and `git checkout` the assets the build rewrites.
2. `ui\Stress-App.ps1 -Exe <WinMuse.exe> -MusicFolders "a;b" -Label <name> -Scale <s> [-Fetch -KeysFile keys.json]`
   runs the scripted session (scan, idle, play, minimized, Now Playing, page switching, search, settle) and samples
   CPU and memory every second. Keys stay outside the repo.
3. `python ui\make_report.py <results dir> <out.md> --tier <tier>` checks the results against `ui\limits.json` and writes the report.
   Findings written by hand go in `manual-findings.json` in the results dir.

## Tiers

| Tier | Length | What runs |
|---|---|---|
| Quick | 10 min | Internal tests, one stress session at `-Scale 0.33` |
| Featured | 30 min | Internal tests, the build script, a Release stress session (large library) and a Debug session with fetch at `-Scale 0.5` |
| Full | 1 h | Featured, plus a 1-hour soak (`-Scale 6`) to confirm memory stays flat |
| Complete | 2 h | Full, plus visual checks of every page in both themes |

Downloads are not tested. Reports go in `reports\`.

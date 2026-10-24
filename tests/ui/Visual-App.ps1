# Visual test: screenshots of every page in each theme and window size, taken from the window's own pixels (works
# while other windows cover it). No mouse or keyboard input: pages are opened through Uia-Action.ps1.
#
#   .\Visual-App.ps1 -Exe <WinMuse.exe> -SeedData <data dir with a scanned library> -Label visual
# Output: <OutDir>\<Label>\<theme>-<size>-<view>.png, then check with visual_check.py.
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$SeedData,        # a test data dir whose library is already scanned (art included)
    [string]$Label = 'visual',
    [string[]]$Themes = @('Light', 'Dark'),
    [string[]]$Sizes = @('1600x1000', '1100x720', '720x900'),
    [string]$OutDir = (Join-Path $env:LOCALAPPDATA 'WinMuseTests\Results')
)
$ErrorActionPreference = 'Stop'
$shots = Join-Path $OutDir $Label
if (Test-Path $shots) { Remove-Item -Recurse -Force $shots }
New-Item -ItemType Directory -Force $shots | Out-Null
$uia = Join-Path $PSScriptRoot 'Uia-Action.ps1'
$errors = @()

function Ui([string]$action, [string]$arg = '', [string]$arg2 = '') {
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $uia, '-ProcId', $script:proc.Id, '-Action', $action)
    if ($arg) { $argv += @('-Arg', $arg) }
    if ($arg2) { $argv += @('-Arg2', $arg2) }
    $r = try { $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'; (& powershell @argv 2>&1 | Out-String) } finally { $ErrorActionPreference = $old }
    if ("$r" -notmatch '(?m)^ok') { $script:errors += "$($script:theme) $($script:size) $action $arg : $(("$r" -split "`n")[0])"; return $false }
    return $true
}
function Shot([string]$view, [int]$settle = 1500) {
    Start-Sleep -Milliseconds $settle
    Ui 'shot' (Join-Path $shots "$($script:theme)-$($script:size)-$view.png") | Out-Null
}

foreach ($theme in $Themes) {
    # Fresh copy of the seeded library for each theme, so one run can't change the next.
    $data = Join-Path $env:LOCALAPPDATA "WinMuseTests\Data\$Label-$theme"
    if (Test-Path $data) { Remove-Item -Recurse -Force $data }
    robocopy $SeedData $data /E /NFL /NDL /NJH /NJS /NP /XF settings.json log.txt | Out-Null
    $seed = Get-Content (Join-Path $SeedData 'settings.json') -Raw | ConvertFrom-Json
    $settings = [ordered]@{
        MusicFolders = $seed.MusicFolders; WriteMode = 'LibraryOnly'; AutoFixMetadata = $false; AutoFetchArt = $false; AutoFetchLyrics = $false
        Theme = $theme; BackgroundMode = 'AlbumArt'; AnimateBackground = $true; ShowVisualizer = $true; Muted = $true; Volume = 0
    }
    $settings | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $data 'settings.json')
    $env:WINMUSE_DATA_DIR = $data
    $proc = Start-Process $Exe -PassThru
    $deadline = (Get-Date).AddSeconds(30)
    do { Start-Sleep 1; $proc.Refresh() } until ($proc.MainWindowHandle -ne 0 -or (Get-Date) -gt $deadline)
    Start-Sleep 6   # first scan of the (already known) library and Home's rows

    $size = 'start'   # the window as the app opens it
    Shot 'first-run' 0    # the first-run keys dialog (a fresh data folder has no keys)
    Ui 'invoke' 'Skip for now' | Out-Null
    Shot 'home-empty'     # nothing playing yet
    Ui 'nav' 'Home' | Out-Null; Ui 'invoke-id' 'ShuffleAllButton' | Out-Null
    Start-Sleep 3
    foreach ($size in $Sizes) {
        $w, $h = $size -split 'x'
        Ui 'size' $w $h | Out-Null
        foreach ($page in 'Home', 'Songs', 'Albums', 'Artists', 'Genres', 'Playlists', 'Queue', 'Fetch', 'Settings') {
            if (Ui 'nav' $page) { Shot $page.ToLower() }
        }
        if (Ui 'nav' 'Albums') { Start-Sleep 1; if (Ui 'open-item' 0) { Shot 'album-detail' } }
        if (Ui 'nav' 'Artists') { Start-Sleep 1; if (Ui 'open-item' 0) { Shot 'artist-detail' 2500 } }
        if (Ui 'search' 'love') { Shot 'search' }
        if (Ui 'invoke' 'Open Now Playing') { Shot 'nowplaying' 2500; Ui 'invoke' 'Close (Esc)' | Out-Null; Start-Sleep 1 }
    }
    if (Ui 'invoke' 'Mini player (Ctrl+M)') { Start-Sleep 2; $proc.Refresh(); $size = 'mini'; Shot 'mini' }

    $proc.Refresh()
    if ($proc.HasExited) { $errors += "$theme : the app exited (code $($proc.ExitCode))" }
    else { $proc.CloseMainWindow() | Out-Null; if (-not $proc.WaitForExit(8000)) { Stop-Process -Id $proc.Id -Force } }
    $log = Join-Path $data 'log.txt'
    if (Test-Path $log) { Copy-Item $log (Join-Path $shots "$theme-log.txt") }
}
[ordered]@{ label = $Label; exe = $Exe; themes = $Themes; sizes = $Sizes; shots = @(Get-ChildItem $shots -Filter *.png).Count; uiErrors = $errors } |
    ConvertTo-Json | Set-Content (Join-Path $shots 'run.json')
Get-Content (Join-Path $shots 'run.json')

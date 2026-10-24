# Stress test: runs a test copy of the app against a big library and records CPU, memory, threads and handles every
# second through a scripted session (scan, idle, play, minimized, Now Playing, rapid page switching, search).
# No mouse or keyboard input is used (UI actions go through Uia-Action.ps1).
#
#   .\Stress-App.ps1 -Exe <WinMuse.exe> -MusicFolders "a;b" -Label release-2k -Scale 1.0 [-Fetch]
# Output: <OutDir>\<Label>.csv (samples), <Label>-phases.json (per-phase stats), <Label>-log.txt (app log)
param(
    [Parameter(Mandatory)] [string]$Exe,
    [Parameter(Mandatory)] [string]$MusicFolders,   # several: separated by ';'
    [string]$Label = 'stress',
    [double]$Scale = 1.0,             # 1.0 = Featured lengths; 0.33 = Quick; 2 = Full
    [switch]$Fetch,                   # automatic fetch on (needs keys in $KeysFile)
    [string]$KeysFile = '',
    [string]$OutDir = (Join-Path $env:LOCALAPPDATA 'WinMuseTests\Results')
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$data = Join-Path $env:LOCALAPPDATA "WinMuseTests\Data\$Label"
if (Test-Path $data) { Remove-Item -Recurse -Force $data }
New-Item -ItemType Directory -Force $data | Out-Null

# ---- Settings for the test copy: library only (files are never written), fetch on or off.
$settings = [ordered]@{
    MusicFolders = @($MusicFolders -split ';' | Where-Object { $_ }); WriteMode = 'LibraryOnly'
    AutoFixMetadata = [bool]$Fetch; AutoFetchArt = [bool]$Fetch; AutoFetchLyrics = [bool]$Fetch; EmbedFetchedArt = $false
    Theme = 'Dark'; BackgroundMode = 'AlbumArt'; AnimateBackground = $true; ShowVisualizer = $true
}
if ($Fetch -and $KeysFile -and (Test-Path $KeysFile)) { (Get-Content $KeysFile -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $settings[$_.Name] = $_.Value } }
$settings | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $data 'settings.json')

$env:WINMUSE_DATA_DIR = $data
$proc = Start-Process $Exe -PassThru
$cores = [Environment]::ProcessorCount
$csv = Join-Path $OutDir "$Label.csv"
"t,phase,cpu,ws_mb,private_mb,threads,handles" | Set-Content $csv
$samples = New-Object System.Collections.Generic.List[object]
$t0 = Get-Date
$lastCpu = $null; $lastT = $null
$uia = Join-Path $PSScriptRoot 'Uia-Action.ps1'

function Sample([string]$phase) {
    $proc.Refresh()
    if ($proc.HasExited) { throw "The app exited during '$phase' (exit code $($proc.ExitCode))" }
    $now = Get-Date; $cpu = $proc.TotalProcessorTime
    if ($script:lastCpu) {
        $pct = ($cpu - $script:lastCpu).TotalMilliseconds / ($now - $script:lastT).TotalMilliseconds / $cores * 100
        $row = [pscustomobject]@{ t = [math]::Round(($now - $t0).TotalSeconds, 1); phase = $phase; cpu = [math]::Round($pct, 2)
            ws_mb = [math]::Round($proc.WorkingSet64 / 1MB); private_mb = [math]::Round($proc.PrivateMemorySize64 / 1MB); threads = $proc.Threads.Count; handles = $proc.HandleCount }
        $samples.Add($row)
        "$($row.t),$($row.phase),$($row.cpu),$($row.ws_mb),$($row.private_mb),$($row.threads),$($row.handles)" | Add-Content $csv
    }
    $script:lastCpu = $cpu; $script:lastT = $now
}
function Wait-Phase([string]$phase, [double]$seconds, [scriptblock]$every = $null, [double]$everySec = 0) {
    $end = (Get-Date).AddSeconds($seconds * $Scale); $next = Get-Date
    while ((Get-Date) -lt $end) {
        Sample $phase
        if ($every -and (Get-Date) -ge $next) { & $every; $next = (Get-Date).AddSeconds($everySec) }
        Start-Sleep -Milliseconds 1000
    }
}
function Ui([string]$action, [string]$arg = '', [string]$arg2 = '') {
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $uia, '-ProcId', $proc.Id, '-Action', $action)
    if ($arg) { $argv += @('-Arg', $arg) }
    if ($PSBoundParameters.ContainsKey('arg2')) { $argv += @('-Arg2', $(if ($arg2) { $arg2 } else { '""' })) }
    # A failed action is recorded, never fatal: the session carries on measuring.
    $r = try { $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'; (& powershell @argv 2>&1 | Out-String) } finally { $ErrorActionPreference = $old }
    if ("$r" -notmatch '(?m)^ok') { $script:uiErrors += "$action $arg : $(("$r" -split "`n")[0])" }
}
function Count-Tracks {
    try { $c = & python -c "import sqlite3,sys;c=sqlite3.connect(sys.argv[1]);print(c.execute('select count(*) from Tracks').fetchone()[0])" (Join-Path $data 'library.db') 2>$null; return [int]$c } catch { return 0 }
}

$uiErrors = @()
$result = [ordered]@{ label = $Label; exe = $Exe; scale = $Scale; fetch = [bool]$Fetch; started = $t0.ToString('s') }
try {
    # ---- Startup and first scan
    Start-Sleep 2; Sample 'startup'
    $scanStart = Get-Date; $last = -1; $stableSince = Get-Date
    while (((Get-Date) - $scanStart).TotalSeconds -lt 600) {
        Sample 'scan'; Start-Sleep 1
        $n = Count-Tracks
        if ($n -ne $last) { $last = $n; $stableSince = Get-Date }
        elseif ($n -gt 0 -and ((Get-Date) - $stableSince).TotalSeconds -ge 8) { break }
    }
    $result.tracks = $last
    $result.scanSeconds = [math]::Round(((Get-Date) - $scanStart).TotalSeconds - 8)

    Wait-Phase 'idle' 120
    Ui 'nav' 'Home'; Ui 'invoke-id' 'ShuffleAllButton'
    Wait-Phase 'play-home' 90
    Ui 'minimize'
    Wait-Phase 'play-minimized' 120
    Ui 'restore'; Ui 'invoke' 'Open Now Playing'
    Wait-Phase 'play-nowplaying' 90
    Ui 'invoke' 'Close (Esc)'
    $pages = 'Songs', 'Albums', 'Artists', 'Genres', 'Playlists', 'Home', 'Fetch', 'Settings', 'Queue'
    $i = 0
    Wait-Phase 'tab-switching' 120 { Ui 'nav' $pages[$script:i % $pages.Count]; $script:i++ } 1.5
    Ui 'nav' 'Home'
    $words = 'the', 'love', 'a', 'night', 'zz', 'remix', 'live', 'b'
    $j = 0
    Wait-Phase 'search' 60 { Ui 'text-id' 'SearchBox' $words[$script:j % $words.Count]; $script:j++ } 2
    Ui 'text-id' 'SearchBox' ''
    Wait-Phase 'settle' 60
    $result.ok = $true
} catch { $result.ok = $false; $result.error = $_.Exception.Message }
finally {
    $result.uiErrors = $uiErrors
    $logPath = Join-Path $data 'log.txt'
    if (Test-Path $logPath) {
        Copy-Item $logPath (Join-Path $OutDir "$Label-log.txt") -Force
        $log = Get-Content $logPath
        $result.unhandled = @($log | Where-Object { $_ -match 'Unhandled' }).Count
        $result.renderGaps = @($log | Where-Object { $_ -match 'Render gap' }).Count
        $result.decodeDry = @($log | Where-Object { $_ -match 'ran dry' }).Count
    }
    $result.fetched = try { [int](& python -c "import sqlite3,sys;c=sqlite3.connect(sys.argv[1]);print(c.execute('select count(*) from Tracks where MetaState<>0').fetchone()[0])" (Join-Path $data 'library.db') 2>$null) } catch { 0 }
    # Per-phase statistics.
    $result.phases = @($samples | Group-Object phase | ForEach-Object {
        $c = $_.Group.cpu | Sort-Object
        [ordered]@{ phase = $_.Name; seconds = $_.Count; cpuAvg = [math]::Round(($c | Measure-Object -Average).Average, 2)
            cpuP95 = $c[[int][math]::Floor(($c.Count - 1) * 0.95)]; cpuMax = ($c | Measure-Object -Maximum).Maximum
            wsAvg = [math]::Round(($_.Group.ws_mb | Measure-Object -Average).Average); wsMax = ($_.Group.ws_mb | Measure-Object -Maximum).Maximum
            privMax = ($_.Group.private_mb | Measure-Object -Maximum).Maximum; threadsMax = ($_.Group.threads | Measure-Object -Maximum).Maximum
            handlesMax = ($_.Group.handles | Measure-Object -Maximum).Maximum }
    })
    if (-not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null; if (-not $proc.WaitForExit(8000)) { Stop-Process -Id $proc.Id -Force } }
    $result.finished = (Get-Date).ToString('s')
    $result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $OutDir "$Label-phases.json")
    $result | ConvertTo-Json -Depth 5
}

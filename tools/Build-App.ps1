# Build WinMuse (the Music app) with your own name and icon, and optionally install it as a Windows app.
#
#   Right-click > Run with PowerShell, or:  powershell -ExecutionPolicy Bypass -File tools\Build-App.ps1
#
# When the app has been built before, the window opens with the last build's choices and a "Rebuild" button that
# builds the current source again with them and updates the installed copy: the quick way to try each change.
#
# Choices (the "Developer hints" button shows the same in the window):
#   Name          shown in the title bar, window titles, Start menu and Apps list (default "Music Player").
#   Icon          the current icon (kept as it is), one of the bundled designs (tools\icons\*.svg), any color (the
#                 Purple design recolored to it), or your own image (.svg, .png, .jpg, .ico).
#   Title bar logo  follows the accent color (Rings design) or shows the icon exactly as it is.
#   Architecture  Automatic (this PC), x64 or ARM64.
#   Installation  copies the app to an install folder and adds a Start menu shortcut, optionally a desktop shortcut,
#                 an entry in Settings > Apps (with an uninstaller) and "Open with" for audio files.
#   Setup file    also makes "<Name> Setup.exe" or "<Name> Setup.msi" (tools\Make-Installer.ps1): one file to share
#                 that installs the app on another PC, with everything it needs included.
#
# The exe stays WinMuse.exe and your library/settings stay in %LocalAppData%\WinMuse whatever the name.
# SVG icons are drawn with Microsoft Edge (installed with Windows 11) in headless mode.
# The last build's choices are kept in %LocalAppData%\WinMuse-Build\last-build.json (WINMUSE_BUILD_RECORD overrides
# the path, for tests).

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

# Developer hints: a button that explains every option. Set to $false for a release of this tool; nothing else
# depends on it (see "Developer hints" below).
$DeveloperHints = $true

$Root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Project = Join-Path $Root 'src\WinMuse\WinMuse.csproj'
. (Join-Path $PSScriptRoot 'Shared.ps1')
$DefaultName = 'Music Player'
$AppVersion = Get-AppVersion $Root
if (-not $AppVersion) { $AppVersion = '0.0.0' }
# The app's data folder (library, playlists, settings), as the app finds it: WINMUSE_DATA_DIR replaces it in tests.
$DataDir = if ($env:WINMUSE_DATA_DIR) { $env:WINMUSE_DATA_DIR } else { Join-Path $env:LOCALAPPDATA 'WinMuse' }
$Assets = Join-Path $Root 'src\WinMuse\Assets'
$IconDir = Join-Path $PSScriptRoot 'icons'
$Work = Join-Path $env:TEMP 'WinMuse-Build'
New-Item -ItemType Directory -Force $Work | Out-Null
Get-ChildItem $Work -Filter 'preview-*.png' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
$RecordPath = if ($env:WINMUSE_BUILD_RECORD) { $env:WINMUSE_BUILD_RECORD } else { Join-Path $env:LOCALAPPDATA 'WinMuse-Build\last-build.json' }

$Presets = [ordered]@{
    'Purple' = 'purple.svg'; 'Blue' = 'blue.svg'; 'Indigo' = 'indigo.svg'; 'Mint' = 'mint.svg'; 'Amber' = 'amber.svg'
    'Orange' = 'orange.svg'; 'Copper' = 'copper.svg'; 'Wine' = 'wine.svg'; 'Black' = 'black.svg'; 'Indigo (simple)' = 'simple-indigo.svg'
}
$AudioTypes = '.mp3', '.flac', '.m4a', '.aac', '.ogg', '.opus', '.wav', '.aiff', '.aif', '.wma', '.ape', '.wv', '.dsf', '.dff', '.mka'

# ---------------------------------------------------------------- helpers

function Get-NativeArch {
    $a = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
    if ($a -eq 'ARM64') { 'ARM64' } else { 'x64' }
}

function Get-OutputDir([string]$arch) { Join-Path $Root "src\WinMuse\bin\$arch\Release\net10.0-windows10.0.22621.0\win-$($arch.ToLower())" }

function Find-Edge {
    foreach ($p in "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") {
        if (Test-Path $p) { return $p }
    }
    return $null
}

# Draws an SVG to a transparent square PNG with Edge headless.
function Convert-SvgToPng([string]$svg, [string]$png, [int]$size = 512) {
    $edge = Find-Edge
    if (-not $edge) { throw 'Microsoft Edge is required to render SVG icons. Use a PNG image instead.' }
    $html = Join-Path $Work 'render.html'
    $uri = ([Uri](Resolve-Path $svg).Path).AbsoluteUri
    "<!doctype html><html><body style='margin:0;background:transparent'><img src='$uri' width='$size' height='$size' style='display:block;object-fit:contain'></body></html>" |
        Set-Content -Encoding UTF8 $html
    if (Test-Path $png) { Remove-Item $png -Force }
    $edgeArgs = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--default-background-color=00000000', "--window-size=$size,$size",
              "--user-data-dir=`"$(Join-Path $Work 'edge')`"", "--screenshot=`"$png`"", "`"$(([Uri]$html).AbsoluteUri)`"")
    $p = Start-Process $edge -ArgumentList $edgeArgs -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(30000)) { try { $p.Kill() } catch {}; throw 'Rendering the SVG icon timed out.' }
    if (-not (Test-Path $png)) { throw "The SVG icon could not be rendered: $svg" }
}

# The biggest picture in an .ico (frames may be PNG-compressed, which System.Drawing.Icon can't always read).
function Get-LargestIcoFrame([string]$file) {
    $b = [IO.File]::ReadAllBytes($file)
    $count = [BitConverter]::ToUInt16($b, 4); $best = -1; $bestSize = -1
    for ($i = 0; $i -lt $count; $i++) {
        $e = 6 + 16 * $i; $w = if ($b[$e] -eq 0) { 256 } else { $b[$e] }
        if ($w -gt $bestSize) { $bestSize = $w; $best = $e }
    }
    $len = [BitConverter]::ToInt32($b, $best + 8); $off = [BitConverter]::ToInt32($b, $best + 12)
    if ($b[$off] -eq 0x89 -and $b[$off + 1] -eq 0x50) {   # PNG frame
        return [System.Drawing.Image]::FromStream((New-Object IO.MemoryStream(, $b[$off..($off + $len - 1)])))
    }
    return (New-Object System.Drawing.Icon $file, $bestSize, $bestSize).ToBitmap()
}

# Any image to a square, transparent-padded PNG (icons must be square).
function Convert-ImageToSquarePng([string]$file, [string]$png) {
    $ext = [IO.Path]::GetExtension($file).ToLowerInvariant()
    if ($ext -eq '.svg') { Convert-SvgToPng $file $png; return }
    if ($ext -eq '.ico') { $src = Get-LargestIcoFrame $file }
    else { $src = [System.Drawing.Image]::FromFile($file) }
    $n = [Math]::Max($src.Width, $src.Height)
    $bmp = New-Object System.Drawing.Bitmap $n, $n
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'AntiAlias'
    $g.DrawImage($src, [int](($n - $src.Width) / 2), [int](($n - $src.Height) / 2), $src.Width, $src.Height)
    $g.Dispose(); $src.Dispose()
    $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}

function ConvertTo-Hsl([System.Drawing.Color]$c) {
    $r = $c.R / 255.0; $g = $c.G / 255.0; $b = $c.B / 255.0
    $max = [Math]::Max($r, [Math]::Max($g, $b)); $min = [Math]::Min($r, [Math]::Min($g, $b))
    $l = ($max + $min) / 2; $d = $max - $min
    if ($d -lt 1e-6) { return @(0.0, 0.0, $l) }
    $s = if ($l -gt 0.5) { $d / (2 - $max - $min) } else { $d / ($max + $min) }
    $h = if ($max -eq $r) { (($g - $b) / $d) % 6 } elseif ($max -eq $g) { ($b - $r) / $d + 2 } else { ($r - $g) / $d + 4 }
    $h = ($h * 60 + 360) % 360
    return @($h, $s, $l)
}

function ConvertFrom-Hsl([double]$h, [double]$s, [double]$l) {
    $h = (($h % 360) + 360) % 360
    $c = (1 - [Math]::Abs(2 * $l - 1)) * $s; $x = $c * (1 - [Math]::Abs(($h / 60) % 2 - 1)); $m = $l - $c / 2
    $rgb = switch ([int][Math]::Floor($h / 60)) { 0 { $c, $x, 0 } 1 { $x, $c, 0 } 2 { 0, $c, $x } 3 { 0, $x, $c } 4 { $x, 0, $c } default { $c, 0, $x } }
    return '#{0:x2}{1:x2}{2:x2}' -f [int][Math]::Round(($rgb[0] + $m) * 255), [int][Math]::Round(($rgb[1] + $m) * 255), [int][Math]::Round(($rgb[2] + $m) * 255)
}

# Recolors the Purple design to $color: every color takes the new hue (keeping its offset from the design's main hue),
# its saturation scaled to the new color's, and its lightness moved partway toward it; white stays white.
function New-RecoloredSvg([System.Drawing.Color]$color, [string]$outSvg) {
    $svg = Get-Content -Raw (Join-Path $IconDir 'purple.svg')
    $base = ConvertTo-Hsl ([System.Drawing.ColorTranslator]::FromHtml('#7e4de2'))   # the design's tile color
    $target = ConvertTo-Hsl $color
    $satScale = if ($base[1] -gt 0) { $target[1] / $base[1] } else { 1 }
    $lShift = ($target[2] - $base[2]) * 0.6
    $svg = [regex]::Replace($svg, '#[0-9a-fA-F]{6}\b', {
        param($m)
        if ($m.Value -ieq '#FFFFFF') { return $m.Value }
        $hsl = ConvertTo-Hsl ([System.Drawing.ColorTranslator]::FromHtml($m.Value))
        $h = $target[0] + ($hsl[0] - $base[0])
        $s = [Math]::Min(1, [Math]::Max(0, $hsl[1] * $satScale))
        $l = [Math]::Min(0.97, [Math]::Max(0.03, $hsl[2] + $lShift))
        ConvertFrom-Hsl $h $s $l
    })
    Set-Content -Encoding UTF8 -Path $outSvg -Value $svg
}

function Test-Name([string]$n) { return ($n.Trim().Length -gt 0) -and ($n.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -lt 0) }

function Get-Branding {
    $file = Join-Path $Assets 'Branding.json'
    if (Test-Path $file) { try { return Get-Content -Raw $file | ConvertFrom-Json } catch {} }
    return $null
}

# ---------------------------------------------------------------- the previous build

# The last build, so it can be repeated in one click: the choices this tool saved after its last successful build, or,
# for a build made before it kept them, what that build left behind (an app it installed, or the build output).
function Find-PreviousBuild {
    if (Test-Path $RecordPath) {
        try { $r = Get-Content -Raw $RecordPath | ConvertFrom-Json; if ($r.Name -and $r.Arch) { return $r } } catch {}
    }
    $native = Get-NativeArch
    $brand = Get-Branding
    $name = if ($brand -and $brand.Name) { [string]$brand.Name } else { $DefaultName }
    $found = [pscustomobject]@{
        Name = $name; Arch = $native; Launch = $true; Register = $false; InstallDir = ''; Desktop = $false; Apps = $true
        OpenWith = $true; Installer = $false; SetupFormat = 'exe'; ShareDir = [Environment]::GetFolderPath('Desktop')
    }
    # Installed by this tool: listed in Settings > Apps, with its Uninstall.ps1 next to the exe.
    $installed = @(Get-ChildItem 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -like 'WinMuse-*' } | ForEach-Object { Get-ItemProperty $_.PSPath } |
        Where-Object { $_.InstallLocation -and (Test-Path (Join-Path $_.InstallLocation 'WinMuse.exe')) -and (Test-Path (Join-Path $_.InstallLocation 'Uninstall.ps1')) } |
        Sort-Object { (Get-Item (Join-Path $_.InstallLocation 'WinMuse.exe')).LastWriteTime } -Descending)
    if ($installed.Count -gt 0) {
        $i = $installed[0]
        $found.Name = [string]$i.DisplayName; $found.Register = $true; $found.InstallDir = [string]$i.InstallLocation
        $found.Desktop = Test-Path (Join-Path ([Environment]::GetFolderPath('Desktop')) "$($i.DisplayName).lnk")
        $found.OpenWith = Test-Path 'HKCU:\Software\Classes\Applications\WinMuse.exe\shell\open\command'
        return $found
    }
    foreach ($a in $native, 'x64', 'ARM64') {
        if (Test-Path (Join-Path (Get-OutputDir $a) 'WinMuse.exe')) { $found.Arch = $a; return $found }
    }
    return $null
}

# The newest change to the app's source (bin and obj left out), to tell whether a build is up to date.
function Get-SourceTime {
    $src = Join-Path $Root 'src\WinMuse'
    $files = @(Get-ChildItem $src -File) + @(Get-ChildItem $src -Directory | Where-Object { $_.Name -notin 'bin', 'obj' } | Get-ChildItem -Recurse -File)
    return ($files | Measure-Object LastWriteTime -Maximum).Maximum
}

# What the Current build panel shows: the exe it describes (the installed copy, else the build output) and its details.
function Get-BuildStatus($prev) {
    $installedExe = if ($prev.Register -and $prev.InstallDir) { Join-Path $prev.InstallDir 'WinMuse.exe' } else { '' }
    $outputExe = Join-Path (Get-OutputDir $prev.Arch) 'WinMuse.exe'
    $isInstalled = $installedExe -and (Test-Path $installedExe)
    $exe = if ($isInstalled) { $installedExe } elseif (Test-Path $outputExe) { $outputExe } else { return $null }
    $item = Get-Item $exe
    $version = ([string]$item.VersionInfo.ProductVersion -split '\+')[0]
    $where = if ($isInstalled) { "Installed in $($prev.InstallDir)" } else { "Not installed. Build output: $(Split-Path $exe)" }
    $fresh = if ((Get-SourceTime) -gt $item.LastWriteTime) { 'Source has changed since this build.' } else { 'Up to date with the source.' }
    return [pscustomobject]@{
        Title = "$($prev.Name) $version ($($prev.Arch))"
        Where = $where
        When = "Built $($item.LastWriteTime.ToString('g')). $fresh"
        Installed = [bool]$isInstalled
    }
}

$Previous = Find-PreviousBuild
$Status = if ($Previous) { Get-BuildStatus $Previous } else { $null }
if (-not $Status) { $Previous = $null }

# ---------------------------------------------------------------- window

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Build WinMuse'
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9.5)
$form.BackColor = [System.Drawing.Color]::FromArgb(250, 250, 252)
$form.FormBorderStyle = 'FixedDialog'; $form.MaximizeBox = $false
$iconPath = Join-Path $Assets 'AppIcon.ico'
if (Test-Path $iconPath) { $form.Icon = New-Object System.Drawing.Icon $iconPath }
$Accent = [System.Drawing.Color]::FromArgb(124, 77, 226)
$DimColor = [System.Drawing.Color]::FromArgb(96, 96, 108)

function Set-Font($c) { $c.Font = New-Object System.Drawing.Font('Segoe UI', 9.5); return $c }

function Add-Label($parent, [string]$text, [int]$x, [int]$y, [int]$w = 200, [int]$h = 22, [switch]$Bold, [switch]$Dim) {
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $text; $l.Location = New-Object System.Drawing.Point($x, $y); $l.AutoSize = $false
    $l.Size = New-Object System.Drawing.Size($w, $h)
    $l.Font = if ($Bold) { New-Object System.Drawing.Font('Segoe UI Semibold', 10) } else { New-Object System.Drawing.Font('Segoe UI', 9.5) }
    if ($Dim) { $l.ForeColor = $DimColor }
    $parent.Controls.Add($l); return $l
}

function Add-Group([string]$title, [int]$y, [int]$h) {
    $g = New-Object System.Windows.Forms.GroupBox
    $g.Text = $title; $g.Location = New-Object System.Drawing.Point(16, $y); $g.Size = New-Object System.Drawing.Size(712, $h)
    $g.Font = New-Object System.Drawing.Font('Segoe UI Semibold', 10)
    $form.Controls.Add($g); return $g
}

function New-Control([string]$type, [string]$text, [int]$x, [int]$y, [int]$w = 0, [int]$h = 0) {
    $c = Set-Font (New-Object "System.Windows.Forms.$type")
    if ($text) { $c.Text = $text }
    $c.Location = New-Object System.Drawing.Point($x, $y)
    if ($w -gt 0) { $c.Size = New-Object System.Drawing.Size($w, $h) } else { $c.AutoSize = $true }
    return $c
}

function Set-Primary($button) {
    $button.BackColor = $Accent; $button.ForeColor = [System.Drawing.Color]::White; $button.FlatStyle = 'Flat'
    $button.FlatAppearance.BorderSize = 0; $button.Font = New-Object System.Drawing.Font('Segoe UI Semibold', 10)
}

$y = 12

# ---- Current build (only when the app has been built before)
if ($Previous) {
    $gCurrent = Add-Group 'Current build' $y 96
    $lblTitle = Add-Label $gCurrent $Status.Title 16 26 500 22 -Bold
    $lblWhere = Add-Label $gCurrent $Status.Where 16 50 510 20 -Dim; $lblWhere.AutoEllipsis = $true
    $tips = New-Object System.Windows.Forms.ToolTip; $tips.SetToolTip($lblWhere, $Status.Where)   # the whole path
    $lblWhen = Add-Label $gCurrent $Status.When 16 70 510 20 -Dim
    $btnRebuild = New-Control Button $(if ($Previous.Register) { 'Rebuild and update' } else { 'Rebuild' }) 540 34 156 36
    Set-Primary $btnRebuild
    $gCurrent.Controls.Add($btnRebuild)
    $y += 104
}

# ---- App
$gApp = Add-Group 'App' $y 64
Add-Label $gApp 'Name' 16 30 100 | Out-Null
$txtName = New-Control TextBox $DefaultName 130 27 300 26
$gApp.Controls.Add($txtName)
$y += 72

# ---- Icon
$gIcon = Add-Group 'Icon' $y 224
$picPreview = New-Object System.Windows.Forms.PictureBox
$picPreview.Location = New-Object System.Drawing.Point(540, 24); $picPreview.Size = New-Object System.Drawing.Size(150, 150)
$picPreview.SizeMode = 'Zoom'; $picPreview.BackColor = [System.Drawing.Color]::Transparent
$gIcon.Controls.Add($picPreview)
$lblPreview = Add-Label $gIcon 'Preview' 520 178 190 40 -Dim; $lblPreview.TextAlign = 'TopCenter'

$rbCurrent = New-Control RadioButton 'Current icon' 16 28
$rbPreset = New-Control RadioButton 'Design' 16 62
$cmbPreset = New-Control ComboBox '' 150 60 220 26; $cmbPreset.DropDownStyle = 'DropDownList'
foreach ($k in $Presets.Keys) { [void]$cmbPreset.Items.Add($k) }; $cmbPreset.SelectedIndex = 0
$rbColor = New-Control RadioButton 'Custom color' 16 96
$txtColor = New-Control TextBox '#1DB954' 150 94 110 26
$btnPick = New-Control Button 'Choose…' 268 93 80 28
$swatch = New-Object System.Windows.Forms.Panel; $swatch.Location = New-Object System.Drawing.Point(356, 94); $swatch.Size = New-Object System.Drawing.Size(26, 26); $swatch.BorderStyle = 'FixedSingle'
$rbFile = New-Control RadioButton 'Image file' 16 130
$txtFile = New-Control TextBox '' 150 128 290 26
$btnFile = New-Control Button 'Browse…' 446 127 80 28
Add-Label $gIcon 'SVG, PNG, JPG or ICO' 150 158 300 20 -Dim | Out-Null
$chkAccent = New-Control CheckBox 'Match title bar logo to accent color' 16 188; $chkAccent.Checked = $true
$gIcon.Controls.AddRange(@($rbCurrent, $rbPreset, $cmbPreset, $rbColor, $txtColor, $btnPick, $swatch, $rbFile, $txtFile, $btnFile, $chkAccent))
$y += 232

# ---- Build
$gBuild = Add-Group 'Build' $y 96
Add-Label $gBuild 'Architecture' 16 31 110 | Out-Null
$cmbArch = New-Control ComboBox '' 130 28 220 26; $cmbArch.DropDownStyle = 'DropDownList'
$native = Get-NativeArch
[void]$cmbArch.Items.Add("Automatic ($native)"); [void]$cmbArch.Items.Add('x64'); [void]$cmbArch.Items.Add('ARM64'); $cmbArch.SelectedIndex = 0
$chkClean = New-Control CheckBox 'Clean build' 16 62
$chkLaunch = New-Control CheckBox 'Open app when finished' 200 62; $chkLaunch.Checked = $true
$gBuild.Controls.AddRange(@($cmbArch, $chkClean, $chkLaunch))
$y += 104

# ---- Installation
$gReg = Add-Group 'Installation' $y 136
$chkRegister = New-Control CheckBox 'Install for current user' 16 28
Add-Label $gReg 'Location' 36 63 90 | Out-Null
$txtInstall = New-Control TextBox '' 130 60 480 26
$btnInstall = New-Control Button 'Browse…' 616 59 80 28
$chkDesktop = New-Control CheckBox 'Desktop shortcut' 36 98
$chkApps = New-Control CheckBox 'List in Settings > Apps' 200 98; $chkApps.Checked = $true
$chkOpenWith = New-Control CheckBox 'Add to "Open with" for audio files' 400 98; $chkOpenWith.Checked = $true
$gReg.Controls.AddRange(@($chkRegister, $txtInstall, $btnInstall, $chkDesktop, $chkApps, $chkOpenWith))
$y += 144

# ---- Setup file to share
$gShare = Add-Group 'Setup file' $y 120
$chkInstaller = New-Control CheckBox 'Create setup file' 16 28
Add-Label $gShare 'Format' 220 31 70 | Out-Null
$cmbFormat = New-Control ComboBox '' 300 28 310 26; $cmbFormat.DropDownStyle = 'DropDownList'
[void]$cmbFormat.Items.Add('Setup program (.exe)'); [void]$cmbFormat.Items.Add('Windows Installer package (.msi)'); $cmbFormat.SelectedIndex = 0
Add-Label $gShare 'Save to' 220 63 70 | Out-Null
$txtShare = New-Control TextBox ([Environment]::GetFolderPath('Desktop')) 300 60 310 26
$btnShare = New-Control Button 'Browse…' 616 59 80 28
Add-Label $gShare 'Unsigned. Windows SmartScreen may show a warning when it runs on another PC.' 36 92 660 20 -Dim | Out-Null
$gShare.Controls.AddRange(@($chkInstaller, $cmbFormat, $txtShare, $btnShare))
$y += 128

# ---- Output + buttons
$log = New-Object System.Windows.Forms.TextBox
$log.Multiline = $true; $log.ScrollBars = 'Vertical'; $log.ReadOnly = $true; $log.WordWrap = $false
$log.Location = New-Object System.Drawing.Point(16, $y); $log.Size = New-Object System.Drawing.Size(712, 88)
$log.Font = New-Object System.Drawing.Font('Cascadia Mono, Consolas', 8.5); $log.BackColor = [System.Drawing.Color]::White
$y += 100
$progress = New-Object System.Windows.Forms.ProgressBar; $progress.Location = New-Object System.Drawing.Point(16, ($y + 12)); $progress.Size = New-Object System.Drawing.Size(410, 10); $progress.Style = 'Marquee'; $progress.Visible = $false
$btnUninstall = New-Control Button 'Uninstall…' 436 $y 92 34
$btnBuild = New-Control Button 'Build' 536 $y 92 34; Set-Primary $btnBuild
$btnClose = New-Control Button 'Close' 636 $y 92 34
$form.Controls.AddRange(@($log, $progress, $btnBuild, $btnClose, $btnUninstall))
$form.AcceptButton = $btnBuild; $form.CancelButton = $btnClose
$form.ClientSize = New-Object System.Drawing.Size(744, ($y + 34 + 14))

function Write-Log([string]$text) { $log.AppendText($text + "`r`n") }

# ---------------------------------------------------------------- Developer hints
# What each option does, for developers working on the app. Remove for a release by setting $DeveloperHints = $false
# at the top (or delete this section).

if ($DeveloperHints) {
    $HintSections = [ordered]@{
        'Current build' = [ordered]@{
            'Rebuild and update' = 'Builds the current source again with the choices of the last build, keeps the current icon, and replaces the installed copy (a running copy is closed first). Shown as "Rebuild" when the last build was not installed. The quickest way to try a change.'
            'Details' = 'Version and architecture of the last build, where it is, when it was built, and whether any file under src\WinMuse changed after that. The choices are kept in %LocalAppData%\WinMuse-Build\last-build.json.'
        }
        'App' = [ordered]@{
            'Name' = 'Shown in the title bar, window titles, Start menu and Settings > Apps. The executable is always WinMuse.exe, and the library and settings stay in %LocalAppData%\WinMuse whatever the name. Written to src\WinMuse\Assets\Branding.json.'
        }
        'Icon' = [ordered]@{
            'Current icon' = 'Keeps AppIcon.ico and AppIcon.png in src\WinMuse\Assets as they are. Nothing is redrawn.'
            'Design' = 'One of the bundled designs in tools\icons.'
            'Custom color' = 'The Purple design recolored to the chosen color.'
            'Image file' = 'An SVG, PNG, JPG or ICO of your own. Images that are not square are padded with transparency. SVG files are rendered with Microsoft Edge in headless mode.'
            'Match title bar logo to accent color' = 'On: the title bar logo is drawn in the app''s accent color (the simple design for "Indigo (simple)", otherwise the Rings design). Off: it shows the icon unchanged. Ignored with Current icon, which keeps the logo setting of the last build.'
        }
        'Build' = [ordered]@{
            'Architecture' = 'x64 runs on Intel and AMD processors, ARM64 on Snapdragon and other ARM processors. Automatic uses this PC''s. 32-bit (x86) is not supported.'
            'Clean build' = 'Rebuilds every file instead of only what changed. Slower; use it when the app seems out of date after a build.'
            'Open app when finished' = 'Starts the app when everything is done (the installed copy when installing) and closes this window. After a failure the window stays open.'
        }
        'Installation' = [ordered]@{
            'Install for current user' = 'Copies the build to Location and adds a Start menu shortcut, with an Uninstall.ps1 next to the exe. No administrator rights needed. A copy running from Location is closed first.'
            'Desktop shortcut' = 'Also adds a shortcut on the desktop.'
            'List in Settings > Apps' = 'Adds an entry in Settings > Apps with Uninstall. Uninstalling keeps the library and settings.'
            'Add to "Open with" for audio files' = 'Offers the app under "Open with" for 15 audio formats. Default apps are not changed.'
            'Uninstall…' = 'Opens a choice of three. Uninstall: removes the app installed in Location (by this tool or a .exe setup file) or by the .msi made for this name, with its shortcuts and Settings > Apps entry; the library and settings are kept. Uninstall and delete app data: also deletes %LocalAppData%\WinMuse (library, playlists, settings, artwork, lyrics, log), after a second confirmation; music files are never touched. Clean up build files: deletes every project''s bin and obj folders, %TEMP%\WinMuse-Build and the downloaded FFmpeg libraries in external; the next build restores them. Running copies that would be affected are closed first, after asking.'
        }
        'Setup file' = [ordered]@{
            'Create setup file' = 'Also makes "<Name> Setup.exe" or "<Name> Setup.msi" (tools\Make-Installer.ps1): one self-contained file that installs the app on another PC with nothing else needed. ARM64 builds add "(ARM64)" to the file name. Takes a few minutes.'
            'Format' = 'Setup program (.exe): a small setup window (tools\Installer); "/silent" installs without it. Windows Installer package (.msi): the standard Windows setup wizard (tools\InstallerMsi, built with WiX Toolset 5, downloaded from NuGet on first use); also works with msiexec, Group Policy and Intune. Both install for the current user without administrator rights and add the same shortcuts, "Open with" and Settings > Apps entry. Installing a newer setup file of the same name replaces the copy the older one installed.'
            'Unsigned' = 'The file has no code signature, so SmartScreen on other PCs asks first: More info > Run anyway.'
        }
    }

    function Show-DeveloperHints {
        $f = New-Object System.Windows.Forms.Form
        $f.Text = 'Developer hints'; $f.StartPosition = 'CenterParent'; $f.ClientSize = New-Object System.Drawing.Size(620, 640)
        $f.MinimizeBox = $false; $f.ShowIcon = $false; $f.ShowInTaskbar = $false
        $box = New-Object System.Windows.Forms.RichTextBox
        $box.Dock = 'Fill'; $box.ReadOnly = $true; $box.BorderStyle = 'None'; $box.BackColor = [System.Drawing.Color]::White
        $box.Font = New-Object System.Drawing.Font('Segoe UI', 9.5)
        $head = New-Object System.Drawing.Font('Segoe UI Semibold', 11); $term = New-Object System.Drawing.Font('Segoe UI Semibold', 9.5)
        foreach ($section in $HintSections.Keys) {
            $box.SelectionFont = $head; $box.AppendText("$section`n")
            foreach ($option in $HintSections[$section].Keys) {
                $box.SelectionFont = $term; $box.AppendText("$option`n")
                $box.SelectionFont = $box.Font; $box.AppendText("$($HintSections[$section][$option])`n`n")
            }
        }
        $box.SelectionStart = 0
        $pad = New-Object System.Windows.Forms.Panel; $pad.Dock = 'Fill'; $pad.Padding = New-Object System.Windows.Forms.Padding(16, 12, 8, 8); $pad.BackColor = [System.Drawing.Color]::White
        $pad.Controls.Add($box); $f.Controls.Add($pad)
        [void]$f.ShowDialog($form)
    }

    $btnHints = New-Control Button 'Developer hints' 16 $btnBuild.Top 130 34
    $btnHints.Add_Click({ Show-DeveloperHints })
    $form.Controls.Add($btnHints)
    $progress.Left = 156; $progress.Width = 270
}

# ---------------------------------------------------------------- behaviour

function Get-SafeName { ($txtName.Text.Trim() -replace '[^\w\- ]', '').Trim() }
function Update-InstallDefault { if (-not $txtInstall.Tag) { $txtInstall.Text = Join-Path $env:LOCALAPPDATA ("Programs\" + $(if (Get-SafeName) { Get-SafeName } else { $DefaultName })) } }
$txtInstall.Add_TextChanged({ if ($txtInstall.Focused) { $txtInstall.Tag = 'edited' } })
$txtName.Add_TextChanged({ Update-InstallDefault })
Update-InstallDefault

function Update-RegisterEnabled { foreach ($c in $txtInstall, $btnInstall, $chkDesktop, $chkApps, $chkOpenWith) { $c.Enabled = $chkRegister.Checked } }
$chkRegister.Add_CheckedChanged({ Update-RegisterEnabled }); Update-RegisterEnabled
function Get-SetupFormat { if ($cmbFormat.SelectedIndex -eq 1) { 'msi' } else { 'exe' } }
function Update-ShareEnabled { foreach ($c in $cmbFormat, $txtShare, $btnShare) { $c.Enabled = $chkInstaller.Checked } }
$chkInstaller.Add_CheckedChanged({ Update-ShareEnabled }); Update-ShareEnabled

function Get-ColorOrNull { try { return [System.Drawing.ColorTranslator]::FromHtml($txtColor.Text.Trim()) } catch { return $null } }

# The window starts with the choices of the previous build, so Build repeats it unless something is changed.
function Set-Choices($prev) {
    $txtName.Text = [string]$prev.Name
    $cmbArch.SelectedIndex = if ($prev.Arch -eq $native) { 0 } elseif ($prev.Arch -eq 'ARM64') { 2 } else { 1 }
    $chkLaunch.Checked = [bool]$prev.Launch
    $chkRegister.Checked = [bool]$prev.Register
    if ($prev.InstallDir) { $txtInstall.Text = [string]$prev.InstallDir; $txtInstall.Tag = 'edited' }
    $chkDesktop.Checked = [bool]$prev.Desktop; $chkApps.Checked = [bool]$prev.Apps; $chkOpenWith.Checked = [bool]$prev.OpenWith
    $chkInstaller.Checked = [bool]$prev.Installer
    $cmbFormat.SelectedIndex = if ($prev.SetupFormat -eq 'msi') { 1 } else { 0 }
    if ($prev.ShareDir) { $txtShare.Text = [string]$prev.ShareDir }
    $rbCurrent.Checked = $true
}

function Save-Choices([string]$arch) {
    $record = [ordered]@{
        Name = $txtName.Text.Trim(); Arch = $arch; Launch = $chkLaunch.Checked; Register = $chkRegister.Checked
        InstallDir = $txtInstall.Text.Trim(); Desktop = $chkDesktop.Checked; Apps = $chkApps.Checked; OpenWith = $chkOpenWith.Checked
        Installer = $chkInstaller.Checked; SetupFormat = (Get-SetupFormat); ShareDir = $txtShare.Text.Trim(); Version = $AppVersion; BuiltAt = (Get-Date).ToString('o')
    }
    try {
        New-Item -ItemType Directory -Force (Split-Path $RecordPath) | Out-Null
        [IO.File]::WriteAllText($RecordPath, ($record | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))
    } catch { Write-Log "The build choices could not be saved: $($_.Exception.Message)" }
}

$previewCache = @{}
# Draws the chosen icon into $png. Returns the path, or throws with a message.
function Resolve-IconPng([string]$png) {
    if ($rbCurrent.Checked) {
        $current = Join-Path $Assets 'AppIcon.png'
        if (-not (Test-Path $current)) { throw 'No current icon was found in src\WinMuse\Assets.' }
        Copy-Item $current $png -Force
    } elseif ($rbPreset.Checked) {
        $svg = Join-Path $IconDir $Presets[$cmbPreset.SelectedItem]
        Convert-SvgToPng $svg $png
    } elseif ($rbColor.Checked) {
        $c = Get-ColorOrNull; if (-not $c) { throw 'Enter a hex color, for example #1DB954.' }
        $svg = Join-Path $Work 'recolored.svg'; New-RecoloredSvg $c $svg
        Convert-SvgToPng $svg $png
    } else {
        if (-not (Test-Path $txtFile.Text)) { throw 'Select an image file.' }
        Convert-ImageToSquarePng $txtFile.Text $png
    }
    return $png
}

function Update-Preview {
    $key = if ($rbCurrent.Checked) { 'current' } elseif ($rbPreset.Checked) { "p:$($cmbPreset.SelectedItem)" } elseif ($rbColor.Checked) { "c:$($txtColor.Text)" } else { "f:$($txtFile.Text)" }
    $swatch.BackColor = $(if ($c = Get-ColorOrNull) { $c } else { [System.Drawing.Color]::Transparent })
    $chkAccent.Enabled = -not $rbCurrent.Checked
    try {
        if (-not $previewCache.ContainsKey($key)) {
            $form.Cursor = 'WaitCursor'
            $png = Join-Path $Work ("preview-" + [guid]::NewGuid().ToString('N') + '.png')
            Resolve-IconPng $png | Out-Null
            $bytes = [IO.File]::ReadAllBytes($png)
            $previewCache[$key] = [System.Drawing.Image]::FromStream((New-Object IO.MemoryStream(, $bytes)))
            Remove-Item $png -Force -ErrorAction SilentlyContinue
        }
        $picPreview.Image = $previewCache[$key]; $lblPreview.Text = 'Preview'
    } catch { $picPreview.Image = $null; $lblPreview.Text = $_.Exception.Message }
    finally { $form.Cursor = 'Default' }
}

$rbCurrent.Add_CheckedChanged({ if ($rbCurrent.Checked) { Update-Preview } })
$rbPreset.Add_CheckedChanged({ if ($rbPreset.Checked) { Update-Preview } })
$rbColor.Add_CheckedChanged({ if ($rbColor.Checked) { Update-Preview } })
$rbFile.Add_CheckedChanged({ if ($rbFile.Checked) { $chkAccent.Checked = $false; Update-Preview } })
$cmbPreset.Add_SelectedIndexChanged({ if ($form.Visible) { $rbPreset.Checked = $true; Update-Preview } })
$txtColor.Add_Leave({ $rbColor.Checked = $true; Update-Preview })
$txtColor.Add_KeyDown({ if ($_.KeyCode -eq 'Enter') { $rbColor.Checked = $true; Update-Preview; $_.SuppressKeyPress = $true } })
$btnPick.Add_Click({
    $dlg = New-Object System.Windows.Forms.ColorDialog; $dlg.FullOpen = $true
    if ($c = Get-ColorOrNull) { $dlg.Color = $c }
    if ($dlg.ShowDialog() -eq 'OK') { $txtColor.Text = '#{0:X2}{1:X2}{2:X2}' -f $dlg.Color.R, $dlg.Color.G, $dlg.Color.B; $rbColor.Checked = $true; Update-Preview }
})
$btnFile.Add_Click({
    $dlg = New-Object System.Windows.Forms.OpenFileDialog; $dlg.Filter = 'Images|*.svg;*.png;*.jpg;*.jpeg;*.ico|All files|*.*'
    if ($dlg.ShowDialog() -eq 'OK') { $txtFile.Text = $dlg.FileName; $rbFile.Checked = $true; Update-Preview }
})
$btnInstall.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog; $dlg.SelectedPath = $txtInstall.Text
    if ($dlg.ShowDialog() -eq 'OK') { $txtInstall.Text = $dlg.SelectedPath; $txtInstall.Tag = 'edited' }
})
$btnShare.Add_Click({
    $dlg = New-Object System.Windows.Forms.FolderBrowserDialog; $dlg.SelectedPath = $txtShare.Text
    if ($dlg.ShowDialog() -eq 'OK') { $txtShare.Text = $dlg.SelectedPath }
})
$btnClose.Add_Click({ $form.Close() })

# ---- Registration

function New-Shortcut([string]$lnk, [string]$target, [string]$name) {
    $ws = New-Object -ComObject WScript.Shell
    $s = $ws.CreateShortcut($lnk); $s.TargetPath = $target; $s.WorkingDirectory = Split-Path $target
    $s.IconLocation = "$target,0"; $s.Description = $name; $s.Save()
}

function Register-App([string]$installDir, [string]$name) {
    $exe = Join-Path $installDir 'WinMuse.exe'
    $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) "$name.lnk"
    New-Shortcut $startMenu $exe $name; Write-Log "Start menu shortcut: $startMenu"
    $desktopLnk = Join-Path ([Environment]::GetFolderPath('Desktop')) "$name.lnk"
    if ($chkDesktop.Checked) { New-Shortcut $desktopLnk $exe $name; Write-Log "Desktop shortcut: $desktopLnk" }

    $classes = 'HKCU:\Software\Classes\Applications\WinMuse.exe'
    if ($chkOpenWith.Checked) {
        New-Item -Force "$classes\shell\open\command" | Out-Null
        Set-ItemProperty $classes 'FriendlyAppName' $name
        Set-ItemProperty "$classes\shell\open\command" '(default)' "`"$exe`" `"%1`""
        New-Item -Force "$classes\SupportedTypes" | Out-Null
        foreach ($t in $AudioTypes) {
            Set-ItemProperty "$classes\SupportedTypes" $t ''
            New-Item -Force "HKCU:\Software\Classes\$t\OpenWithList\WinMuse.exe" | Out-Null
        }
        Write-Log "Registered ""Open with"" for $($AudioTypes.Count) audio file types"
    }

    # The uninstaller lives next to the app; it copies itself to TEMP before deleting the folder.
    $uninstall = Join-Path $installDir 'Uninstall.ps1'
    @"
# Removes $name (installed by tools\Build-App.ps1). Your library and settings in %LocalAppData%\WinMuse are kept.
`$dir = '$installDir'
if (`$PSCommandPath -like "`$dir*") { `$t = Join-Path `$env:TEMP 'WinMuse-Uninstall.ps1'; Copy-Item `$PSCommandPath `$t -Force; Start-Process powershell "-NoProfile -ExecutionPolicy Bypass -File ```"`$t```"" -WindowStyle Hidden; exit }
Get-Process WinMuse -ErrorAction SilentlyContinue | Where-Object { `$_.Path -like "`$dir*" } | Stop-Process -Force
Start-Sleep -Milliseconds 800
Remove-Item -Force -ErrorAction SilentlyContinue '$startMenu', '$desktopLnk'
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$(Get-AppRegKey $name)', '$classes'
foreach (`$t in '$($AudioTypes -join "','")') { Remove-Item -Recurse -Force -ErrorAction SilentlyContinue "HKCU:\Software\Classes\`$t\OpenWithList\WinMuse.exe" }
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue `$dir
"@ | Set-Content -Encoding UTF8 $uninstall

    if ($chkApps.Checked) {
        $key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$(Get-AppRegKey $name)"
        New-Item -Force $key | Out-Null
        $size = [int]((Get-ChildItem $installDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
        $props = @{
            DisplayName = $name; DisplayIcon = "$exe,0"; DisplayVersion = $AppVersion; Publisher = 'WinMuse'
            InstallLocation = $installDir; UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstall`""
        }
        foreach ($k in $props.Keys) { Set-ItemProperty $key $k $props[$k] }
        Set-ItemProperty $key NoModify 1 -Type DWord; Set-ItemProperty $key NoRepair 1 -Type DWord; Set-ItemProperty $key EstimatedSize $size -Type DWord
        Write-Log "Listed in Settings > Apps as ""$name"""
    }
}

# ---- Uninstall and clean up

function Get-FolderSize([string[]]$paths) {
    $sum = 0L
    foreach ($p in $paths) {
        if (Test-Path $p) { $sum += [long](Get-ChildItem $p -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum }
    }
    return $sum
}

function Format-Size([long]$bytes) {
    if ($bytes -ge 1GB) { return '{0:N1} GB' -f ($bytes / 1GB) }
    if ($bytes -ge 1MB) { return '{0:N0} MB' -f ($bytes / 1MB) }
    return '{0:N0} KB' -f [math]::Max(1, [math]::Ceiling($bytes / 1KB))
}

# Folders only needed to build the app: each project's bin and obj, this tool's temporary files and the downloaded
# FFmpeg libraries. The next build creates them again.
function Get-BuildFolders {
    $projects = Get-ChildItem (Join-Path $Root 'src'), (Join-Path $Root 'tests'), (Join-Path $Root 'tools') -Recurse -File -Include '*.csproj', '*.wixproj' -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    $folders = foreach ($p in $projects) { Join-Path $p.DirectoryName 'bin'; Join-Path $p.DirectoryName 'obj' }
    $folders = @($folders) + @($Work, (Join-Path $Root 'external\ffmpeg'), (Join-Path $Root 'external\ffmpeg-arm64'))
    return @($folders | Where-Object { Test-Path $_ } | Select-Object -Unique)
}

# The Windows Installer package installed for an app name (found by the upgrade code made from the name), or $null.
function Find-MsiProduct([string]$name) {
    try {
        $msi = New-Object -ComObject WindowsInstaller.Installer
        $codes = @($msi.GetType().InvokeMember('RelatedProducts', 'GetProperty', $null, $msi, @(Get-MsiUpgradeCode $name)))
        foreach ($code in $codes) {
            $location = try { [string]$msi.GetType().InvokeMember('ProductInfo', 'GetProperty', $null, $msi, @($code, 'InstallLocation')) } catch { '' }
            return [pscustomobject]@{ Code = [string]$code; Dir = $location.TrimEnd('\') }
        }
    } catch {}
    return $null
}

# The installed app this window can remove: the copy in Location (installed by this tool or by a .exe setup file), or
# the .msi installed for the name. $null when there is none.
function Find-Installation {
    $dir = $txtInstall.Text.Trim().TrimEnd('\')
    if ($dir -and (Test-Path (Join-Path $dir 'Uninstall.ps1'))) { return [pscustomobject]@{ Kind = 'Tool'; Dir = $dir } }
    if ($dir -and (Test-Path (Join-Path $dir 'Uninstall.exe')) -and (Test-Path (Join-Path $dir 'setup.ini'))) { return [pscustomobject]@{ Kind = 'Setup'; Dir = $dir } }
    $product = Find-MsiProduct $txtName.Text.Trim()
    if ($product) { return [pscustomobject]@{ Kind = 'Msi'; Dir = $product.Dir; Code = $product.Code } }
    return $null
}

# Closes the copies of the app that would hold files open, after asking. $false when the user declines.
function Close-RunningApp([scriptblock]$filter, [string]$reason) {
    $running = @(Get-Process WinMuse -ErrorAction SilentlyContinue | Where-Object { $_.Path -and (& $filter $_.Path) })
    if ($running.Count -eq 0) { return $true }
    $answer = [System.Windows.Forms.MessageBox]::Show("The app is running. Close it to $reason`?", 'Uninstall', 'OKCancel', 'Question')
    if ($answer -ne 'OK') { return $false }
    foreach ($p in $running) { $p.CloseMainWindow() | Out-Null }
    foreach ($p in $running) { if (-not $p.WaitForExit(4000)) { $p | Stop-Process -Force -ErrorAction SilentlyContinue } }
    Start-Sleep -Milliseconds 500
    return $true
}

function Wait-FolderRemoved([string]$dir, [int]$seconds = 30) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ($dir -and (Test-Path $dir) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300; [System.Windows.Forms.Application]::DoEvents() }
}

function Invoke-Uninstall($inst) {
    switch ($inst.Kind) {
        'Tool' {
            Start-Process powershell "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $inst.Dir 'Uninstall.ps1')`"" -WindowStyle Hidden -Wait
            Wait-FolderRemoved $inst.Dir
        }
        'Setup' {
            Start-Process (Join-Path $inst.Dir 'Uninstall.exe') '/silent' -Wait
            Wait-FolderRemoved $inst.Dir
        }
        'Msi' {
            $p = Start-Process msiexec.exe "/x $($inst.Code) /qn /norestart" -Wait -PassThru
            if ($p.ExitCode -notin 0, 3010) { throw "Windows Installer could not remove the app (error $($p.ExitCode))." }
        }
    }
    if ($inst.Dir -and (Test-Path (Join-Path $inst.Dir 'WinMuse.exe'))) { throw "The app could not be removed from $($inst.Dir)." }
}

# Deletes folders, reporting what could not be deleted (a file in use) instead of stopping at the first one.
function Remove-Folders([string[]]$folders) {
    $failed = @()
    foreach ($f in $folders) {
        Remove-Item -Recurse -Force -LiteralPath $f -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $f) { $failed += $f }
    }
    return $failed
}

# The Current build panel after something was removed: what is left of the last build.
function Update-CurrentBuild {
    if (-not $Previous) { return }
    $s = Get-BuildStatus $Previous
    if ($s) {
        $lblTitle.Text = $s.Title; $lblWhere.Text = $s.Where; $lblWhen.Text = $s.When; $tips.SetToolTip($lblWhere, $s.Where)
    } else {
        $lblTitle.Text = [string]$Previous.Name
        $lblWhere.Text = 'Not on this PC anymore. Rebuild to build it again with the same choices.'; $tips.SetToolTip($lblWhere, '')
        $lblWhen.Text = ''
    }
}

# The Uninstall window: one of three choices, each saying exactly what it removes and what it keeps.
function Show-UninstallDialog {
    $name = $txtName.Text.Trim(); if (-not $name) { $name = $DefaultName }
    $inst = Find-Installation
    $hasData = Test-Path $DataDir
    $buildFolders = Get-BuildFolders
    $outputExe = Join-Path (Get-OutputDir $(if ($cmbArch.SelectedIndex -eq 0) { $native } else { [string]$cmbArch.SelectedItem })) 'WinMuse.exe'

    $d = New-Object System.Windows.Forms.Form
    $d.Text = 'Uninstall'; $d.StartPosition = 'CenterParent'; $d.FormBorderStyle = 'FixedDialog'
    $d.MinimizeBox = $false; $d.MaximizeBox = $false; $d.ShowInTaskbar = $false; $d.ShowIcon = $false
    $d.Font = $form.Font; $d.BackColor = $form.BackColor; $d.ClientSize = New-Object System.Drawing.Size(580, 436)
    Add-Label $d 'Choose what to remove' 24 18 520 26 -Bold | Out-Null

    $choices = @(
        @{
            Title = "Uninstall $name"
            Text = if ($inst) { 'Removes the app, its shortcuts and its entry in Settings > Apps. Your library, playlists and settings are kept.' }
                   else { "$name is not installed in the location set under Installation." }
            Where = if ($inst -and $inst.Dir) { $inst.Dir } elseif ($inst) { 'Installed with the Windows Installer package' } else { '' }
            Enabled = [bool]$inst; Button = 'Uninstall'
        }
        @{
            Title = if ($inst) { 'Uninstall and delete app data' } else { 'Delete app data' }
            Text = if ($hasData) { "$(if ($inst) { 'Also deletes' } else { 'Deletes' }) your library, playlists, settings, play history, artwork and lyrics ($(Format-Size (Get-FolderSize $DataDir))). Your music files are not touched." }
                   else { 'There is no app data on this PC.' }
            Where = if ($hasData) { $DataDir } else { '' }
            Enabled = [bool]($inst -or $hasData); Button = if ($inst) { 'Uninstall and delete' } else { 'Delete' }
        }
        @{
            Title = 'Clean up build files'
            Text = if ($buildFolders.Count) {
                       "Deletes build output, temporary files and downloaded libraries ($(Format-Size (Get-FolderSize $buildFolders))). The source code is kept; the next build takes longer." +
                       $(if (-not $inst -and (Test-Path $outputExe)) { ' The app is not installed, so its only built copy is removed too.' } else { ' The installed app is not affected.' })
                   } else { 'There are no build files to remove.' }
            Where = if ($buildFolders.Count) { "$Root" } else { '' }
            Enabled = [bool]$buildFolders.Count; Button = 'Clean up'
        }
    )

    $btnOk = New-Control Button '' 356 388 116 34; Set-Primary $btnOk
    $btnCancel = New-Control Button 'Cancel' 480 388 76 34
    $pathTips = New-Object System.Windows.Forms.ToolTip
    $btnOk.DialogResult = 'OK'; $btnCancel.DialogResult = 'Cancel'
    $d.AcceptButton = $btnOk; $d.CancelButton = $btnCancel

    $radios = @()
    $y = 60
    foreach ($c in $choices) {
        $rb = New-Control RadioButton $c.Title 24 $y
        $rb.Font = New-Object System.Drawing.Font('Segoe UI Semibold', 10); $rb.Enabled = $c.Enabled; $rb.Tag = $c.Button
        $rb.Add_CheckedChanged({ if ($this.Checked) { $btnOk.Text = $this.Tag } })
        $d.Controls.Add($rb); $radios += $rb
        $desc = Add-Label $d $c.Text 44 ($y + 28) 512 40 -Dim
        if (-not $c.Enabled) { $desc.ForeColor = [System.Drawing.Color]::FromArgb(150, 150, 158) }
        if ($c.Where) {
            $loc = Add-Label $d "Location: $($c.Where)" 44 ($y + 70) 512 20 -Dim
            $loc.AutoEllipsis = $true; $loc.Font = New-Object System.Drawing.Font('Segoe UI', 8.5); $pathTips.SetToolTip($loc, $c.Where)
        }
        $y += 104
    }
    $d.Controls.AddRange(@($btnOk, $btnCancel))
    $first = $radios | Where-Object { $_.Enabled } | Select-Object -First 1
    if ($first) { $first.Checked = $true } else { $btnOk.Text = 'Uninstall'; $btnOk.Enabled = $false; $btnOk.BackColor = [System.Drawing.Color]::FromArgb(190, 190, 198) }

    if ($d.ShowDialog($form) -ne 'OK') { return $null }
    return [pscustomobject]@{ Choice = [array]::IndexOf($radios, ($radios | Where-Object { $_.Checked })); Installation = $inst; Name = $name; BuildFolders = $buildFolders }
}

$btnUninstall.Add_Click({
    $pick = Show-UninstallDialog
    if (-not $pick) { return }
    $inst = $pick.Installation
    $log.Clear()
    try {
        Set-Busy $true
        if ($pick.Choice -eq 1) {
            $confirm = [System.Windows.Forms.MessageBox]::Show("Permanently delete your library, playlists and settings?`n`nThis cannot be undone. Your music files are not touched.",
                'Delete app data', 'YesNo', 'Warning', 'Button2')
            if ($confirm -ne 'Yes') { return }
        }
        # Copies of the app that would hold the files open: the installed one, any (they all use the data), or the build output.
        $installDir = if ($inst -and $inst.Dir) { "$($inst.Dir)\*" } else { '*' }
        $closed = switch ($pick.Choice) {
            0 { Close-RunningApp { param($p) $p -like $installDir } 'uninstall it' }
            1 { Close-RunningApp { param($p) $true } 'delete its data' }
            2 { Close-RunningApp { param($p) $p -like "$Root\*" } 'remove its build files' }
        }
        if (-not $closed) { return }
        if ($pick.Choice -in 0, 1 -and $inst) {
            Write-Log "Uninstalling $($pick.Name)…"; [System.Windows.Forms.Application]::DoEvents()
            Invoke-Uninstall $inst
            Write-Log "Uninstalled $($pick.Name)$(if ($inst.Dir) { " from $($inst.Dir)" })."
        }
        if ($pick.Choice -eq 1 -and (Test-Path $DataDir)) {
            Write-Log 'Deleting app data…'; [System.Windows.Forms.Application]::DoEvents()
            if (Remove-Folders @($DataDir)) { throw "Some app data could not be deleted. Close any program using $DataDir and try again." }
            Write-Log "Deleted app data ($DataDir)."
        }
        if ($pick.Choice -eq 2) {
            Write-Log 'Removing build files…'; [System.Windows.Forms.Application]::DoEvents()
            $size = Get-FolderSize $pick.BuildFolders
            $left = @(Remove-Folders $pick.BuildFolders)
            New-Item -ItemType Directory -Force $Work | Out-Null   # this window keeps its temporary files there
            Write-Log "Removed $(Format-Size ($size - (Get-FolderSize $left))) of build files."
            foreach ($f in $left) { Write-Log "Could not remove (in use): $f" }
        }
        Write-Log 'Completed.'
    } catch { Write-Log ('Error: ' + $_.Exception.Message) }
    finally { Set-Busy $false; Update-CurrentBuild }
})

# ---- Build (runs dotnet in the background; a timer follows its output so the window stays responsive)

$state = @{}
$timer = New-Object System.Windows.Forms.Timer; $timer.Interval = 300

function Set-Busy([bool]$busy) {
    $controls = @($gApp, $gIcon, $gBuild, $gReg, $gShare, $btnBuild, $btnUninstall)
    if ($Previous) { $controls += $gCurrent }
    foreach ($c in $controls) { $c.Enabled = -not $busy }
    $progress.Visible = $busy
}

function Start-Build {
    $log.Clear()
    $name = $txtName.Text.Trim()
    if (-not (Test-Name $name)) { [System.Windows.Forms.MessageBox]::Show('Enter a name. It cannot contain \ / : * ? " < > |', 'Name', 'OK', 'Warning') | Out-Null; return }
    $arch = if ($cmbArch.SelectedIndex -eq 0) { $native } else { [string]$cmbArch.SelectedItem }
    try {
        Set-Busy $true
        Write-Log "Building $name for $arch"
        $brandingFile = Join-Path $Assets 'Branding.json'
        if ($rbCurrent.Checked) {
            # The icon and the title bar logo stay as they are; only a new name is written.
            $brand = Get-Branding
            $logo = if ($brand -and $brand.Logo) { [string]$brand.Logo } else { 'Rings' }
            Write-Log 'Icon: current'
        } else {
            Write-Log 'Generating icon…'; [System.Windows.Forms.Application]::DoEvents()
            $png = Resolve-IconPng (Join-Path $Work 'icon.png')
            & (Join-Path $PSScriptRoot 'Make-Icons.ps1') -FromPng $png | Out-Null
            Write-Log 'Icon generated (AppIcon.ico, AppIcon.png)'
            $logo = if ($chkAccent.Checked) { if ($rbPreset.Checked -and $cmbPreset.SelectedItem -like '*simple*') { 'Simple' } else { 'Rings' } } else { 'Image' }
            $brand = $null
        }
        if (-not ($brand -and $brand.Name -eq $name -and $brand.Logo -eq $logo)) {
            @{ Name = $name; Logo = $logo } | ConvertTo-Json | Set-Content -Encoding UTF8 $brandingFile
        }
        Write-Log "Branding: $name, title bar logo: $logo"

        $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
        if (-not $dotnet) { throw 'The .NET SDK was not found. Install the .NET 10 SDK and try again.' }
        # FFmpeg for this CPU first (fetched during the build it would miss the copy to the output on the first build).
        Write-Log 'Checking FFmpeg libraries…'; [System.Windows.Forms.Application]::DoEvents()
        & (Join-Path $PSScriptRoot 'Get-FFmpeg.ps1') -Arch $arch.ToLower() | Out-Null
        $buildLog = Join-Path $Work 'build.log'; Set-Content $buildLog ''
        $target = if ($chkClean.Checked) { '-t:Rebuild' } else { '' }
        $quoted = '"' + ($name -replace '"', '') + '"'
        $cmd = "dotnet build `"$Project`" -c Release -p:Platform=$arch $target -p:Product=$quoted -p:AssemblyTitle=$quoted -p:Description=$quoted -nologo -v:minimal > `"$buildLog`" 2>&1"
        Write-Log 'Compiling… A first build can take several minutes.'
        $state.Process = Start-Process cmd.exe -ArgumentList '/c', $cmd -PassThru -WindowStyle Hidden
        $state.Log = $buildLog; $state.Read = 0; $state.Name = $name; $state.Arch = $arch; $state.Phase = 'build'; $state.Setup = $null
        $timer.Start()
    } catch {
        Write-Log ('Error: ' + $_.Exception.Message); Set-Busy $false
    }
}

$btnBuild.Add_Click({ Start-Build })
if ($Previous) { $btnRebuild.Add_Click({ Set-Choices $Previous; $chkClean.Checked = $false; Start-Build }) }

$timer.Add_Tick({
    # New build output since the last tick.
    try {
        $fs = [IO.File]::Open($state.Log, 'Open', 'Read', 'ReadWrite'); $fs.Seek($state.Read, 'Begin') | Out-Null
        $sr = New-Object IO.StreamReader($fs); $chunk = $sr.ReadToEnd(); $state.Read = $fs.Position; $sr.Close()
        foreach ($line in ($chunk -split "`r?`n")) { if ($line.Trim()) { Write-Log ('  ' + $line.Trim()) } }
    } catch {}
    if (-not $state.Process.HasExited) { return }
    $timer.Stop()
    $done = $false
    try {
        if ($state.Phase -eq 'installer') {
            if ($state.Process.ExitCode -ne 0 -or -not (Test-Path $state.Setup)) { throw 'Creating the setup file failed. See the output above.' }
            Write-Log "Setup file: $($state.Setup)"
            $done = $true
        } else {
            $out = Get-OutputDir $state.Arch
            if ($state.Process.ExitCode -ne 0 -or -not (Test-Path (Join-Path $out 'WinMuse.exe'))) { throw 'The build failed. See the output above.' }
            Write-Log "Build output: $out"
            $state.Exe = Join-Path $out 'WinMuse.exe'
            if ($chkRegister.Checked) {
                $dir = $txtInstall.Text.Trim()
                $running = Get-Process WinMuse -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dir*" }
                if ($running) {
                    if ([System.Windows.Forms.MessageBox]::Show("$($state.Name) is running. Close it and continue the installation?", 'Install', 'YesNo', 'Question') -ne 'Yes') { throw 'Installation canceled because the app is running.' }
                    $running | ForEach-Object { $_.CloseMainWindow() | Out-Null }; Start-Sleep 2
                    Get-Process WinMuse -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dir*" } | Stop-Process -Force
                }
                Write-Log "Installing to $dir…"; [System.Windows.Forms.Application]::DoEvents()
                New-Item -ItemType Directory -Force $dir | Out-Null
                robocopy $out $dir /E /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
                if ($LASTEXITCODE -ge 8) { throw "Copying to $dir failed (robocopy exit code $LASTEXITCODE)." }
                Register-App $dir $state.Name
                $state.Exe = Join-Path $dir 'WinMuse.exe'
            }
            Save-Choices $state.Arch
            if ($chkInstaller.Checked) {
                # Second step, in the background like the build: publish self-contained, zip, wrap in the installer.
                # Built for the architecture chosen above; ARM64 installers say so in their name (they don't run on Intel/AMD PCs).
                $suffix = if ($state.Arch -eq 'ARM64') { ' (ARM64)' } else { '' }
                $format = Get-SetupFormat
                $state.Setup = Join-Path $txtShare.Text.Trim() "$($state.Name) Setup$suffix.$format"
                $setupLog = Join-Path $Work 'installer.log'; Set-Content $setupLog ''
                $script = Join-Path $PSScriptRoot 'Make-Installer.ps1'
                $cmd = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$script`" -Name `"$($state.Name -replace '"', '')`" -Arch $($state.Arch) -Format $format -Out `"$($state.Setup)`" > `"$setupLog`" 2>&1"
                Write-Log 'Creating setup file… This can take several minutes.'
                $state.Process = Start-Process cmd.exe -ArgumentList '/c', $cmd -PassThru -WindowStyle Hidden
                $state.Log = $setupLog; $state.Read = 0; $state.Phase = 'installer'
                $timer.Start()
                return
            }
            $done = $true
        }
    } catch { Write-Log ('Error: ' + $_.Exception.Message) }
    finally { if (-not $timer.Enabled) { Set-Busy $false } }
    if (-not $done) { return }   # after a failure the window stays open so the output can be read
    # Finished: open the app (and show the setup file in File Explorer), then close the window.
    Write-Log 'Completed.'
    if ($chkLaunch.Checked) { Start-Process $state.Exe }
    if ($state.Setup) { Start-Process explorer.exe "/select,`"$($state.Setup)`"" }
    $form.Close()
})

if ($Previous) { Set-Choices $Previous } else { $rbPreset.Checked = $true }
$form.Add_Shown({ Update-Preview })
[void]$form.ShowDialog()

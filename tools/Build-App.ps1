# Build Music (WinMuse) with your own name and icon, and optionally install it as a Windows app.
#
#   Right-click > Run with PowerShell, or:  powershell -ExecutionPolicy Bypass -File tools\Build-App.ps1
#
# Choices:
#   Name          shown in the title bar, window titles, Start menu and Apps list (default "Music").
#   Icon          one of the bundled designs (tools\icons\*.svg, default Purple), any color (the design is recolored
#                 to it), or your own image (.svg, .png, .jpg, .ico).
#   Title-bar logo  follows the accent color (Rings design) or shows the icon exactly as it is.
#   Architecture  Auto (this PC), x64 or ARM64.
#   Register      copies the app to an install folder and adds a Start menu shortcut, optionally a desktop shortcut,
#                 an entry in Settings > Apps (with an uninstaller) and "Open with" for audio files.
#   Installer     also makes "<Name> Setup.exe" (tools\Make-Installer.ps1): one file to share that installs the app on
#                 another PC, with everything it needs included.
#
# The exe stays WinMuse.exe and your library/settings stay in %LocalAppData%\WinMuse whatever the name.
# SVG icons are drawn with Microsoft Edge (installed with Windows 11) in headless mode.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms, System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$Root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Project = Join-Path $Root 'src\WinMuse\WinMuse.csproj'
$Assets = Join-Path $Root 'src\WinMuse\Assets'
$IconDir = Join-Path $PSScriptRoot 'icons'
$Work = Join-Path $env:TEMP 'WinMuse-Build'
New-Item -ItemType Directory -Force $Work | Out-Null

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

function Find-Edge {
    foreach ($p in "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") {
        if (Test-Path $p) { return $p }
    }
    return $null
}

# Draws an SVG to a transparent square PNG with Edge headless.
function Convert-SvgToPng([string]$svg, [string]$png, [int]$size = 512) {
    $edge = Find-Edge
    if (-not $edge) { throw 'Microsoft Edge was not found; it is needed to draw SVG icons. Use a PNG instead.' }
    $html = Join-Path $Work 'render.html'
    $uri = ([Uri](Resolve-Path $svg).Path).AbsoluteUri
    "<!doctype html><html><body style='margin:0;background:transparent'><img src='$uri' width='$size' height='$size' style='display:block;object-fit:contain'></body></html>" |
        Set-Content -Encoding UTF8 $html
    if (Test-Path $png) { Remove-Item $png -Force }
    $edgeArgs = @('--headless=new', '--disable-gpu', '--hide-scrollbars', '--default-background-color=00000000', "--window-size=$size,$size",
              "--user-data-dir=`"$(Join-Path $Work 'edge')`"", "--screenshot=`"$png`"", "`"$(([Uri]$html).AbsoluteUri)`"")
    $p = Start-Process $edge -ArgumentList $edgeArgs -PassThru -WindowStyle Hidden
    if (-not $p.WaitForExit(30000)) { try { $p.Kill() } catch {}; throw 'Drawing the SVG timed out.' }
    if (-not (Test-Path $png)) { throw "The SVG could not be drawn: $svg" }
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

# ---------------------------------------------------------------- window

$form = New-Object System.Windows.Forms.Form
$form.Text = 'Build Music'
$form.Size = New-Object System.Drawing.Size(760, 924)
$form.StartPosition = 'CenterScreen'
$form.Font = New-Object System.Drawing.Font('Segoe UI', 9.5)
$form.BackColor = [System.Drawing.Color]::FromArgb(250, 250, 252)
$form.FormBorderStyle = 'FixedDialog'; $form.MaximizeBox = $false
$iconPath = Join-Path $Assets 'AppIcon.ico'
if (Test-Path $iconPath) { $form.Icon = New-Object System.Drawing.Icon $iconPath }

function Add-Label($parent, [string]$text, [int]$x, [int]$y, [int]$w = 200, [switch]$Bold, [switch]$Dim) {
    $l = New-Object System.Windows.Forms.Label
    $l.Text = $text; $l.Location = New-Object System.Drawing.Point($x, $y); $l.AutoSize = $false
    $l.Size = New-Object System.Drawing.Size($w, $(if ($Dim) { 40 } else { 22 }))   # hints may wrap to two lines
    if ($Bold) { $l.Font = New-Object System.Drawing.Font('Segoe UI Semibold', 10) }
    if ($Dim) { $l.ForeColor = [System.Drawing.Color]::FromArgb(100, 100, 110) }
    $parent.Controls.Add($l); return $l
}

function Add-Group([string]$title, [int]$y, [int]$h) {
    $g = New-Object System.Windows.Forms.GroupBox
    $g.Text = $title; $g.Location = New-Object System.Drawing.Point(16, $y); $g.Size = New-Object System.Drawing.Size(712, $h)
    $g.Font = New-Object System.Drawing.Font('Segoe UI Semibold', 10)
    $form.Controls.Add($g); return $g
}

function Set-Font($c) { $c.Font = New-Object System.Drawing.Font('Segoe UI', 9.5); return $c }

# ---- App
$gApp = Add-Group 'App' 12 74
Set-Font (Add-Label $gApp 'Name' 16 32 60) | Out-Null
$txtName = Set-Font (New-Object System.Windows.Forms.TextBox)
$txtName.Location = New-Object System.Drawing.Point(80, 29); $txtName.Size = New-Object System.Drawing.Size(260, 26); $txtName.Text = 'Music'
$gApp.Controls.Add($txtName)
Set-Font (Add-Label $gApp 'Title bar, window, Start menu and Apps list. The exe stays WinMuse.exe.' 352 32 350 -Dim) | Out-Null

# ---- Icon
$gIcon = Add-Group 'Icon' 96 246
$picPreview = New-Object System.Windows.Forms.PictureBox
$picPreview.Location = New-Object System.Drawing.Point(540, 30); $picPreview.Size = New-Object System.Drawing.Size(150, 150)
$picPreview.SizeMode = 'Zoom'; $picPreview.BackColor = [System.Drawing.Color]::Transparent
$gIcon.Controls.Add($picPreview)
$lblPreview = Set-Font (Add-Label $gIcon 'Preview' 540 184 150 -Dim); $lblPreview.TextAlign = 'MiddleCenter'

$rbPreset = Set-Font (New-Object System.Windows.Forms.RadioButton); $rbPreset.Text = 'Design'; $rbPreset.Location = New-Object System.Drawing.Point(16, 32); $rbPreset.AutoSize = $true; $rbPreset.Checked = $true
$cmbPreset = Set-Font (New-Object System.Windows.Forms.ComboBox); $cmbPreset.DropDownStyle = 'DropDownList'
$cmbPreset.Location = New-Object System.Drawing.Point(150, 30); $cmbPreset.Size = New-Object System.Drawing.Size(220, 26)
foreach ($k in $Presets.Keys) { [void]$cmbPreset.Items.Add($k) }; $cmbPreset.SelectedIndex = 0

$rbColor = Set-Font (New-Object System.Windows.Forms.RadioButton); $rbColor.Text = 'Any color'; $rbColor.Location = New-Object System.Drawing.Point(16, 72); $rbColor.AutoSize = $true
$txtColor = Set-Font (New-Object System.Windows.Forms.TextBox); $txtColor.Location = New-Object System.Drawing.Point(150, 70); $txtColor.Size = New-Object System.Drawing.Size(110, 26); $txtColor.Text = '#1DB954'
$btnPick = Set-Font (New-Object System.Windows.Forms.Button); $btnPick.Text = 'Pick…'; $btnPick.Location = New-Object System.Drawing.Point(268, 69); $btnPick.Size = New-Object System.Drawing.Size(70, 28)
$swatch = New-Object System.Windows.Forms.Panel; $swatch.Location = New-Object System.Drawing.Point(346, 70); $swatch.Size = New-Object System.Drawing.Size(26, 26); $swatch.BorderStyle = 'FixedSingle'

$rbFile = Set-Font (New-Object System.Windows.Forms.RadioButton); $rbFile.Text = 'My own image'; $rbFile.Location = New-Object System.Drawing.Point(16, 112); $rbFile.AutoSize = $true
$txtFile = Set-Font (New-Object System.Windows.Forms.TextBox); $txtFile.Location = New-Object System.Drawing.Point(150, 110); $txtFile.Size = New-Object System.Drawing.Size(290, 26)
$btnFile = Set-Font (New-Object System.Windows.Forms.Button); $btnFile.Text = 'Browse…'; $btnFile.Location = New-Object System.Drawing.Point(446, 109); $btnFile.Size = New-Object System.Drawing.Size(80, 28)
Set-Font (Add-Label $gIcon 'SVG, PNG, JPG or ICO. Square images look best (others are padded).' 150 140 380 -Dim) | Out-Null

$chkAccent = Set-Font (New-Object System.Windows.Forms.CheckBox)
$chkAccent.Text = 'Title-bar logo follows the accent color (otherwise it shows the icon exactly as it is)'
$chkAccent.Location = New-Object System.Drawing.Point(16, 176); $chkAccent.Size = New-Object System.Drawing.Size(510, 40); $chkAccent.Checked = $true
$gIcon.Controls.AddRange(@($rbPreset, $cmbPreset, $rbColor, $txtColor, $btnPick, $swatch, $rbFile, $txtFile, $btnFile, $chkAccent))

# ---- Build
$gBuild = Add-Group 'Build' 350 104
Set-Font (Add-Label $gBuild 'Architecture' 16 32 100) | Out-Null
$cmbArch = Set-Font (New-Object System.Windows.Forms.ComboBox); $cmbArch.DropDownStyle = 'DropDownList'
$cmbArch.Location = New-Object System.Drawing.Point(120, 29); $cmbArch.Size = New-Object System.Drawing.Size(220, 26)
$native = Get-NativeArch
[void]$cmbArch.Items.Add("Auto (this PC: $native)"); [void]$cmbArch.Items.Add('x64'); [void]$cmbArch.Items.Add('ARM64'); $cmbArch.SelectedIndex = 0
Set-Font (Add-Label $gBuild 'x64 runs on Intel/AMD PCs; ARM64 on Snapdragon/ARM PCs. 32-bit (x86) is not supported.' 352 32 350 -Dim) | Out-Null
$chkClean = Set-Font (New-Object System.Windows.Forms.CheckBox); $chkClean.Text = 'Clean build (slower; use if something looks stale)'; $chkClean.Location = New-Object System.Drawing.Point(16, 66); $chkClean.AutoSize = $true
$chkLaunch = Set-Font (New-Object System.Windows.Forms.CheckBox); $chkLaunch.Text = 'Open the app when done'; $chkLaunch.Location = New-Object System.Drawing.Point(400, 66); $chkLaunch.AutoSize = $true; $chkLaunch.Checked = $true
$gBuild.Controls.AddRange(@($cmbArch, $chkClean, $chkLaunch))

# ---- Register
$gReg = Add-Group 'Install as a Windows app' 462 150
$chkRegister = Set-Font (New-Object System.Windows.Forms.CheckBox); $chkRegister.Text = 'Install and register (Start menu shortcut)'; $chkRegister.Location = New-Object System.Drawing.Point(16, 30); $chkRegister.AutoSize = $true
Set-Font (Add-Label $gReg 'Install to' 36 64 70) | Out-Null
$txtInstall = Set-Font (New-Object System.Windows.Forms.TextBox); $txtInstall.Location = New-Object System.Drawing.Point(110, 61); $txtInstall.Size = New-Object System.Drawing.Size(500, 26)
$btnInstall = Set-Font (New-Object System.Windows.Forms.Button); $btnInstall.Text = 'Browse…'; $btnInstall.Location = New-Object System.Drawing.Point(616, 60); $btnInstall.Size = New-Object System.Drawing.Size(80, 28)
$chkDesktop = Set-Font (New-Object System.Windows.Forms.CheckBox); $chkDesktop.Text = 'Desktop shortcut'; $chkDesktop.Location = New-Object System.Drawing.Point(36, 100); $chkDesktop.AutoSize = $true
$chkApps = Set-Font (New-Object System.Windows.Forms.CheckBox); $chkApps.Text = 'Show in Settings > Apps (with Uninstall)'; $chkApps.Location = New-Object System.Drawing.Point(190, 100); $chkApps.AutoSize = $true; $chkApps.Checked = $true
$chkOpenWith = Set-Font (New-Object System.Windows.Forms.CheckBox); $chkOpenWith.Text = '"Open with" for audio files'; $chkOpenWith.Location = New-Object System.Drawing.Point(480, 100); $chkOpenWith.AutoSize = $true; $chkOpenWith.Checked = $true
$gReg.Controls.AddRange(@($chkRegister, $txtInstall, $btnInstall, $chkDesktop, $chkApps, $chkOpenWith))

# ---- Installer to share
$gShare = Add-Group 'Share' 620 96
$chkInstaller = Set-Font (New-Object System.Windows.Forms.CheckBox); $chkInstaller.Text = 'Create installer (.exe to share)'; $chkInstaller.Location = New-Object System.Drawing.Point(16, 30); $chkInstaller.AutoSize = $true
Set-Font (Add-Label $gShare 'Save to' 260 32 60) | Out-Null
$txtShare = Set-Font (New-Object System.Windows.Forms.TextBox); $txtShare.Location = New-Object System.Drawing.Point(320, 29); $txtShare.Size = New-Object System.Drawing.Size(290, 26)
$txtShare.Text = [Environment]::GetFolderPath('Desktop')
$btnShare = Set-Font (New-Object System.Windows.Forms.Button); $btnShare.Text = 'Browse…'; $btnShare.Location = New-Object System.Drawing.Point(616, 28); $btnShare.Size = New-Object System.Drawing.Size(80, 28)
Set-Font (Add-Label $gShare 'One file with everything included: other PCs need nothing installed first. It isn''t signed, so Windows may warn; choose More info > Run anyway.' 36 58 660 -Dim) | Out-Null
$gShare.Controls.AddRange(@($chkInstaller, $txtShare, $btnShare))

# ---- Log + buttons
$log = New-Object System.Windows.Forms.TextBox
$log.Multiline = $true; $log.ScrollBars = 'Vertical'; $log.ReadOnly = $true; $log.WordWrap = $false
$log.Location = New-Object System.Drawing.Point(16, 726); $log.Size = New-Object System.Drawing.Size(712, 104)
$log.Font = New-Object System.Drawing.Font('Cascadia Mono, Consolas', 8.5); $log.BackColor = [System.Drawing.Color]::White
$progress = New-Object System.Windows.Forms.ProgressBar; $progress.Location = New-Object System.Drawing.Point(16, 842); $progress.Size = New-Object System.Drawing.Size(420, 10); $progress.Style = 'Marquee'; $progress.Visible = $false
$btnBuild = New-Object System.Windows.Forms.Button; $btnBuild.Text = 'Build'; $btnBuild.Location = New-Object System.Drawing.Point(536, 836); $btnBuild.Size = New-Object System.Drawing.Size(92, 34)
$btnBuild.BackColor = [System.Drawing.Color]::FromArgb(124, 77, 226); $btnBuild.ForeColor = [System.Drawing.Color]::White; $btnBuild.FlatStyle = 'Flat'; $btnBuild.FlatAppearance.BorderSize = 0
$btnBuild.Font = New-Object System.Drawing.Font('Segoe UI Semibold', 10)
$btnClose = Set-Font (New-Object System.Windows.Forms.Button); $btnClose.Text = 'Close'; $btnClose.Location = New-Object System.Drawing.Point(636, 836); $btnClose.Size = New-Object System.Drawing.Size(92, 34)
$btnUninstall = Set-Font (New-Object System.Windows.Forms.Button); $btnUninstall.Text = 'Uninstall…'; $btnUninstall.Location = New-Object System.Drawing.Point(444, 836); $btnUninstall.Size = New-Object System.Drawing.Size(84, 34)
$form.Controls.AddRange(@($log, $progress, $btnBuild, $btnClose, $btnUninstall))
$form.AcceptButton = $btnBuild; $form.CancelButton = $btnClose

function Write-Log([string]$text) { $log.AppendText($text + "`r`n") }

# ---------------------------------------------------------------- behaviour

function Get-SafeName { ($txtName.Text.Trim() -replace '[^\w\- ]', '').Trim() }
function Update-InstallDefault { if (-not $txtInstall.Tag) { $txtInstall.Text = Join-Path $env:LOCALAPPDATA ("Programs\" + $(if (Get-SafeName) { Get-SafeName } else { 'Music' })) } }
$txtInstall.Add_TextChanged({ if ($txtInstall.Focused) { $txtInstall.Tag = 'edited' } })
$txtName.Add_TextChanged({ Update-InstallDefault })
Update-InstallDefault

function Update-RegisterEnabled { foreach ($c in $txtInstall, $btnInstall, $chkDesktop, $chkApps, $chkOpenWith) { $c.Enabled = $chkRegister.Checked } }
$chkRegister.Add_CheckedChanged({ Update-RegisterEnabled }); Update-RegisterEnabled
function Update-ShareEnabled { foreach ($c in $txtShare, $btnShare) { $c.Enabled = $chkInstaller.Checked } }
$chkInstaller.Add_CheckedChanged({ Update-ShareEnabled }); Update-ShareEnabled

function Get-ColorOrNull { try { return [System.Drawing.ColorTranslator]::FromHtml($txtColor.Text.Trim()) } catch { return $null } }

$previewCache = @{}
# Draws the chosen icon into $png. Returns the path, or throws with a message.
function Resolve-IconPng([string]$png) {
    if ($rbPreset.Checked) {
        $svg = Join-Path $IconDir $Presets[$cmbPreset.SelectedItem]
        Convert-SvgToPng $svg $png
    } elseif ($rbColor.Checked) {
        $c = Get-ColorOrNull; if (-not $c) { throw 'Enter a color like #1DB954.' }
        $svg = Join-Path $Work 'recolored.svg'; New-RecoloredSvg $c $svg
        Convert-SvgToPng $svg $png
    } else {
        if (-not (Test-Path $txtFile.Text)) { throw 'Choose an image file.' }
        Convert-ImageToSquarePng $txtFile.Text $png
    }
    return $png
}

function Update-Preview {
    $key = if ($rbPreset.Checked) { "p:$($cmbPreset.SelectedItem)" } elseif ($rbColor.Checked) { "c:$($txtColor.Text)" } else { "f:$($txtFile.Text)" }
    $swatch.BackColor = $(if ($c = Get-ColorOrNull) { $c } else { [System.Drawing.Color]::Transparent })
    try {
        if (-not $previewCache.ContainsKey($key)) {
            $form.Cursor = 'WaitCursor'
            $png = Join-Path $Work ("preview-" + [guid]::NewGuid().ToString('N') + '.png')
            Resolve-IconPng $png | Out-Null
            $bytes = [IO.File]::ReadAllBytes($png)
            $previewCache[$key] = [System.Drawing.Image]::FromStream((New-Object IO.MemoryStream(, $bytes)))
        }
        $picPreview.Image = $previewCache[$key]; $lblPreview.Text = 'Preview'
    } catch { $picPreview.Image = $null; $lblPreview.Text = $_.Exception.Message }
    finally { $form.Cursor = 'Default' }
}

$rbPreset.Add_CheckedChanged({ if ($rbPreset.Checked) { Update-Preview } })
$rbColor.Add_CheckedChanged({ if ($rbColor.Checked) { Update-Preview } })
$rbFile.Add_CheckedChanged({ if ($rbFile.Checked) { $chkAccent.Checked = $false; Update-Preview } })
$cmbPreset.Add_SelectedIndexChanged({ $rbPreset.Checked = $true; Update-Preview })
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

function Get-RegKeyName { 'WinMuse-' + ((Get-SafeName) -replace ' ', '') }

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
        Write-Log "'Open with $name' added for $($AudioTypes.Count) audio types"
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
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$(Get-RegKeyName)', '$classes'
foreach (`$t in '$($AudioTypes -join "','")') { Remove-Item -Recurse -Force -ErrorAction SilentlyContinue "HKCU:\Software\Classes\`$t\OpenWithList\WinMuse.exe" }
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue `$dir
"@ | Set-Content -Encoding UTF8 $uninstall

    if ($chkApps.Checked) {
        $key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$(Get-RegKeyName)"
        New-Item -Force $key | Out-Null
        $size = [int]((Get-ChildItem $installDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
        $props = @{
            DisplayName = $name; DisplayIcon = "$exe,0"; DisplayVersion = '1.0.0'; Publisher = 'WinMuse'
            InstallLocation = $installDir; UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$uninstall`""
        }
        foreach ($k in $props.Keys) { Set-ItemProperty $key $k $props[$k] }
        Set-ItemProperty $key NoModify 1 -Type DWord; Set-ItemProperty $key NoRepair 1 -Type DWord; Set-ItemProperty $key EstimatedSize $size -Type DWord
        Write-Log "Added to Settings > Apps as '$name'"
    }
}

$btnUninstall.Add_Click({
    $u = Join-Path $txtInstall.Text 'Uninstall.ps1'
    if (-not (Test-Path $u)) { [System.Windows.Forms.MessageBox]::Show("Nothing is installed in`n$($txtInstall.Text)", 'Uninstall') | Out-Null; return }
    if ([System.Windows.Forms.MessageBox]::Show("Remove the app installed in`n$($txtInstall.Text)?`n`nYour library and settings are kept.", 'Uninstall', 'YesNo', 'Question') -ne 'Yes') { return }
    Start-Process powershell "-NoProfile -ExecutionPolicy Bypass -File `"$u`"" -WindowStyle Hidden -Wait
    Write-Log "Uninstalled from $($txtInstall.Text)"
})

# ---- Build (runs dotnet in the background; a timer follows its log so the window stays responsive)

$state = @{}
$timer = New-Object System.Windows.Forms.Timer; $timer.Interval = 300

function Set-Busy([bool]$busy) {
    foreach ($c in $gApp, $gIcon, $gBuild, $gReg, $gShare, $btnBuild, $btnUninstall) { $c.Enabled = -not $busy }
    $progress.Visible = $busy
}

$btnBuild.Add_Click({
    $log.Clear()
    $name = $txtName.Text.Trim()
    if (-not (Test-Name $name)) { [System.Windows.Forms.MessageBox]::Show('Enter a name without \ / : * ? " < > |', 'Name') | Out-Null; return }
    $arch = if ($cmbArch.SelectedIndex -eq 0) { $native } else { [string]$cmbArch.SelectedItem }
    try {
        Set-Busy $true
        Write-Log "Name: $name   Architecture: $arch"
        Write-Log 'Drawing the icon…'; [System.Windows.Forms.Application]::DoEvents()
        $png = Resolve-IconPng (Join-Path $Work 'icon.png')
        & (Join-Path $PSScriptRoot 'Make-Icons.ps1') -FromPng $png | Out-Null
        Write-Log 'Icon written (AppIcon.ico / AppIcon.png)'
        $logo = if ($chkAccent.Checked) { if ($rbPreset.Checked -and $cmbPreset.SelectedItem -like '*simple*') { 'Simple' } else { 'Rings' } } else { 'Image' }
        @{ Name = $name; Logo = $logo } | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $Assets 'Branding.json')
        Write-Log "Branding: $name, title-bar logo $logo"

        $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
        if (-not $dotnet) { throw 'The .NET SDK (dotnet) was not found. Install the .NET 10 SDK.' }
        # FFmpeg for this CPU first (fetched during the build it would miss the copy to the output on the first build).
        Write-Log 'Checking FFmpeg…'; [System.Windows.Forms.Application]::DoEvents()
        & (Join-Path $PSScriptRoot 'Get-FFmpeg.ps1') -Arch $arch.ToLower() | Out-Null
        $buildLog = Join-Path $Work 'build.log'; Set-Content $buildLog ''
        $target = if ($chkClean.Checked) { '-t:Rebuild' } else { '' }
        $quoted = '"' + ($name -replace '"', '') + '"'
        $cmd = "dotnet build `"$Project`" -c Release -p:Platform=$arch $target -p:Product=$quoted -p:AssemblyTitle=$quoted -p:Description=$quoted -nologo -v:minimal > `"$buildLog`" 2>&1"
        Write-Log 'Building (the first build takes a few minutes)…'
        $state.Process = Start-Process cmd.exe -ArgumentList '/c', $cmd -PassThru -WindowStyle Hidden
        $state.Log = $buildLog; $state.Read = 0; $state.Name = $name; $state.Arch = $arch; $state.Phase = 'build'; $state.Setup = $null
        $timer.Start()
    } catch {
        Write-Log ('Error: ' + $_.Exception.Message); Set-Busy $false
    }
})

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
            if ($state.Process.ExitCode -ne 0 -or -not (Test-Path $state.Setup)) { throw 'Making the installer failed. See the messages above.' }
            $done = $true
        } else {
            $out = Join-Path $Root "src\WinMuse\bin\$($state.Arch)\Release\net10.0-windows10.0.22621.0\win-$($state.Arch.ToLower())"
            if ($state.Process.ExitCode -ne 0 -or -not (Test-Path (Join-Path $out 'WinMuse.exe'))) { throw 'The build failed. See the messages above.' }
            Write-Log "Built: $out"
            $state.Exe = Join-Path $out 'WinMuse.exe'
            if ($chkRegister.Checked) {
                $dir = $txtInstall.Text.Trim()
                $running = Get-Process WinMuse -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dir*" }
                if ($running) {
                    if ([System.Windows.Forms.MessageBox]::Show("$($state.Name) is running from the install folder. Close it to update it?", 'Install', 'YesNo', 'Question') -ne 'Yes') { throw 'Install skipped: the app is still running.' }
                    $running | ForEach-Object { $_.CloseMainWindow() | Out-Null }; Start-Sleep 2
                    Get-Process WinMuse -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$dir*" } | Stop-Process -Force
                }
                Write-Log "Installing to $dir…"; [System.Windows.Forms.Application]::DoEvents()
                New-Item -ItemType Directory -Force $dir | Out-Null
                robocopy $out $dir /E /NFL /NDL /NJH /NJS /NP /R:2 /W:1 | Out-Null
                if ($LASTEXITCODE -ge 8) { throw "Copying to $dir failed (robocopy $LASTEXITCODE)." }
                Register-App $dir $state.Name
                $state.Exe = Join-Path $dir 'WinMuse.exe'
            }
            if ($chkInstaller.Checked) {
                # Second step, in the background like the build: publish self-contained, zip, wrap in the installer.
                # Built for the architecture chosen above; ARM64 installers say so in their name (they don't run on Intel/AMD PCs).
                $suffix = if ($state.Arch -eq 'ARM64') { ' (ARM64)' } else { '' }
                $state.Setup = Join-Path $txtShare.Text.Trim() "$($state.Name) Setup$suffix.exe"
                $setupLog = Join-Path $Work 'installer.log'; Set-Content $setupLog ''
                $script = Join-Path $PSScriptRoot 'Make-Installer.ps1'
                $cmd = "powershell -NoProfile -ExecutionPolicy Bypass -File `"$script`" -Name `"$($state.Name -replace '"', '')`" -Arch $($state.Arch) -Out `"$($state.Setup)`" > `"$setupLog`" 2>&1"
                Write-Log 'Making the installer (a few minutes)…'
                $state.Process = Start-Process cmd.exe -ArgumentList '/c', $cmd -PassThru -WindowStyle Hidden
                $state.Log = $setupLog; $state.Read = 0; $state.Phase = 'installer'
                $timer.Start()
                return
            }
            $done = $true
        }
    } catch { Write-Log ('Error: ' + $_.Exception.Message) }
    finally { if (-not $timer.Enabled) { Set-Busy $false } }
    if (-not $done) { return }   # after a failure the tool stays open so the messages can be read
    # Finished: open the app (and show the installer in File Explorer), then close the tool.
    Write-Log 'Done.'
    if ($chkLaunch.Checked) { Start-Process $state.Exe }
    if ($state.Setup) { Start-Process explorer.exe "/select,`"$($state.Setup)`"" }
    $form.Close()
})

$form.Add_Shown({ Update-Preview })
[void]$form.ShowDialog()

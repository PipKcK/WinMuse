# Makes a setup file to share that installs the app on another PC without building it:
#   exe  "<Name> Setup.exe": the app appended to a small setup program (tools\Installer).
#   msi  "<Name> Setup.msi": a Windows Installer package (tools\InstallerMsi, built with WiX Toolset 5 from NuGet),
#        which also suits msiexec, Group Policy and Intune.
#
#   powershell -ExecutionPolicy Bypass -File tools\Make-Installer.ps1 -Name "Music Player" -Arch x64 [-Format msi] [-Out "C:\Users\me\Desktop\Music Player Setup.msi"]
#
# The app is published self-contained (the .NET runtime and Windows App SDK travel with it, so the other PC needs
# nothing installed first). Both formats install it for the current user without administrator rights, with Start
# menu/desktop shortcuts, "Open with" and an entry in Settings > Apps (with Uninstall).
# Uses the icon and name already written to src\WinMuse\Assets by Build-App.ps1 (or the defaults).
param(
    [string]$Name = 'Music Player',
    [ValidateSet('x64', 'ARM64')] [string]$Arch = 'x64',
    [ValidateSet('exe', 'msi')] [string]$Format = 'exe',
    [string]$Out = '',      # empty: "<Name> Setup.<Format>" on the desktop
    [string]$Version = ''   # empty: the <Version> in src\WinMuse\WinMuse.csproj (the single source of the app's version)
)
$ErrorActionPreference = 'Stop'
$Root = Resolve-Path (Join-Path $PSScriptRoot '..')
. (Join-Path $PSScriptRoot 'Shared.ps1')
if (-not $Version) {
    $Version = Get-AppVersion $Root
    if (-not $Version) { throw 'No <Version> found in src\WinMuse\WinMuse.csproj.' }
}
if (-not $Out) { $Out = Join-Path ([Environment]::GetFolderPath('Desktop')) "$Name Setup.$Format" }
$Work = Join-Path $env:TEMP 'WinMuse-Build\installer'
$publish = Join-Path $Work "app-$Arch"
$rid = "win-$($Arch.ToLower())"
$quoted = '"' + ($Name -replace '"', '') + '"'
$icon = Join-Path $Root 'src\WinMuse\Assets\AppIcon.ico'
# Identifies this app (by name) in Settings > Apps, so a newer setup file replaces the copy an older one installed.
$regKey = Get-AppRegKey $Name
$utf8 = New-Object Text.UTF8Encoding $false

function Step([string]$text) { Write-Output $text }

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
New-Item -ItemType Directory -Force $Work | Out-Null

# FFmpeg for this CPU first: fetched during the build itself, it would arrive after the files to ship were listed.
& (Join-Path $PSScriptRoot 'Get-FFmpeg.ps1') -Arch $Arch.ToLower()

Step "Publishing a self-contained copy for $Arch (includes the .NET runtime)…"
# ContinuousIntegrationBuild: deterministic output whose debug info names source files relative to the repository,
# not by this PC's folders.
& dotnet publish (Join-Path $Root 'src\WinMuse\WinMuse.csproj') -c Release -p:Platform=$Arch -r $rid --self-contained true `
    -p:WindowsAppSDKSelfContained=true -p:ContinuousIntegrationBuild=true "-p:Product=$quoted" "-p:AssemblyTitle=$quoted" "-p:Description=$quoted" -o $publish -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Publishing the app failed.' }
if (-not (Test-Path (Join-Path $publish 'WinMuse.exe'))) { throw 'The published app is missing WinMuse.exe.' }

# The app's name comes from Assets\Branding.json; set it in the published copy (the repo's file is left alone).
$branding = Join-Path $publish 'Assets\Branding.json'
$b = if (Test-Path $branding) { Get-Content $branding -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
$b | Add-Member -Force NoteProperty Name $Name
[IO.File]::WriteAllText($branding, ($b | ConvertTo-Json), $utf8)

$size = (Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum).Sum
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
if (Test-Path $Out) { Remove-Item -Force $Out }

if ($Format -eq 'exe') {
    # What the setup program needs to know, read back from the zip.
    [IO.File]::WriteAllText((Join-Path $publish 'setup.ini'), "Name=$Name`nRegKey=$regKey`nVersion=$Version`nArch=$Arch`n", $utf8)

    Step ("Compressing {0:N0} MB…" -f ($size / 1MB))
    $zip = Join-Path $Work 'app.zip'
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    # Entries named with "/" (a standard zip; .NET Framework's CreateFromDirectory would use "\").
    $archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        $base = (Resolve-Path $publish).Path.TrimEnd('\') + '\'
        foreach ($f in Get-ChildItem $publish -Recurse -File) {
            $entry = $f.FullName.Substring($base.Length).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $f.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    } finally { $archive.Dispose() }

    Step 'Building the setup program…'
    $stubDir = Join-Path $Work 'stub'
    & dotnet build (Join-Path $PSScriptRoot 'Installer\Installer.csproj') -c Release -o $stubDir "-p:SetupIcon=$icon" "-p:SetupName=$quoted" -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Building the setup program failed.' }

    # Setup.exe + the zip + the zip's length + a marker (see tools\Installer\Payload.cs).
    Step 'Packing…'
    $outStream = [IO.File]::Create($Out)
    try {
        foreach ($part in (Join-Path $stubDir 'Setup.exe'), $zip) {
            $in = [IO.File]::OpenRead($part); try { $in.CopyTo($outStream) } finally { $in.Dispose() }
        }
        $len = [BitConverter]::GetBytes([long](Get-Item $zip).Length)
        $outStream.Write($len, 0, 8)
        $marker = [Text.Encoding]::ASCII.GetBytes('WMSETUP1')
        $outStream.Write($marker, 0, 8)
    } finally { $outStream.Dispose() }
    Remove-Item -Force $zip -ErrorAction SilentlyContinue
} else {
    # Windows Installer versions are major.minor.build, each part a number (major and minor at most 255).
    if ($Version -notmatch '^(\d{1,3})\.(\d{1,3})(\.(\d{1,5}))?' -or [int]$Matches[1] -gt 255 -or [int]$Matches[2] -gt 255 -or ($Matches[4] -and [int]$Matches[4] -gt 65535)) {
        throw "Version $Version can't be used for a Windows Installer package (it needs major.minor.build, e.g. 1.2.3)."
    }
    $msiVersion = '{0}.{1}.{2}' -f $Matches[1], $Matches[2], $(if ($Matches[4]) { $Matches[4] } else { 0 })
    $msiWork = Join-Path $Work 'msi'
    if (Test-Path $msiWork) { Remove-Item -Recurse -Force $msiWork }
    New-Item -ItemType Directory -Force $msiWork | Out-Null

    # The wizard's images: white, with the app's icon (the WiX defaults carry the WiX look).
    Add-Type -AssemblyName System.Drawing
    $png = Join-Path $Root 'src\WinMuse\Assets\AppIcon.png'
    $logo = if (Test-Path $png) { [System.Drawing.Image]::FromFile($png) } else { (New-Object System.Drawing.Icon $icon, 256, 256).ToBitmap() }
    function New-WizardImage([int]$w, [int]$h, [scriptblock]$draw, [string]$path) {
        $bmp = New-Object System.Drawing.Bitmap $w, $h
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = 'HighQualityBicubic'; $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::White)
        & $draw $g
        $g.Dispose(); $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Bmp); $bmp.Dispose()
    }
    $banner = Join-Path $msiWork 'Banner.bmp'; $dialog = Join-Path $msiWork 'Dialog.bmp'
    # Banner (493x58): icon on the right, clear of the title text on the left.
    New-WizardImage 493 58 { param($g) $g.DrawImage($logo, 437, 9, 40, 40) } $banner
    # Welcome and finish pages (493x312): a light panel on the left with the icon; the text sits to its right.
    New-WizardImage 493 312 {
        param($g)
        $panel = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(243, 243, 243))
        $g.FillRectangle($panel, 0, 0, 164, 312); $panel.Dispose()
        $g.DrawImage($logo, 34, 100, 96, 96)
    } $dialog
    $logo.Dispose()

    # Name, version and paths for Package.wxs (a preprocessor include, so any name works without escaping for MSBuild).
    $upgradeCode = Get-MsiUpgradeCode $Name
    $safeName = $Name -replace '\$', ''
    $defines = @(
        '<?xml version="1.0" encoding="utf-8"?>', '<Include>'
        "  <?define AppName = `"$safeName`" ?>"
        "  <?define AppNameFormatted = `"$($safeName -replace '[\[\]]', '[\$0]')`" ?>"
        "  <?define AppVersion = `"$msiVersion`" ?>"
        "  <?define RegKey = `"$regKey`" ?>"
        "  <?define UpgradeCode = `"$upgradeCode`" ?>"
        "  <?define AppFiles = `"$publish`" ?>"
        "  <?define IconFile = `"$icon`" ?>"
        "  <?define BannerBmp = `"$banner`" ?>"
        "  <?define DialogBmp = `"$dialog`" ?>"
        '</Include>'
    ) -join "`r`n"
    $definesFile = Join-Path $msiWork 'Defines.wxi'
    [IO.File]::WriteAllText($definesFile, $defines, $utf8)

    Step ("Building the Windows Installer package ({0:N0} MB to compress)…" -f ($size / 1MB))
    $msiOut = Join-Path $msiWork 'out'
    & dotnet build (Join-Path $PSScriptRoot 'InstallerMsi\InstallerMsi.wixproj') -c Release -p:Platform=$Arch "-p:DefinesFile=$definesFile" -o $msiOut -nologo -v:minimal
    if ($LASTEXITCODE -ne 0) { throw 'Building the Windows Installer package failed.' }
    $built = Join-Path $msiOut 'Setup.msi'
    if (-not (Test-Path $built)) { throw 'The Windows Installer package is missing from the build output.' }
    Move-Item -Force $built $Out
    Remove-Item -Recurse -Force $msiWork -ErrorAction SilentlyContinue
}
Remove-Item -Recurse -Force $publish -ErrorAction SilentlyContinue

Step ("Setup file: $Out ({0:N0} MB)" -f ((Get-Item $Out).Length / 1MB))

# Makes "<Name> Setup.exe": one file to share that installs the app on another PC without building it.
#
#   powershell -ExecutionPolicy Bypass -File tools\Make-Installer.ps1 -Name "Music" -Arch x64 -Out "C:\Users\me\Desktop\Music Setup.exe"
#
# The app is published self-contained (the .NET runtime and Windows App SDK travel with it, so the other PC needs
# nothing installed first), zipped, and appended to the installer (tools\Installer), which installs it for the current
# user with Start menu/desktop shortcuts, "Open with" and an entry in Settings > Apps (with Uninstall).
# Uses the icon and name already written to src\WinMuse\Assets by Build-App.ps1 (or the defaults).
param(
    [string]$Name = 'Music',
    [ValidateSet('x64', 'ARM64')] [string]$Arch = 'x64',
    [string]$Out = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Music Setup.exe'),
    [string]$Version = '1.0.0'
)
$ErrorActionPreference = 'Stop'
$Root = Resolve-Path (Join-Path $PSScriptRoot '..')
$Work = Join-Path $env:TEMP 'WinMuse-Build\installer'
$publish = Join-Path $Work "app-$Arch"
$zip = Join-Path $Work 'app.zip'
$rid = "win-$($Arch.ToLower())"
$quoted = '"' + ($Name -replace '"', '') + '"'

function Step([string]$text) { Write-Output $text }

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
New-Item -ItemType Directory -Force $Work | Out-Null

# FFmpeg for this CPU first: fetched during the build itself, it would arrive after the files to ship were listed.
& (Join-Path $PSScriptRoot 'Get-FFmpeg.ps1') -Arch $Arch.ToLower()

Step "Publishing a self-contained copy for $Arch (includes the .NET runtime)…"
& dotnet publish (Join-Path $Root 'src\WinMuse\WinMuse.csproj') -c Release -p:Platform=$Arch -r $rid --self-contained true `
    -p:WindowsAppSDKSelfContained=true "-p:Product=$quoted" "-p:AssemblyTitle=$quoted" "-p:Description=$quoted" -o $publish -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Publishing the app failed.' }
if (-not (Test-Path (Join-Path $publish 'WinMuse.exe'))) { throw 'The published app is missing WinMuse.exe.' }

# The app's name comes from Assets\Branding.json; set it in the published copy (the repo's file is left alone).
$branding = Join-Path $publish 'Assets\Branding.json'
$b = if (Test-Path $branding) { Get-Content $branding -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
$b | Add-Member -Force NoteProperty Name $Name
[IO.File]::WriteAllText($branding, ($b | ConvertTo-Json), (New-Object Text.UTF8Encoding $false))

# What the installer needs to know, read back from the zip.
$regKey = 'WinMuse-' + (($Name -replace '[^\w\- ]', '') -replace ' ', '')
[IO.File]::WriteAllText((Join-Path $publish 'setup.ini'), "Name=$Name`nRegKey=$regKey`nVersion=$Version`nArch=$Arch`n", (New-Object Text.UTF8Encoding $false))

$size = (Get-ChildItem $publish -Recurse -File | Measure-Object Length -Sum).Sum
Step ("Compressing {0:N0} MB…" -f ($size / 1MB))
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

Step 'Building the installer…'
$icon = Join-Path $Root 'src\WinMuse\Assets\AppIcon.ico'
$stubDir = Join-Path $Work 'stub'
& dotnet build (Join-Path $PSScriptRoot 'Installer\Installer.csproj') -c Release -o $stubDir "-p:SetupIcon=$icon" "-p:SetupName=$quoted" -nologo -v:minimal
if ($LASTEXITCODE -ne 0) { throw 'Building the installer failed.' }

# Setup.exe + the zip + the zip's length + a marker (see tools\Installer\Payload.cs).
Step 'Packing…'
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
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
Remove-Item -Recurse -Force $publish, $zip -ErrorAction SilentlyContinue

Step ("Installer: $Out ({0:N0} MB)" -f ((Get-Item $Out).Length / 1MB))

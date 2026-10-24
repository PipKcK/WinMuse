# Downloads the LGPL shared FFmpeg 8.1 build (BtbN) into external/ffmpeg (x64) or external/ffmpeg-arm64 (ARM64).
# Run once; the build also invokes it if the DLLs for the CPU it builds for are missing.
param(
    [ValidateSet('x64', 'arm64')] [string]$Arch = 'x64',
    [string]$Dest = (Join-Path $PSScriptRoot $(if ($Arch -eq 'arm64') { '..\external\ffmpeg-arm64' } else { '..\external\ffmpeg' }))
)
$ErrorActionPreference = 'Stop'
if (Test-Path (Join-Path $Dest "avcodec-62.dll")) { Write-Host "FFmpeg already present."; return }
$build = if ($Arch -eq 'arm64') { 'winarm64' } else { 'win64' }
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-$build-lgpl-shared-8.1.zip"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ffmpeg-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Force $tmp | Out-Null
$zip = Join-Path $tmp "ffmpeg.zip"
Write-Host "Downloading FFmpeg 8.1 for $Arch (LGPL, shared)..."
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
Expand-Archive $zip -DestinationPath $tmp
New-Item -ItemType Directory -Force $Dest | Out-Null
Get-ChildItem $tmp -Recurse -Filter *.dll | Copy-Item -Destination $Dest -Force
Get-ChildItem $tmp -Recurse -Filter LICENSE* | Select-Object -First 1 | Copy-Item -Destination (Join-Path $Dest "LICENSE.txt") -Force
Remove-Item $tmp -Recurse -Force
Write-Host "FFmpeg installed to $Dest"

# Downloads the LGPL shared FFmpeg 8.1 build (BtbN) into external/ffmpeg. Run once; the build also invokes it if DLLs are missing.
param([string]$Dest = (Join-Path $PSScriptRoot "..\external\ffmpeg"))
$ErrorActionPreference = 'Stop'
if (Test-Path (Join-Path $Dest "avcodec-62.dll")) { Write-Host "FFmpeg already present."; return }
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip"
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("ffmpeg-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Force $tmp | Out-Null
$zip = Join-Path $tmp "ffmpeg.zip"
Write-Host "Downloading FFmpeg 8.1 (LGPL, shared)..."
$ProgressPreference = 'SilentlyContinue'
Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
Expand-Archive $zip -DestinationPath $tmp
New-Item -ItemType Directory -Force $Dest | Out-Null
Get-ChildItem $tmp -Recurse -Filter *.dll | Copy-Item -Destination $Dest -Force
Get-ChildItem $tmp -Recurse -Filter LICENSE* | Select-Object -First 1 | Copy-Item -Destination (Join-Path $Dest "LICENSE.txt") -Force
Remove-Item $tmp -Recurse -Force
Write-Host "FFmpeg installed to $Dest"

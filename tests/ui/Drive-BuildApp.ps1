# Drives tools\Build-App.ps1 without mouse or keyboard: fields are set with WM_SETTEXT and buttons pressed with
# BM_CLICK on their window handles, so it works while the PC is in use.
#   .\Drive-BuildApp.ps1 -Name "Music Test" -Color "#1DB954" -InstallDir C:\Temp\MusicTest [-Register] [-InstallerDir C:\Temp\Dist [-SetupFormat msi]]
#   -KeepIcon builds with the current icon instead of -Color (src\WinMuse\Assets is left as it is when the name is
#   also unchanged).
param(
    [string]$Name = 'Music Test',
    [string]$Color = '#1DB954',
    [switch]$KeepIcon,
    [string]$InstallDir = (Join-Path $env:TEMP 'WinMuseBuildTest'),
    [switch]$Register,
    [string]$InstallerDir = '',      # set: also tick "Create setup file" and save it here
    [ValidateSet('exe', 'msi')] [string]$SetupFormat = 'exe',
    [int]$TimeoutSec = 1200
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class W {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, string l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, StringBuilder l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static string GetText(IntPtr h) {
    int n = (int)SendMessage(h, 0x000E, IntPtr.Zero, IntPtr.Zero);   // WM_GETTEXTLENGTH
    var sb = new StringBuilder(n + 1); SendMessage(h, 0x000D, (IntPtr)(n + 1), sb); return sb.ToString();   // WM_GETTEXT
  }
}
"@
$tool = Join-Path $PSScriptRoot '..\..\tools\Build-App.ps1'
# Known starting choices (the tool opens with the last build's), kept apart from the developer's own last build.
$env:WINMUSE_BUILD_RECORD = Join-Path $env:TEMP 'WinMuseBuildTest-last-build.json'
@{ Name = $Name; Arch = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' }); Launch = $true; Register = $false; InstallDir = ''
   Desktop = $false; Apps = $true; OpenWith = $true; Installer = $false; SetupFormat = $SetupFormat; ShareDir = '' } | ConvertTo-Json | Set-Content -Encoding UTF8 $env:WINMUSE_BUILD_RECORD
$p = Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$tool`"" -PassThru
$deadline = (Get-Date).AddSeconds(40)
do { Start-Sleep 1; $p.Refresh() } until ($p.MainWindowTitle -eq 'Build WinMuse' -or (Get-Date) -gt $deadline)
if ($p.MainWindowTitle -ne 'Build WinMuse') { throw 'Build-App window did not open' }
Start-Sleep 4  # first preview render
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
function All { $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) }
# The smallest control with that name: the "Build" button, not the "Build" group box around the build options.
function Handle([string]$name) {
    $m = @(All | Where-Object { $_.Current.Name -eq $name -and $_.Current.NativeWindowHandle } | Sort-Object { $_.Current.BoundingRectangle.Height })
    if ($m.Count -eq 0) { throw "control '$name' not found" }
    return [IntPtr]$m[0].Current.NativeWindowHandle
}
function Click([string]$name) { [W]::PostMessage((Handle $name), 0xF5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; Start-Sleep -Milliseconds 400 }
function SetText([IntPtr]$h, [string]$text) { [W]::SendMessage($h, 0x000C, [IntPtr]::Zero, $text) | Out-Null }
# Text boxes top to bottom: Name, Color, File, Install dir, Share folder, then the log (WinForms edits: "WindowsForms10.EDIT…").
$edits = @(All | Where-Object { $_.Current.ClassName -like 'WindowsForms10.EDIT*' -and -not $_.Current.IsOffscreen } | Sort-Object { $_.Current.BoundingRectangle.Y }, { $_.Current.BoundingRectangle.X })
$nameBox = [IntPtr]$edits[0].Current.NativeWindowHandle
$colorBox = [IntPtr]$edits[1].Current.NativeWindowHandle
$installBox = [IntPtr]$edits[3].Current.NativeWindowHandle
$shareBox = [IntPtr]$edits[4].Current.NativeWindowHandle
$logBox = [IntPtr]$edits[-1].Current.NativeWindowHandle
SetText $nameBox $Name
if (-not $KeepIcon) { SetText $colorBox $Color; Click 'Custom color' }
if ($Register) {
    Click 'Install for current user'
    SetText $installBox $InstallDir
    Click 'Desktop shortcut'
}
if ($InstallerDir) {
    Click 'Create setup file'
    New-Item -ItemType Directory -Force $InstallerDir | Out-Null
    SetText $shareBox $InstallerDir
}
Click 'Open app when finished'
Start-Sleep 3   # recolored preview
Click 'Build'
$start = Get-Date
# Done when the tool closes itself (success), or when its log shows an error (it stays open after a failure).
$text = ''
do {
    Start-Sleep 3
    if (-not $p.HasExited) { $text = [W]::GetText($logBox) }
} until ($p.HasExited -or $text -match 'Error:' -or ((Get-Date) - $start).TotalSeconds -gt $TimeoutSec)
$elapsed = ((Get-Date) - $start).TotalSeconds
$closedItself = $p.HasExited
if (-not $closedItself) { [W]::PostMessage($p.MainWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
[pscustomobject]@{
    Completed = $closedItself; Seconds = [int]$elapsed; ClosedItself = $closedItself
    LogTail = (($text -split "`r?`n" | Where-Object { $_ }) | Select-Object -Last 6) -join ' | '
}

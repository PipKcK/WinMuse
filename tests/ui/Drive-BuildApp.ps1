# Drives tools\Build-App.ps1 without mouse or keyboard: fields are set with WM_SETTEXT and buttons pressed with
# BM_CLICK on their window handles, so it works while the PC is in use.
#   .\Drive-BuildApp.ps1 -Name "Music Test" -Color "#1DB954" -InstallDir C:\Temp\MusicTest [-Register]
param(
    [string]$Name = 'Music Test',
    [string]$Color = '#1DB954',
    [string]$InstallDir = (Join-Path $env:TEMP 'WinMuseBuildTest'),
    [switch]$Register,
    [int]$TimeoutSec = 900
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class W {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, string l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
}
"@
$tool = Join-Path $PSScriptRoot '..\..\tools\Build-App.ps1'
$log = Join-Path $env:TEMP 'WinMuse-Build\build.log'
$p = Start-Process powershell -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$tool`"" -PassThru
$deadline = (Get-Date).AddSeconds(40)
do { Start-Sleep 1; $p.Refresh() } until ($p.MainWindowTitle -eq 'Build Music' -or (Get-Date) -gt $deadline)
if ($p.MainWindowTitle -ne 'Build Music') { throw 'Build-App window did not open' }
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
# Text boxes in tab order: Name, Color, File, Install dir (WinForms edits have the class name "WindowsForms10.EDIT…").
$edits = @(All | Where-Object { $_.Current.ClassName -like 'WindowsForms10.EDIT*' -and -not $_.Current.IsOffscreen } | Sort-Object { $_.Current.BoundingRectangle.Y }, { $_.Current.BoundingRectangle.X })
$nameBox = [IntPtr]$edits[0].Current.NativeWindowHandle
$colorBox = [IntPtr]$edits[1].Current.NativeWindowHandle
$installBox = [IntPtr]$edits[3].Current.NativeWindowHandle
SetText $nameBox $Name
SetText $colorBox $Color; Click 'Any color'
if ($Register) {
    Click 'Install and register (Start menu shortcut)'
    SetText $installBox $InstallDir
    Click 'Desktop shortcut'
}
Click 'Open the app when done'
Start-Sleep 3   # recolored preview
Click 'Build'
$start = Get-Date
# Done when the "Build complete" box appears (or an error lands in the log).
do {
    Start-Sleep 3
    # The box is owned by the tool's window, so look for it among the window's children.
    $box = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Build complete')))
} until ($box -or ((Get-Date) - $start).TotalSeconds -gt $TimeoutSec)
$elapsed = ((Get-Date) - $start).TotalSeconds
if ($box) {
    $ok = $box.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'OK')))
    [W]::PostMessage([IntPtr]$ok.Current.NativeWindowHandle, 0xF5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
}
# After a successful build the tool closes itself once the box is dismissed.
$closedItself = $p.WaitForExit(10000)
if (-not $closedItself) { [W]::PostMessage($p.MainWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
[pscustomobject]@{ Completed = [bool]$box; Seconds = [int]$elapsed; ClosedItself = $closedItself; BuildLogTail = (Get-Content $log -Tail 3 -ErrorAction SilentlyContinue) -join ' | ' }

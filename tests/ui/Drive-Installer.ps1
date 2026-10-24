# Drives a "<Name> Setup.exe" made by tools\Make-Installer.ps1 through its window (no mouse or keyboard: WM_SETTEXT and
# BM_CLICK on the controls), installs into -InstallDir and takes screenshots of each step.
#   .\Drive-Installer.ps1 -Setup "...\Music Test Setup.exe" -InstallDir C:\Temp\MusicTest -Shots C:\Temp\shots
param(
    [Parameter(Mandatory)] [string]$Setup,
    [Parameter(Mandatory)] [string]$InstallDir,
    [string]$Shots = (Join-Path $env:LOCALAPPDATA 'WinMuseTests\Results\installer'),
    [switch]$NoOpenWith,
    [int]$TimeoutSec = 300
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class W3 {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, string l);
  [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
}
"@
New-Item -ItemType Directory -Force $Shots | Out-Null
$uia = Join-Path $PSScriptRoot 'Uia-Action.ps1'
$p = Start-Process $Setup -PassThru
$deadline = (Get-Date).AddSeconds(30)
do { Start-Sleep 1; $p.Refresh() } until ($p.MainWindowHandle -ne 0 -or (Get-Date) -gt $deadline)
if ($p.MainWindowHandle -eq 0) { throw 'The installer window did not open.' }
Start-Sleep 1
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
function Find([string]$name) {
    $e = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)))
    if (-not $e) { throw "control '$name' not found" }
    [IntPtr]$e.Current.NativeWindowHandle
}
function Click([string]$name) { [W3]::PostMessage((Find $name), 0xF5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; Start-Sleep -Milliseconds 400 }
function Shot([string]$n) { & powershell -NoProfile -ExecutionPolicy Bypass -File $uia -ProcId $p.Id -Action shot -Arg (Join-Path $Shots $n) | Out-Null }

Shot '1-start.png'
$edit = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.ClassName -like 'WindowsForms10.EDIT*' })[0]
[W3]::SendMessage([IntPtr]$edit.Current.NativeWindowHandle, 0x000C, [IntPtr]::Zero, $InstallDir) | Out-Null
if ($NoOpenWith) { Click (@($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like 'Add "Open with*' })[0].Current.Name) }
Click 'Install'
$start = Get-Date
Start-Sleep 2; Shot '2-installing.png'
# Done when the button turns into "Finish" (or the status shows a failure).
do {
    Start-Sleep 2
    $finish = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Finish')))
    $failed = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, 'Close')))
} until ($finish -or $failed -or ((Get-Date) - $start).TotalSeconds -gt $TimeoutSec)
$seconds = [int]((Get-Date) - $start).TotalSeconds
Shot '3-done.png'
if ($finish) {
    Click ((@($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, [System.Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -like 'Open * now' })[0]).Current.Name)   # untick "Open now"
    Click 'Finish'
} else { [W3]::PostMessage($p.MainWindowHandle, 0x10, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null }
[pscustomobject]@{ Installed = [bool]$finish; Seconds = $seconds; Closed = $p.WaitForExit(10000) }

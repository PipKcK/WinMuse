# One UI action on a running test copy, through UI Automation patterns only (no mouse or keyboard), in its own short
# process (a long-lived UIA client sometimes stalls against this app).
#   .\Uia-Action.ps1 -ProcId 1234 -Action nav -Arg Songs
# Actions: nav <page>, invoke <name>, invoke-id <AutomationId>, text-id <AutomationId> <text>, search <text>,
#          open-item <n> (the n-th grid/list item in the page, 0-based), size <w> <h>, shot <png path>, minimize, restore
param([int]$ProcId, [string]$Action, [string]$Arg, [string]$Arg2)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -ReferencedAssemblies System.Drawing @"
using System; using System.Drawing; using System.Drawing.Imaging; using System.Runtime.InteropServices;
namespace W { public static class U {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint f);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
  struct RECT { public int L, T, R, B; }
  // Captures the window's own pixels (PW_RENDERFULLCONTENT), so it works while other windows cover it.
  public static void Shot(IntPtr h, string path) {
    RECT w, f; GetWindowRect(h, out w);
    if (DwmGetWindowAttribute(h, 9, out f, 16) != 0) f = w;   // DWMWA_EXTENDED_FRAME_BOUNDS: without the invisible resize border
    using (var bmp = new Bitmap(w.R - w.L, w.B - w.T)) {
      using (var g = Graphics.FromImage(bmp)) { var dc = g.GetHdc(); PrintWindow(h, dc, 2); g.ReleaseHdc(dc); }
      using (var crop = bmp.Clone(new Rectangle(f.L - w.L, f.T - w.T, f.R - f.L, f.B - f.T), bmp.PixelFormat)) crop.Save(path, ImageFormat.Png);
    }
  }
} }
"@
$p = Get-Process -Id $ProcId -ErrorAction Stop
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$A = [System.Windows.Automation.AutomationElement]
function ByName([string]$n, [string]$type) {
    foreach ($e in $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $n)))) {
        if (-not $type -or $e.Current.ControlType.ProgrammaticName -eq $type) { return $e } }
}
function ById([string]$id) { $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id))) }
function Press($e) {
    if (-not $e) { throw "not found: $Arg" }
    $pat = $null
    if ($e.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pat)) { $pat.Invoke() }
    elseif ($e.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$pat)) { $pat.Select() }
    elseif ($e.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pat)) { $pat.Toggle() }
    else { throw "no pattern: $Arg" }
}
switch ($Action) {
    'nav'       { Press (ByName $Arg 'ControlType.ListItem') }
    'invoke'    { Press (ByName $Arg 'ControlType.Button') }
    'invoke-id' { Press (ById $Arg) }
    'text-id'   {
        # The element itself, or the text field inside it (AutoSuggestBox).
        $e = ById $Arg; $pat = $null
        if (-not $e.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pat)) {
            $edit = $e.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
            $pat = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        }
        $pat.SetValue($Arg2)
    }
    'search'    {
        # Type the query, then press the search box's own search button (submits like Enter).
        $box = ById 'SearchBox'
        $edit = $box.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
        $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Arg)
        Start-Sleep -Milliseconds 300
        $q = $box.FindFirst([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, 'QueryButton')))
        if (-not $q) { throw 'search button not found' }
        $q.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    'open-item' {
        $items = @($root.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ClassNameProperty, 'GridViewItem'))))
        if ($items.Count -le [int]$Arg) { throw "only $($items.Count) items" }
        Press $items[[int]$Arg]
    }
    'size'      { [W.U]::ShowWindow($p.MainWindowHandle, 9) | Out-Null; [W.U]::SetWindowPos($p.MainWindowHandle, [IntPtr]::Zero, 40, 40, [int]$Arg, [int]$Arg2, 0x0014) | Out-Null }   # SWP_NOZORDER|SWP_NOACTIVATE
    'shot'      { [W.U]::Shot($p.MainWindowHandle, $Arg) }
    'minimize'  { [W.U]::ShowWindow($p.MainWindowHandle, 6) | Out-Null }   # SW_MINIMIZE
    'restore'   { [W.U]::ShowWindow($p.MainWindowHandle, 4) | Out-Null }   # SW_SHOWNOACTIVATE: doesn't take focus
}
"ok"

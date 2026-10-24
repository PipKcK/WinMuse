# One UI action on a running test copy, through UI Automation patterns only (no mouse or keyboard), in its own short
# process (a long-lived UIA client sometimes stalls against this app).
#   .\Uia-Action.ps1 -ProcId 1234 -Action nav -Arg Songs
# Actions: nav <page>, invoke <name>, invoke-id <AutomationId>, text-id <AutomationId> <text>, minimize, restore
param([int]$ProcId, [string]$Action, [string]$Arg, [string]$Arg2)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type -Name U -Namespace W -MemberDefinition '[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);'
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
    'minimize'  { [W.U]::ShowWindow($p.MainWindowHandle, 6) | Out-Null }   # SW_MINIMIZE
    'restore'   { [W.U]::ShowWindow($p.MainWindowHandle, 4) | Out-Null }   # SW_SHOWNOACTIVATE: doesn't take focus
}
"ok"

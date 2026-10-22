<#
.SYNOPSIS
    Lists the providers the live widget picker's provider list shows.

.DESCRIPTION
    The picker is a window of its own, with a ProviderList and, for the
    provider that is selected, a detail pane with an "Add widget" button.
    This lists the list's items and their text, and finds the add button
    for the System Monitor provider.
#>
[CmdletBinding()]
param(
    [string] $CardName = "System Monitor"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase

$root = [System.Windows.Automation.AutomationElement]::RootElement

# The picker is a window of its own. It may be a top-level window or a
# child of the board, so the whole desktop is searched for it.
$picker = $null
foreach ($e in $root.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($e.Current.Name -eq "Add widget" -and
        $e.Current.ControlType.ProgrammaticName -eq "ControlType.Window") {
        $picker = $e
        break
    }
}
if (-not $picker) { throw "The 'Add widget' picker window is not open." }

Write-Host "Picker window pid: $($picker.Current.ProcessId)"

# The provider list.
$list = $null
foreach ($e in $picker.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($e.Current.AutomationId -eq "ProviderList") { $list = $e; break }
}
if (-not $list) { throw "The provider list was not found." }

Write-Host "--- provider list items ---"
foreach ($e in $list.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    $c = $e.Current
    $r = $c.BoundingRectangle
    $rect = if ($r.IsEmpty) { "<empty>" } else { "$([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height)" }
    "id='$($c.AutomationId)' name='$($c.Name)' type=$($c.ControlType.ProgrammaticName.Replace('ControlType.','')) rect=$rect"
}

Write-Host ""
Write-Host "--- every button in the picker ---"
foreach ($e in $picker.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    $c = $e.Current
    if ($c.ControlType.ProgrammaticName -ne "ControlType.Button") { continue }
    $r = $c.BoundingRectangle
    $rect = if ($r.IsEmpty) { "<empty>" } else { "$([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height)" }
    "id='$($c.AutomationId)' name='$($c.Name)' rect=$rect"
}

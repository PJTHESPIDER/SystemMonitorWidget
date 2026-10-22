<#
.SYNOPSIS
    Opens the card's "More options" menu and lists what the host offers.

.DESCRIPTION
    The menu is a popup window of its own. This opens it and lists the
    items, so the check knows what the host will let the user do with
    the card (resize, remove, and so on).
#>
[CmdletBinding()]
param(
    [string] $CardName = "System Monitor"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase

$root = [System.Windows.Automation.AutomationElement]::RootElement

$p = Get-Process -Name "WidgetBoard" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { throw "The Widgets board process is not running." }
$pid_ = $p.Id

$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $pid_)
$board = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $board) { throw "The board window was not found." }

# The "More options" button belongs to the card.
$more = $null
foreach ($e in $board.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($e.Current.Name -ne "More options") { continue }
    $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($e)
    if ($parent -and $parent.Current.Name -eq $CardName) { $more = $e; break }
}
if (-not $more) { throw "The card's More options button was not found." }

$more.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Start-Sleep -Seconds 2

Write-Host "--- menu items ---"
foreach ($e in $root.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    $c = $e.Current
    if ($c.ProcessId -ne $pid_) { continue }
    if ($c.ControlType.ProgrammaticName -ne "ControlType.MenuItem") { continue }
    $r = $c.BoundingRectangle
    $rect = if ($r.IsEmpty) { "<empty>" } else { "$([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height)" }
    "id='$($c.AutomationId)' name='$($c.Name)' rect=$rect"
}

Write-Host "--- every element of the menu window ---"
foreach ($e in $root.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    $c = $e.Current
    if ($c.ProcessId -ne $pid_) { continue }
    if ($c.ControlType.ProgrammaticName -notmatch "Menu|Button|Text") { continue }
    $r = $c.BoundingRectangle
    if ($r.IsEmpty) { continue }
    $rect = "$([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height)"
    if ($c.ControlType.ProgrammaticName -match "Text") { continue }
    "id='$($c.AutomationId)' name='$($c.Name)' type=$($c.ControlType.ProgrammaticName.Replace('ControlType.','')) rect=$rect"
}

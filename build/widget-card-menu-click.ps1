<#
.SYNOPSIS
    Clicks an item in the card's "More options" menu by name.

.DESCRIPTION
    The menu items do not expose an Invoke pattern, so the item is
    clicked with synthetic mouse input at the point UI Automation
    reports for it. That is the same point the user's own click would
    land on.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ItemName,

    [string] $CardName = "System Monitor"
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase

if (-not ("BoardMouse" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public class BoardMouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, int e);
}
"@
}

function Invoke-MouseClick([int]$x, [int]$y) {
    [BoardMouse]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 400
    [BoardMouse]::mouse_event(0x0002, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 60
    [BoardMouse]::mouse_event(0x0004, 0, 0, 0, 0)
}

$root = [System.Windows.Automation.AutomationElement]::RootElement

$p = Get-Process -Name "WidgetBoard" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $p) { throw "The Widgets board process is not running." }
$pid_ = $p.Id

$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $pid_)
$board = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $board) { throw "The board window was not found." }

# --- open the menu ----------------------------------------------------------

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

# --- find the item ----------------------------------------------------------

$item = $null
for ($wait = 1; $wait -le 10 -and -not $item; $wait++) {
    Start-Sleep -Milliseconds 500
    foreach ($e in $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.ProcessId -ne $pid_) { continue }
        if ($e.Current.ControlType.ProgrammaticName -ne "ControlType.MenuItem") { continue }
        if ($e.Current.Name -eq $ItemName) { $item = $e; break }
    }
}
if (-not $item) { throw "The menu item '$ItemName' was not found." }

$r = $item.Current.BoundingRectangle
Write-Host "Clicking '$ItemName' at $([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height)"
Invoke-MouseClick ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
Start-Sleep -Seconds 3
Write-Host "Clicked."

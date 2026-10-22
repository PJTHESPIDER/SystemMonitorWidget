<#
.SYNOPSIS
    Removes the card from the live Widgets board.

.DESCRIPTION
    The "Remove" item in the card's menu is a popup item that the host
    handles itself. The click is tried at the item's own text position
    and, if the menu is still open afterwards, the click is repeated:
    the first click on a popup that does not yet own the input focus
    only takes the focus.
#>
[CmdletBinding()]
param(
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
    Start-Sleep -Milliseconds 120
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

function Get-MenuItem([string]$name) {
    foreach ($e in $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.ProcessId -ne $pid_) { continue }
        if ($e.Current.ControlType.ProgrammaticName -ne "ControlType.MenuItem") { continue }
        if ($e.Current.Name -eq $name) { return $e }
    }
    return $null
}

for ($attempt = 1; $attempt -le 5; $attempt++) {
    # --- open the menu ---------------------------------------------------
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

    $item = Get-MenuItem "Remove"
    if (-not $item) { throw "The 'Remove' menu item was not found." }

    $r = $item.Current.BoundingRectangle
    # The click lands on the item's own text, not on the empty part of the
    # item's rectangle.
    $x = [int]($r.X + 40)
    $y = [int]($r.Y + $r.Height / 2)
    Write-Host "Attempt $attempt : clicking 'Remove' at $x,$y"

    Invoke-MouseClick $x $y
    Start-Sleep -Seconds 2

    if (-not (Get-MenuItem "Remove")) {
        Write-Host "The menu closed."
        break
    }
    Write-Host "The menu is still open; trying again."
}

Start-Sleep -Seconds 4

# --- confirm the card is gone ----------------------------------------------

$stillThere = $false
foreach ($e in $board.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($e.Current.AutomationId -eq "RootGrid" -and $e.Current.Name -eq $CardName) {
        $stillThere = $true
        break
    }
}

if ($stillThere) { Write-Warning "The card is still on the board." }
else { Write-Host "The card has been removed from the board." }

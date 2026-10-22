<#
.SYNOPSIS
    Adds the System Monitor card to the live Widgets board.

.DESCRIPTION
    The package reinstall removes the card from the board, so the check
    has to put it back. The host's own "Add widgets" button opens the
    picker, the picker lists the registered providers, and each provider
    has an "Add widget" button.

    The picker is a window of its own, in a process of its own, so it is
    found by searching the whole desktop for its title rather than by
    looking inside the board window.
#>
[CmdletBinding()]
param(
    [string] $CardName = "System Monitor",
    [int] $BoardPid = 0
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
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
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

if ($BoardPid -le 0) {
    $p = Get-Process -Name "WidgetBoard" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { throw "The Widgets board process is not running." }
    $BoardPid = $p.Id
}

$root = [System.Windows.Automation.AutomationElement]::RootElement

function Find-ById([string]$id, [int]$pid_) {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $pid_)
    foreach ($e in $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants, $cond)) {
        if ($e.Current.AutomationId -eq $id) { return $e }
    }
    return $null
}

function Find-ByNameAndType([string]$name, [string]$type) {
    foreach ($e in $root.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.Name -eq $name -and
            $e.Current.ControlType.ProgrammaticName -eq "ControlType.$type") {
            return $e
        }
    }
    return $null
}

# --- open the picker --------------------------------------------------------

$pickerButton = Find-ById "WidgetPickerButton" $BoardPid
if (-not $pickerButton) { throw "The 'Add widgets' button was not found on the board." }

$r = $pickerButton.Current.BoundingRectangle
Write-Host "Opening the picker from $($r.X),$($r.Y)"
Invoke-MouseClick ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
Start-Sleep -Seconds 3

# --- pick the provider ------------------------------------------------------

$listItem = $null
for ($wait = 1; $wait -le 20 -and -not $listItem; $wait++) {
    Start-Sleep -Milliseconds 500
    $listItem = Find-ByNameAndType $CardName "ListItem"
}
if (-not $listItem) { throw "The '$CardName' provider was not listed in the picker." }

$r = $listItem.Current.BoundingRectangle
Write-Host "Selecting '$CardName' at $($r.X),$($r.Y) $($r.Width)x$($r.Height)"
Invoke-MouseClick ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
Start-Sleep -Seconds 3

# --- add the widget ---------------------------------------------------------

$addButton = $null
for ($wait = 1; $wait -le 20 -and -not $addButton; $wait++) {
    Start-Sleep -Milliseconds 500
    $addButton = Find-ByNameAndType "Add widget" "Button"
}
if (-not $addButton) { throw "The 'Add widget' button was not found." }

$r = $addButton.Current.BoundingRectangle
Write-Host "Clicking 'Add widget' at $($r.X),$($r.Y) $($r.Width)x$($r.Height)"
Invoke-MouseClick ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
Start-Sleep -Seconds 6

# --- confirm ----------------------------------------------------------------

$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $BoardPid)
$board = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if (-not $board) { throw "The board window was not found." }

$found = $false
foreach ($e in $board.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($e.Current.AutomationId -eq "RootGrid" -and $e.Current.Name -eq $CardName) {
        Write-Host "The card is on the board: $($e.Current.BoundingRectangle)"
        $found = $true
        break
    }
}

if (-not $found) { Write-Warning "The card was not found on the board after adding it." }

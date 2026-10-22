<#
.SYNOPSIS
    Dumps the UI Automation tree of the desktop, for the widgets host.

.DESCRIPTION
    A read-only helper for the live-host check. The board's picker and
    the provider list live in windows of their own, so the tree of the
    board window alone does not show them. This walks the whole desktop
    and prints the elements that belong to the widgets processes.
#>
[CmdletBinding()]
param(
    [int] $MaxDepth = 6
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase

$root = [System.Windows.Automation.AutomationElement]::RootElement

function Show-Tree($element, [int]$depth, [int]$maxDepth) {
    if ($depth -gt $maxDepth) { return }

    $walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
    $child = $walker.GetFirstChild($element)

    while ($child) {
        $c = $child.Current
        $r = $c.BoundingRectangle
        $rect = if ($r.IsEmpty) { "<empty>" } else { "$([int]$r.X),$([int]$r.Y) $([int]$r.Width)x$([int]$r.Height)" }
        $off = $c.IsOffscreen

        "{0}[{1}] id='{2}' name='{3}' type={4} pid={5} off={6} rect={7}" -f `
            ("  " * $depth), $depth, $c.AutomationId, $c.Name,
            $c.ControlType.ProgrammaticName.Replace("ControlType.", ""),
            $c.ProcessId, $off, $rect

        Show-Tree $child ($depth + 1) $maxDepth
        $child = $walker.GetNextSibling($child)
    }
}

# The desktop's own children are the top-level windows.
$walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker
$window = $walker.GetFirstChild($root)
while ($window) {
    $c = $window.Current
    $procName = try { (Get-Process -Id $c.ProcessId -ErrorAction SilentlyContinue).ProcessName } catch { "?" }

    if ($procName -match "Widget|WindowsWidgets|WidgetsExperienceHost|WidgetBoard" -or
        $c.Name -match "Widget") {
        "WINDOW name='$($c.Name)' pid=$($c.ProcessId) proc=$procName"
        Show-Tree $window 1 $MaxDepth
    }

    $window = $walker.GetNextSibling($window)
}

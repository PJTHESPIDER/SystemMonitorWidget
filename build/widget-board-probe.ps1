<#
.SYNOPSIS
    Drives the live Windows Widgets board for the System Monitor card:
    reads the rendered text of the card and resizes it between the
    Small, Medium and Large sizes the host offers.

.DESCRIPTION
    This is a validation helper for the live-host check. It is
    read-only with respect to the widget provider: it opens the card's
    own "More options" menu, picks a size, and reads back what the host
    rendered. It does not install, remove or re-register anything.

    The rendered text is read through UI Automation, which is the same
    tree the host exposes to assistive technology. That is what lets the
    check distinguish "the template says X" from "the host rendered X".

.EXAMPLE
    ./build/widget-board-probe.ps1 -Size Large
    ./build/widget-board-probe.ps1 -ReadOnly
#>
[CmdletBinding()]
param(
    # The size to request from the host's own menu.
    [ValidateSet("Small", "Medium", "Large")]
    [string] $Size,

    # Read the card without changing anything.
    [switch] $ReadOnly,

    # Board process id. Defaults to the running WidgetBoard process.
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
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
}

function Get-BoardProcess {
    if ($BoardPid -gt 0) { return $BoardPid }
    $p = Get-Process -Name "WidgetBoard" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) {
        # The host is not running. Start it the way the shell does.
        Start-Process "ms-widgetboard:" -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 4
        $p = Get-Process -Name "WidgetBoard" -ErrorAction SilentlyContinue | Select-Object -First 1
    }
    if (-not $p) { throw "The Widgets board process is not running." }
    return $p.Id
}

function Get-BoardRoot([int]$pid_) {
    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $pid_)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
}

function Get-Dashboard($board) {
    $all = $board.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($e in $all) {
        if ($e.Current.AutomationId -eq "RootGrid" -and
            $e.Current.Name -eq "System Monitor") { return $e }
    }
    return $null
}

function Get-CardText($board) {
    $all = $board.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    $texts = New-Object System.Collections.Generic.List[string]
    foreach ($e in $all) {
        $c = $e.Current
        if ($c.ControlType.ProgrammaticName -ne "ControlType.Text") { continue }
        if ([string]::IsNullOrWhiteSpace($c.Name)) { continue }
        if ($c.Name -eq "System Monitor") { continue }
        if ($c.Name -eq "Widgets") { continue }
        $texts.Add($c.Name)
    }
    return $texts
}

function Invoke-MouseClick([int]$x, [int]$y) {
    [BoardMouse]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 400
    [BoardMouse]::mouse_event(0x0002, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 60
    [BoardMouse]::mouse_event(0x0004, 0, 0, 0, 0)
}

$pid_ = Get-BoardProcess
$board = Get-BoardRoot $pid_
if (-not $board) { throw "The Widgets board window was not found." }

$proc = Get-Process -Id $pid_
[BoardMouse]::SetForegroundWindow($proc.MainWindowHandle) | Out-Null
Start-Sleep -Milliseconds 300

Write-Host "Board process: $pid_  window: $($proc.MainWindowTitle)"

if (-not $ReadOnly -and $Size) {
    # The host's context menu is a separate popup window, so it is found
    # by searching the whole desktop and keeping only the elements the
    # board owns. Searching by name alone would match unrelated windows.
    $root = [System.Windows.Automation.AutomationElement]::RootElement

    # The host sometimes drops the first synthetic click, so the size is
    # requested and then verified against the card's own rectangle. The
    # rectangle is the ground truth: it is what the host actually laid
    # out, not what the menu said.
    $expected = switch ($Size) {
        "Small"  { 148 }
        "Medium" { 306 }
        "Large"  { 464 }
    }

    $applied = $false
    for ($attempt = 1; $attempt -le 4 -and -not $applied; $attempt++) {
        $card = Get-Dashboard (Get-BoardRoot $pid_)
        if (-not $card) { throw "The System Monitor card is not on the board. Add it from Add Widgets." }

        $all = (Get-BoardRoot $pid_).FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        $more = $null
        foreach ($e in $all) {
            if ($e.Current.Name -ne "More options") { continue }
            $parent = [System.Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($e)
            if ($parent -and $parent.Current.Name -eq "System Monitor") { $more = $e; break }
        }
        if (-not $more) { throw "The System Monitor card's More options button was not found." }

        $more.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        $item = $null
        for ($wait = 1; $wait -le 10 -and -not $item; $wait++) {
            Start-Sleep -Milliseconds 500
            $all = $root.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                [System.Windows.Automation.Condition]::TrueCondition)
            foreach ($e in $all) {
                if ($e.Current.ProcessId -ne $pid_) { continue }
                if ($e.Current.AutomationId -eq "${Size}MenuItem") { $item = $e; break }
            }
        }
        if (-not $item) {
            throw "The '$Size' menu item was not found. The host offered a different set of sizes."
        }

        $r = $item.Current.BoundingRectangle
        Write-Host "Requesting $Size at $($r.X),$($r.Y) $($r.Width)x$($r.Height) (attempt $attempt)"
        Invoke-MouseClick ([int]($r.X + $r.Width / 2)) ([int]($r.Y + $r.Height / 2))
        Start-Sleep -Seconds 4

        $card = Get-Dashboard (Get-BoardRoot $pid_)
        $height = if ($card) { $card.Current.BoundingRectangle.Height } else { 0 }
        if ($height -eq $expected) { $applied = $true }
        else { Write-Host "  card height is $height, expected $expected; retrying" }
    }
    if (-not $applied) {
        Write-Warning "The card did not settle at the $Size size (expected a height of $expected)."
    }
}

$board = Get-BoardRoot $pid_
$card = Get-Dashboard $board
if ($card) {
    Write-Host "Card rect: $($card.Current.BoundingRectangle)"
} else {
    Write-Host "Card rect: <card not found>"
}

Write-Host "--- rendered text ---"
Get-CardText $board | ForEach-Object { Write-Host "  '$_'" }

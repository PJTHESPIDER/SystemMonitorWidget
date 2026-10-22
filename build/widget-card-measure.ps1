<#
.SYNOPSIS
    Measures one card on the live Widgets board: its rendered text, the
    geometry of every text run, and a screenshot cropped to the card.

.DESCRIPTION
    The three things are captured in the same pass, so the numbers and
    the picture describe the same rendering. Reading them separately
    invites comparing a layout that has since changed.

    The geometry is the evidence for layout defects: a text run whose
    width equals its column's width and whose height is one line is
    clipped rather than wrapped, and a run whose height is two lines
    wrapped. The card's own rectangle is the boundary a run may not
    cross.
#>
[CmdletBinding()]
param(
    [string] $CardName = "System Monitor",
    [string] $OutputPath = "C:\Users\payto\AppData\Local\Temp\widget-measure.png",
    [int] $BoardPid = 0
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase, System.Drawing, System.Windows.Forms

if ($BoardPid -le 0) {
    $p = Get-Process -Name "WidgetBoard" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) { throw "The Widgets board process is not running." }
    $BoardPid = $p.Id
}

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $BoardPid)
$board = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)

# The board is a flyout: it can close itself while a measurement is
# being set up. When that happens it is reopened and the window is
# waited for again, rather than the measurement being abandoned.
if (-not $board) {
    Write-Host "The board window was closed; reopening it..."
    Start-Process "ms-widgetboard:" -ErrorAction SilentlyContinue
    for ($wait = 1; $wait -le 20 -and -not $board; $wait++) {
        Start-Sleep -Milliseconds 500
        $proc = Get-Process -Name "WidgetBoard" -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $proc) { continue }
        $BoardPid = $proc.Id
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $BoardPid)
        $board = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
    }
}
if (-not $board) { throw "The Widgets board window was not found." }
Write-Host "Board process: $BoardPid"

function Get-Card {
    foreach ($e in $board.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)) {
        if ($e.Current.AutomationId -eq "RootGrid" -and $e.Current.Name -eq $CardName) { return $e }
    }
    return $null
}

$card = Get-Card
if (-not $card) { throw "The '$CardName' card is not on the board." }
$rect = $card.Current.BoundingRectangle

Write-Host "=== card '$CardName' ==="
Write-Host "rect: x=$([int]$rect.X) y=$([int]$rect.Y) w=$([int]$rect.Width) h=$([int]$rect.Height)"
Write-Host "bottom edge: $([int]($rect.Y + $rect.Height))"
Write-Host ""

# --- rendered text runs, in board order -------------------------------------
$runs = New-Object System.Collections.Generic.List[object]
foreach ($e in $board.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    $c = $e.Current
    if ($c.ControlType.ProgrammaticName -ne "ControlType.Text") { continue }
    if ([string]::IsNullOrWhiteSpace($c.Name)) { continue }
    $r = $c.BoundingRectangle
    if ($r.IsEmpty) { continue }
    # Only the runs that belong to this card.
    if ($r.X -lt $rect.X - 2 -or $r.X -gt ($rect.X + $rect.Width)) { continue }
    if ($r.Y -lt $rect.Y - 2 -or $r.Y -gt ($rect.Y + $rect.Height)) { continue }
    $runs.Add([pscustomobject]@{
        Text = $c.Name; X = [int]$r.X; Y = [int]$r.Y; W = [int]$r.Width; H = [int]$r.Height
    })
}

Write-Host "--- text runs (bottom edge = $([int]($rect.Y + $rect.Height))) ---"
foreach ($r in ($runs | Sort-Object Y, X)) {
    $over = if (($r.Y + $r.H) -gt ($rect.Y + $rect.Height)) { "  << OVERFLOWS CARD" } else { "" }
    "{0,-48} x={1,4} w={2,4} y={3,4} h={4,3}{5}" -f $r.Text, $r.X, $r.W, $r.Y, $r.H, $over
}

# --- the screenshot ---------------------------------------------------------
$virtual = [System.Windows.Forms.SystemInformation]::VirtualScreen
$bmp = New-Object System.Drawing.Bitmap($virtual.Width, $virtual.Height)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($virtual.X, $virtual.Y, 0, 0, $bmp.Size)

$x = [int]($rect.X - $virtual.X)
$y = [int]($rect.Y - $virtual.Y)
$w = [int]$rect.Width
$h = [int]$rect.Height
$crop = New-Object System.Drawing.Bitmap($w, $h)
$g2 = [System.Drawing.Graphics]::FromImage($crop)
$g2.DrawImage($bmp,
    (New-Object System.Drawing.Rectangle(0, 0, $w, $h)),
    (New-Object System.Drawing.Rectangle($x, $y, $w, $h)),
    [System.Drawing.GraphicsUnit]::Pixel)
$crop.Save($OutputPath, [System.Drawing.Imaging.ImageFormat]::Png)
$g2.Dispose(); $crop.Dispose(); $g.Dispose(); $bmp.Dispose()
Write-Host ""
Write-Host "Saved $OutputPath ($w x $h)"

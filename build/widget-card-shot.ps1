<#
.SYNOPSIS
    Captures a screenshot of one card on the live Widgets board.

.DESCRIPTION
    The board's cards are laid out by the host, so the capture is
    driven by the card's own rectangle as reported by UI Automation
    rather than by hard-coded pixel coordinates. That keeps the capture
    correct when the board moves.

    The image is cropped to the card exactly, so a clipped figure, a
    clipped label and an overflowing card are all visible in the result
    rather than being inferred from the accessibility tree alone.
#>
[CmdletBinding()]
param(
    # The card to capture.
    [string] $CardName = "System Monitor",

    # Where to write the PNG.
    [string] $OutputPath = "C:\Users\payto\AppData\Local\Temp\widget-card.png",

    # The board process id. Defaults to the running WidgetBoard process.
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
if (-not $board) { throw "The Widgets board window was not found." }

$card = $null
foreach ($e in $board.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($e.Current.AutomationId -eq "RootGrid" -and $e.Current.Name -eq $CardName) {
        $card = $e
        break
    }
}
if (-not $card) { throw "The '$CardName' card is not on the board." }

$rect = $card.Current.BoundingRectangle
Write-Host "Card '$CardName' at $($rect.X),$($rect.Y) $($rect.Width)x$($rect.Height)"

# The board window is on one monitor, so the card's rectangle is in
# screen coordinates of the monitor it sits on. A virtual screen capture
# starts at the virtual screen's origin, which is not the origin of the
# monitor the card is on, so the offset is worked out from the capture.
$virtual = [System.Windows.Forms.SystemInformation]::VirtualScreen
Write-Host "Virtual screen origin: $($virtual.X),$($virtual.Y)"

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
Write-Host "Saved $OutputPath ($w x $h)"

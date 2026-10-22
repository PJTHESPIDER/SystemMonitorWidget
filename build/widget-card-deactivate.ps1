<#
.SYNOPSIS
    Measures the provider process's CPU use, to tell whether the widget
    is still being polled.

.DESCRIPTION
    The provider polls the sensors on a five-second interval while a
    widget is active, and stops when every widget is deactivated. CPU
    time is the observable that tells the two apart from outside the
    process: a provider that is still collecting keeps accruing CPU
    time, and one that has stopped does not.

    The reading is the delta over a window, in CPU-seconds per second,
    so it is comparable between runs.
#>
[CmdletBinding()]
param(
    [int] $WindowSeconds = 10,
    [int] $ProviderPid = 0
)

$ErrorActionPreference = 'Stop'

if ($ProviderPid -le 0) {
    $p = Get-Process -Name "SystemMonitor" -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like "*com.test.systemmonitor*" } |
        Select-Object -First 1
    if (-not $p) { throw "The provider process is not running." }
    $ProviderPid = $p.Id
}

$before = Get-Process -Id $ProviderPid -ErrorAction SilentlyContinue
if (-not $before) { throw "The provider process $ProviderPid is not running." }

$cpu0 = $before.CPU
$start = Get-Date

Start-Sleep -Seconds $WindowSeconds

$after = Get-Process -Id $ProviderPid -ErrorAction SilentlyContinue
if (-not $after) {
    Write-Host "The provider process has exited."
    return
}

$cpu1 = $after.CPU
$elapsed = ((Get-Date) - $start).TotalSeconds

"{0,6:N2} CPU-seconds in {1:N0}s = {2:N3} CPU-s per second" -f ($cpu1 - $cpu0), $elapsed, (($cpu1 - $cpu0) / $elapsed)
"process id: $ProviderPid   working set: $([math]::Round($after.WorkingSet64/1MB,1)) MB"

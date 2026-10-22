<#
.SYNOPSIS
    Installs (or reinstalls) the SystemMonitor test MSIX for the current user.

.DESCRIPTION
    The package path is derived from this script's own location, so the
    script works from any checkout location and on any machine.

    The package's identity and the widget's COM registration are unchanged:
    the script installs exactly the package that
    build/Create-TestMsix.ps1 produced.

.EXAMPLE
    ./build/install-msix.ps1
#>
[CmdletBinding()]
param(
    [string] $OutputFolder = "msix-output",
    [string] $PackageName = "com.test.systemmonitor"
)

$ErrorActionPreference = 'Stop'

function Fail([string]$msg) { Write-Error $msg; exit 1 }

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $scriptDir $OutputFolder
$msixPath = Join-Path $outputDir "SystemMonitor.msix"

if (-not (Test-Path $msixPath)) {
    Fail "Package not found: $msixPath`nRun build/Create-TestMsix.ps1 first."
}

# Remove the previous installation if there is one, so a reinstall does not
# fail on a package that is already registered.
$existing = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Removing existing package: $($existing.PackageFullName)"
    try {
        Remove-AppxPackage -Package $existing.PackageFullName -ErrorAction Stop
    }
    catch {
        Fail "Remove-AppxPackage failed: $($_.Exception.Message)"
    }
    Write-Host "Removed."
}

Write-Host "Installing $msixPath ..."
# Add-AppxPackage is a cmdlet, not a native command, so it does not set
# $LASTEXITCODE. The failure has to be caught, not polled for.
try {
    Add-AppxPackage -Path $msixPath -ErrorAction Stop
}
catch {
    Fail "Add-AppxPackage failed: $($_.Exception.Message)"
}

$installed = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
if (-not $installed) { Fail "The package was not registered after installation." }

Write-Host "Installation complete: $($installed.PackageFullName)"
Write-Host "The widget can now be added from the Windows Widgets board."

$msixPath = "E:\Code\SystemMonitor\build\msix-output\SystemMonitor.msix"

# Remove existing installation if present
$existing = Get-AppxPackage -Name "com.test.systemmonitor" -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Removing existing package: $($existing.PackageFullName)"
    Remove-AppxPackage -Package $existing.PackageFullName
    Write-Host "Removed."
}

# Install fresh
Write-Host "Installing $msixPath ..."
Add-AppxPackage -Path $msixPath
Write-Host "Installation complete."

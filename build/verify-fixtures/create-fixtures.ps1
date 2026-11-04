# Creates Windows Authenticode verification test fixtures.
# Does NOT download or execute any installer. Uses local fixtures only.
# Does NOT add certificates to trust stores or weaken production policy.

$fixturesDir = 'E:\Code\SystemMonitor\build\verify-fixtures'
$unsignedPath = Join-Path $fixturesDir 'unsigned_fixture.exe'
$modifiedSignedPath = Join-Path $fixturesDir 'modified_signed.exe'

New-Item -ItemType Directory -Path $fixturesDir -Force | Out-Null

# Fixture 1: Unsigned executable (SystemMonitor.exe from build extract — not Authenticode signed)
Copy-Item -Path 'E:\Code\SystemMonitor\build\verify-extract8\SystemMonitor.exe' -Destination $unsignedPath -Force

# Fixture 2: Modified copy of the real signed PawnIO installer (signature broken)
Copy-Item -Path 'E:\Code\SystemMonitor\build\pawnio_install\PawnIO_setup.exe' -Destination $modifiedSignedPath -Force
# Append a single byte using FileStream to avoid .NET API availability issues
$fs = [IO.File]::OpenWrite($modifiedSignedPath)
$fs.Seek(0, [IO.SeekOrigin]::End) | Out-Null
$fs.WriteByte(0x00)
$fs.Close()

Write-Output "Fixtures created:"
Get-Item $unsignedPath | Select-Object FullName, Length | Format-List
Get-Item $modifiedSignedPath | Select-Object FullName, Length | Format-List

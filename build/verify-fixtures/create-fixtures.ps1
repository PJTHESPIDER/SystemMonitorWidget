[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [string]$UnsignedPublishExe,

    [Parameter(Mandatory=$true)]
    [string]$OutputDir,

    [string]$SourceCommit,

    [string]$PublishCommand
)

$ErrorActionPreference = 'Stop'

function Fail([string]$msg) { Write-Error $msg; exit 1 }

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$buildDir = Split-Path -Parent $scriptDir
$repoRoot = Split-Path -Parent $buildDir
$pawnioInstaller = Join-Path $buildDir 'pawnio_install\PawnIO_setup.exe'
$trackedFixtureDir = Join-Path $scriptDir 'verify-fixtures'

# --- Validate and resolve UnsignedPublishExe ---
if (-not (Test-Path -LiteralPath $UnsignedPublishExe -PathType Leaf)) {
    Fail "UnsignedPublishExe not found or not a file: $UnsignedPublishExe"
}
$exePath = Resolve-Path -LiteralPath $UnsignedPublishExe

# --- Resolve OutputDir ---
$outDir = Resolve-Path -LiteralPath $OutputDir -ErrorAction SilentlyContinue
if (-not $outDir) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null; $outDir = $OutputDir }
else { $outDir = $outDir.Path }

$unsignedOut = Join-Path $outDir 'unsigned_fixture.exe'
$tamperedOut = Join-Path $outDir 'modified_signed.exe'

# --- Collision checks ---
if ($exePath.Path -eq $unsignedOut -or $exePath.Path -eq $tamperedOut) {
    Fail "Output path collides with input: $exePath"
}
$tfd = Resolve-Path -LiteralPath $trackedFixtureDir -ErrorAction SilentlyContinue
if ($tfd -and $outDir -eq $tfd.Path) {
    Fail "OutputDir must not be the tracked fixture directory ($trackedFixtureDir). Use a separate temporary directory for validation."
}
if ((Test-Path -LiteralPath $unsignedOut) -or (Test-Path -LiteralPath $tamperedOut)) {
    Fail "Output fixture files already exist in $outDir. Remove them first or use a different OutputDir."
}

# --- Validate UnsignedPublishExe ---
Write-Output "Validating unsigned publish input: $exePath"

# PE executable check (MZ + PE signature)
$bytes = [System.IO.File]::ReadAllBytes($exePath.Path)
if ($bytes.Length -lt 64 -or $bytes[0] -ne 0x4D -or $bytes[1] -ne 0x5A) {
    Fail "Not a PE executable (missing MZ header): $exePath"
}
$peOffset = [BitConverter]::ToUInt32($bytes, 0x3C)
if ($peOffset -ge $bytes.Length - 4) { Fail "Invalid PE offset in $exePath" }
$peSig = [BitConverter]::ToUInt32($bytes, $peOffset)
if ($peSig -ne 0x00004550) { Fail "Invalid PE signature in $exePath" }
$machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
$arch = switch ($machine) {
    0x8664 { 'x64' }
    0x14c  { 'x86' }
    0xaa64 { 'ARM64' }
    default { "Unknown (0x{0:X4})" -f $machine }
}
Write-Output "Architecture: $arch"

# Unsigned Authenticode check
$sig = Get-AuthenticodeSignature -LiteralPath $exePath.Path
if ($sig.Status -ne 'NotSigned' -or $sig.SignatureType -ne 'None') {
    Fail "Input is not unsigned. Status=$($sig.Status), SignatureType=$($sig.SignatureType)"
}
Write-Output "Signature status: NotSigned (validated)"

# --- Validate PawnIO Installer Source ---
if (-not (Test-Path -LiteralPath $pawnioInstaller -PathType Leaf)) {
    Fail "PawnIO installer not found at tracked location: $pawnioInstaller"
}
$pawnioSig = Get-AuthenticodeSignature -LiteralPath $pawnioInstaller
if ($pawnioSig.Status -ne 'Valid' -or $pawnioSig.SignatureType -ne 'Authenticode') {
    Fail "PawnIO installer signature invalid. Status=$($pawnioSig.Status), Type=$($pawnioSig.SignatureType)"
}
Write-Output "PawnIO installer: Valid Authenticode signature confirmed"

# --- Observed Artifact Properties ---
$hash = Get-FileHash -LiteralPath $exePath.Path -Algorithm SHA256 | Select-Object -ExpandProperty Hash
$size = (Get-Item -LiteralPath $exePath.Path).Length

Write-Output "=== Observed Artifact ==="
Write-Output "SHA-256:   $hash"
Write-Output "Size:      $size bytes"
Write-Output "Arch:      $arch"
Write-Output "Signature: NotSigned (SignatureType=None)"

# --- Supplied Metadata (clearly labeled) ---
if ($SourceCommit) { Write-Output "SourceCommit (supplied): $SourceCommit" }
else { Write-Output "SourceCommit: unknown (not supplied)" }

if ($PublishCommand) { Write-Output "PublishCommand (supplied): $PublishCommand" }
else { Write-Output "PublishCommand: unknown (not supplied)" }

Write-Output "BuildProvenance: observed from provided executable only"

# --- Create Tampered Fixture ---
Write-Output "Creating tampered signed fixture..."
Copy-Item -LiteralPath $pawnioInstaller -Destination $tamperedOut -Force

# Append one byte (current method)
$fs = [System.IO.FileStream]::new($tamperedOut, [IO.FileMode]::Append, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $fs.WriteByte(0x00) } finally { $fs.Dispose() }

# --- Verify Tampering Produces Expected Result ---
$tamperedSig = Get-AuthenticodeSignature -LiteralPath $tamperedOut
if ($tamperedSig.Status -eq 'Valid') {
    Fail "Tampering FAILED: modified fixture still shows Valid signature. Status=$($tamperedSig.Status), Type=$($tamperedSig.SignatureType)"
}
Write-Output "Tampered fixture status: $($tamperedSig.Status) / $($tamperedSig.SignatureType) (non-Valid confirmed)"

# --- Write Unsigned Fixture ---
Copy-Item -LiteralPath $exePath.Path -Destination $unsignedOut -Force

# Validate the copied unsigned fixture too
$copiedSig = Get-AuthenticodeSignature -LiteralPath $unsignedOut
if ($copiedSig.Status -ne 'NotSigned' -or $copiedSig.SignatureType -ne 'None') {
    Fail "Copied unsigned fixture validation failed. Status=$($copiedSig.Status), Type=$($copiedSig.SignatureType)"
}

Write-Output "=== Fixtures Written ==="
Get-Item -LiteralPath $unsignedOut | Select-Object FullName, Length | Format-List
Get-Item -LiteralPath $tamperedOut | Select-Object FullName, Length | Format-List

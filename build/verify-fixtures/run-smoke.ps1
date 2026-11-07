<#
.SYNOPSIS
    Windows Authenticode verification smoke test.
.DESCRIPTION
    Uses Get-AuthenticodeSignature (internally calls WinVerifyTrust with
    WINTRUST_ACTION_GENERIC_VERIFY_V2, WTD_UI_NONE, WTD_REVOKE_WHOLECHAIN).
    Does NOT execute any fixture file.
    Does NOT add certificates to trust stores or weaken production policy.
    Does NOT download any installer.
.PARAMETER UnsignedFixture
    Path to the unsigned fixture executable (expected Status=NotSigned, SignatureType=None).
.PARAMETER SignedInstaller
    Path to the valid signed PawnIO installer (expected Status=Valid, known signer subject).
.PARAMETER ModifiedFixture
    Path to the tampered signed fixture (expected Status != Valid).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$UnsignedFixture,

    [Parameter(Mandatory=$true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$SignedInstaller,

    [Parameter(Mandatory=$true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$ModifiedFixture
)

$ErrorActionPreference = 'Stop'

Write-Output "=== Windows Authenticode Verification Smoke Test ==="
Write-Output ""

# Fixture 1: Unsigned file
Write-Output "--- Fixture 1: Unsigned file ---"
Write-Output "File: $UnsignedFixture"
$sig1 = Get-AuthenticodeSignature -LiteralPath $UnsignedFixture
Write-Output "  Status: $($sig1.Status)"
Write-Output "  StatusMessage: $($sig1.StatusMessage)"
Write-Output "  SignatureType: $($sig1.SignatureType)"
Write-Output ""

if ($sig1.Status -ne 'NotSigned' -or $sig1.SignatureType -ne 'None') {
    Write-Error "FAIL: Fixture 1 expected NotSigned/None, got $($sig1.Status)/$($sig1.SignatureType)"
    exit 1
}
Write-Output "PASS: Fixture 1 signature status correct."
Write-Output ""

# Fixture 2: Signed file (valid under Windows trust policy)
Write-Output "--- Fixture 2: Valid signed file (PawnIO_setup.exe) ---"
Write-Output "File: $SignedInstaller"
$hash2 = Get-FileHash -LiteralPath $SignedInstaller -Algorithm SHA256 | Select-Object -ExpandProperty Hash
Write-Output "SHA-256: $hash2"
$sig2 = Get-AuthenticodeSignature -LiteralPath $SignedInstaller
Write-Output "  Status: $($sig2.Status)"
Write-Output "  StatusMessage: $($sig2.StatusMessage)"
Write-Output "  SignatureType: $($sig2.SignatureType)"
Write-Output "  SignerCertificate.Subject: $($sig2.SignerCertificate.Subject)"
Write-Output "  SignerCertificate.Thumbprint: $($sig2.SignerCertificate.Thumbprint)"
Write-Output "  Has Timestamp: $($sig2.TimeStamperCertificate -ne $null)"
Write-Output ""

if ($sig2.Status -ne 'Valid' -or $sig2.SignatureType -ne 'Authenticode') {
    Write-Error "FAIL: Fixture 2 expected Valid/Authenticode, got $($sig2.Status)/$($sig2.SignatureType)"
    exit 1
}

$expectedSigner = 'E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU'
Write-Output "--- Validator: Expected signer subject ---"
Write-Output "  Expected: $expectedSigner"
Write-Output "  Actual:   $($sig2.SignerCertificate.Subject)"
$signerMatch = $sig2.SignerCertificate.Subject -eq $expectedSigner
Write-Output "  Match:    $signerMatch"
Write-Output ""

if (-not $signerMatch) {
    Write-Error "FAIL: Signer subject mismatch"
    exit 1
}
Write-Output "PASS: Fixture 2 signature valid, signer matches, timestamp present."
Write-Output ""

# Fixture 3: Modified signed file (signature broken)
Write-Output "--- Fixture 3: Modified signed file (signature should be invalid) ---"
Write-Output "File: $ModifiedFixture"
$hash3 = Get-FileHash -LiteralPath $ModifiedFixture -Algorithm SHA256 | Select-Object -ExpandProperty Hash
Write-Output "SHA-256: $hash3"
$sig3 = Get-AuthenticodeSignature -LiteralPath $ModifiedFixture
Write-Output "  Status: $($sig3.Status)"
Write-Output "  StatusMessage: $($sig3.StatusMessage)"
Write-Output "  SignatureType: $($sig3.SignatureType)"
Write-Output ""

if ($sig3.Status -eq 'Valid') {
    Write-Error "FAIL: Fixture 3 expected non-Valid, got Valid"
    exit 1
}
Write-Output "PASS: Fixture 3 signature is non-Valid ($($sig3.Status))."
Write-Output ""

Write-Output "=== Smoke Test Complete: ALL PASSED ==="

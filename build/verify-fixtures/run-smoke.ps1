# Windows Authenticode verification smoke test.
# Uses Get-AuthenticodeSignature (internally calls WinVerifyTrust with
# WINTRUST_ACTION_GENERIC_VERIFY_V2, WTD_UI_NONE, WTD_REVOKE_WHOLECHAIN).
#
# Does NOT execute any fixture file.
# Does NOT add certificates to trust stores or weaken production policy.
# Does NOT download any installer.

$signedPath = 'E:\Code\SystemMonitor\build\pawnio_install\PawnIO_setup.exe'
$unsignedPath = 'E:\Code\SystemMonitor\build\verify-fixtures\unsigned_fixture.exe'
$modifiedSignedPath = 'E:\Code\SystemMonitor\build\verify-fixtures\modified_signed.exe'

Write-Output "=== Windows Authenticode Verification Smoke Test ==="
Write-Output ""

# Fixture 1: Unsigned file
Write-Output "--- Fixture 1: Unsigned file ---"
Write-Output "File: $unsignedPath"
$sig1 = Get-AuthenticodeSignature $unsignedPath
Write-Output "  Status: $($sig1.Status)"
Write-Output "  StatusMessage: $($sig1.StatusMessage)"
Write-Output "  SignatureType: $($sig1.SignatureType)"
Write-Output ""

# Fixture 2: Signed file (valid under Windows trust policy)
Write-Output "--- Fixture 2: Valid signed file (PawnIO_setup.exe) ---"
Write-Output "File: $signedPath"
Write-Output "SHA-256: $(Get-FileHash $signedPath -Algorithm SHA256 | Select-Object -ExpandProperty Hash)"
$sig2 = Get-AuthenticodeSignature $signedPath
Write-Output "  Status: $($sig2.Status)"
Write-Output "  StatusMessage: $($sig2.StatusMessage)"
Write-Output "  SignatureType: $($sig2.SignatureType)"
Write-Output "  SignerCertificate.Subject: $($sig2.SignerCertificate.Subject)"
Write-Output "  SignerCertificate.Thumbprint: $($sig2.SignerCertificate.Thumbprint)"
Write-Output "  Has Timestamp: $($sig2.TimeStamperCertificate -ne $null)"
Write-Output ""

# Fixture 3: Modified signed file (signature broken)
Write-Output "--- Fixture 3: Modified signed file (signature should be invalid) ---"
Write-Output "File: $modifiedSignedPath"
Write-Output "SHA-256: $(Get-FileHash $modifiedSignedPath -Algorithm SHA256 | Select-Object -ExpandProperty Hash)"
$sig3 = Get-AuthenticodeSignature $modifiedSignedPath
Write-Output "  Status: $($sig3.Status)"
Write-Output "  StatusMessage: $($sig3.StatusMessage)"
Write-Output "  SignatureType: $($sig3.SignatureType)"
Write-Output ""

# Expected signer subject (from TrustedPawnIoInstaller pin)
$expectedSigner = 'E=admin@namazso.eu, CN=namazso.eu, O=namazso, L=Debrecen, C=HU'
Write-Output "--- Validator: Expected signer subject ---"
Write-Output "  Expected: $expectedSigner"
Write-Output "  Actual:   $($sig2.SignerCertificate.Subject)"
Write-Output "  Match:    $($sig2.SignerCertificate.Subject -eq $expectedSigner)"
Write-Output ""

Write-Output "=== Smoke Test Complete ==="

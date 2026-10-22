<#
.SYNOPSIS
    Exports the public certificate from the signing PFX and installs it into
    the current user's Trusted People store, so Windows will trust packages
    signed with the test certificate.

.DESCRIPTION
    This is the "trust the test signer" step. It is only needed for
    self-signed test packages; a package signed with a certificate that
    Windows already trusts does not need it.

    The script does not contain a password. The PFX password comes from the
    SYSTEMMONITOR_CERT_PASSWORD environment variable, from the -CertPassword
    parameter, or from an interactive prompt.

.PARAMETER CertPassword
    The password of the signing PFX. Prefer the environment variable so the
    password does not appear in the shell history.

.EXAMPLE
    $env:SYSTEMMONITOR_CERT_PASSWORD = '...'
    ./build/install-cert.ps1
#>
[CmdletBinding()]
param(
    [string] $OutputFolder = "msix-output",
    [SecureString] $CertPassword
)

$ErrorActionPreference = 'Stop'

function Fail([string]$msg) { Write-Error $msg; exit 1 }

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$outputDir = Join-Path $scriptDir $OutputFolder

$pfxPath = Join-Path $outputDir "testcert.pfx"
$cerPath = Join-Path $outputDir "testcert.cer"

if (-not (Test-Path $pfxPath)) {
    Fail "Signing certificate not found: $pfxPath`nRun build/Create-TestMsix.ps1 first."
}

if ([string]::IsNullOrWhiteSpace($env:SYSTEMMONITOR_CERT_PASSWORD)) {
    $securePwd = if ($CertPassword) { $CertPassword } else { Read-Host "Password for $pfxPath (input is not echoed)" -AsSecureString }
}
else {
    $securePwd = ConvertTo-SecureString -String $env:SYSTEMMONITOR_CERT_PASSWORD -AsPlainText -Force
}

# Get-PfxData reads the public part of the PFX; the private key is never
# written anywhere.
$pfxData = Get-PfxData -FilePath $pfxPath -Password $securePwd
$securePwd = $null

if (-not $pfxData.EndEntityCertificates -or $pfxData.EndEntityCertificates.Count -eq 0) {
    Fail "No end-entity certificate found in $pfxPath."
}

$certObj = $pfxData.EndEntityCertificates[0]

# Export as a DER-encoded .cer (public certificate only).
$certBytes = $certObj.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
[System.IO.File]::WriteAllBytes($cerPath, $certBytes)
Write-Host "Exported public certificate to: $cerPath"

Write-Host "Installing to CurrentUser\TrustedPeople..."
& certutil -user -addstore "TrustedPeople" $cerPath
if ($LASTEXITCODE -ne 0) { Fail "certutil failed to install the certificate." }

Write-Host "Done. The test certificate is trusted for the current user."

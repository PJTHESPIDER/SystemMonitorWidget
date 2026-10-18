# Step 1: Extract the public .cer from the PFX (no password needed for .cer export)
$pfxPath = "E:\Code\SystemMonitor\build\msix-output\testcert.pfx"
$cerPath = "E:\Code\SystemMonitor\build\msix-output\testcert.cer"
$certPwd = ConvertTo-SecureString "P@ssw0rd!" -AsPlainText -Force

$cert = Get-PfxData -FilePath $pfxPath -Password $certPwd
$certObj = $cert.EndEntityCertificates[0]

# Export as DER-encoded .cer
$certBytes = $certObj.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
[System.IO.File]::WriteAllBytes($cerPath, $certBytes)
Write-Host "Exported .cer to: $cerPath"

# Step 2: Install to CurrentUser stores using certutil
Write-Host "Installing to CurrentUser\TrustedPeople..."
& certutil -user -addstore "TrustedPeople" $cerPath

Write-Host "Done. Certificate installed."

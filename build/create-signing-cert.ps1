# Re-create the signing cert with a usable PFX
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq "D578E7433B8E21107611499279B8DB15BE3D1A26" }
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=Developer" -KeyExportPolicy Exportable -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(10)
}
Write-Host "Cert thumbprint: $($cert.Thumbprint)"

# Export PFX with a simple password
$pfxPath = "E:\Code\SystemMonitor\build\msix-output-new\testcert.pfx"
$pwd = ConvertTo-SecureString -String "testpassword" -AsPlainText -Force
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $pwd | Out-Null
Write-Host "Exported PFX to: $pfxPath"

# Sign the MSIX
$msixPath = "E:\Code\SystemMonitor\build\msix-output-new\SystemMonitor.msix"
$signtool = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
& "$signtool" sign /fd SHA256 /f $pfxPath /p "testpassword" /tr http://timestamp.digicert.com /td SHA256 $msixPath
Write-Host "Signed MSIX: $msixPath (exit code: $LASTEXITCODE)"

param(
    [string] $ProjectRelativePath = "SystemMonitor/SystemMonitor.csproj",
    [string] $PackageManifestPath = "SystemMonitor/SystemMonitor/Package.appxmanifest",
    [string] $WidgetsManifestPath = "WidgetsManifest.xml",
    [string] $AssetsRelativePath = "SystemMonitor/Assets",
    [string] $OutputFolder = "msix-output",
    [string] $PublisherCN = "Developer",
    [string] $IdentityName = "com.test.systemmonitor",
    [string] $CertPassword = "P@ssw0rd!",
    [string] $Runtime = "win-x64"
)

function Fail([string]$msg){ Write-Error $msg; exit 1 }

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir
$repoRoot = Split-Path -Parent $scriptDir

$projectPath = Join-Path $repoRoot $ProjectRelativePath
if (-not (Test-Path $projectPath)) { Fail "Project file not found: $projectPath" }

$packageManifest = Join-Path $repoRoot $PackageManifestPath
if (-not (Test-Path $packageManifest)) { Fail "Package manifest not found: $packageManifest" }

$widgetsManifest = Join-Path $repoRoot $WidgetsManifestPath
if (-not (Test-Path $widgetsManifest)) { Fail "Widgets manifest not found: $widgetsManifest" }

$assetsDir = Join-Path $repoRoot $AssetsRelativePath

$publishDir = Join-Path $scriptDir "publish-temp"
$packageRoot = Join-Path $scriptDir "package-root"
$msixOutDir = Join-Path $scriptDir $OutputFolder
Remove-Item -Recurse -Force $publishDir, $packageRoot, $msixOutDir -ErrorAction SilentlyContinue
New-Item -Force -ItemType Directory -Path $publishDir, $packageRoot, $msixOutDir | Out-Null

dotnet publish $projectPath -c Release -r $Runtime -o $publishDir /p:SelfContained=false
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed." }

Copy-Item -Force -Recurse "$publishDir\*" $packageRoot
Copy-Item -Force $packageManifest (Join-Path $packageRoot "AppxManifest.xml")
Copy-Item -Force $widgetsManifest $packageRoot
if (Test-Path $assetsDir) { Copy-Item -Force -Recurse $assetsDir (Join-Path $packageRoot "Assets") }
$assetRoot = Join-Path $packageRoot "Assets"
$assetAliases = @{
    "SplashScreen.scale-200.png" = "SplashScreen.png"
    "Square150x150Logo.scale-200.png" = "Square150x150Logo.png"
    "Square44x44Logo.scale-200.png" = "Square44x44Logo.png"
    "Wide310x150Logo.scale-200.png" = "Wide310x150Logo.png"
}
foreach ($alias in $assetAliases.GetEnumerator()) {
    $sourceAsset = Join-Path $assetRoot $alias.Key
    if (Test-Path $sourceAsset) { Copy-Item -Force $sourceAsset (Join-Path $assetRoot $alias.Value) }
}

# Update manifest identity + publisher
$manifestPathInRoot = Join-Path $packageRoot "AppxManifest.xml"
$manifestText = Get-Content -Raw $packageManifest
$manifestText = $manifestText -replace '(<Identity\b[^>]*\bName=")[^"]+("[^>]*>)', ('$1' + $IdentityName + '$2')
$manifestText = $manifestText -replace '(<Identity\b[^>]*\bPublisher=")[^"]+("[^>]*>)', ('$1CN=' + $PublisherCN + '$2')
$manifestText = $manifestText -replace '\$targetnametoken\$\.exe', 'SystemMonitor.exe'
$manifestText = $manifestText -replace '\$targetentrypoint\$', 'Windows.FullTrustApplication'
$processorArchitecture = switch ($Runtime) {
    "win-x86" { "x86" }
    "win-arm64" { "arm64" }
    default { "x64" }
}
if ($manifestText -match 'ProcessorArchitecture="[^"]+"') {
    $manifestText = $manifestText -replace 'ProcessorArchitecture="[^"]+"', "ProcessorArchitecture=`"$processorArchitecture`""
} else {
    $manifestText = $manifestText -replace '(<Identity\s+)', ('$1ProcessorArchitecture="' + $processorArchitecture + '" ')
}
Set-Content -Path $manifestPathInRoot -Value $manifestText -Encoding utf8

# Create or reuse certificate PFX
$pfxPath = Join-Path $msixOutDir "testcert.pfx"
$publisherString = "CN=$PublisherCN"
$cert = $null
$trustedRoots = Get-ChildItem Cert:\LocalMachine\Root -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq $publisherString }
foreach ($tr in $trustedRoots) {
    $myCert = Get-Item "Cert:\CurrentUser\My\$($tr.Thumbprint)" -ErrorAction SilentlyContinue
    if ($myCert -and $myCert.HasPrivateKey) {
        $cert = $myCert
        Write-Host "Reusing trusted root certificate: $($cert.Thumbprint)"
        break
    }
}
if (-not $cert) {
    Write-Host "Creating new self-signed certificate..."
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $publisherString -KeyExportPolicy Exportable -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(10)
}
if (-not $cert) { Fail "Certificate creation failed." }
$securePwd = ConvertTo-SecureString -String $CertPassword -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePwd | Out-Null

# Locate required tools — check PATH first, then fall back to Windows Kits
function Find-SdkTool([string]$name) {
    $fromPath = (Get-Command $name -ErrorAction SilentlyContinue).Source
    if ($fromPath) { return $fromPath }
    $kitsBase = "C:\Program Files (x86)\Windows Kits\10\bin"
    return Get-ChildItem -Path $kitsBase -Recurse -Filter $name -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -match "x64" } |
        Sort-Object -Property FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
$makeappx = Find-SdkTool "makeappx.exe"
$signtool  = Find-SdkTool "signtool.exe"
if (-not $makeappx) { Fail "makeappx.exe not found. Install Windows SDK." }
if (-not $signtool) { Fail "signtool.exe not found. Install Windows SDK." }

$msixName = "SystemMonitor.msix"
$msixPath = Join-Path $msixOutDir $msixName

& "$makeappx" pack /d $packageRoot /p $msixPath
if ($LASTEXITCODE -ne 0) { Fail "makeappx failed." }

& "$signtool" sign /fd SHA256 /f $pfxPath /p $CertPassword /tr http://timestamp.digicert.com /td SHA256 $msixPath
if ($LASTEXITCODE -ne 0) { Fail "signtool failed." }

Write-Host "MSIX created: $msixPath"
Write-Host "PFX created: $pfxPath"
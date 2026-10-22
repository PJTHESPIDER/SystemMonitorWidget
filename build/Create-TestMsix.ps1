<#
.SYNOPSIS
    Builds, packages and signs the SystemMonitor test MSIX.

.DESCRIPTION
    This is the reproducible packaging path for a clean checkout. It:

      1. publishes the project for the requested runtime,
      2. assembles the package root from the published output,
      3. rewrites the manifest's identity and architecture with XML-aware
         edits (never with regular expressions over the raw text),
      4. packs the root with makeappx,
      5. signs the package with signtool.

    The script deliberately does NOT contain a signing password. The
    password is supplied on the command line, through the
    SYSTEMMONITOR_CERT_PASSWORD environment variable, or interactively.
    A password baked into a script is a published password.

.PARAMETER CertPassword
    The password for the signing certificate's PFX. Prefer the
    SYSTEMMONITOR_CERT_PASSWORD environment variable so the password does
    not end up in the shell history or in a process listing.

.EXAMPLE
    $env:SYSTEMMONITOR_CERT_PASSWORD = '...'
    ./build/Create-TestMsix.ps1 -Runtime win-x64
#>
[CmdletBinding()]
param(
    [string] $ProjectRelativePath = "SystemMonitor/SystemMonitor.csproj",
    [string] $PackageManifestPath = "SystemMonitor/SystemMonitor/Package.appxmanifest",
    [string] $WidgetsManifestPath = "WidgetsManifest.xml",
    [string] $AssetsRelativePath = "SystemMonitor/Assets",
    [string] $OutputFolder = "msix-output",
    [string] $PublisherCN = "Developer",
    [string] $IdentityName = "com.test.systemmonitor",
    [string] $Runtime = "win-x64",
    [SecureString] $CertPassword
)

$ErrorActionPreference = 'Stop'

function Fail([string]$msg) { Write-Error $msg; exit 1 }

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $scriptDir

# ---------------------------------------------------------------------------
# Resolve the paths, and refuse to continue if any of them is missing.
# ---------------------------------------------------------------------------

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

# ---------------------------------------------------------------------------
# Validate the directories before deleting anything under them.
#
# The three directories above are deleted recursively. A mistake in a path
# — a variable that resolved to an empty string, a relative path that
# landed somewhere unexpected — would turn "clean the output" into
# "delete an arbitrary directory". So each target is resolved to an
# absolute path, checked to be inside the build directory, and checked to
# be a directory rather than a file or a symlink to somewhere else.
# ---------------------------------------------------------------------------

function Assert-SafeToDelete([string]$relativeName, [string]$root) {
    # Reject the obviously wrong inputs before touching the filesystem. A
    # rooted name is not a subdirectory of the build folder, and a name
    # that walks up with ".." is trying to escape it.
    if ([string]::IsNullOrWhiteSpace($relativeName)) {
        Fail "Refusing to delete an empty path."
    }
    if ([System.IO.Path]::IsPathRooted($relativeName)) {
        Fail "Refusing to delete '$relativeName': it is an absolute path, not a folder under the build directory."
    }
    if ($relativeName -match '(^|[\\/])\.\.([\\/]|$)') {
        Fail "Refusing to delete '$relativeName': it walks outside the build directory."
    }

    $resolvedRoot = [System.IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
    $resolvedPath = [System.IO.Path]::GetFullPath((Join-Path $root $relativeName))

    if (-not $resolvedPath.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        Fail "Refusing to delete '$resolvedPath': it is not inside '$resolvedRoot'."
    }

    if ($resolvedPath.TrimEnd('\') -eq $resolvedRoot.TrimEnd('\')) {
        Fail "Refusing to delete the build directory itself: '$resolvedPath'."
    }

    if (Test-Path $resolvedPath) {
        $item = Get-Item -LiteralPath $resolvedPath -Force
        if (-not $item.PSIsContainer) {
            Fail "Refusing to delete '$resolvedPath': it is a file, not a directory."
        }
        if ($item.LinkType) {
            Fail "Refusing to delete '$resolvedPath': it is a link, not a real directory."
        }
    }

    return $resolvedPath
}

$publishDir = Assert-SafeToDelete "publish-temp" $scriptDir
$packageRoot = Assert-SafeToDelete "package-root" $scriptDir
$msixOutDir = Assert-SafeToDelete $OutputFolder $scriptDir

foreach ($dir in @($publishDir, $packageRoot, $msixOutDir)) {
    if (Test-Path $dir) {
        Remove-Item -LiteralPath $dir -Recurse -Force
    }
    New-Item -Force -ItemType Directory -Path $dir | Out-Null
}

# ---------------------------------------------------------------------------
# Publish.
# ---------------------------------------------------------------------------

Write-Host "Publishing $Runtime to $publishDir ..."
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

# ---------------------------------------------------------------------------
# Rewrite the manifest's identity and architecture.
#
# The edits are made through the XML DOM, so an attribute that happens to
# look like an Identity element elsewhere in the file, or a
# "$targetnametoken$" placeholder in a comment, cannot be corrupted by a
# pattern match. The COM class ids, the package identity's structure and
# the widget extension are all left exactly as they are.
# ---------------------------------------------------------------------------

$processorArchitecture = switch ($Runtime) {
    "win-x86" { "x86" }
    "win-arm64" { "arm64" }
    "win-x64" { "x64" }
    default { Fail "Unsupported runtime '$Runtime'. Use win-x64, win-x86 or win-arm64." }
}

$manifestPathInRoot = Join-Path $packageRoot "AppxManifest.xml"

$xml = New-Object System.Xml.XmlDocument
$xml.PreserveWhitespace = $true
$xml.Load($packageManifest)

$ns = New-Object System.Xml.XmlNamespaceManager($xml.NameTable)
$ns.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")

$identity = $xml.SelectSingleNode("/f:Package/f:Identity", $ns)
if (-not $identity) { Fail "No Identity element found in $packageManifest." }

$identity.SetAttribute("Name", $IdentityName)
$identity.SetAttribute("Publisher", "CN=$PublisherCN")
$identity.SetAttribute("ProcessorArchitecture", $processorArchitecture)

# The manifest refers to the executable through the target-name token.
# Resolve it here, on the element that carries it, rather than by
# replacing the text of the whole document.
$executableNodes = $xml.SelectNodes("//*[@Executable]", $ns)
foreach ($node in $executableNodes) {
    if ($node.GetAttribute("Executable") -eq '$targetnametoken$.exe') {
        $node.SetAttribute("Executable", "SystemMonitor.exe")
    }
}

$entryPointNodes = $xml.SelectNodes("//*[@EntryPoint]", $ns)
foreach ($node in $entryPointNodes) {
    if ($node.GetAttribute("EntryPoint") -eq '$targetentrypoint$') {
        $node.SetAttribute("EntryPoint", "Windows.FullTrustApplication")
    }
}

# The appx manifest schema is order-sensitive, and the Windows App SDK
# expects UTF-8 with a byte-order mark for a packed package.
$xmlSettings = New-Object System.Xml.XmlWriterSettings
$xmlSettings.Indent = $false
$xmlSettings.Encoding = New-Object System.Text.UTF8Encoding($true)

$writer = [System.Xml.XmlWriter]::Create($manifestPathInRoot, $xmlSettings)
try { $xml.Save($writer) } finally { $writer.Close() }

Write-Host "Manifest identity: $IdentityName / CN=$PublisherCN / $processorArchitecture"

# ---------------------------------------------------------------------------
# Certificate.
#
# The password is never defaulted. It comes from the parameter or from the
# environment, and the script asks for it interactively when neither is
# set.
# ---------------------------------------------------------------------------

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

if ([string]::IsNullOrWhiteSpace($env:SYSTEMMONITOR_CERT_PASSWORD)) {
    $securePwd = if ($CertPassword) { $CertPassword } else { Read-Host "Password for the signing certificate (input is not echoed)" -AsSecureString }
}
else {
    $securePwd = ConvertTo-SecureString -String $env:SYSTEMMONITOR_CERT_PASSWORD -Force -AsPlainText
}

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePwd | Out-Null

# The password is needed in plain text for signtool's /p switch. Keep it
# in a variable that is cleared as soon as the signing is done.
$plainPassword = [System.Net.NetworkCredential]::new("", $securePwd).Password

# ---------------------------------------------------------------------------
# Locate the SDK tools.
# ---------------------------------------------------------------------------

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
if (-not $makeappx) { Fail "makeappx.exe not found. Install the Windows SDK." }
if (-not $signtool) { Fail "signtool.exe not found. Install the Windows SDK." }

$msixName = "SystemMonitor.msix"
$msixPath = Join-Path $msixOutDir $msixName

Write-Host "Packing $msixPath ..."
& "$makeappx" pack /d $packageRoot /p $msixPath
if ($LASTEXITCODE -ne 0) { Fail "makeappx failed." }

try {
    & "$signtool" sign /fd SHA256 /f $pfxPath /p $plainPassword /tr http://timestamp.digicert.com /td SHA256 $msixPath
    if ($LASTEXITCODE -ne 0) { Fail "signtool failed." }
}
finally {
    $plainPassword = $null
    $securePwd = $null
}

Write-Host "MSIX created: $msixPath"
Write-Host "PFX created:  $pfxPath"
Write-Host ""
Write-Host "The PFX contains a private key. It is not committed to the repository."
Write-Host "Install the public certificate with build/install-cert.ps1 before installing the package."

# Builds the DexTor OS launcher as an MSIX package for Microsoft Store.
#
# Store upload (unsigned - the Store signs it after certification):
#   .\scripts\build-store-package.ps1 -Version 0.1.40
# The identity defaults below are copied from Partner Center > DexTor OS > Product management >
# Product identity; the build refuses to make an upload with an empty or placeholder value.
#
# Local test package, signed with the self-signed test certificate
# (scripts\new-test-signing-certificate.ps1; created on first use):
#   .\scripts\build-store-package.ps1 -Version 0.1.40 -TestSigned
# Its Identity Publisher is the test certificate's subject, as signing demands, so it is
# not the package that goes to the Store. Installing it needs the certificate in
# Local Machine > Trusted People (an administrator decision, not done here).
#
# Output: out\store\<version>\
#   layout\                          the package contents (launcher, runtime\qemu, Assets, manifest)
#   DexTorOS_<version>_x64.msix      unsigned Store package
#   DexTorOS_<version>_x64.msixupload  the same, as Partner Center's upload format
#   test\DexTorOS-Test_<version>_x64.msix  (with -TestSigned) locally signed test package
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0',

    # Partner Center > DexTor OS > Product management > Product identity (23 Oct 2026).
    [string]$IdentityName = 'DexTorOS.DexTorOS',
    [string]$Publisher = 'CN=49F632C2-9124-4DA8-BD5B-58D93636480D',
    [string]$PublisherDisplayName = 'DexTor OS',

    # A prepared QEMU payload (scripts\prepare-qemu-payload.ps1 output). Default:
    # out\qemu-runtime\qemu-<version from runtime\qemu-manifest.json>, prepared if missing.
    [string]$QemuRuntimePath,

    [string]$MaxVersionTested = '10.0.26200.0',

    # Adds the restricted allowElevation capability (see the manifest template).
    [switch]$AllowElevation,

    # Also builds a test package signed with the local test certificate.
    [switch]$TestSigned,
    [string]$CertificateThumbprint,

    # Builds only the signed test package; the Store identity is not needed then.
    [switch]$TestOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$storeRoot = Join-Path $repoRoot 'packaging\DexTor.Store'
$outRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot "out\store\$Version"))
$layout = Join-Path $outRoot 'layout'
$packageVersion = "$Version.0"

if ($TestOnly) { $TestSigned = $true }

# --- Identity -----------------------------------------------------------------------------
$placeholderPattern = '^\s*$|PLACEHOLDER|<|>|\$\('
function Test-Placeholder([string]$Value) { return [string]::IsNullOrWhiteSpace($Value) -or $Value -match $placeholderPattern }

if (-not $TestOnly) {
    $missing = @()
    if (Test-Placeholder $IdentityName) { $missing += '-IdentityName (Package/Identity/Name)' }
    if (Test-Placeholder $Publisher) { $missing += '-Publisher (Package/Identity/Publisher, "CN=...")' }
    if (Test-Placeholder $PublisherDisplayName) { $missing += '-PublisherDisplayName (Package/Properties/PublisherDisplayName)' }
    if ($missing.Count -gt 0) {
        throw ("A Store upload needs the package identity from Partner Center > DexTor OS > Product management > Product identity. Missing: " +
            ($missing -join ', ') + '. For a local test package only, run with -TestOnly.')
    }
    if ($Publisher -notmatch '^CN=') {
        throw "-Publisher must be the full distinguished name from Partner Center, starting with CN=: $Publisher"
    }
    if ($IdentityName -notmatch '^[A-Za-z0-9.-]{3,50}$') {
        throw "-IdentityName is not a valid package name (3-50 letters, digits, dots, dashes): $IdentityName"
    }
}

# --- Tools --------------------------------------------------------------------------------
function Find-SdkTool([string]$Name) {
    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Directory |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\$Name" } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1
        if ($candidate) { return $candidate }
    }
    # No Windows SDK: the Microsoft.Windows.SDK.BuildTools package that get-signtool.ps1
    # unpacks carries makeappx and makepri beside signtool.
    $signTool = & (Join-Path $PSScriptRoot 'get-signtool.ps1') | Select-Object -Last 1
    $tool = Join-Path (Split-Path -Parent $signTool) $Name
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) {
        throw "$Name was not found in the Windows SDK or the SDK build tools package."
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $tool
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
        throw "$Name is not signed by Microsoft: $($signature.Status)"
    }
    return $tool
}

function Invoke-Checked([string]$FilePath, [string[]]$ArgumentList) {
    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) { throw "$(Split-Path -Leaf $FilePath) failed with exit code $LASTEXITCODE." }
}

$makeAppx = Find-SdkTool 'makeappx.exe'
$makePri = Find-SdkTool 'makepri.exe'

# --- QEMU payload -------------------------------------------------------------------------
$qemuVersion = [string](Get-Content -LiteralPath (Join-Path $repoRoot 'runtime\qemu-manifest.json') -Raw | ConvertFrom-Json).runtime.version
if ([string]::IsNullOrWhiteSpace($QemuRuntimePath)) {
    $QemuRuntimePath = Join-Path $repoRoot "out\qemu-runtime\qemu-$qemuVersion"
    if (-not (Test-Path -LiteralPath (Join-Path $QemuRuntimePath 'qemu-runtime-files.json'))) {
        Write-Host "Preparing the QEMU $qemuVersion payload..."
        & (Join-Path $PSScriptRoot 'prepare-qemu-payload.ps1') -OutputPath $QemuRuntimePath
    }
}
$QemuRuntimePath = [System.IO.Path]::GetFullPath($QemuRuntimePath)
$qemuManifestPath = Join-Path $QemuRuntimePath 'qemu-runtime-files.json'
if (-not (Test-Path -LiteralPath $qemuManifestPath -PathType Leaf)) {
    throw "Not a prepared QEMU payload (no qemu-runtime-files.json): $QemuRuntimePath"
}
$qemuManifest = Get-Content -LiteralPath $qemuManifestPath -Raw | ConvertFrom-Json
if ([string]$qemuManifest.version -ne $qemuVersion) {
    throw "The QEMU payload is version $($qemuManifest.version); the launcher expects $qemuVersion."
}
Write-Host "Verifying the QEMU payload ($($qemuManifest.fileCount) files)..."
$qemuPrefix = $QemuRuntimePath + [System.IO.Path]::DirectorySeparatorChar
foreach ($file in $qemuManifest.files) {
    $candidate = [System.IO.Path]::GetFullPath((Join-Path $QemuRuntimePath ([string]$file.path)))
    if (-not $candidate.StartsWith($qemuPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "QEMU payload manifest names a path outside its root: $($file.path)"
    }
    if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne ([string]$file.sha256)) {
        throw "QEMU payload file does not match its SHA-256: $($file.path)"
    }
}

# --- Layout -------------------------------------------------------------------------------
if (Test-Path -LiteralPath $outRoot) {
    Remove-Item -LiteralPath $outRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $layout -Force | Out-Null

Write-Host "Publishing the launcher $Version (self-contained, win-x64)..."
# Not single-file: a package is installed, not carried around, and a plain layout starts
# without unpacking anything into %TEMP%. The USB provisioner is the launcher itself,
# restarted elevated (UsbProvisionerClient), so no second executable is needed.
Invoke-Checked 'dotnet' @(
    'publish', (Join-Path $repoRoot 'src\DexTor.Launcher\DexTor.Launcher.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-o', $layout,
    '-p:PublishReadyToRun=true', '-p:PublishSingleFile=false',
    '-p:DebugType=None', '-p:DebugSymbols=false',
    "-p:Version=$Version", "-p:VersionPrefix=$Version",
    "-p:AssemblyVersion=$Version.0", "-p:FileVersion=$Version.0",
    '-p:DexTorChannel=release'
)

$qemuDestination = Join-Path $layout 'runtime\qemu'
New-Item -ItemType Directory -Path $qemuDestination -Force | Out-Null
Copy-Item -Path (Join-Path $QemuRuntimePath '*') -Destination $qemuDestination -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\THIRD-PARTY-NOTICES.md') -Destination $layout
# The terms the Store build is used under; Partner Center's "additional license terms" link here too.
Copy-Item -LiteralPath (Join-Path $repoRoot 'publish\overlay\EULA.md') -Destination $layout

$assetsSource = Join-Path $storeRoot 'Assets'
if (-not (Get-ChildItem -LiteralPath $assetsSource -Filter 'Square44x44Logo.targetsize-*.png' -ErrorAction SilentlyContinue)) {
    throw "The visual assets are missing; run scripts\new-store-assets.ps1: $assetsSource"
}

$template = Get-Content -LiteralPath (Join-Path $storeRoot 'AppxManifest.template.xml') -Raw
# The template's explanatory header is for people reading the repository, not the package.
$template = [regex]::Replace($template, '(?s)^(<\?xml[^>]*\?>\s*)<!--.*?-->\s*', '$1')

function New-PackageLayout {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$PackagePublisher,
        [Parameter(Mandatory)][string]$DisplayPublisher
    )

    $extra = if ($AllowElevation) { '    <rescap:Capability Name="allowElevation" />' } else { '' }
    $escape = { param($text) [System.Security.SecurityElement]::Escape($text) }
    $manifest = $template.
        Replace('$(IdentityName)', (& $escape $Name)).
        Replace('$(Publisher)', (& $escape $PackagePublisher)).
        Replace('$(PublisherDisplayName)', (& $escape $DisplayPublisher)).
        Replace('$(Version)', $packageVersion).
        Replace('$(MaxVersionTested)', $MaxVersionTested).
        Replace('$(ExtraCapabilities)', $extra)
    if ($manifest -match '\$\(') { throw 'A token was left unreplaced in AppxManifest.xml.' }
    [System.IO.File]::WriteAllText((Join-Path $Root 'AppxManifest.xml'), $manifest, (New-Object System.Text.UTF8Encoding $false))

    # resources.pri maps Assets\Square44x44Logo.png to its scale- and targetsize- variants.
    # It is built from the assets alone: indexing the whole layout would make resources of
    # QEMU's keymaps and firmware files.
    $priRoot = Join-Path $outRoot ("pri-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $priRoot 'Assets') -Force | Out-Null
    Copy-Item -Path (Join-Path $assetsSource '*.png') -Destination (Join-Path $priRoot 'Assets')
    $priConfig = $priRoot + '.priconfig.xml'
    Invoke-Checked $makePri @('createconfig', '/cf', $priConfig, '/dq', 'en-US', '/pv', '10.0.0', '/o')
    # One resources.pri for the one package: the default config splits scales into resource
    # packs, which only an .msixbundle carries.
    $config = [xml](Get-Content -LiteralPath $priConfig -Raw)
    $packaging = $config.SelectSingleNode('/resources/packaging')
    if ($packaging) { [void]$packaging.ParentNode.RemoveChild($packaging) }
    $config.Save($priConfig)
    Invoke-Checked $makePri @('new', '/pr', $priRoot, '/cf', $priConfig, '/mn', (Join-Path $Root 'AppxManifest.xml'), '/of', (Join-Path $Root 'resources.pri'), '/o')
    Remove-Item -LiteralPath $priConfig -Force

    $assetsTarget = Join-Path $Root 'Assets'
    New-Item -ItemType Directory -Path $assetsTarget -Force | Out-Null
    Copy-Item -Path (Join-Path $assetsSource '*.png') -Destination $assetsTarget
    Remove-Item -LiteralPath $priRoot -Recurse -Force
}

function New-Msix([string]$Root, [string]$Path) {
    Write-Host "Packing $(Split-Path -Leaf $Path)..."
    # makeappx lists every file it packs; that is shown only when packing fails.
    $output = & $makeAppx pack /d $Root /p $Path /o 2>&1
    if ($LASTEXITCODE -ne 0) {
        $output | Write-Host
        throw "makeappx failed with exit code $LASTEXITCODE."
    }
}

$built = @()
if (-not $TestOnly) {
    New-PackageLayout -Root $layout -Name $IdentityName -PackagePublisher $Publisher -DisplayPublisher $PublisherDisplayName
    $msix = Join-Path $outRoot "DexTorOS_$($packageVersion)_x64.msix"
    New-Msix $layout $msix
    # .msixupload is a zip holding the package (and, if there were any, its symbols).
    $upload = Join-Path $outRoot "DexTorOS_$($packageVersion)_x64.msixupload"
    $zip = $upload + '.zip'
    Compress-Archive -LiteralPath $msix -DestinationPath $zip -CompressionLevel Optimal -Force
    Move-Item -LiteralPath $zip -Destination $upload -Force
    $built += $msix, $upload
}

if ($TestSigned) {
    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        $CertificateThumbprint = & (Join-Path $PSScriptRoot 'new-test-signing-certificate.ps1') | Select-Object -Last 1
    }
    $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object Thumbprint -eq $CertificateThumbprint | Select-Object -First 1
    if (-not $certificate) { throw "Certificate $CertificateThumbprint is not in CurrentUser\My." }

    $testRoot = Join-Path $outRoot 'test'
    $testLayout = Join-Path $testRoot 'layout'
    New-Item -ItemType Directory -Path $testLayout -Force | Out-Null
    # The same files; only the manifest differs (its Publisher must equal the signer).
    Get-ChildItem -LiteralPath $layout -Force | Where-Object { $_.Name -notin @('AppxManifest.xml', 'resources.pri', 'Assets') } |
        Copy-Item -Destination $testLayout -Recurse -Force
    $testName = if (Test-Placeholder $IdentityName) { 'DexTorOS.LocalTest' } else { "$IdentityName.LocalTest" }
    New-PackageLayout -Root $testLayout -Name $testName -PackagePublisher $certificate.Subject -DisplayPublisher 'DexTor OS (test)'
    $testMsix = Join-Path $testRoot "DexTorOS-Test_$($packageVersion)_x64.msix"
    New-Msix $testLayout $testMsix

    $signTool = Find-SdkTool 'signtool.exe'
    Invoke-Checked $signTool @('sign', '/fd', 'SHA256', '/sha1', $CertificateThumbprint, $testMsix)
    $signature = Get-AuthenticodeSignature -LiteralPath $testMsix
    if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) {
        throw "The test package is not signed with $CertificateThumbprint."
    }
    $built += $testMsix
}

Write-Host ''
Write-Host "Package version: $packageVersion"
foreach ($path in $built) {
    Write-Host ("  {0}  ({1:N0} MB)" -f $path, ((Get-Item -LiteralPath $path).Length / 1MB))
}
if (-not $TestOnly) {
    Write-Host ''
    Write-Host 'Upload the .msixupload in Partner Center > DexTor OS > Packages. Do not sign it: the Store signs it after certification.'
}

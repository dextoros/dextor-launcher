[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0',

    # UnsignedTest: no signature. TestSigned: signed with the self-signed test certificate
    # (scripts\new-test-signing-certificate.ps1), for test PCs that trust it.
    # SignedRelease: signed with a certificate from a trusted authority, for the public.
    [ValidateSet('UnsignedTest', 'TestSigned', 'SignedRelease')]
    [string]$Mode = 'UnsignedTest',

    [string]$QemuRuntimePath,

    [string]$CertificateThumbprint,

    [string]$TimestampUrl = 'http://timestamp.acs.microsoft.com',

    [switch]$SkipMsiValidation
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releaseBase = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'out\release'))
$releaseRoot = [System.IO.Path]::GetFullPath((Join-Path $releaseBase $Version))
$publishDir = Join-Path $releaseRoot 'app'
$packageDir = Join-Path $releaseRoot 'packages'
$wixBuildDir = Join-Path $releaseRoot 'wix-build'
$installerProject = Join-Path $repoRoot 'packaging\DexTor.Installer\DexTor.Installer.wixproj'
$setupProject = Join-Path $repoRoot 'packaging\DexTor.Setup\DexTor.Setup.wixproj'
$noticesPath = Join-Path $repoRoot 'packaging\THIRD-PARTY-NOTICES.md'

if (-not $releaseRoot.StartsWith($releaseBase + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Resolved release directory is outside the expected output root: $releaseRoot"
}

if ($Mode -eq 'SignedRelease') {
    if ($SkipMsiValidation) {
        throw 'SignedRelease cannot skip MSI ICE validation.'
    }

    if ((Get-Content -LiteralPath $noticesPath -Raw).Contains('not yet sufficient for public distribution', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'SignedRelease is blocked until THIRD-PARTY-NOTICES.md contains the reviewed QEMU and dependency notices/source availability statement.'
    }

    if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
        throw 'SignedRelease requires -CertificateThumbprint. Managed Artifact Signing integration will replace this local certificate-store path.'
    }

    # QEMU is not bundled: the launcher downloads and verifies the pinned runtime itself,
    # so the installer stays small. -QemuRuntimePath remains available but optional.
}

if ($Mode -eq 'TestSigned' -and [string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    throw 'TestSigned requires -CertificateThumbprint. Create the test certificate with scripts\new-test-signing-certificate.ps1.'
}

if ($QemuRuntimePath) {
    $QemuRuntimePath = [System.IO.Path]::GetFullPath($QemuRuntimePath)
    if (-not (Test-Path -LiteralPath $QemuRuntimePath -PathType Container)) {
        throw "QEMU runtime directory was not found: $QemuRuntimePath"
    }

    if (-not (Test-Path -LiteralPath (Join-Path $QemuRuntimePath 'qemu-system-x86_64.exe') -PathType Leaf)) {
        throw "QEMU runtime is missing qemu-system-x86_64.exe: $QemuRuntimePath"
    }

    $qemuPayloadManifestPath = Join-Path $QemuRuntimePath 'qemu-runtime-files.json'
    if (-not (Test-Path -LiteralPath $qemuPayloadManifestPath -PathType Leaf)) {
        throw "QEMU runtime is missing qemu-runtime-files.json. Prepare it with scripts\prepare-qemu-payload.ps1: $QemuRuntimePath"
    }

    $qemuPayloadManifest = Get-Content -LiteralPath $qemuPayloadManifestPath -Raw | ConvertFrom-Json
    $qemuRuntimePrefix = $QemuRuntimePath + [System.IO.Path]::DirectorySeparatorChar
    foreach ($file in $qemuPayloadManifest.files) {
        $candidatePath = [System.IO.Path]::GetFullPath((Join-Path $QemuRuntimePath ([string]$file.path)))
        if (-not $candidatePath.StartsWith($qemuRuntimePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "QEMU payload manifest contains a path outside its runtime root: $($file.path)"
        }
        if (-not (Test-Path -LiteralPath $candidatePath -PathType Leaf)) {
            throw "QEMU payload file is missing: $($file.path)"
        }
        $actualFileHash = (Get-FileHash -LiteralPath $candidatePath -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actualFileHash -ne ([string]$file.sha256).ToLowerInvariant()) {
            throw "QEMU payload SHA-256 mismatch: $($file.path)"
        }
    }
}

if (Test-Path -LiteralPath $releaseRoot) {
    Remove-Item -LiteralPath $releaseRoot -Recurse -Force
}

New-Item -ItemType Directory -Path $publishDir, $packageDir, $wixBuildDir -Force | Out-Null

function Invoke-Checked {
    param(
        [Parameter(Mandatory)]
        [string]$FilePath,

        [Parameter(Mandatory)]
        [string[]]$ArgumentList
    )

    & $FilePath @ArgumentList
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path -LiteralPath $kitsRoot) {
        $candidate = Get-ChildItem -LiteralPath $kitsRoot -Directory |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
            Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
            Select-Object -First 1

        if ($candidate) {
            return $candidate
        }
    }

    # No Windows SDK: take Microsoft's signtool from its NuGet package into out\tools.
    $fetched = & (Join-Path $PSScriptRoot 'get-signtool.ps1') | Select-Object -Last 1
    if ($fetched -and (Test-Path -LiteralPath $fetched -PathType Leaf)) {
        return $fetched
    }

    throw 'SignTool.exe was not found and could not be fetched.'
}

function Add-AuthenticodeSignature {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$SignToolPath,

        [switch]$Append
    )

    $arguments = @(
        'sign', '/sha1', $CertificateThumbprint,
        '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256',
        '/d', 'DexTor OS', '/du', 'https://dextoros.com'
    )
    if ($Append) {
        $arguments += '/as'
    }
    $arguments += $Path
    Invoke-Checked $SignToolPath $arguments
}

function Assert-AuthenticodeSignature {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$SignToolPath
    )

    if ($Mode -eq 'TestSigned') {
        # The test root is trusted only on test PCs, so a chain check here would fail by
        # design. What must hold is that the file carries our certificate's signature.
        $signature = Get-AuthenticodeSignature -LiteralPath $Path
        # Files the runtime ships already signed by Microsoft keep that signature.
        if ($signature.Status -eq 'Valid' -and $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) {
            return
        }
        if ($null -eq $signature.SignerCertificate -or $signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint) {
            throw "Not signed with the test certificate: $Path"
        }
        if ($signature.Status -notin @('Valid', 'UnknownError', 'NotTrusted')) {
            throw "Test signature is broken ($($signature.Status)): $Path"
        }
        return
    }

    Invoke-Checked $SignToolPath @('verify', '/pa', '/all', '/v', $Path)
}

function Assert-WindowsGuiExecutable {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $bytes = [System.IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -lt 512) {
        throw "Executable is too small to contain a valid PE header: $Path"
    }

    $peOffset = [System.BitConverter]::ToInt32($bytes, 0x3c)
    $subsystemOffset = $peOffset + 24 + 68
    if ($peOffset -lt 0 -or $subsystemOffset + 2 -gt $bytes.Length) {
        throw "Executable has an invalid PE header: $Path"
    }

    $windowsGuiSubsystem = 2
    $actualSubsystem = [System.BitConverter]::ToUInt16($bytes, $subsystemOffset)
    if ($actualSubsystem -ne $windowsGuiSubsystem) {
        throw "Launcher must use the Windows GUI subsystem and must not open a console window. Actual subsystem: $actualSubsystem"
    }
}

function Assert-MsiProductVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$ExpectedVersion
    )

    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $installer.GetType().InvokeMember(
        'OpenDatabase',
        'InvokeMethod',
        $null,
        $installer,
        @([System.IO.Path]::GetFullPath($Path), 0))
    $view = $database.GetType().InvokeMember(
        'OpenView',
        'InvokeMethod',
        $null,
        $database,
        @("SELECT `Value` FROM `Property` WHERE `Property`='ProductVersion'"))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
    $record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    $actualVersion = $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, 1)
    # The database stays open - and the MSI locked against signing and deletion - until
    # every COM object on it is released, so they are released here, not left to the GC.
    $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
    foreach ($comObject in @($record, $view, $database, $installer)) {
        [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject)
    }
    [System.GC]::Collect()
    [System.GC]::WaitForPendingFinalizers()
    if ($actualVersion -ne $ExpectedVersion) {
        throw "MSI ProductVersion mismatch. Expected $ExpectedVersion, found $actualVersion."
    }
}

function Assert-PeProductVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$ExpectedVersion
    )

    $actualVersion = (Get-Item -LiteralPath $Path).VersionInfo.ProductVersion
    if (-not $actualVersion.StartsWith($ExpectedVersion, [System.StringComparison]::Ordinal)) {
        throw "PE ProductVersion mismatch for $Path. Expected $ExpectedVersion, found $actualVersion."
    }
}

Write-Host "[1/7] Running repository checks..."
& (Join-Path $repoRoot 'scripts\check.ps1')
if ($LASTEXITCODE -ne 0) {
    throw "Repository checks failed with exit code $LASTEXITCODE."
}

Write-Host "[2/7] Publishing self-contained Windows x64 application..."
Invoke-Checked 'dotnet' @(
    'publish', (Join-Path $repoRoot 'src\DexTor.Launcher\DexTor.Launcher.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-o', $publishDir,
    # Precompiled: the first start after installing took about two seconds before the
    # window appeared, most of it compiling the launcher on a cold disk.
    '-p:PublishReadyToRun=true',
    '-p:PublishSingleFile=false', '-p:DebugType=None', '-p:DebugSymbols=false',
    "-p:Version=$Version", "-p:VersionPrefix=$Version",
    "-p:AssemblyVersion=$Version.0", "-p:FileVersion=$Version.0",
    "-p:DexTorChannel=$(if ($Mode -eq 'SignedRelease') { 'release' } else { 'test' })"
)
Assert-WindowsGuiExecutable -Path (Join-Path $publishDir 'DexTor.Launcher.exe')
Invoke-Checked 'dotnet' @(
    'publish', (Join-Path $repoRoot 'src\DexTor.Provisioner\DexTor.Provisioner.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-o', $publishDir,
    '-p:PublishSingleFile=false', '-p:DebugType=None', '-p:DebugSymbols=false',
    "-p:Version=$Version", "-p:VersionPrefix=$Version",
    "-p:AssemblyVersion=$Version.0", "-p:FileVersion=$Version.0",
    "-p:DexTorChannel=$(if ($Mode -eq 'SignedRelease') { 'release' } else { 'test' })"
)

Copy-Item -LiteralPath $noticesPath -Destination (Join-Path $publishDir 'THIRD-PARTY-NOTICES.md')

if ($QemuRuntimePath) {
    $qemuDestination = Join-Path $publishDir 'runtime\qemu'
    New-Item -ItemType Directory -Path $qemuDestination -Force | Out-Null
    Copy-Item -Path (Join-Path $QemuRuntimePath '*') -Destination $qemuDestination -Recurse -Force
} else {
    Write-Warning 'Building an installer without bundled QEMU. This output is for installer testing only.'
}

$signToolPath = $null
if ($Mode -ne 'UnsignedTest') {
    Write-Host "[3/7] Signing and verifying application payload..."
    $signToolPath = Find-SignTool
    $peFiles = Get-ChildItem -LiteralPath $publishDir -Recurse -File |
        Where-Object { $_.Extension -in @('.exe', '.dll') }

    foreach ($file in $peFiles) {
        $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
        if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
            Add-AuthenticodeSignature `
                -Path $file.FullName `
                -SignToolPath $signToolPath `
                -Append:($null -ne $signature.SignerCertificate)
        }
        Assert-AuthenticodeSignature -Path $file.FullName -SignToolPath $signToolPath
    }
} else {
    Write-Host "[3/7] Skipping signing for the explicit UnsignedTest build."
}

Write-Host "[4/7] Building MSI..."
$msiBuildDir = Join-Path $wixBuildDir 'msi'
New-Item -ItemType Directory -Path $msiBuildDir -Force | Out-Null
$msiBuildArguments = @(
    'build', $installerProject, '-c', 'Release',
    "-p:ProductVersion=$Version", "-p:PayloadDir=$publishDir",
    "-p:Version=$Version", "-p:VersionPrefix=$Version", "-p:FileVersion=$Version.0",
    '--no-incremental',
    "-p:OutputPath=$msiBuildDir"
)
if ($SkipMsiValidation) {
    $msiBuildArguments += '-p:SuppressValidation=true'
}
Invoke-Checked 'dotnet' $msiBuildArguments

$builtMsiPath = Join-Path $msiBuildDir 'DexTorOS-x64.msi'
if (-not (Test-Path -LiteralPath $builtMsiPath -PathType Leaf)) {
    throw "Expected MSI was not created: $builtMsiPath"
}
$msiPath = Join-Path $packageDir 'DexTorOS-x64.msi'
Copy-Item -LiteralPath $builtMsiPath -Destination $msiPath
Assert-MsiProductVersion -Path $msiPath -ExpectedVersion $Version

if ($Mode -ne 'UnsignedTest') {
    Add-AuthenticodeSignature -Path $msiPath -SignToolPath $signToolPath
    Assert-AuthenticodeSignature -Path $msiPath -SignToolPath $signToolPath
}

Write-Host "[5/7] Building Setup bundle..."
$bundleBuildDir = Join-Path $wixBuildDir 'bundle'
New-Item -ItemType Directory -Path $bundleBuildDir -Force | Out-Null
$bundleBuildArguments = @(
    'build', $setupProject, '-c', 'Release',
    "-p:ProductVersion=$Version", "-p:MsiPath=$msiPath",
    "-p:Version=$Version", "-p:VersionPrefix=$Version", "-p:FileVersion=$Version.0",
    '--no-incremental',
    "-p:OutputPath=$bundleBuildDir"
)
if ($Mode -ne 'UnsignedTest') {
    $bundleBuildArguments += @(
        '-p:SignOutput=true',
        "-p:SignToolPath=$signToolPath",
        "-p:CertificateThumbprint=$CertificateThumbprint",
        "-p:TimestampUrl=$TimestampUrl"
    )
}
Invoke-Checked 'dotnet' $bundleBuildArguments

$builtSetupPath = Join-Path $bundleBuildDir 'DexTorOS-Setup-x64.exe'
if (-not (Test-Path -LiteralPath $builtSetupPath -PathType Leaf)) {
    throw "Expected Setup bundle was not created: $builtSetupPath"
}
$setupPath = Join-Path $packageDir 'DexTorOS-Setup-x64.exe'
Copy-Item -LiteralPath $builtSetupPath -Destination $setupPath
Assert-PeProductVersion -Path $setupPath -ExpectedVersion $Version

if ($Mode -ne 'UnsignedTest') {
    Assert-AuthenticodeSignature -Path $setupPath -SignToolPath $signToolPath
}

Write-Host "[6/7] Writing checksums..."
$checksumLines = Get-ChildItem -LiteralPath $packageDir -File |
    Where-Object { $_.Extension -in @('.exe', '.msi') } |
    Sort-Object Name |
    ForEach-Object {
        $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
        "$($hash.Hash.ToLowerInvariant())  $($_.Name)"
    }
$checksumLines | Set-Content -LiteralPath (Join-Path $packageDir 'SHA256SUMS.txt') -Encoding utf8

$bundledQemuManifestPath = Join-Path $publishDir 'runtime\qemu\qemu-runtime-files.json'
$bundledQemuManifest = if (Test-Path -LiteralPath $bundledQemuManifestPath) {
    Get-Content -LiteralPath $bundledQemuManifestPath -Raw | ConvertFrom-Json
} else {
    $null
}
$releaseManifest = [ordered]@{
    schemaVersion = 1
    version = $Version
    channel = 'beta'
    architecture = 'x64'
    productionSigned = $Mode -eq 'SignedRelease'
    testSigned = $Mode -eq 'TestSigned'
    qemuBundled = -not [string]::IsNullOrWhiteSpace($QemuRuntimePath)
    qemuVersion = if ($bundledQemuManifest) { [string]$bundledQemuManifest.version } else { $null }
    qemuPayloadManifestSha256 = if ($bundledQemuManifest) {
        (Get-FileHash -LiteralPath $bundledQemuManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    } else {
        $null
    }
    artifacts = @(
        Get-ChildItem -LiteralPath $packageDir -File |
            Where-Object { $_.Extension -in @('.exe', '.msi') } |
            Sort-Object Name |
            ForEach-Object {
                $hash = Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
                [ordered]@{
                    fileName = $_.Name
                    size = $_.Length
                    sha256 = $hash.Hash.ToLowerInvariant()
                }
            }
    )
}
$releaseManifest |
    ConvertTo-Json -Depth 5 |
    Set-Content -LiteralPath (Join-Path $packageDir 'release-manifest.json') -Encoding utf8

Write-Host "[7/7] Release build completed."
Write-Host "Mode: $Mode"
Write-Host "Output: $packageDir"
if ($Mode -eq 'UnsignedTest') {
    Write-Warning 'UnsignedTest artifacts are not public releases and will trigger Windows trust warnings.'
}

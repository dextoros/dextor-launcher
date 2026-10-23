[CmdletBinding()]
param(
    [string]$SourceRuntimePath,

    [string]$OutputPath,

    [switch]$ForceDownload
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'out'))
$manifestPath = Join-Path $repoRoot 'runtime\qemu-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$runtime = $manifest.runtime
$version = [string]$runtime.version
$expectedInstallerHash = ([string]$runtime.installerSha512).ToLowerInvariant()
$downloadRoot = Join-Path $outRoot 'qemu-download-cache'
$extractRoot = Join-Path $outRoot "qemu-extract\qemu-$version"
$installerPath = Join-Path $downloadRoot "qemu-w64-setup-$version.exe"
# Which URL the cached installer came from, recorded as binarySourceUrl in the payload.
$installerSourcePath = $installerPath + '.source-url'
$binarySourceUrl = $null

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $outRoot "qemu-runtime\qemu-$version"
}
$OutputPath = [System.IO.Path]::GetFullPath($OutputPath)

function Assert-OutputPath {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($outRoot + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Generated QEMU paths must stay under the repository out directory: $fullPath"
    }
}

function Reset-GeneratedDirectory {
    param([Parameter(Mandatory)][string]$Path)

    Assert-OutputPath $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

Assert-OutputPath $OutputPath
Assert-OutputPath $downloadRoot
Assert-OutputPath $extractRoot

if ([string]::IsNullOrWhiteSpace($SourceRuntimePath)) {
    New-Item -ItemType Directory -Path $downloadRoot -Force | Out-Null
    $downloadRequired = $ForceDownload -or -not (Test-Path -LiteralPath $installerPath -PathType Leaf)
    if (-not $downloadRequired) {
        $cachedHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA512).Hash.ToLowerInvariant()
        $downloadRequired = $cachedHash -ne $expectedInstallerHash
    }

    if ($downloadRequired) {
        # DexTor's mirror first, then the fallbacks, as the launcher does. Every URL serves
        # the same pinned file, so each download is checked against the same SHA-512.
        $candidateUrls = @([string]$runtime.sourceUrl)
        if ($runtime.PSObject.Properties.Name -contains 'fallbackSourceUrls') {
            $candidateUrls += @($runtime.fallbackSourceUrls | ForEach-Object { [string]$_ })
        }
        $candidateUrls = @($candidateUrls | Where-Object { $_ } | Select-Object -Unique)
        foreach ($url in $candidateUrls) {
            if (-not $url.StartsWith('https://', [System.StringComparison]::Ordinal)) {
                throw "QEMU download URLs must use HTTPS: $url"
            }
        }

        $temporaryInstaller = $installerPath + '.tmp'
        $failures = @()
        foreach ($url in $candidateUrls) {
            if (Test-Path -LiteralPath $temporaryInstaller) {
                Remove-Item -LiteralPath $temporaryInstaller -Force
            }
            Write-Host "Downloading pinned QEMU $version installer from $url ..."
            try {
                Invoke-WebRequest -Uri $url -OutFile $temporaryInstaller -UseBasicParsing -MaximumRedirection 5
            } catch {
                $failures += "${url}: $($_.Exception.Message)"
                Write-Warning "Could not download from ${url}: $($_.Exception.Message)"
                continue
            }
            $downloadedHash = (Get-FileHash -LiteralPath $temporaryInstaller -Algorithm SHA512).Hash.ToLowerInvariant()
            if ($downloadedHash -ne $expectedInstallerHash) {
                Remove-Item -LiteralPath $temporaryInstaller -Force
                $failures += "${url}: SHA-512 mismatch, received $downloadedHash"
                Write-Warning "QEMU installer from $url failed SHA-512 verification (received $downloadedHash)."
                continue
            }
            Move-Item -LiteralPath $temporaryInstaller -Destination $installerPath -Force
            $binarySourceUrl = $url
            break
        }
        if (-not $binarySourceUrl) {
            if (Test-Path -LiteralPath $temporaryInstaller) {
                Remove-Item -LiteralPath $temporaryInstaller -Force
            }
            throw "No QEMU installer URL produced the pinned SHA-512 $expectedInstallerHash.`n$($failures -join "`n")"
        }
        Set-Content -LiteralPath $installerSourcePath -Value $binarySourceUrl -Encoding utf8
    }

    $installerHash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA512).Hash.ToLowerInvariant()
    if ($installerHash -ne $expectedInstallerHash) {
        throw 'Cached QEMU installer failed SHA-512 verification.'
    }
    if (-not $binarySourceUrl -and (Test-Path -LiteralPath $installerSourcePath -PathType Leaf)) {
        $binarySourceUrl = (Get-Content -LiteralPath $installerSourcePath -Raw).Trim()
    }

    Reset-GeneratedDirectory $extractRoot
    Write-Host 'Extracting QEMU into an isolated staging directory...'
    $process = Start-Process -FilePath $installerPath -ArgumentList '/S', "/D=$extractRoot" -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) {
        throw "QEMU installer extraction failed with exit code $($process.ExitCode)."
    }
    $SourceRuntimePath = $extractRoot
} else {
    $SourceRuntimePath = [System.IO.Path]::GetFullPath($SourceRuntimePath)
}

if (-not (Test-Path -LiteralPath $SourceRuntimePath -PathType Container)) {
    throw "QEMU source runtime directory was not found: $SourceRuntimePath"
}
if (-not (Test-Path -LiteralPath (Join-Path $SourceRuntimePath 'qemu-system-x86_64.exe') -PathType Leaf)) {
    throw "QEMU source runtime is missing qemu-system-x86_64.exe: $SourceRuntimePath"
}
if (-not (Test-Path -LiteralPath (Join-Path $SourceRuntimePath 'share') -PathType Container)) {
    throw "QEMU source runtime is missing its share/firmware directory: $SourceRuntimePath"
}

Reset-GeneratedDirectory $OutputPath
$includedExecutables = @($runtime.payloadPolicy.includedExecutables | ForEach-Object { ([string]$_).ToLowerInvariant() })
$excludedExtensions = @($runtime.payloadPolicy.excludedExtensions | ForEach-Object { ([string]$_).ToLowerInvariant() })
$excludedPaths = @($runtime.payloadPolicy.excludedPaths | ForEach-Object { ([string]$_).ToLowerInvariant() })
$sourceFiles = Get-ChildItem -LiteralPath $SourceRuntimePath -Recurse -File | Where-Object {
    $extension = $_.Extension.ToLowerInvariant()
    $relativePath = [System.IO.Path]::GetRelativePath($SourceRuntimePath, $_.FullName).Replace('\', '/').ToLowerInvariant()
    if ($excludedExtensions -contains $extension) {
        return $false
    }
    if ($excludedPaths | Where-Object { $relativePath -like $_ }) {
        return $false
    }
    if ($extension -eq '.exe') {
        return $includedExecutables -contains $_.Name.ToLowerInvariant()
    }
    return $true
}

foreach ($file in $sourceFiles) {
    $relativePath = [System.IO.Path]::GetRelativePath($SourceRuntimePath, $file.FullName)
    $destinationPath = Join-Path $OutputPath $relativePath
    $destinationDirectory = Split-Path -Parent $destinationPath
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destinationPath -Force
}

$requiredExecutable = Join-Path $OutputPath 'qemu-system-x86_64.exe'
$requiredShare = Join-Path $OutputPath 'share'
if (-not (Test-Path -LiteralPath $requiredExecutable -PathType Leaf) -or -not (Test-Path -LiteralPath $requiredShare -PathType Container)) {
    throw 'Prepared QEMU payload failed the required runtime layout check.'
}

$fileRecords = @(Get-ChildItem -LiteralPath $OutputPath -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relativePath = [System.IO.Path]::GetRelativePath($OutputPath, $_.FullName).Replace('\', '/')
    $record = [ordered]@{
        path = $relativePath
        size = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if ($_.Extension -in @('.exe', '.dll')) {
        $signature = Get-AuthenticodeSignature -LiteralPath $_.FullName
        $record.authenticodeStatus = $signature.Status.ToString()
        $record.authenticodeSigner = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null }
    }
    [pscustomobject]$record
})

$payloadManifest = [ordered]@{
    schemaVersion = 1
    name = [string]$runtime.name
    version = $version
    distribution = [string]$runtime.distribution
    # The URL this installer was actually downloaded from; null when the runtime came from
    # -SourceRuntimePath or a cache older than this record. Any of them is the same file.
    binarySourceUrl = $binarySourceUrl
    binaryUpstreamUrl = if ($runtime.PSObject.Properties.Name -contains 'upstreamUrl') { [string]$runtime.upstreamUrl } else { [string]$runtime.sourceUrl }
    binaryInstallerSha512 = $expectedInstallerHash
    # The installer is built from Stefan Weil's tree, not from the release tarball alone.
    buildSourceRepository = if ($runtime.PSObject.Properties.Name -contains 'buildSource') { [string]$runtime.buildSource.repository } else { $null }
    buildSourceCommit = if ($runtime.PSObject.Properties.Name -contains 'buildSource') { [string]$runtime.buildSource.commit } else { $null }
    correspondingSourceUrl = [string]$runtime.sourceArchiveUrl
    correspondingSourceSignatureUrl = [string]$runtime.sourceSignatureUrl
    generatedAtUtc = [DateTime]::UtcNow.ToString('O')
    fileCount = $fileRecords.Count
    totalBytes = ($fileRecords | Measure-Object -Property size -Sum).Sum
    files = $fileRecords
}
$payloadManifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputPath 'qemu-runtime-files.json') -Encoding utf8

Write-Host "QEMU redistribution payload is ready: $OutputPath"
Write-Host "Files: $($fileRecords.Count)"
Write-Host ("Size: {0:N2} MiB" -f ($payloadManifest.totalBytes / 1MB))
Write-Warning 'The generated payload still requires third-party license/source-offer review before SignedRelease is allowed.'

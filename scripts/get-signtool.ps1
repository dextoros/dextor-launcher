# Unpacks Microsoft's signtool.exe from the official Microsoft.Windows.SDK.BuildTools
# NuGet package into out\tools\signtool. Nothing is installed: the Windows SDK is not
# needed for signing, only this one tool. Prints the path to signtool.exe.
[CmdletBinding()]
param(
    [string]$Version = '10.0.28000.2705'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$toolRoot = Join-Path $repoRoot "out\tools\signtool\$Version"
$existing = Get-ChildItem -LiteralPath $toolRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Select-Object -First 1
if ($existing) {
    Write-Output $existing.FullName
    return
}

$package = "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/$Version/microsoft.windows.sdk.buildtools.$Version.nupkg"
$archive = Join-Path ([System.IO.Path]::GetTempPath()) "sdk-buildtools-$Version.zip"
Write-Host "Downloading signtool from $package"
Invoke-WebRequest -Uri $package -OutFile $archive -UseBasicParsing
try {
    New-Item -ItemType Directory -Path $toolRoot -Force | Out-Null
    Expand-Archive -LiteralPath $archive -DestinationPath $toolRoot -Force
}
finally {
    Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
}

$signTool = Get-ChildItem -LiteralPath $toolRoot -Recurse -Filter signtool.exe |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Select-Object -First 1
if (-not $signTool) {
    throw "The package did not contain an x64 signtool.exe: $package"
}

# Microsoft signs signtool itself; refuse to use a copy that is not.
$signature = Get-AuthenticodeSignature -LiteralPath $signTool.FullName
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
    throw "signtool.exe is not signed by Microsoft: $($signature.Status) $($signature.SignerCertificate.Subject)"
}

Write-Output $signTool.FullName

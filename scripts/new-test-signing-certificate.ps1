# Creates the DexTor OS *test* code-signing certificate on this build machine.
#
# What it is for: signing test builds, so that on test PCs that trust it (see
# trust-test-certificate.ps1) Windows shows "Verified publisher: DexTor OS" instead of
# "Unknown publisher", and so the signing path of the build is exercised end to end.
#
# What it is NOT: a public certificate. It is self-signed, so Chrome Safe Browsing and
# SmartScreen on anyone else's PC treat the files exactly as unsigned. Public releases
# need a certificate from a trusted authority; its thumbprint then replaces this one.
#
# The private key is created non-exportable in the current user's store and never
# leaves it. Only the public part is written, to out\signing\DexTorOS-Test.cer.
[CmdletBinding()]
param(
    [string]$Subject = 'CN=DexTor OS (test), O=DexTor OS',
    [int]$Years = 5
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outDir = Join-Path $repoRoot 'out\signing'
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

$existing = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $Subject -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

$certificate = if ($existing) {
    Write-Host "Using the existing test certificate."
    $existing
} else {
    New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -FriendlyName 'DexTor OS test code signing' `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddYears($Years)
}

$cerPath = Join-Path $outDir 'DexTorOS-Test.cer'
Export-Certificate -Cert $certificate -FilePath $cerPath -Type CERT | Out-Null

Write-Host ""
Write-Host "Test signing certificate: $($certificate.Subject)"
Write-Host "Valid until:              $($certificate.NotAfter.ToString('yyyy-MM-dd'))"
Write-Host "Public part for test PCs: $cerPath"
Write-Host ""
Write-Host "Sign builds with it:"
Write-Host "  set DEXTOR_SIGN_THUMBPRINT=$($certificate.Thumbprint)  &&  build-portable.bat"
Write-Host "  .\scripts\build-release.ps1 -Version <x.y.z> -Mode TestSigned -CertificateThumbprint $($certificate.Thumbprint)"
Write-Output $certificate.Thumbprint

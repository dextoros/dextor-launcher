# Run on a TEST PC, as administrator, to trust DexTor OS test builds:
#   powershell -ExecutionPolicy Bypass -File trust-test-certificate.ps1 -Certificate DexTorOS-Test.cer
# Undo with -Remove.
#
# It adds the public test certificate to "Trusted Root Certification Authorities" and
# "Trusted Publishers" of this PC. After that, the UAC prompt of a test build names
# "DexTor OS" as a verified publisher. Only do this on machines used for testing:
# a trusted root is trusted for everything it signs.
[CmdletBinding()]
param(
    [string]$Certificate = (Join-Path $PSScriptRoot 'DexTorOS-Test.cer'),
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script as administrator.'
}

$stores = 'Cert:\LocalMachine\Root', 'Cert:\LocalMachine\TrustedPublisher'
if ($Remove) {
    foreach ($store in $stores) {
        Get-ChildItem $store | Where-Object Subject -like 'CN=DexTor OS (test)*' | ForEach-Object {
            Remove-Item -LiteralPath $_.PSPath
            Write-Host "Removed $($_.Thumbprint) from $store"
        }
    }
    return
}

if (-not (Test-Path -LiteralPath $Certificate -PathType Leaf)) {
    throw "Certificate file not found: $Certificate"
}

$parsed = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (Resolve-Path $Certificate).Path
if ($parsed.Subject -notlike 'CN=DexTor OS (test)*') {
    throw "This is not the DexTor OS test certificate: $($parsed.Subject)"
}

foreach ($store in $stores) {
    Import-Certificate -FilePath $Certificate -CertStoreLocation $store | Out-Null
    Write-Host "Trusted $($parsed.Thumbprint) in $store"
}

<#
.SYNOPSIS
    Signs a DexTor OS image for the launcher: writes <image>.sha256 and <image>.sig next to it.

.DESCRIPTION
    The launcher downloads an image only when <image>.sig is the release key's ECDSA P-256
    signature over the image's SHA-256, and the image hashes to <image>.sha256. This script
    produces both files; it is the same as

        sha256sum DextorOS_X.iso > DextorOS_X.iso.sha256
        openssl dgst -sha256 -sign dextor-os-release.key.pem -out DextorOS_X.iso.sig DextorOS_X.iso

    and checks the signature against the public key before it finishes. Upload both files
    next to the image before lastversion.link points at it.

.EXAMPLE
    .\scripts\sign-image.ps1 -Image D:\out\DextorOS_1_1_46.iso -PrivateKey D:\keys\dextor-os-release.key.pem
#>
param(
    [Parameter(Mandatory = $true)][string]$Image,
    [Parameter(Mandatory = $true)][string]$PrivateKey
)

$ErrorActionPreference = 'Stop'
$imagePath = (Resolve-Path $Image).Path
$name = Split-Path $imagePath -Leaf

$stream = [System.IO.File]::OpenRead($imagePath)
try {
    $digest = [System.Security.Cryptography.SHA256]::HashData($stream)
}
finally {
    $stream.Dispose()
}

$key = [System.Security.Cryptography.ECDsa]::Create()
try {
    $key.ImportFromPem((Get-Content -Raw $PrivateKey))
    if ($key.KeySize -ne 256) { throw "The release key must be ECDSA P-256." }
    $signature = $key.SignHash($digest, [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)
    if (-not $key.VerifyHash($digest, $signature, [System.Security.Cryptography.DSASignatureFormat]::Rfc3279DerSequence)) {
        throw "The signature just made does not verify."
    }

    $spki = $key.ExportSubjectPublicKeyInfo()
}
finally {
    $key.Dispose()
}

$hex = [Convert]::ToHexStringLower($digest)
[System.IO.File]::WriteAllText("$imagePath.sha256", "$hex  $name`n")
[System.IO.File]::WriteAllBytes("$imagePath.sig", $signature)

$fingerprint = [Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($spki), 0, 20) -replace '(.{4})(?!$)', '$1 '
Write-Host "SHA-256:     $hex"
Write-Host "Signature:   $imagePath.sig ($($signature.Length) bytes)"
Write-Host "Key:         $fingerprint"
Write-Host "The launcher expects the key E246 B0C5 33BA 5EE8 939A 45FC 3C38 F706 E288 E56D."

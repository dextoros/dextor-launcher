# Regenerates every bitmap made from the DexTor OS mark (docs\icon.svg):
#   docs\icon.png        1024 x 1024 master
#   docs\favicon.png     64 x 64, embedded in the launcher
#   docs\favicon.ico     16, 20, 24, 32, 40, 48, 64, 256 - the launcher's ApplicationIcon
#   packaging\DexTor.Store\Assets\*.png   MSIX visual assets (scale-100..400, targetsize-16..256)
#
# The renderer is the small stand-alone tool in scripts\store-assets (SkiaSharp + Svg.Skia
# from nuget.org); nothing is installed. Run it after docs\icon.svg changes and commit
# the results.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$tool = Join-Path $PSScriptRoot 'store-assets\StoreAssets.csproj'
$svg = Join-Path $repoRoot 'docs\icon.svg'
$docs = Join-Path $repoRoot 'docs'
$assets = Join-Path $repoRoot 'packaging\DexTor.Store\Assets'

# Old files go first, so a size dropped from the tool does not linger in the package.
if (Test-Path -LiteralPath $assets) {
    Get-ChildItem -LiteralPath $assets -Filter '*.png' -File | Remove-Item -Force
}

& dotnet run --project $tool -c Release -- $svg $docs $assets
if ($LASTEXITCODE -ne 0) {
    throw "The asset renderer failed with exit code $LASTEXITCODE."
}

Write-Host ''
Write-Host "Icons written to $docs and $assets."

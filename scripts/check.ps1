param(
    [switch]$SkipRestore = $false
)

# The .NET SDK host is resolved by the shared helper, so this script and
# diagnose-usb.ps1 cannot disagree about which SDK a run used.
. (Join-Path $PSScriptRoot 'lib\DotnetHost.ps1')

$dotnet = Resolve-DotnetExecutable
if (-not $dotnet) {
    Write-Host ".NET SDK is required. Install .NET SDK 10.0.100 (or newer 10.x) and rerun."
    Write-Host "https://dotnet.microsoft.com/en-us/download/dotnet/10.0"
    exit 1
}

Add-DotnetToProcessPath -DotnetExecutable $dotnet

$dotnetVersion = & $dotnet --version
$normalizedDotnetVersion = ($dotnetVersion -split '[-+]')[0]
$parsedDotnetVersion = $null
if (-not [version]::TryParse($normalizedDotnetVersion, [ref]$parsedDotnetVersion)) {
    Write-Host "Failed to detect a valid .NET SDK version."
    Write-Host ".NET SDK is required. Install .NET SDK 10.0.100 (or newer 10.x) and rerun."
    Write-Host "https://dotnet.microsoft.com/en-us/download/dotnet/10.0"
    exit 1
}

$minimumVersion = [version]"10.0.100"
if ($parsedDotnetVersion -lt $minimumVersion) {
    Write-Host "Unsupported .NET SDK version: $dotnetVersion"
    Write-Host "Install .NET SDK 10.0.100 (or newer 10.x) and rerun."
    Write-Host "https://dotnet.microsoft.com/en-us/download/dotnet/10.0"
    exit 1
}

Write-Host "DexTor check using $dotnet (SDK $dotnetVersion)."

if (-not $SkipRestore) {
    & $dotnet restore
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

# The real-QEMU smoke suite is opt-in: it needs a provisioned, provenance-verified
# runtime, so it is excluded here and run deliberately with
#   dotnet test --filter "Category=RealQemu"   (with DEXTOR_REAL_QEMU=1)
& $dotnet test --filter "Category!=RealQemu"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "DexTor check completed."

<#
.SYNOPSIS
    Read-only USB removal diagnostic for an explicitly named DexTor data image.

.DESCRIPTION
    Runs the production removal resolver against one storage path you name, and writes a
    report to one host-side file you name.

    It reads. It does not create an image, write a journal, recover ownership, repair,
    flush, dismount, eject or format anything, it does not start a session, a recovery, a
    provisioning run or a virtual machine, and it does not ask for elevation or change any
    machine or user configuration. The image is opened read-only and shared; every handle
    is closed before the report is written.

    The device is never chosen for you: -DataImagePath is mandatory and there is no default
    drive and no "first removable device" fallback. The report is never written to the
    device being inspected — the destination must resolve to a different volume, so a drive
    letter, a mounted folder or any other alias of the same volume is refused too.

    Acceptance means the resolver would name this device. It is not a promise that Windows
    will approve the removal, and it says nothing about whether guest storage is consistent.

.PARAMETER DataImagePath
    Full path of the image to inspect, for example E:\DEXTOR\storage\data.img. Mandatory.

.PARAMETER OutputPath
    Full path of the report file to write. Must be on host storage, on a different volume
    from the image, in a directory that already exists.

.PARAMETER Configuration
    Build configuration of the diagnostic binary. Debug (default) or Release.

.PARAMETER Force
    Overwrite an existing report file.

.PARAMETER SkipBuild
    Use the diagnostic binary already built for -Configuration instead of building it.

.OUTPUTS
    Exit codes, passed through from the diagnostic:
      0   accepted   the resolver would name this device as the removal target
      10  refused    the resolver refused; the report gives the reason
      11  missing    the named image is not there, or it disappeared
      12  denied     Windows denied the read
      13  unknown    the storage could not be read, so nothing was established
      64  usage      the arguments were not usable
      65  output     the report destination was not verified host-side storage
      66  unsupported  the host is not Windows
      70  failure    something else failed
      1   this wrapper could not get as far as running the diagnostic

.EXAMPLE
    .\scripts\diagnose-usb.ps1 `
        -DataImagePath "E:\DEXTOR\storage\data.img" `
        -OutputPath "C:\Users\me\Desktop\dextor-usb-report.txt"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$DataImagePath,

    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$OutputPath,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [switch]$Force,

    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'lib\DotnetHost.ps1')

# The device selection lives in the product resolver, reached through this one binary.
# Nothing here enumerates disks, and there is deliberately no PowerShell or WMI copy of
# that decision to drift away from it.
$project = Join-Path $repoRoot 'src\DexTor.SessionHost\DexTor.SessionHost.csproj'
$executable = Join-Path $repoRoot "src\DexTor.SessionHost\bin\$Configuration\net10.0-windows\DexTor.SessionHost.exe"

if (-not $SkipBuild) {
    $dotnet = Resolve-DotnetExecutable
    if (-not $dotnet) {
        Write-Host '.NET SDK is required. Install .NET SDK 10.0.100 (or newer 10.x) and rerun.'
        Write-Host 'https://dotnet.microsoft.com/en-us/download/dotnet/10.0'
        Write-Host 'Alternatively, pass -SkipBuild to use a diagnostic binary that is already built.'
        exit 1
    }

    Add-DotnetToProcessPath -DotnetExecutable $dotnet

    # Only the session host is built. The Launcher is a different project and is not
    # started, built or touched by entering diagnostic mode.
    & $dotnet build $project -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Building the diagnostic failed (exit $LASTEXITCODE)."
        exit 1
    }
}

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    Write-Host "The diagnostic binary was not found: $executable"
    Write-Host 'Run without -SkipBuild, or build the session host for this configuration first.'
    exit 1
}

# Each value is passed as one argument. Spaces and non-ASCII characters are ordinary
# content of a path here, and the argument boundaries are preserved as given.
$arguments = @('--diagnose-usb', '--data-image', $DataImagePath, '--output', $OutputPath)
if ($Force) { $arguments += '--force' }

& $executable @arguments
exit $LASTEXITCODE

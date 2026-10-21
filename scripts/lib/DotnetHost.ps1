# Resolves the .NET SDK launcher without depending on PATH, and without changing machine
# or user PATH. Dot-source this file; the caller decides what to do when it returns $null.
#
# This is the single implementation. `check.ps1` and `diagnose-usb.ps1` both use it so the
# two entry points cannot disagree about which SDK a run used.

function Resolve-DotnetExecutable {
    $onPath = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $candidates = @()
    if ($env:DOTNET_ROOT) { $candidates += (Join-Path $env:DOTNET_ROOT 'dotnet.exe') }
    if ($env:DOTNET_HOST_PATH) { $candidates += $env:DOTNET_HOST_PATH }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'dotnet\dotnet.exe') }
    if ($env:LOCALAPPDATA) { $candidates += (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe') }

    # The installer records the host location here; it is authoritative when the
    # well-known directories do not apply.
    foreach ($key in @(
            'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\x64',
            'HKLM:\SOFTWARE\dotnet\Setup\InstalledVersions\arm64')) {
        try {
            $installLocation = (Get-ItemProperty -Path $key -Name 'InstallLocation' -ErrorAction Stop).InstallLocation
            if ($installLocation) { $candidates += (Join-Path $installLocation 'dotnet.exe') }
        }
        catch {
            # Key absent on this machine; the well-known paths above still apply.
        }
    }

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }

    return $null
}

# Makes the resolved host visible to child processes for this run only.
function Add-DotnetToProcessPath {
    param([Parameter(Mandatory = $true)][string]$DotnetExecutable)

    $directory = Split-Path -Parent $DotnetExecutable
    if (($env:Path -split ';') -notcontains $directory) {
        $env:Path = "$directory;$env:Path"
    }
}

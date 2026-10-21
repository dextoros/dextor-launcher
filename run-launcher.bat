@echo off
setlocal EnableExtensions EnableDelayedExpansion

set "SCRIPT_DIR=%~dp0"

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"

if /I "%CONFIG%"=="Debug" goto CONFIG_OK
if /I "%CONFIG%"=="Release" goto CONFIG_OK

echo Invalid configuration "%CONFIG%". Use Debug or Release.
exit /b 1

goto :eof

:CONFIG_OK
rem The SDK is found by the same helper check.ps1 and diagnose-usb.ps1 use, so this
rem entry point works when dotnet is installed but not on PATH. Nothing is added to the
rem machine or user PATH: the resolved host is used by full path, for this run only.
set "DOTNET_EXE="
for /f "usebackq delims=" %%i in (`powershell -NoProfile -ExecutionPolicy Bypass -Command ". '%~dp0scripts\lib\DotnetHost.ps1'; Resolve-DotnetExecutable" 2^>nul`) do set "DOTNET_EXE=%%i"
if not defined DOTNET_EXE goto NO_DOTNET

for /f "usebackq tokens=1 delims=." %%v in (`"%DOTNET_EXE%" --version 2^>nul`) do set "DOTNET_MAJOR=%%v"
if "%DOTNET_MAJOR%"=="" goto DOTNET_VERSION_ERROR
if !DOTNET_MAJOR! LSS 10 goto DOTNET_UNSUPPORTED

set "APPDATA=%TEMP%\dextor-launcher-appdata"
set "LOCALAPPDATA=%TEMP%\dextor-launcher-local-appdata"
set "USERPROFILE=%TEMP%\dextor-launcher-user"
if not exist "%APPDATA%" mkdir "%APPDATA%"
if not exist "%LOCALAPPDATA%" mkdir "%LOCALAPPDATA%"
if not exist "%USERPROFILE%" mkdir "%USERPROFILE%"

set "PROJECT=%~dp0src\DexTor.Launcher\DexTor.Launcher.csproj"
if not exist "%PROJECT%" (
    echo Project not found: %PROJECT%
    echo Run this script from the repository root.
    exit /b 1
)

"%DOTNET_EXE%" build "%PROJECT%" -c %CONFIG%
if errorlevel 1 (
    echo Build failed.
    exit /b 1
)

"%DOTNET_EXE%" run --project "%PROJECT%" -c %CONFIG%
if errorlevel 1 (
    echo Run failed.
    exit /b 1
)

exit /b 0

:NO_DOTNET
echo .NET SDK not found.
echo Install .NET SDK 10.0.100 (or newer 10.x) and rerun:
echo https://dotnet.microsoft.com/en-us/download/dotnet/10.0
exit /b 1

:DOTNET_VERSION_ERROR
echo Failed to detect .NET SDK version.
goto SDK_HELP

:DOTNET_UNSUPPORTED
echo Unsupported .NET version: %DOTNET_MAJOR%
goto SDK_HELP

:SDK_HELP
echo Install .NET SDK 10.0.100 (or newer 10.x) and rerun:
echo https://dotnet.microsoft.com/en-us/download/dotnet/10.0
exit /b 1

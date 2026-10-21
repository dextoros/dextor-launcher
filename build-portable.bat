@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem Builds the portable DexTor OS launcher: ONE self-contained .exe, like
rem balenaEtcher-portable. Nothing is installed on the target PC; deleting the file
rem removes the program. USB creation restarts the same file elevated.
rem
rem Optional code signing (removes "Unknown publisher" from the Windows prompt):
rem   set DEXTOR_SIGN_THUMBPRINT=<certificate thumbprint in the user or machine store>
rem or
rem   set DEXTOR_SIGN_PFX=<path to .pfx>  and  set DEXTOR_SIGN_PASSWORD=<password>
rem Optional: set DEXTOR_SIGN_TIMESTAMP=<RFC 3161 URL> (default: DigiCert).

set "REPO_ROOT=%~dp0"
set "CONFIGURATION=Release"
set "RUNTIME_ID=win-x64"
rem The version is stamped into the file and its name, so an old copy can never pass
rem for a new one: build-portable.bat 0.1.16
set "VERSION=%~1"
if "%VERSION%"=="" set "VERSION=0.0.0"
set "PUBLISH_DIR=%REPO_ROOT%out\portable-publish"
set "OUTPUT_EXE=%REPO_ROOT%out\DexTorOS-Portable-%VERSION%-%RUNTIME_ID%.exe"
set "LAUNCHER_PROJECT=%REPO_ROOT%src\DexTor.Launcher\DexTor.Launcher.csproj"
if "%DEXTOR_SIGN_TIMESTAMP%"=="" set "DEXTOR_SIGN_TIMESTAMP=http://timestamp.digicert.com"

echo.
echo === DexTor OS portable build ===
echo Version: %VERSION%
echo Target: %RUNTIME_ID%
echo Output: %OUTPUT_EXE%
echo.

where dotnet >nul 2>nul
if errorlevel 1 goto NO_DOTNET

for /f "tokens=1 delims=." %%v in ('dotnet --version 2^>nul') do set "DOTNET_MAJOR=%%v"
if "!DOTNET_MAJOR!"=="" goto DOTNET_VERSION_ERROR
if !DOTNET_MAJOR! LSS 10 goto DOTNET_UNSUPPORTED

if not exist "%LAUNCHER_PROJECT%" goto PROJECT_NOT_FOUND

if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
if errorlevel 1 goto CLEAN_FAILED
if exist "%OUTPUT_EXE%" del /q "%OUTPUT_EXE%"
if exist "%OUTPUT_EXE%" goto CLEAN_FAILED
if not exist "%REPO_ROOT%out" mkdir "%REPO_ROOT%out"

echo [1/3] Publishing the single-file launcher...
rem The USB provisioner is compiled into the launcher; runtime metadata is embedded and
rem unpacked by .NET into its own temporary cache when the file starts.
dotnet publish "%LAUNCHER_PROJECT%" -c %CONFIGURATION% -r %RUNTIME_ID% --self-contained true -o "%PUBLISH_DIR%" ^
  -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=None -p:DebugSymbols=false -p:Version=%VERSION% -p:VersionPrefix=%VERSION% -p:FileVersion=%VERSION%.0
if errorlevel 1 goto BUILD_FAILED
if not exist "%PUBLISH_DIR%\DexTor.Launcher.exe" goto OUTPUT_INVALID
copy /y "%PUBLISH_DIR%\DexTor.Launcher.exe" "%OUTPUT_EXE%" >nul
if errorlevel 1 goto OUTPUT_INVALID

echo [2/3] Signing...
if not "%DEXTOR_SIGN_THUMBPRINT%"=="" goto SIGN
if not "%DEXTOR_SIGN_PFX%"=="" goto SIGN
echo   Skipped: no certificate configured. Windows will show "Unknown publisher".
goto SIGN_DONE

:SIGN
rem Windows PowerShell must not inherit PowerShell 7's module path, or its own modules
rem (Get-AuthenticodeSignature, Expand-Archive) fail to load when this runs from pwsh.
set "PSModulePath="
set "SIGNTOOL="
for /f "delims=" %%s in ('dir /b /s /o-n "%ProgramFiles(x86)%\Windows Kits\10\bin\*signtool.exe" 2^>nul ^| findstr /i "\\x64\\signtool.exe"') do (
  if not defined SIGNTOOL set "SIGNTOOL=%%s"
)
if not defined SIGNTOOL (
  rem No Windows SDK: take Microsoft's signtool from its NuGet package into out\tools.
  for /f "usebackq delims=" %%s in (`powershell -NoProfile -ExecutionPolicy Bypass -File "%REPO_ROOT%scripts\get-signtool.ps1"`) do set "SIGNTOOL=%%s"
)
if not defined SIGNTOOL goto NO_SIGNTOOL
if not "%DEXTOR_SIGN_THUMBPRINT%"=="" (
  "%SIGNTOOL%" sign /fd sha256 /tr "%DEXTOR_SIGN_TIMESTAMP%" /td sha256 /sha1 "%DEXTOR_SIGN_THUMBPRINT%" /d "DexTor OS" "%OUTPUT_EXE%"
) else (
  "%SIGNTOOL%" sign /fd sha256 /tr "%DEXTOR_SIGN_TIMESTAMP%" /td sha256 /f "%DEXTOR_SIGN_PFX%" /p "%DEXTOR_SIGN_PASSWORD%" /d "DexTor OS" "%OUTPUT_EXE%"
)
if errorlevel 1 goto SIGN_FAILED
rem A trusted certificate must pass the full chain check. The self-signed test one is
rem trusted only on test PCs, so for it the signer's thumbprint is what is checked.
powershell -NoProfile -Command "$s=Get-AuthenticodeSignature -LiteralPath '%OUTPUT_EXE%'; $t='%DEXTOR_SIGN_THUMBPRINT%'; if($s.Status -eq 'Valid'){exit 0}; if($t -and $s.SignerCertificate -and $s.SignerCertificate.Thumbprint -eq $t -and $s.Status -in 'UnknownError','NotTrusted'){Write-Host '  Signed with the test certificate (trusted only on test PCs).'; exit 0}; Write-Host ('  Signature check failed: ' + $s.Status); exit 1"
if errorlevel 1 goto SIGN_FAILED

:SIGN_DONE
echo [3/3] Cleaning up...
rmdir /s /q "%PUBLISH_DIR%" 2>nul

echo.
echo Build completed successfully.
echo.
echo Portable launcher for the other laptop:
echo   %OUTPUT_EXE%
echo.
echo Copy this one file and run it. Nothing is installed; delete the file to remove it.
echo Windows asks for administrator permission only when the USB drive is written.
exit /b 0

:NO_DOTNET
echo ERROR: .NET SDK was not found.
goto SDK_HELP

:DOTNET_VERSION_ERROR
echo ERROR: Could not detect the installed .NET SDK version.
goto SDK_HELP

:DOTNET_UNSUPPORTED
echo ERROR: .NET SDK 10 or newer is required. Found major version !DOTNET_MAJOR!.
goto SDK_HELP

:SDK_HELP
echo Install .NET SDK 10.0.100 or newer and run this file again:
echo https://dotnet.microsoft.com/download/dotnet/10.0
exit /b 1

:PROJECT_NOT_FOUND
echo ERROR: Launcher project was not found under:
echo   %REPO_ROOT%src
exit /b 1

:CLEAN_FAILED
echo ERROR: Could not remove the previous build. Close the running launcher and retry.
exit /b 1

:BUILD_FAILED
echo ERROR: Portable publish failed.
exit /b 1

:OUTPUT_INVALID
echo ERROR: Publish finished without the launcher executable.
exit /b 1

:NO_SIGNTOOL
echo ERROR: A certificate is configured but signtool.exe was not found.
echo Install the Windows SDK signing tools, or unset DEXTOR_SIGN_THUMBPRINT / DEXTOR_SIGN_PFX.
exit /b 1

:SIGN_FAILED
echo ERROR: Signing failed. The unsigned file is at:
echo   %OUTPUT_EXE%
exit /b 1

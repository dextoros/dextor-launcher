# Packaging

This folder holds the Windows installer (`DexTor.Installer`, an MSI) and the setup
bootstrapper (`DexTor.Setup`, a WiX Burn bundle), plus their themes and translations.

## Portable build

From the repository root:

```bat
build-portable.bat 0.1.0
```

The script builds a portable edition in the spirit of balenaEtcher Portable: **one**
self-contained file, `out\DexTorOS-Portable-<version>-win-x64.exe`. The version is the first
argument; it is also shown at the bottom of the wizard window and on the first line of the
USB creation log.

Nothing is installed and .NET is not required; to remove the program, delete the file. The
image cache, the QEMU runtime and the USB creation log live only in the user's temporary folder,
`%LOCALAPPDATA%\DexTor`.

- Downloads resume: after a cancel, a dropped connection or closing the launcher, the part
  already downloaded is kept and the next download continues from there.
- Every start cleans up what interrupted runs left behind: abandoned extractions, old QEMU
  versions and stale USB write requests. Once the release index has been read, images of
  previous versions are removed too. The verified current image and runtime stay, so they are
  not downloaded again.
- The QEMU runtime is taken from the official QEMU for Windows installer, run without
  administrator rights (`RunAsInvoker`) only to unpack it into `%LOCALAPPDATA%\DexTor\Runtime`.
  The shortcuts and registry entries that installer adds for that folder are removed right
  away and again on every start.

The USB provisioner is built into the same file: to write a drive, the launcher restarts itself
with administrator rights in `--provision` mode. No second `.exe` is unpacked next to it.

The administrator prompt (UAC) appears only when writing to the drive begins, and it cannot be
avoided: writing to a physical disk on Windows requires administrator rights. The prompt names
an "unknown publisher" until the file is signed with a code-signing certificate. The script
signs the result itself when a certificate is configured:

```bat
set DEXTOR_SIGN_THUMBPRINT=<certificate thumbprint in the store>
rem or
set DEXTOR_SIGN_PFX=C:\path\to\certificate.pfx
set DEXTOR_SIGN_PASSWORD=<password>
build-portable.bat
```

`signtool.exe` comes from the Windows SDK; without the SDK it is downloaded from Microsoft's
official NuGet package (`scripts\get-signtool.ps1`, into `out\tools`, nothing installed).
Without a certificate, signing is skipped.

### Test signing

Until a certificate from a trusted authority is in place, test builds are signed with a
self-signed "DexTor OS (test)" certificate:

```powershell
.\scripts\new-test-signing-certificate.ps1        # once; prints the thumbprint
set DEXTOR_SIGN_THUMBPRINT=<thumbprint> && build-portable.bat
.\scripts\build-release.ps1 -Version 0.1.0 -Mode TestSigned -CertificateThumbprint <thumbprint>
```

The private key is created non-exportable in the current user's store on the build machine.
Only `out\signing\DexTorOS-Test.cer` leaves it. On a test PC, once, as administrator:

```powershell
powershell -ExecutionPolicy Bypass -File trust-test-certificate.ps1 -Certificate DexTorOS-Test.cer   # -Remove undoes it
```

UAC then shows "Verified publisher: DexTor OS". **Browsers, SmartScreen and other PCs do not
trust the test signature**: for them the file is still unsigned. Public builds need a
certificate from a trusted authority (OV/EV or Azure Trusted Signing) and the `SignedRelease`
mode.

## Windows installer

To check the MSI and the setup locally without a production certificate:

```powershell
.\scripts\build-release.ps1 -Version 0.1.0 -Mode UnsignedTest
```

The result appears in `out\release\0.1.0\packages`. Such a package is for testing only: Windows
warns about an unknown publisher.

If the local or CI account cannot reach Windows Installer for the ICE validation, `UnsignedTest`
alone accepts `-SkipMsiValidation`. In `SignedRelease` the validation cannot be turned off.

`SignedRelease` is the mode for public releases. It requires a prepared QEMU runtime and a
production certificate, and signs the payload, the MSI, the inner Burn engine and the final
setup.

To prepare the pinned, minimal QEMU payload from the official Windows installer:

```powershell
.\scripts\prepare-qemu-payload.ps1
```

The script checks the installer's SHA-512 before unpacking, keeps only
`qemu-system-x86_64.exe`, its runtime dependencies and the x86 firmware and data, then writes a
complete `qemu-runtime-files.json` with SHA-256 and Authenticode status for every file.
Documentation, icons and firmware for other architectures are excluded by the declarative
policy in `runtime/qemu-manifest.json`. The result and the download cache stay under `out` and
are never committed.

The Start Menu and Desktop shortcuts are separate components with an HKCU registry key path,
and `Launcher.exe` is a file component of its own; this resolves ICE38, ICE43, ICE57 and the
cross-component reference ICE69. The application is still installed per machine.

## Setup appearance

The Burn bootstrapper uses `DexTor.Setup/DexTorTheme.xml`: a dark DexTor background, violet
headings and primary actions, and graphic buttons with hover, pressed and keyboard-focus states.
Its title bar remains Windows-managed. This native WiX theme does not implement the launcher's
glass effect.

Button assets are checked in under `DexTor.Setup/Assets`; regenerate them with
`DexTor.Setup/Generate-ThemeAssets.ps1`. Every asset must also be listed as a bootstrapper
payload in `Bundle.wxs`. Check the actual welcome and help windows after theme edits:
compilation alone does not validate runtime image loading.

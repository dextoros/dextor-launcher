# Third-party notices

DexTor OS for Windows is built on the work of other open-source projects. Thank you to all of
their authors. This file lists every third-party component that DexTor OS for Windows ships,
downloads or is built with, under which license, and in which deliverable.

The DexTor OS for Windows code itself is source-available under the
[PolyForm Strict License 1.0.0](../LICENSE). The components below keep their own licenses;
nothing in the PolyForm terms limits the rights those licenses give you.

## At a glance

| Component | Version | License | Portable `.exe` | Installer (MSI / Setup) |
|---|---|---|---|---|
| [.NET runtime and Windows Desktop runtime](#net-runtime-and-microsoftextensions) | 10.0 | MIT | included | included |
| [Microsoft.Extensions.Logging](#net-runtime-and-microsoftextensions) | 9.0.0 | MIT | included | included |
| [DiscUtils](#discutils) | 0.16.13 | MIT | included | included |
| [Inter typeface](#inter) | 4.1 | SIL OFL 1.1 | included | included |
| [QEMU](#qemu) | 11.1.0 | GPL-2.0-or-later and others | **downloaded on first use**, not included | included in `SignedRelease` builds |
| [WiX Toolset](#wix-toolset) | 6.0.2 | MS-RL | not used | bootstrapper engine and theme |

"Included" means the component is part of the file we publish. "Downloaded on first use" means
the launcher fetches the component's official build from its publisher when it is needed, checks
it against a pinned hash, and runs it from the user's own cache; DexTor does not redistribute it.

---

## .NET runtime and Microsoft.Extensions

The portable launcher is a self-contained .NET application: the .NET runtime
(`Microsoft.NETCore.App`), the Windows Desktop runtime (`Microsoft.WindowsDesktop.App`, which
provides Windows Forms) and `Microsoft.Extensions.Logging` are bundled into the executable.

- Projects: https://github.com/dotnet/runtime, https://github.com/dotnet/winforms
- License: MIT
- The .NET runtime's own third-party notices:
  https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT

```text
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## DiscUtils

DexTor uses DiscUtils to read the DexTor OS image and the boot files inside it when it prepares a
USB drive: `DiscUtils.Core`, `DiscUtils.Streams`, `DiscUtils.Fat` and `DiscUtils.Iso9660`.

- Project: https://github.com/DiscUtils/DiscUtils
- Version: 0.16.13, source commit `59d7cadab839c6d8dfcf52f8be5efe6d2ced190f`
- License: MIT

```text
Copyright (c) 2008-2011, Kenneth Bell
Copyright (c) 2014, Quamotion

Permission is hereby granted, free of charge, to any person obtaining a
copy of this software and associated documentation files (the "Software"),
to deal in the Software without restriction, including without limitation
the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the
Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```

## Inter

The wizard is set in Inter (Regular, Medium and SemiBold). The fonts are embedded in the launcher
and loaded privately by its process; nothing is installed into Windows.

- Project: https://github.com/rsms/inter
- License: SIL Open Font License 1.1, full text below and in
  [`src/DexTor.Launcher/Assets/Fonts/Inter-LICENSE.txt`](../src/DexTor.Launcher/Assets/Fonts/Inter-LICENSE.txt)

```text
Copyright (c) 2016 The Inter Project Authors (https://github.com/rsms/inter)

This Font Software is licensed under the SIL Open Font License, Version 1.1.
This license is copied below, and is also available with a FAQ at:
http://scripts.sil.org/OFL

-----------------------------------------------------------
SIL OPEN FONT LICENSE Version 1.1 - 26 February 2007
-----------------------------------------------------------

PREAMBLE
The goals of the Open Font License (OFL) are to stimulate worldwide
development of collaborative font projects, to support the font creation
efforts of academic and linguistic communities, and to provide a free and
open framework in which fonts may be shared and improved in partnership
with others.

The OFL allows the licensed fonts to be used, studied, modified and
redistributed freely as long as they are not sold by themselves. The
fonts, including any derivative works, can be bundled, embedded,
redistributed and/or sold with any software provided that any reserved
names are not used by derivative works. The fonts and derivatives,
however, cannot be released under any other type of license. The
requirement for fonts to remain under this license does not apply
to any document created using the fonts or their derivatives.

DEFINITIONS
"Font Software" refers to the set of files released by the Copyright
Holder(s) under this license and clearly marked as such. This may
include source files, build scripts and documentation.

"Reserved Font Name" refers to any names specified as such after the
copyright statement(s).

"Original Version" refers to the collection of Font Software components as
distributed by the Copyright Holder(s).

"Modified Version" refers to any derivative made by adding to, deleting,
or substituting -- in part or in whole -- any of the components of the
Original Version, by changing formats or by porting the Font Software to a
new environment.

"Author" refers to any designer, engineer, programmer, technical
writer or other person who contributed to the Font Software.

PERMISSION AND CONDITIONS
Permission is hereby granted, free of charge, to any person obtaining
a copy of the Font Software, to use, study, copy, merge, embed, modify,
redistribute, and sell modified and unmodified copies of the Font
Software, subject to the following conditions:

1) Neither the Font Software nor any of its individual components,
in Original or Modified Versions, may be sold by itself.

2) Original or Modified Versions of the Font Software may be bundled,
redistributed and/or sold with any software, provided that each copy
contains the above copyright notice and this license. These can be
included either as stand-alone text files, human-readable headers or
in the appropriate machine-readable metadata fields within text or
binary files as long as those fields can be easily viewed by the user.

3) No Modified Version of the Font Software may use the Reserved Font
Name(s) unless explicit written permission is granted by the corresponding
Copyright Holder. This restriction only applies to the primary font name as
presented to the users.

4) The name(s) of the Copyright Holder(s) or the Author(s) of the Font
Software shall not be used to promote, endorse or advertise any
Modified Version, except to acknowledge the contribution(s) of the
Copyright Holder(s) and the Author(s) or with their explicit written
permission.

5) The Font Software, modified or unmodified, in part or in whole,
must be distributed entirely under this license, and must not be
distributed under any other license. The requirement for fonts to
remain under this license does not apply to any document created
using the Font Software.

TERMINATION
This license becomes null and void if any of the above conditions are
not met.

DISCLAIMER
THE FONT SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO ANY WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT
OF COPYRIGHT, PATENT, TRADEMARK, OR OTHER RIGHT. IN NO EVENT SHALL THE
COPYRIGHT HOLDER BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
INCLUDING ANY GENERAL, SPECIAL, INDIRECT, INCIDENTAL, OR CONSEQUENTIAL
DAMAGES, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
FROM, OUT OF THE USE OR INABILITY TO USE THE FONT SOFTWARE OR FROM
OTHER DEALINGS IN THE FONT SOFTWARE.
```

## QEMU

DexTor OS runs inside Windows on QEMU with the Windows Hypervisor Platform. QEMU is free software
released mainly under the GNU General Public License, version 2 or later; some of its files and
bundled firmware use other compatible licenses.

- Project: https://www.qemu.org/ · License: https://www.qemu.org/license/
- Pinned version: QEMU 11.1.0, Stefan Weil's QEMU for Windows x64
- Windows build: Stefan Weil's installer `qemu-w64-setup-20260811.exe`,
  SHA-512 `5bcf9eed634e8575a37b74f445af41a2fe4106da512d0c30c368301d4c105037fdfab40a5287367a28a957624cddebbc8c07e16c88ab6634f554cdf3d16bf543`
  - DexTor's mirror (unmodified copy, same SHA-512):
    https://github.com/dextoros/qemu-runtime/releases/tag/qemu-11.1.0-20260811
  - Original: https://qemu.weilnetz.de/w64/2026/qemu-w64-setup-20260811.exe
- Corresponding source, attached to the mirror release next to the installer:
  - The tree the installer was built from: Stefan Weil's QEMU tree at
    https://repo.or.cz/qemu/ar7.git, commit `e470268ff446ec56728215a987ed166d5ed8fb64` (the
    `v11.1.0` tag merged into his tree; `qemu-system-x86_64 --version` reports
    `v11.1.0-12130-ge470268ff4`), as `qemu-weilnetz-e470268ff446.tar.xz`.
  - QEMU 11.1.0 with the firmware sources: https://download.qemu.org/qemu-11.1.0.tar.xz
    (signature: https://download.qemu.org/qemu-11.1.0.tar.xz.sig). The firmware in the
    installer is identical to the images built from it.
  - The installer also contains third-party DLLs from MSYS2's MinGW-w64 packages, each under
    its own license; the mirror release lists them with their source packages.

**Portable launcher.** QEMU is not part of the executable. When DexTor OS is first started inside
Windows, the launcher downloads the installer above - from DexTor's mirror, or from
qemu.weilnetz.de if the mirror cannot be reached - checks its SHA-512, and unpacks it into
`%LOCALAPPDATA%\DexTor\Runtime` without administrator rights. The mirror hosts Stefan Weil's file
unchanged; DexTor does not build or modify QEMU. QEMU stays under its own licenses above, and
the mirror release carries the corresponding source.

**Installer.** A `SignedRelease` installer contains a minimal subset of that same build
(`qemu-system-x86_64.exe`, its runtime libraries and the x86 firmware), listed with SHA-256
hashes in `runtime/qemu/qemu-runtime-files.json` inside the installed application, together with
the license files above. Every installer we publish that contains QEMU comes with the
corresponding QEMU source archives listed above attached to the same release.

**Microsoft Store package.** The Store package contains that same build of QEMU, unmodified, in
`runtime\qemu` (listed with SHA-256 hashes in `runtime\qemu\qemu-runtime-files.json`, next to
QEMU's `COPYING` and `COPYING.LIB`), and never downloads QEMU. Its corresponding source is the
mirror release above.

**Written offer.** For at least three years after we last distribute a given version of the
launcher, its installer or its Store package, we will give any third party, on request, a
complete machine-readable copy of the corresponding source code of QEMU and of every library in
it licensed under the GNU GPL, LGPL or AGPL, as we distributed them - free of charge as a
download, or on a physical medium for no more than our cost of making and shipping it. Ask
through https://github.com/dextoros/qemu-runtime/issues. This offer is in addition to the source
archives already published next to the binaries.

## WiX Toolset

The MSI and the setup bootstrapper are built with WiX Toolset 6.0.2. The setup's theme,
`packaging/DexTor.Setup/DexTorTheme.xml`, is based on the WiX Standard Bootstrapper Application
theme and keeps its .NET Foundation copyright notice. The Burn engine that runs the setup is part
of WiX and is distributed under the Microsoft Reciprocal License (MS-RL).

- Project: https://github.com/wixtoolset/wix · Documentation: https://docs.firegiant.com/wix/
- License: https://github.com/wixtoolset/wix/blob/main/LICENSE.TXT

The portable launcher does not contain any WiX component.

# DexTor OS for Windows - End User License Agreement

**Version 1.0.** Effective on the date of the release it ships with.

This End User License Agreement (the "**Agreement**") is between you and the DexTor OS authors,
the copyright holders of DexTor OS for Windows, named "DexTor OS" in [`LICENSE`](LICENSE)
("**we**", "**us**").

It applies to the official release builds of **DexTor OS for Windows** (the "**Launcher**"):
the portable executable, the installer, the Microsoft Store package when it is available, and
their updates, as published by us at <https://github.com/dextoros/dextor-launcher/releases>,
<https://dextoros.com> or in the Microsoft Store.

By downloading, installing or using the Launcher you accept this Agreement. If you use it on
behalf of an organization, you accept it for that organization and confirm that you may do so.
If you do not accept it, do not use the Launcher.

## 1. What you may do

We grant you a worldwide, non-exclusive, non-transferable, royalty-free license to install and
use the official, unmodified Launcher on any number of devices that you own or control, for
**any lawful purpose, personal or commercial**.

If you are an organization (for example a company, fund, exchange or trading desk), this
includes:

- use by your employees and contractors, for your purposes;
- copies for internal deployment, for example through your software distribution tools or an
  internal network share that only your organization can reach;
- backup and archive copies.

## 2. What you may not do

You may not, and may not help others to:

- redistribute the Launcher publicly or to third parties, sell, rent, lend or sublicense it, or
  host it or offer it as a service for others - point them to the official download pages
  instead;
- modify the Launcher, except as section 3 allows, or distribute works based on it;
- remove or change copyright, license or attribution notices;
- decompile, disassemble or reverse-engineer the Launcher, except as section 3 allows or to the
  extent applicable law expressly permits this despite this restriction;
- use the Launcher to break any law, including sanctions and export-control laws.

The source code of the Launcher is published separately under the **PolyForm Strict License
1.0.0** ([`LICENSE`](LICENSE)). That license governs the source code; this Agreement governs the
use of the official release builds. If they conflict on the use of a release build, this
Agreement applies.

## 3. Security research and verification

You may test, analyze, debug, modify copies of, decompile, disassemble and reverse-engineer the
Launcher on devices and USB drives that you own or are authorized to test, for good-faith security
research. Report what you find as described in [`SECURITY.md`](SECURITY.md) and follow its
disclosure terms. We will not treat research done this way as a breach of this Agreement.

You may also check that a release build matches the published source code, including by building
it yourself from that source.

This section covers only our rights in the Launcher. It does not allow testing of systems you do
not own or are not authorized to test, such as our websites and download servers, and it does
not grant any rights on behalf of others, such as the authors of QEMU or hosting providers.

## 4. Third-party components

The Launcher uses and downloads third-party software, including QEMU (GPL-2.0-or-later). Each
component keeps its own license, listed in
[`packaging/THIRD-PARTY-NOTICES.md`](packaging/THIRD-PARTY-NOTICES.md) and attached to each release.
Nothing in this Agreement limits the rights those licenses give you in those components, including
in copies obtained from the Microsoft Store. The source code of QEMU and of the libraries shipped
with it is published at <https://github.com/dextoros/qemu-runtime/releases>, and
[`packaging/THIRD-PARTY-NOTICES.md`](packaging/THIRD-PARTY-NOTICES.md) contains our written offer
to provide it.

## 5. What the Launcher does on your computer

- **It overwrites the USB drive you select.** Before writing, it shows the drive and asks you to
  confirm. Writing replaces the partitions and contents of the drive: treat everything on it as
  lost and back up anything you need first. This is **not a secure erase** of the whole drive;
  some old data may remain recoverable with special tools.
- It needs **administrator rights** to write the USB drive and, if you choose to turn it on, to
  enable the Windows Hypervisor Platform. Windows asks for your approval each time. The rest of the
  Launcher, including the virtual machine, runs with your normal user rights.
- When you start DexTor OS inside Windows, the Launcher runs it in a virtual machine (QEMU with
  the Windows Hypervisor Platform) and gives it the DexTor OS USB drive.
- It keeps the downloaded image, the QEMU runtime, settings, journals and diagnostic logs in
  `%LOCALAPPDATA%\DexTor`, as described in the [README](README.md).

## 6. Network and privacy

On its own, the Launcher connects only to:

- **dextoros.com**, and the addresses it points to, for the release information, the DexTor OS
  image and its hash and signature;
- the **DexTor mirror on GitHub**, with **qemu.weilnetz.de** as a fallback, for the QEMU for
  Windows installer, which it checks against a pinned hash.

The servers you connect to see your IP address and request details such as the time and the
address requested, as with any download. These connections do not go through Tor.

The Launcher has no built-in analytics and sends no automatic crash or diagnostic reports.
Diagnostic reports are saved on your computer and reach us only if you choose to share them.

This section describes the Launcher itself. What Windows, your browser (for links you open) and
DexTor OS or other software inside the virtual machine do is outside the Launcher and governed by
their own terms.

## 7. DexTor OS, wallets and digital assets

The Launcher prepares and starts DexTor OS; DexTor OS and its applications may come with their
own terms. Neither the Launcher nor this Agreement is financial, investment or legal advice.
**You alone are responsible for your keys, passwords, recovery phrases and digital assets.**
We never ask for them and cannot recover them.

## 8. Beta software

The Launcher is a **beta**. It may contain errors, change or stop working. Do not rely on it as
your only copy of any data or as your only way to access any assets.

## 9. Updates and new versions of this Agreement

We may release updates. Some may be needed to keep using the Launcher, for example to keep image
verification working. Each release is covered by the version of this Agreement it ships with;
a new version applies to the releases it ships with, not to earlier ones.

## 10. No warranty

To the maximum extent permitted by law, the Launcher is provided **"as is" and "as available"**,
without warranties of any kind, express or implied, including merchantability, fitness for a
particular purpose, non-infringement, and that it will be uninterrupted, secure or error-free.

## 11. Limitation of liability

To the maximum extent permitted by law, we are not liable for any indirect, incidental, special,
consequential or punitive damages, or for any loss of data, funds, digital assets, profits or
business, arising from or related to the Launcher, even if we were told such loss was possible.
Our total liability for all claims related to the Launcher is limited to the amount you paid us
for it. The Launcher is provided free of charge.

Nothing in this Agreement limits liability that cannot be limited by law.

## 12. Termination

This Agreement applies until it ends. It ends automatically if you breach it. You may end it at
any time by uninstalling or deleting the Launcher and deleting `%LOCALAPPDATA%\DexTor`. When it
ends, you must stop using the Launcher. Sections 2, 4, 7, 10, 11, 13, 14 and 15 survive.

## 13. Consumer rights

If you use the Launcher as a consumer, you keep any rights that the law of the country where you
live gives you and that cannot be waived by agreement.

## 14. Governing law

**[Placeholder - governing law and venue are to be set with legal counsel.]**

## 15. General

If any provision is held unenforceable, the rest remains in effect. Our failure to enforce a
provision is not a waiver. You may not transfer this Agreement. We may assign it, together with
our rights in the Launcher, to another person or company. This English version prevails over any
translation.

## 16. Contact

<https://dextoros.com> · security reports: see [`SECURITY.md`](SECURITY.md)

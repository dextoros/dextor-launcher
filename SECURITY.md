# Security policy

The launcher writes raw disks with administrator rights and handles a private, encrypted
storage, so we take security reports seriously.

## Reporting a vulnerability

Please report it **privately** through GitHub:
[Report a vulnerability](https://github.com/dextoros/dextor-launcher/security/advisories/new).
Do not open a public issue.

Include, if you can:

- the launcher version (shown at the bottom of the wizard window) and your Windows version;
- what an attacker can do, and the steps to reproduce it;
- a proof of concept, logs or a diagnostic report, with personal data removed.

We will acknowledge the report within 3 working days, keep you informed while we work on a fix,
and credit you in the release notes unless you prefer otherwise.

## Supported versions

Only the latest release receives security fixes.

## Scope

In scope: the launcher, the elevated USB provisioner, the session host, the installer and the
update flow in this repository. QEMU and the DexTor OS guest are separate projects; report
issues in them to their maintainers, or to us if you are unsure where they belong.

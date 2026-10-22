# Security policy

The launcher writes raw disks with administrator rights and provides guest-managed private
storage, so we take security reports seriously.

## Reporting a vulnerability

Please report it **privately** through GitHub:
[Report a vulnerability](https://github.com/dextoros/dextor-launcher/security/advisories/new).
Do not open a public issue.

Include, if you can:

- the launcher version (shown at the bottom of the wizard window) and your Windows version;
- what an attacker can do, and the steps to reproduce it;
- a proof of concept, logs or a diagnostic report, with personal data removed.

We aim to acknowledge the report within 3 working days, keep you informed while we work on a fix,
and credit you in the release notes unless you prefer otherwise.

## Supported versions

Only the latest release receives security fixes.

## Scope

In scope: the launcher, the elevated USB provisioner, the session host, the installer and the
update flow in this repository. QEMU and the DexTor OS guest are separate projects; report
issues in them to their maintainers, or to us if you are unsure where they belong.

## Coordinated disclosure

Please allow up to 90 days from your initial report for investigation and a fix, or disclose
after a fix is released if that happens sooner. Any extension should be explicitly agreed
with you; reporting does not require waiting indefinitely for a fix.

Test only on systems and USB drives you own or have permission to test. Avoid disruption,
denial of service, and access to other people's data. If you encounter personal data,
stop testing and report the minimum details needed to explain the issue privately.

This policy does not grant rights over third-party systems or components. Third-party
licenses remain applicable.

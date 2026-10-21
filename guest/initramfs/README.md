# DexTor initramfs storage adapter

The initramfs hides from Welcome the difference between Windows VM mode and Direct Boot mode.
Before Welcome starts, its one required output is:

```text
/dev/dextor-data -> <validated block device containing data.img>
```

The full discovery, validation, LUKS and systemd ordering contract is described in
[`../welcome/README.md`](../welcome/README.md).

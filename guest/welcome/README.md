# DexTor OS Welcome: protected-storage contract

This is the exact contract for the Welcome component, which runs before GNOME starts. Its job is
to safely find, create or open the single protected storage on the USB drive, and only then let
the desktop session start.

## The main rule

Welcome never looks for `/dev/vda`, `/dev/sda`, `/dev/loop0`, a Windows drive letter or the
`data.img` file. It works only with one alias:

```text
/dev/dextor-data
```

The early-boot storage adapter must create this alias before Welcome starts. If the alias is
missing, Welcome shows a connection error and **formats nothing**.

## Windows VM mode

The launcher attaches `DEXTOR/storage/data.img` as a separate raw VirtIO block device:

```text
-drive file=<USB>/DEXTOR/storage/data.img,if=none,id=dextor-data,format=raw,cache=writeback
-device virtio-blk-pci,drive=dextor-data,serial=DEXTOR_DATA
```

In Linux its stable transport name is:

```text
/dev/disk/by-id/virtio-DEXTOR_DATA
```

The early-boot adapter creates the alias only after these checks:

```sh
source=/dev/disk/by-id/virtio-DEXTOR_DATA
test -L "$source"
device="$(readlink -f -- "$source")"
test -b "$device"
test "$(lsblk -dnro TYPE -- "$device")" = disk
ln -sfn -- "$device" /dev/dextor-data
```

On a typical machine the target is `/dev/vda`, but that is a transport detail, not the contract.

## Direct Boot mode

When booted directly there is no QEMU device. The initramfs adapter must:

1. Find, on **the same physical USB drive DexTor OS booted from**, the GPT partition with the
   partition name `DEXTOR` and the NTFS label `DEXTOR`.
2. Mount it read-write at a private early-boot mount point, for example `/run/dextor/usb`.
3. Check `/run/dextor/usb/DEXTOR/metadata/device.json`: `format == 3`, `state == "ready"`,
   `dataImagePath == "DEXTOR\\storage\\data.img"`.
4. Convert the metadata path to a Linux path, resolve its canonical path and make sure it is
   still inside `/run/dextor/usb/DEXTOR/`.
5. Check that the file is a regular file, not a symlink, that its size equals
   `dataImageSizeBytes`, and that it is at least 1 GiB.
6. Attach the file read-write to a free loop device and point `/dev/dextor-data` at that loop
   device.

The minimal final part:

```sh
loop_device="$(losetup --find --show --direct-io=on -- /run/dextor/usb/DEXTOR/storage/data.img)"
test -b "$loop_device"
ln -sfn -- "$loop_device" /dev/dextor-data
```

Never take the first volume labelled `DEXTOR`: labels can repeat. Its physical parent must be
the boot USB drive; if that is ambiguous, stop.

## Welcome logic

Welcome always has one input: `/dev/dextor-data`.

1. Wait for the alias with a bounded timeout; check `test -b "$(readlink -f /dev/dextor-data)"`.
2. Call `cryptsetup isLuks /dev/dextor-data`.
3. If it is LUKS, show the unlock screen and open the mapper named `dextor-private`.
4. If it is not LUKS, show the first-run screen. Only after the user explicitly confirms, run
   `cryptsetup luksFormat` on **the same** `/dev/dextor-data`, then `luksOpen`, `mkfs.ext4` and
   the initial layout.
5. If the signature is not LUKS but the device holds non-zero or unknown data, **do not
   format**; show a recovery error.
6. After opening, check or create the filesystem on `/dev/mapper/dextor-private`, mount it at
   `/run/dextor/private`, and only after that succeeds continue to GNOME.

## Password and privileges

- The Welcome UI runs as an unprivileged user.
- Block device, cryptsetup and mount operations are done by a root service with a closed,
  typed API; Welcome never builds shell commands from UI input.
- The password is passed only through stdin or a separate file descriptor and never appears in
  argv, the environment, the journal or exception text.
- Never run `cryptsetup`, `mount` or `losetup` with `shell=True`.

## systemd ordering

The storage adapter must finish before Welcome, and Welcome before the display manager and GNOME:

```text
dextor-storage-adapter.service
            ↓
dextor-welcome.service
            ↓
display-manager.service / graphical.target
```

Welcome must never hang forever. Every wait and retry has a timeout and leads to a clear retry
or recovery screen.

## Done when

- VM: `/dev/disk/by-id/virtio-DEXTOR_DATA` exists, and `/dev/dextor-data` points at the same
  block device.
- Direct Boot: `/dev/dextor-data` points at the loop device of exactly the metadata-validated
  `DEXTOR/storage/data.img` on the boot USB drive.
- Switching between VM and Direct Boot opens the same LUKS UUID and the same data.
- A second start never runs `luksFormat` or `mkfs` again.
- When the storage is missing, ambiguous or unknown, GNOME does not start and no data is changed.

# Manual encrypted-host bootstrap

This interface is for the two permanent Servers.Guru application hosts only.
Staging uses a fully allocated 48 GiB file and production uses 64 GiB. Both use
the fixed chain:

```text
/var/lib/hook2stream-data.luks
  -> /dev/loopN
  -> LUKS2 mapper /dev/mapper/hook2stream-data
  -> ext4 mounted at /srv/hook2stream
```

The script never accepts a passphrase in an argument, environment variable, or
key file. It does not write `/etc/crypttab`, a key file, a swap entry in
`/etc/fstab`, or enable an automatic-unlock unit. Keep each environment's
passphrase/recovery material in encrypted operator escrow outside the VPS,
GitHub, Storj, terminal logs, and shell history.

Separately install the exact `proc-hidepid.fstab.example` record in
`/etc/fstab` and remount `/proc` before provisioning runtime secrets. This is a
procfs privacy control only; it is not an automatic LUKS unlock or swap entry.

## First initialization

Stop Docker before changing its data-root. Run only the command matching the
host and keep the terminal attached while `cryptsetup` asks for the new unique
passphrase twice:

```sh
sudo systemctl stop docker.service docker.socket
sudo ./scripts/bootstrap-encrypted-host.sh initialize app staging
# or, on the production host only:
sudo ./scripts/bootstrap-encrypted-host.sh initialize app production
```

Initialization is deliberately narrow. It formats only a backing file created
by that same invocation. If the exact path already exists, the command stops
without formatting it. `unlock` likewise requires a valid existing LUKS2
header and ext4 filesystem. An interrupted or unexpected file must be inspected
and resolved manually; never delete or reformat it merely to make the script
pass.

The command installs root-owned systemd mount, swap, and Docker guard files,
sets Docker's data-root to `/srv/hook2stream/docker`, creates the encrypted
release/config/log/scratch layout, and creates and activates a fully allocated
4 GiB swap file below the encrypted mount. It does not start Docker.

After provisioning secrets and the reviewed host policies, validate and then
start Docker explicitly:

```sh
sudo ./scripts/validate-host.sh app staging   # staging host only
sudo ./scripts/validate-host.sh app production # production host only
sudo systemctl start docker.service
```

## Every reboot

Docker is guarded and cannot start while `/srv/hook2stream` is absent. Use the
verified operator access path or the provider VNC recovery console, then run:

```sh
sudo ./scripts/bootstrap-encrypted-host.sh unlock app staging
# or, on the production host only:
sudo ./scripts/bootstrap-encrypted-host.sh unlock app production

sudo ./scripts/validate-host.sh app staging   # choose the matching environment
sudo systemctl start docker.service
```

`unlock` idempotently reuses the one loop device already attached to the exact
backing file, verifies an already-open mapper points through that loop to the
same file, mounts only the expected ext4 filesystem, and activates only the
encrypted swap file. It refuses duplicate loop attachments, an unexpected
mapper, an unexpected mount source, a missing/invalid LUKS2 header, or an
existing volume without ext4. Use the read-only status command for diagnosis:

```sh
sudo ./scripts/bootstrap-encrypted-host.sh status app staging
sudo systemctl status srv-hook2stream.mount hook2stream-encrypted-swap.service
sudo cryptsetup status hook2stream-data
sudo findmnt /srv/hook2stream
sudo swapon --show
```

Do not enable the mount or swap unit. Manual unlock is an intentional MVP
availability tradeoff; reboot downtime continues until an operator supplies the
off-host passphrase and completes validation.

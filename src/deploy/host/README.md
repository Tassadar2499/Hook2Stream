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

## Installed release control plane and GHCR identity

Install the forced-command control plane from the reviewed current checkout;
never run its rollback program from a release candidate directory:

```sh
sudo install -d -o root -g root -m 0755 /usr/local/libexec/hook2stream/lib
sudo install -o root -g root -m 0555 scripts/deploy-forced-command.sh \
  /usr/local/libexec/hook2stream/deploy-forced-command.sh
sudo install -o root -g root -m 0555 scripts/rollback-application.sh \
  /usr/local/libexec/hook2stream/rollback-application.sh
sudo install -o root -g root -m 0555 scripts/validate-candidate.sh \
  /usr/local/libexec/hook2stream/validate-candidate.sh
sudo install -o root -g root -m 0555 scripts/lib/forced-command-trust.sh \
  /usr/local/libexec/hook2stream/lib/forced-command-trust.sh
sudo install -o root -g root -m 0555 scripts/deploy-forced-launcher.sh \
  /usr/local/sbin/hook2stream-deploy-launcher
```

The launcher and host validator require every installed file above, including
the rollback orchestrator. A successful forward deployment writes rollback
capability protocol v2 and atomically selects
`release-state/active-infrastructure-release.json`. Application rollback keeps
that marker unchanged and uses only its root-private Compose/helper source.
Both the current application release and target must have protocol-v2 records;
pre-v2 releases must be forward-deployed successfully under the new gate before
they can become rollback targets.

Configure GHCR only after the LUKS mount and installed trust helper are active.
Use a distinct GitHub credential and separately generated identity suffix per
environment:

```sh
openssl rand -hex 16 # generate off-host, independently per environment
sudo ../../../deploy/providers/serversguru/configure-ghcr-pull-auth.sh \
  staging ENVIRONMENT_SPECIFIC_GITHUB_USER 32_HEX_ID
```

Copy all four printed non-secret pins to `/etc/hook2stream/deploy.conf`.
The installer proves the credential can log in and pins its Docker auth plus a
root-only environment identity attestation. GitHub does not expose PAT scopes
through this login, so `read:packages`-only and non-reuse across environments
remain explicit operator attestations. Before enabling production, compare the
two hosts off-host and require different credential identity values and auth
hashes. A killed rotation may leave only unique `.config.json.tmp.*` or
`.identity.attestation.tmp.*` files; the next rotation removes trusted
root-owned `0600` instances and rejects symlinks or other debris.

## Authenticated release-gate inputs

Install [`authenticated-e2e.sh`](authenticated-e2e.sh) as
`/usr/local/libexec/hook2stream/authenticated-e2e.sh`, owned by `root:root` and
mode `0500`. It is the complete implementation used by
`post-deploy-e2e.sh`, not a capability stub. Python 3, `docker`, `ffprobe` and
the standard CA bundle must be present on the host.

Create `/srv/hook2stream/e2e` as `root:root` mode `0700` only after the LUKS
mount is active. Install these environment-specific inputs as regular,
non-symlink `root:root` files with mode `0400` or `0600`:

- a short-lived Mozilla/Netscape cookie jar containing the dedicated invited
  test account's OAuth session and CSRF cookies;
- one scalar file containing the exact expected account email;
- a licensed MP3 fixture between 1 KiB and 25 MiB;
- on staging, a reviewed same-SKU soak baseline JSON.

The hook reads their paths from the matching environment file. It checks the
OAuth cookie against `/api/v1/auth/session`, refreshes CSRF only in memory, and
never places Cookie, CSRF, Bearer, Stripe or media-secret values in argv,
stdout, stderr, URLs, or persisted state. A bearer-token file is supported for
a future production authentication scheme via
`HOOK2STREAM_E2E_AUTH_KIND=bearer-token`; it is accepted only when the public
account endpoint authenticates the exact expected account.

The staging baseline has this strict schema and is valid for 90 days:

```json
{"schema":"hook2stream-soak-baseline-v1","environment":"staging","providerSku":"MTL1-3","renderSecondsPerItem":190,"tolerancePercent":20,"recordedAt":"2026-08-28T00:00:00Z"}
```

`renderSecondsPerItem` must come from the retained accepted same-SKU probe; the
number above illustrates the schema and is not an accepted measurement. The
release gate records the real 18-item render duration. The 60-minute soak
rejects a real duration more than 20 percent slower than the baseline, runs a
bounded `lavfi` to null FFmpeg load for 3600 seconds in a networkless,
read-only ephemeral container made from the running immutable worker image
with the same three-vCPU/1536-MiB limits, and performs at least 60 checks.
Any five-minute steal window over 10 percent, cgroup throttled time over 10
percent, OOM/restart, or failed authenticated HEAD/Range check fails closed. A
faster real render is accepted.

Staging must use Stripe test credentials. The gate creates a real test Checkout
Session, signs one bounded `checkout.session.completed` payload with the
root-managed test webhook secret, submits the exact bytes twice, and proves the
second delivery is a duplicate. This path is cryptographically blocked when
the API key or Checkout Session is live. Production uses its dedicated invited
QA account for deterministic encrypted upload/range, OpenRouter pipeline and
preview verification, but performs no billing or final-render POST. The same
immutable digest's staging receipt supplies Stripe test, 18-render/ZIP and soak
evidence. Controlled live payment, render/download and refund remain explicit
post-deploy operator procedures.

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

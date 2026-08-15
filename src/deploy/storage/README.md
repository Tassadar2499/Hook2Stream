# Hook2Stream remote storage runtime

This directory is the standalone storage-host bundle for the closed MVP. One
copy runs on the staging storage VPS and another, with unrelated credentials,
runs on the production storage VPS. It is not the local/CI
`compose.minio.yaml` overlay.

The bundle has two persistent services:

- source-built MinIO, pinned by the storage candidate to an immutable digest;
- official Caddy, also digest-pinned, bound only to the host's Tailscale IPv4
  TCP port 443.

MinIO port 9000 exists only on an internal Docker network. Port 9001 is neither
configured nor published, and the browser console is disabled. Caddy has no
access log, so S3 object keys and signed query parameters are not written to its
logs. There is no UDP listener and no HTTP/3.

This remains single-node storage. It has no replica, automatic failover,
provider snapshot assumption, or independent media copy. Losing the storage
disk can permanently lose media; losing the production storage VPS also loses
the PostgreSQL backups kept there. This is an explicit 90-day closed-pilot
risk, not a durability or SLA claim.

## Fixed topology

| Setting | Staging | Production |
|---|---|---|
| Private URL | `https://h2s-storage-staging.<tailnet>.ts.net` | `https://h2s-storage-production.<tailnet>.ts.net` |
| Encrypted container | 64 GiB | 256 GiB |
| Media bucket | `hook2stream-staging-media` | `hook2stream-production-media` |
| Media hard quota | 35 GiB | 160 GiB |
| Backup bucket | `hook2stream-staging-pg-backups` | `hook2stream-production-pg-backups` |
| Backup prefix | `hook2stream/staging/postgres/` | `hook2stream/production/postgres/` |
| Backup hard quota | 10 GiB | 30 GiB |
| Current backup expiry | 7 days | 35 days |

Both environments use region `us-east-1` and path-style S3. Media versioning is
suspended. Media lifecycle aborts incomplete multipart uploads after one day
and expires temporary objects below the literal `staging/` object prefix after
one day. The latter prefix is an upload-stage namespace in both environments;
it does not mean the staging deployment.

Backup versioning is enabled. A current backup becomes eligible for expiry at
7 or 35 days. A version that becomes non-current is independently retained for
7 or 35 days from that transition, and expired delete markers are removed.
MinIO lifecycle processing is asynchronous, so these are eligibility
boundaries, not a wall-clock deletion SLA. The application purge additionally
uses the absolute object `LastModified` retention boundary plus a two-hour
margin; lifecycle is the storage-side backstop and does not shorten that
application contract.

## Host and encrypted mount

The canonical storage chain is:

```text
/var/lib/hook2stream-storage.luks
  -> LUKS2 mapper hook2stream-storage
  -> /srv/hook2stream-storage
```

The backing file must be fully allocated, root-owned mode `0600`, and remain
locked after reboot until an operator unlocks it from the provider console.
Keep every LUKS recovery key off all VPS instances. The complete construction
and validation procedure is in
[`docs/operations/hook2stream-mvp-runbook.md`](../../../docs/operations/hook2stream-mvp-runbook.md).

Place all storage state below the mounted filesystem:

```text
/srv/hook2stream-storage/docker
/srv/hook2stream-storage/minio-data
/srv/hook2stream-storage/releases
/srv/hook2stream-storage/release-state
/srv/hook2stream-storage/secrets/current
/srv/hook2stream-storage/swapfile
```

Set Docker's `data-root` to `/srv/hook2stream-storage/docker` before pulling an
image. Install the supplied unit drop-in so socket activation or an operator
cannot start Docker against an unencrypted fallback directory:

```bash
sudo install -D -o root -g root -m 0644 \
  src/deploy/storage/host/docker.service.d/hook2stream-storage-mount.conf \
  /etc/systemd/system/docker.service.d/hook2stream-storage-mount.conf
sudo systemctl daemon-reload
```

The drop-in uses `RequiresMountsFor=/srv/hook2stream-storage`, an explicit
`After=srv-hook2stream\x2dstorage.mount`, and
`ConditionPathIsMountPoint=/srv/hook2stream-storage`. Do not start Docker until
the mapper is unlocked, the filesystem is mounted, and encrypted swap is
enabled. The host validator needs a running Docker daemon, so start the guarded
unit only after completing the host, secret, TLS, and environment setup below.

Reserve the three numeric container identities before creating any owned path.
First, every collision check below must print nothing. If one prints an
existing account or group, stop and resolve the collision instead of reusing
it:

```bash
getent passwd 10001; getent group 10001
getent passwd 10002; getent group 10002
getent passwd 10003; getent group 10003
```

Then create the exact non-login identities:

```bash
sudo groupadd --gid 10001 hook2stream-minio
sudo useradd --uid 10001 --gid 10001 --home-dir /nonexistent \
  --shell /usr/sbin/nologin --no-create-home hook2stream-minio
sudo groupadd --gid 10002 hook2stream-storage-caddy
sudo useradd --uid 10002 --gid 10002 --home-dir /nonexistent \
  --shell /usr/sbin/nologin --no-create-home hook2stream-storage-caddy
sudo groupadd --gid 10003 hook2stream-storage-init
sudo useradd --uid 10003 --gid 10003 --home-dir /nonexistent \
  --shell /usr/sbin/nologin --no-create-home hook2stream-storage-init
```

Verify the IDs and read-only host posture. None of these accounts may equal the
operator or deploy user, have a login shell, belong to `docker`, or write host
configuration directories:

```bash
getent passwd hook2stream-minio hook2stream-storage-caddy hook2stream-storage-init
getent group hook2stream-minio hook2stream-storage-caddy hook2stream-storage-init docker
sudo -u hook2stream-minio test ! -w /etc/hook2stream-storage
sudo -u hook2stream-storage-caddy test ! -w /etc/hook2stream-storage
sudo -u hook2stream-storage-init test ! -w /etc/hook2stream-storage
```

Secret-bearing one-shot tools use UID 10003 and `MC_HOST_*` environment
credentials. Prevent other ordinary host users from reading their `/proc`
environment. Add or merge the following `/proc` entry in `/etc/fstab` (never
leave duplicate `/proc` entries), then reboot or remount it. Do not add a
`gid=` visibility exception; both host and forced-deployment validation reject
one even when `hidepid=2` is also present:

```text
proc /proc proc defaults,hidepid=2 0 0
```

```bash
sudo mount -o remount,hidepid=2 /proc
findmnt -no OPTIONS /proc | tr ',' '\n' | grep -E '^(hidepid=2|hidepid=invisible)$'
```

Create the runtime directories only after mounting the encrypted filesystem:

```bash
sudo chown root:root /srv/hook2stream-storage
sudo chmod 0755 /srv/hook2stream-storage
sudo install -d -o 10001 -g 10001 -m 0750 /srv/hook2stream-storage/minio-data
sudo install -d -o root -g root -m 0700 \
  /srv/hook2stream-storage/releases \
  /srv/hook2stream-storage/release-state
sudo install -d -o root -g 2000 -m 0750 \
  /srv/hook2stream-storage/secrets/current
sudo install -o root -g root -m 0600 \
  src/deploy/storage/host/managed-identities.v1.empty \
  /srv/hook2stream-storage/release-state/managed-identities.v1
```

Use a dedicated numeric secrets group; examples use GID `2000`. Neither the
operator nor `hook2stream-storage-deploy` may belong to that group or to the
`docker` group.

## Storage secrets

Create these regular, non-symlink files below the exact
`/srv/hook2stream-storage/secrets/current` directory:

| File | Purpose |
|---|---|
| `minio_root_user` / `minio_root_password` | MinIO boot and local initializer only |
| `s3_bootstrap_access_key` / `s3_bootstrap_secret_key` | read-only app topology/marker probe |
| `s3_runtime_access_key` / `s3_runtime_secret_key` | media ciphertext only |
| `backup_s3_access_key` / `backup_s3_secret_key` | PostgreSQL backup prefix only |
| `storage-tls.crt` / `storage-tls.key` | Tailscale HTTPS certificate and key for Caddy |

The directory is `root:<SECRETS_GID>` mode `0750`. Every listed file is
`root:<SECRETS_GID>` mode `0640`. Credential files contain exactly one
non-empty line and must use only the literal alphabet `[A-Za-z0-9._+-]`; `/`,
`=`, `:`, `@`, whitespace, and padded base64 are rejected because `mc` consumes
the values through `MC_HOST_*`. All eight root/bootstrap/runtime/backup values
must differ, and staging values must never be reused in production. Generate
20-character access IDs (including `minio_root_user`) with
`openssl rand -hex 10`, and 40-character passwords/secret keys with
`openssl rand -hex 20`. Hex is already base64url-safe and has no padding. Write
the output directly into the encrypted `0640` files through an
operator-controlled secret workflow; never place it in `.env`, Git, a candidate
artifact, shell arguments, terminal logs, or CI output.

The local initializer mounts root credentials only into MinIO and the one-shot
`minio-init` tools container. The app receives no root credentials. Storage init
owns buckets, quotas, versioning, lifecycle, policies, and users; therefore app
profiles set their remote lifecycle/multipart configuration flags to `false`.
The bootstrap identity is read-only. Runtime and backup policies are mutually
isolated, and deployment executes live allow/deny probes before Caddy starts.
Every initializer run removes and recreates each current managed user, attaches
one policy, and parses `mc admin user info --json` to require that exact policy
and an empty group set. This clears stale or injected broad grants; expect a
brief authorization gap inside the accepted deployment maintenance window.
Root endpoint credentials use `MC_HOST_*`, and managed user secret keys enter
`mc admin user add` only over stdin, not process arguments.

The encrypted, root-only managed-identity inventory records the current
bootstrap, runtime, and backup access-key IDs. On rotation, init first removes
every prior ID that is no longer one of the three current IDs, verifies the old
user is absent, and only then reconciles the new users. Immediately after init
commits those user mutations—and before any authenticated IAM, isolation, or
Caddy probe can fail—the root deployment wrapper atomically persists the new
inventory. A retry can therefore retire identities created by a failed attempt
instead of orphaning an active credential.
The file is `root:root` mode `0600` at the canonical path shown above; do not
edit it by hand. The root wrapper streams its four strict lines to the one-shot
UID 10003 init process over stdin, so it is never weakened or mounted into a
container. A missing, malformed, stale-permission, or symlinked inventory
blocks deployment. An all-empty first-run inventory is accepted only while the
MinIO data directory is empty; non-empty storage requires the prior audited
inventory and cannot silently bless rogue identities. Run access-key rotation
only in a maintenance window: a failed revoke/create is intentionally
fail-closed and may leave the role unavailable until a forward fix succeeds.

## Tailscale TLS and network policy

Give each storage host its environment-specific Tailscale identity and issue
the exact certificate on that host. The tailnet part is exactly one DNS label:

```bash
sudo tailscale cert \
  --cert-file /srv/hook2stream-storage/secrets/current/storage-tls.crt.new \
  --key-file /srv/hook2stream-storage/secrets/current/storage-tls.key.new \
  h2s-storage-staging.REPLACE_WITH_TAILNET.ts.net
sudo chown root:2000 \
  /srv/hook2stream-storage/secrets/current/storage-tls.crt.new \
  /srv/hook2stream-storage/secrets/current/storage-tls.key.new
sudo chmod 0640 \
  /srv/hook2stream-storage/secrets/current/storage-tls.crt.new \
  /srv/hook2stream-storage/secrets/current/storage-tls.key.new
```

Use the production hostname on the production host. Inspect the new certificate,
verify that its public key matches the private key, then move both `.new` files
over the canonical regular files. Re-run the same immutable deployment so its
`--force-recreate caddy` step binds the new inodes. Do this before certificate
expiry; do not weaken TLS verification or treat the leaf certificate as a CA.

UFW is default-deny. Storage hosts accept only TCP 22 and TCP 443 on
`tailscale0`; neither is opened on the public interface. Deny routed and IPv6
traffic unless an equivalent reviewed IPv6 policy exists. Tailscale ACLs must
allow only the matching app tag to storage TCP 443 and the matching ephemeral
storage-CI tag to SSH. Staging identities must not reach production.

The private, unauthenticated network marker is exactly:

```text
GET /.well-known/hook2stream-storage-protocol
200
body: 1
```

This endpoint is reachable only through Tailscale. Application deployment also
performs an authenticated disposable-object S3 probe; the marker alone is not
an authorization or data-integrity check.

## Environment configuration

Copy the matching example to a root-owned `0600` host file:

```bash
sudo install -d -o root -g root -m 0755 /etc/hook2stream-storage
sudo install -o root -g root -m 0600 \
  src/deploy/storage/environments/staging.env.example \
  /etc/hook2stream-storage/staging.env
```

Replace the Tailscale hostname/IP placeholders and review resource limits. On
the production host use `production.env.example` and `production.env`. Remove
the four example candidate-owned assignments (`STORAGE_RELEASE_VERSION`,
`MINIO_IMAGE`, `MINIO_MC_IMAGE`, and `CADDY_IMAGE`) from the host base file;
the forced command strips any copies and appends only validated digest values
from the candidate.

Do not change bucket names, quotas, region, paths, retention, protocol version,
or object format. `validate-config.sh` rejects drift, a Tailscale IP not assigned
to `tailscale0`, unsafe secret modes, tags instead of digests, and a data path
outside the canonical encrypted mount.

Copy `host/deploy.conf.example` to
`/etc/hook2stream-storage/deploy.conf`, set the exact repository and environment
file, remove `STORAGE_STAGING_SIGNERS` on staging, and keep the file root-owned
mode `0600`.

The MinIO source-approval policy is release-independent host policy, never
candidate authority. From the exact reviewed protected-`main` commit, inspect
the diff and record its SHA-256, then install that reviewed file before sending
the candidate:

```bash
sha256sum src/deploy/storage/minio-security-policy.json
sudo install -o root -g root -m 0600 \
  src/deploy/storage/minio-security-policy.json \
  /etc/hook2stream-storage/minio-security-policy.json
sudo src/deploy/scripts/validate-host.sh storage staging
# Use "production" on the production storage host.
```

Do not copy the policy out of a candidate archive and do not let CI replace it.
The current policy deliberately has an empty `approvedSourceReleases` set, so
both the host gate and Storage CI block the first deployment. Go-live remains
blocked until a supported High/Critical-clean source release and exact commit
receive a positive monotonic `securitySequence` approval in protected main.
Install a reviewed current policy before a forward source upgrade or emergency
revocation; never delete an old approval to emulate rollback, lower a sequence,
or bypass the host validator. The forced command independently validates the
canonical path, root ownership/mode, exact schema, release/commit match, and
monotonic sequence before it mutates the format floor or Docker.

A forward source upgrade changes the pinned `MINIO_RELEASE`/`MINIO_COMMIT` in
`src/deploy/minio/Dockerfile`, the seven-field `storage-release.json`, the CI
source-binding contract, and the root policy approval in the same reviewed main
change. The release-independent host validators accept structural future pins;
after pulling the digest they also require its immutable
`com.hook2stream.minio.source-release` and
`com.hook2stream.minio.source-commit` labels to equal the policy-approved
manifest before MinIO is allowed to start. These dedicated labels are separate
from OCI version/revision metadata, which the GitHub build action sets to the
Hook2Stream repository commit.
Reinstalling the root wrapper is therefore not part of an ordinary approved
forward upgrade.

After the encrypted mount and swap, service identities, `/proc` isolation,
secrets, TLS, Tailscale/firewall policy, environment files, and the
release-independent gate from **Forced-command installation** are ready, start
Docker through the guarded unit. Then run the host validator before any image
pull or candidate deployment:

```bash
sudo systemctl start docker
sudo src/deploy/scripts/validate-host.sh storage staging
# Use "production" on the production storage host.
```

If either command fails, stop here. Do not manually bypass the mount guard or
pull an image to diagnose it.

## Forced-command installation

Install the release-independent gate as root. Preserve the `lib` subdirectory:

`/usr/local`, `/usr/local/sbin`, and `/usr/local/libexec` must be real
root:root `0755` directories; the launcher rejects a writable/symlink parent.

```bash
sudo install -d -o root -g root -m 0755 \
  /usr/local/libexec/hook2stream-storage/lib
sudo install -o root -g root -m 0555 \
  src/deploy/storage/scripts/lib/storage-common.sh \
  /usr/local/libexec/hook2stream-storage/lib/storage-common.sh
sudo install -o root -g root -m 0555 \
  src/deploy/storage/scripts/storage-forced-command.sh \
  src/deploy/storage/scripts/validate-candidate.sh \
  src/deploy/storage/scripts/validate-production-approval.sh \
  /usr/local/libexec/hook2stream-storage/
sudo install -o root -g root -m 0555 \
  src/deploy/storage/scripts/storage-deploy-launcher.sh \
  /usr/local/sbin/hook2stream-storage-deploy-launcher
```

Install `/etc/hook2stream-storage/deploy.conf` and the environment file as
root:root `0600` before running the host validator. The launcher checks the
wrapper directory, `lib` directory, wrapper, validators, and sourced common
library on every invocation; any symlink, owner, group, or mode drift blocks
the root transition.

Create `hook2stream-storage-deploy` as a key-only SSH user with no Docker,
secret-group, or sudo access except the exact launcher rule. Install
`host/sudoers.example` through `visudo -cf`, and install the environment-specific
CI public key using the exact options from `host/authorized_keys.example`:

```text
restrict,command="sudo -n /usr/local/sbin/hook2stream-storage-deploy-launcher"
```

Do not add agent forwarding, port forwarding, PTY, or a general shell. CI sends
only this SSH original command:

```text
deploy-storage storage-candidate-<40-hex-sha>-<run-id>-<attempt>
```

and streams a bounded tar envelope on stdin. The outer envelope contains only
`candidate/`; production additionally contains `approval/` with the exact
signed staging receipt. The validator rejects missing or extra files, tags,
wrong repositories/runs, checksum mismatch, traversal, links, special files,
and expanded-size overflow.

Production also needs a root-owned mode `0600` allowed-signers file. Copy
`host/storage-staging-receipt-allowed-signers.example`, replace the key, and keep
the principal `hook2stream-storage-staging`. Verification uses SSH signature
namespace `hook2stream-storage-staging-receipt`.

Successful stdout is exactly one line beginning
`HOOK2STREAM_STORAGE_REMOTE_RECEIPT=`. All deployment logs go to stderr. The
receipt binds repository candidate identity, image and bundle hashes, actual
digests, and policy/quota/versioning/lifecycle/digest checks.

## Compatibility floor and recovery

`storage-release.json` independently records storage protocol, MinIO on-disk
format, H2SE object format, and the exact MinIO source release and 40-hex
commit. The host policy maps an approved release/commit pair to a unique
positive `securitySequence`. Before the first Docker pull or mutation, the
forced command atomically raises the protocol, storage-format, and MinIO
security-sequence floors in `release-state/storage-format-floor.json`, records
the attempted source identity, and sets `pendingReleaseSha`. Only after all
runtime checks pass does it clear the pending value and update
`lastSuccessfulReleaseSha`.

If a format- or source-changing deployment fails, the raised pending floor
remains. An older security sequence is intentionally ineligible even if it used
to work: recover with a reviewed forward fix at the same or a higher sequence.
This permits an approved patched forward upgrade while rejecting new-to-old
MinIO rollback. If MinIO data is non-empty but the floor file is missing,
deployment fails closed. Do not synthesize or lower the marker; recover the
original release state or perform an explicitly reviewed data recovery.

Storage rollback never changes H2SE ciphertext. Once the application writes an
H2SE v1 object, no application release without an H2SE v1 reader is eligible.

## Reboot procedure

After every reboot:

1. Keep Docker stopped; the mount guard should make an early start fail.
2. Through the provider console, attach the exact backing file to a loop device,
   unlock `hook2stream-storage` interactively, and mount it at the canonical path.
3. Enable only the encrypted 4 GiB swap file.
4. Start Docker through the guarded unit; a missing mount must make it fail.
5. Run the storage host validator, inspect UFW/Tailscale state, then confirm
   MinIO and Caddy health and that no 9000/9001 host listener exists.
6. Perform an authenticated disposable S3 probe from the matching app host.

Manual unlock downtime is accepted for this MVP. Never configure an off-host
key to auto-unlock the storage filesystem.

## Validation and live acceptance

Offline contracts require no daemon or credentials:

```bash
bash src/deploy/storage/tests/validate-storage-deployment.sh
```

Storage CI additionally runs the real server and client digests after both have
passed scanning and before it creates a candidate:

```bash
MINIO_IMAGE='ghcr.io/owner/hook2stream-minio@sha256:<64-hex>' \
MINIO_MC_IMAGE='docker.io/minio/mc@sha256:<64-hex>' \
CADDY_IMAGE='docker.io/library/caddy@sha256:<64-hex>' \
bash src/deploy/storage/tests/run-minio-acceptance.sh
```

The live test creates unique staging and production Compose projects on
internal networks, publishes no MinIO or PostgreSQL ports, and binds each exact
Caddy digest only to a distinct test-loopback TCP 443 address. It generates a
short-lived certificate, verifies Caddy health and the protocol endpoint over
real HTTPS, runs three init transactions, injects an effective broad policy,
forces a post-init failure, and proves both the broad and failed-attempt IDs
are retired on the following rotations,
verifies exact quotas/versioning/lifecycle and effective IAM isolation,
rotates a runtime access-key ID and proves the broad retired identity is denied,
interrupts and aborts a real multipart upload, restarts MinIO, and rechecks
persistent state. Against that same exact MinIO digest, the focused application
integration uploads H2SE ciphertext, exercises plaintext range/download
behavior, and proves plaintext markers are absent from the real stored object.
The runner verifies actual digests and removes its projects/data on exit. It
requires digest-only images; mutable tags are rejected.

Before live Stripe, complete the production restore drill from the main
operations runbook: restore an age-encrypted PostgreSQL backup into an empty
temporary app contour, restore the H2SE keyring from off-host escrow, and decrypt
a real media object. Repeat monthly and before risky migrations.

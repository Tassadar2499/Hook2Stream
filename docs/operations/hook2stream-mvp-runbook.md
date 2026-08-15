# Hook2Stream MVP operations runbook

This is the operator contract for the invite-only paid MVP. It uses four
independent IT-Garage VPS instances: one application host and one remote MinIO
host for each environment. It does not claim high availability, PITR, or media
durability. The risk acceptance expires 90 days after the first paid user.

## Environment matrix and order gate

| Role | Staging | Production |
|---|---|---|
| Public application URL | `https://staging.hook2stream.com` | `https://hook2stream.com` |
| App VPS | RZ-W-4, Germany, 4 vCPU / 8 GB / 160 GB NVMe | FI-MX-5, Finland, 8 vCPU / 16 GB / 240 GB NVMe |
| App encrypted container | 112 GiB at `/srv/hook2stream` | 176 GiB at `/srv/hook2stream` |
| Private storage URL | `https://h2s-storage-staging.<tailnet>.ts.net` | `https://h2s-storage-production.<tailnet>.ts.net` |
| Storage VPS | FI-MX-4, Finland, 4 vCPU / 8 GB / 90 GB NVMe | RZ-W-8, Germany, 8 vCPU / 16 GB / 320 GB NVMe |
| Storage encrypted container | 64 GiB at `/srv/hook2stream-storage` | 256 GiB at `/srv/hook2stream-storage` |
| Media quota | 35 GiB, unversioned | 160 GiB, unversioned |
| PostgreSQL backup quota/retention | 10 GiB / 7 days, versioned | 30 GiB / 35 days, versioned |
| Integrations | Google and Stripe test, dedicated OpenRouter key | Google and Stripe live, dedicated OpenRouter key |

The published VPS total was EUR 95.56 for the displayed billing period when
this contract was written. Before every order, verify SKU, stock, period, VAT,
IPv4 charges, and the current values in the official
[`tariffs.json`](https://it-garage.pro/js/tariffs.json). Stop and review the
architecture when the total exceeds EUR 110 before VAT or any one VPS price
changes by more than 10 percent. Domain, Stripe, OpenRouter, and excess traffic
are not included.

Provisioning is deliberately manual. The repository does not assume an
IT-Garage API, Terraform provider, provider firewall, snapshot product, private
network, or detachable volume because none is part of the public contract.
Before ordering, obtain a written answer to every item in
[`it-garage-support-checklist.md`](./it-garage-support-checklist.md). Production
is blocked unless Ubuntu 24.04 amd64, TUN/Tailscale, a stable public IPv4 for
both app hosts, recovery console access, the FFmpeg workload, and network Fair
Use thresholds are confirmed.

### Current mandatory storage security block

As reviewed on 2026-08-15, no MinIO source release is approved for staging or
production. The final community source pin in this repository is affected by
four applicable High advisories with no patched OSS release:

- [CVE-2026-34204](https://github.com/advisories/GHSA-3rh2-v3gr-35p9);
- [CVE-2026-39414](https://github.com/advisories/GHSA-h749-fxx7-pwpg);
- [CVE-2026-40344](https://github.com/advisories/GHSA-9c4q-hq6p-c237);
- [CVE-2026-41145](https://github.com/advisories/GHSA-hv4r-mvr4-25vw).

The reviewed source allowlist is therefore empty. Storage CI stops before it
publishes or runs that image, no storage candidate can be created, and the
root-owned host gate independently rejects every unapproved release/commit.
Do not treat reverse-proxy workarounds or a scanner miss as approval. Removing
this block requires a separately reviewed choice: supported managed S3,
licensed current AIStor, or an auditable fork carrying regression-tested fixes
for every applicable advisory. The fork option is not recommended for this
MVP. Provisioning may be rehearsed without user data, but staging storage,
production storage, paid traffic, and recovery claims remain blocked.

## Domain and public edge

Immediately before purchase, look up `hook2stream.com` in ICANN Lookup. If it is
registered, stop and ask for a replacement; never choose one automatically.
Register through Cloudflare Registrar and enable 2FA, auto-renew, registrar
lock, DNSSEC, and the registrar's own billing/domain-expiry notifications. Create
DNS-only records:

- `A @` to the production app IPv4;
- `A staging` to the staging app IPv4;
- `CNAME www` to `@`.

Do not create application AAAA records until equivalent IPv6 UFW rules have
been tested. Caddy owns public TLS. Production redirects `www` with status 308;
staging emits `X-Robots-Tag: noindex, nofollow, noarchive`. Google callbacks are
`/api/v1/auth/callback`; Stripe webhooks are
`/api/v1/billing/stripe/webhook`. Unknown Google accounts must fail closed and
production accepts only pre-issued invites.

The MinIO endpoints are not public DNS records. Their `*.ts.net` names and TLS
certificates are supplied by Tailscale. Do not proxy them through Cloudflare.

## File-backed LUKS2 layout

There are no assumed provider volumes. Each host uses one fully allocated,
root-owned mode `0600` backing file on the local NVMe:

| Host role | Backing file | Mapper | Mount |
|---|---|---|---|
| app | `/var/lib/hook2stream-data.luks` | `hook2stream-data` | `/srv/hook2stream` |
| storage | `/var/lib/hook2stream-storage.luks` | `hook2stream-storage` | `/srv/hook2stream-storage` |

Choose the size from the environment matrix. Use `fallocate`, verify allocated
blocks equal the logical size, then use `cryptsetup luksFormat --type luks2` on
a loop device attached to that exact file. The operator owns all unlock keys;
no key or recovery material is stored on an app host, storage host, MinIO, or in
GitHub. Create the filesystem on the mapper, not on the loop device.

App Docker data, named volumes including PostgreSQL, release state, secrets,
logs, worker scratch, and swap live below `/srv/hook2stream`. Storage Docker
data, MinIO data/configuration, release state, secrets, certificates, logs, and
swap live below `/srv/hook2stream-storage`. A swap file must be mode `0600` and
must resolve to the encrypted mount. Never enable a root-filesystem swapfile.

Configure Docker data roots as follows before pulling an image:

- app: `/srv/hook2stream/docker`;
- storage: `/srv/hook2stream-storage/docker`.

Docker and Hook2Stream systemd units must use `RequiresMountsFor`, `After`, and
`ConditionPathIsMountPoint` for their role mount. Automatic loop attachment and
LUKS unlock are forbidden. After every reboot, use the provider console to
attach the backing file to a loop device, unlock the mapper interactively,
mount it, enable encrypted swap, verify the mount, then start Docker through its
mount-guarded systemd unit and run the full host validator immediately. The
validator needs the daemon in order to prove its data-root and volumes.
Downtime until this manual recovery finishes is an accepted MVP limitation.

Validate before first deploy and after every reboot:

```bash
sudo src/deploy/scripts/validate-host.sh app staging
sudo src/deploy/scripts/validate-host.sh app production
sudo src/deploy/scripts/validate-host.sh storage staging
sudo src/deploy/scripts/validate-host.sh storage production
```

The validator must prove the complete mount -> dm-crypt -> loop -> backing-file
chain, LUKS2, exact role minimum size, fully allocated root-owned `0600` backing
file, at least 20 percent free space, encrypted swap, Docker root, Tailscale,
UFW, secret modes, and the role-specific listener policy.

## Network policy

Use key-only SSH and named operator accounts that are not members of `docker`
or any secrets group. Disable root, password, and keyboard-interactive SSH.
Operator and CI SSH use `tailscale0`; do not publish TCP 22 to the Internet.
Storage hosts reserve three distinct non-login identities:
`hook2stream-minio` (`10001:10001`), `hook2stream-storage-caddy`
(`10002:10002`), and `hook2stream-storage-init` (`10003:10003`). The operator
and forced-command deploy users must have different UIDs and may not join any
of those groups, the secrets group, or `docker`.
Mount `/proc` with `hidepid=2` (or the equivalent `hidepid=invisible`) and make
the option persistent before installing the runtime. The host validator rejects
the default world-readable process view so short-lived client commands cannot
expose credentials through `/proc/<pid>/cmdline`.

App hosts publish only 80/TCP, 443/TCP, and 443/UDP. PostgreSQL, PgBouncer, API,
workers, storage, and Docker are never host-published. Storage hosts accept only
SSH and HTTPS TCP 443 on `tailscale0`; MinIO 9000 and console 9001 are internal
and must not listen on a public or wildcard host address. Default-deny inbound,
routed, and IPv6 traffic unless an explicit equivalent rule is installed.

Issue storage certificates on their owning hosts with `tailscale cert`. Mount
certificate and private-key files read-only into storage Caddy. Tailscale ACLs
are environment-specific:

- `tag:hook2stream-app-staging` -> `h2s-storage-staging`:443;
- `tag:hook2stream-app-production` -> `h2s-storage-production`:443;
- `tag:hook2stream-ci-staging` -> staging app:22;
- `tag:hook2stream-ci-production` -> production app:22;
- `tag:hook2stream-storage-ci-staging` -> staging storage:22;
- `tag:hook2stream-storage-ci-production` -> production storage:22.

No staging identity may reach a production host. No storage host may reach the
application databases. The app role-specific Squid proxies remain the only
application egress path. Their generated S3 allowlist contains exactly the one
environment storage hostname; wildcard `*.ts.net` access is forbidden. API may
also reach Google and Stripe, and control workers OpenRouter. Backup traffic may
reach only the exact storage hostname for its environment. A deployment test
must prove both the permitted paths and denial of an unrelated HTTPS origin.

## Remote MinIO contract

Deploy `src/deploy/storage` on the two storage VPS instances. The existing
`compose.minio.yaml` is for local development and CI only; it is never copied to
or invoked on staging or production hosts.

Application profiles keep `STORAGE_MODE=external`, use the private Tailscale
HTTPS endpoint, `S3_FORCE_PATH_STYLE=true`, no browser S3 URL or CORS, and
`S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE=false`. Public Hook2Stream HTTP APIs do
not change. Before an application backup or migration, the release wrapper must
authenticate through the S3 egress proxy, require storage protocol version 1,
and prove PUT, HEAD, one single-range GET, and DELETE against a disposable key.

Each storage environment has four independent credential pairs:

- root: MinIO server boot, idempotent topology initialization, and break-glass
  administration only;
- bootstrap: application deployment checks of the pre-created media bucket and
  protocol marker only;
- runtime: media ciphertext access only;
- backup: age-encrypted PostgreSQL backup access only.

Credentials never cross environments or roles. MinIO stores H2SE v1 ciphertext;
media keyrings stay exclusively on the corresponding app host and in encrypted
operator escrow. Backup objects are age ciphertext; the recovery private key is
operator-held off-host. Buckets are private, media is unversioned, backup is
versioned, object lock/public anonymous policies are absent, and lifecycle
removes expired current/non-current versions and delete markers according to
the matrix.

There is intentionally no replica of either MinIO data disk. Loss or corruption
of a storage VPS can permanently destroy media, and the production backup
bucket shares that storage failure domain. This is a recorded 90-day closed
pilot risk, not a durability claim.

The release-independent approval policy is
`src/deploy/storage/minio-security-policy.json`. Before invoking the storage
forced command, review it from current protected `main` and install that exact
file as `/etc/hook2stream-storage/minio-security-policy.json`, root-owned mode
`0600`; record its commit and SHA-256 outside the host. Never install or trust a
candidate-bundled policy. An empty `approvedSourceReleases` array intentionally
blocks deployment. A reviewed forward release needs an exact release, source
commit, source URL, review date, and monotonically increasing security sequence;
install the new current policy before attempting its candidate. Revoking an
approval blocks future promotion but does not patch an already running server,
which must be stopped or replaced through the incident procedure.

## Secrets and H2SE

Every environment has a separate root-owned secrets directory on its encrypted
mount. Secret files are non-symlinks and mode `0640` for their service group.
OAuth, Stripe, OpenRouter, S3, database, session, invite, age, MinIO, and media
keyring values never enter Git, candidate artifacts, Compose environment output,
or CI logs.

The media keyring is environment-specific H2SE v1. Rotate the active KEK every
90 days; retain old KEKs for reads until inventory is zero. Store encrypted
escrow outside all four VPS instances. `AllowLegacyPlaintextReads=false` from
the first deployment. Once any H2SE v1 object exists, application rollback to a
release without the H2SE v1 reader is forbidden.

## Release promotion

App and storage releases are independent immutable pipelines.

The app `CI` workflow builds `release-candidate-<sha>-<run_id>-<attempt>` once,
deploys staging automatically, and records a signed staging receipt. The
production promotion workflow takes `source_ci_run_id`, requires the protected
`production` Environment and two reviewers, and deploys the exact staged image
digests without rebuild. Application rollback changes application images only,
never runs a down migration, and requires a previously successful compatible
receipt.

The storage workflow first applies the exact source allowlist and therefore
currently stops before publishing the unapproved MinIO pin. Once an exact
source release/commit is approved, it builds that pin, resolves all runtime
images by digest, generates SBOM/provenance, and blocks High/Critical CVEs on
those exact digests. It creates
`storage-candidate-<sha>-<run_id>-<attempt>`, automatically deploys the protected
`storage-staging` Environment, and verifies policies, quotas, versioning,
lifecycle, protocol marker, health, and actual running digests. Production uses
`workflow_dispatch(source_ci_run_id)`, protected `storage-production` approval,
the signed staging receipt, and the exact same digests without rebuild.
Before the production approval boundary and again after approval, promotion
loads the policy from current protected `main` and rescans all three candidate
digests against the current vulnerability database. A 90-day-old staging
receipt cannot override a new advisory or revoked source approval.

All four CI connections are ephemeral Tailscale nodes using GitHub OIDC workload
federation, distinct tags and SSH keys, pinned ED25519 host keys, OpenSSH
`StrictHostKeyChecking=yes`, forced commands, and host `flock`. Deploy users do
not have Docker or secrets access. Candidate validators reject tags, unknown or
duplicate image variables, repository/run mismatch, malformed checksums,
symlinks, special files, and archive traversal.

Before either host validator can pass, the encrypted mount root is root:root
`0755`; its release/config/state directories are root-private; deploy configs,
environment files, signer files, and release markers are root:root `0600`.
Each sudo-target launcher is root:root `0555` and revalidates the complete
installed wrapper/validator/library set plus non-writable parent directories
before executing it. Permission drift is a deployment stop, never an automatic
repair.

Storage downgrade after an on-disk format or protocol change is forbidden.
Record the current format/protocol marker on the encrypted storage mount and use
a forward fix. App schema rollback similarly uses a forward fix or a separately
approved restore with writes stopped; it never executes a down migration.

## Backup, restore, and loss drills

The app backup sidecar runs hourly, streams a custom-format `pg_dump` to `age`,
uploads dump and checksum, then publishes an authenticated manifest last. The
age identity is never installed on a VPS. Alert when the newest successful
backup marker is older than two hours.

Before enabling live Stripe, prove this exact recovery path:

1. Treat the app host as lost and provision an empty temporary app contour.
2. Download a production backup manifest, checksum, and ciphertext from the
   production MinIO host; verify the checksum and decrypt off-host.
3. Restore into an empty PostgreSQL instance with writes isolated.
4. Restore the matching escrowed H2SE keyring, fetch a real media object, and
   prove authenticated decrypt plus HTTP Range playback.
5. Record measured RPO/RTO and destroy temporary plaintext and credentials.

Repeat monthly and before risky migrations. Also reboot all four VPS instances
and perform the manual loop/LUKS/mount/swap/Docker sequence. A restore never
overwrites the live database without explicit approval and stopped writes.

## Go-live gates

The current empty MinIO approval set is the first gate and blocks both storage
environments. None of the remaining acceptance results overrides it.

Storage acceptance must prove idempotent initialization, credential isolation,
private policies, quotas, versioning, retention, restart, multipart abort,
protocol v1, H2SE upload/range/download, and absence of public 9000/9001.

Staging app acceptance must prove invite-only OAuth, licensed MP3 upload, every
worker, OpenRouter analysis/artwork, preview seeking, 18 renders, ZIP, Stripe
test checkout and duplicate webhook, concurrent deployment exclusion,
idempotent same-SHA deployment, compatible old-new-old application-only
rollback, egress denial, and at least 20 percent free space.

Run a 60-minute render/network soak. IT-Garage's
[`AUP`](https://it-garage.pro/aup) guarantees a standard vCPU only 25 percent of
a physical core, prohibits sustained 100 percent use, and permits Fair Use
throttling. Any throttling, AUP conflict, OOM, sustained queue growth, or
unacceptable render time blocks production.

Production additionally requires the signed staging receipts, both recovery
drills, TLS/security headers, 308 `www`, controlled live payment/refund,
encrypted upload/download/range, actual image digest verification, and at least
30 minutes of observation after release. Lack of two GitHub reviewers blocks
live payments but not staging.

External uptime monitoring, backup/storage heartbeats, and automated operational
alerts are deferred for the MVP. Operators must inspect local Docker health,
backup age, disk usage, OOM events, queue age, gateway 5xx/GCM failures, MinIO
health, denied egress, and TLS certificate state during release observation and the
scheduled recovery/reboot drills. This accepted lack of automated notification
does not relax any health, backup, soak, or recovery gate, and private MinIO must
never be exposed for inbound monitoring. The
[`public offer`](https://it-garage.pro/public-offer) states 97 percent SLA and
permits deletion three days after payment suspension; enable account 2FA,
automatic payment, and an independent billing alert before storing data.

Before updating a test host that used the removed external-heartbeat bundle,
stop and remove its old storage timer before deploying the new storage candidate:

```bash
sudo systemctl disable --now hook2stream-storage-heartbeat.timer || true
sudo systemctl stop hook2stream-storage-heartbeat.service || true
sudo rm -f \
  /etc/systemd/system/hook2stream-storage-heartbeat.timer \
  /etc/systemd/system/hook2stream-storage-heartbeat.service \
  /usr/local/libexec/hook2stream-storage/storage-heartbeat.sh
sudo systemctl daemon-reload
```

After the new app and storage host validators pass and `systemctl` confirms no
consumer remains, remove the obsolete `backup_heartbeat_url` and
`storage_heartbeat_url` files from their respective encrypted secret directories.
If Vault was rehearsed, remove `heartbeat_url` from `backup-s3` before its next
strict render; only `access_key_id` and `secret_access_key` are accepted.

At 80 percent encrypted-container usage, the local-file design cannot be grown
like an attached cloud volume: schedule a tariff migration or rebuild and prove
restore first. Before public signup, replace self-managed PostgreSQL with a
managed service offering 35-day PITR and replace standalone MinIO with managed
S3 or replicated MinIO plus an independent media copy.

# Hook2Stream single-node closed-alpha bundle

This directory is the budget deployment for one Linux host (for example a
Hetzner Cloud VM) plus external S3-compatible object storage. Caddy is the only
service with published ports. It terminates TLS and serves a single origin:
`/api/*` goes to ASP.NET Core and every other route goes to Next.js. PostgreSQL,
PgBouncer, the API, and all workers stay on Docker networks without host ports.

This topology is suitable for a closed alpha or an operationally accepted
single-node beta. It is **not** the paid-beta NFR topology: one host is a failure
domain, PostgreSQL is self-managed, logical backups are not PITR, and there is
only one control-plane instance. Moving PostgreSQL to a managed 35-day PITR
service and running at least two API/web instances is the production upgrade
path; the application and S3 settings remain the same.

## Temporary single-host MinIO staging

`STORAGE_MODE=minio` activates `compose.minio.yaml` for a personal,
production-like staging environment. It adds a source-built MinIO server on an
isolated Docker network, exposes only its signed S3 API through Caddy, and keeps
the console and administrative API private. The normal `external` storage mode
and its production topology are unchanged.

This profile is deliberately **disposable**. PostgreSQL, media, and encrypted
logical backups all live on the same unencrypted VPS disk, so a host or disk
loss destroys every copy. Use it for no more than 30 days and never admit a
second user; move both media and backups to a maintained external S3 provider
before either limit is reached. The MinIO community source repository is
archived, so CI builds the final CVE-fixed source release
`RELEASE.2025-10-15T17-29-55Z` instead of using the older archived Docker Hub
server image.

For the IT-Garage staging profile, provision an amd64 Ubuntu 24.04 VM with 8
vCPU, 16 GB RAM, and 320 GB NVMe. Configure two DNS-only A records pointing to
its static IPv4 address:

- `APP_DOMAIN=staging.<base-domain>` and
  `PUBLIC_ORIGIN=https://staging.<base-domain>`;
- `S3_PUBLIC_DOMAIN=s3-staging.<base-domain>` and
  `S3_PUBLIC_SERVICE_URL=https://s3-staging.<base-domain>`.

Set the remaining storage values exactly as follows. The release preflight
accepts cleartext only for these two internal Docker-network endpoints and
continues to require HTTPS for the browser-visible origin.

```dotenv
STORAGE_MODE=minio
S3_SERVICE_URL=http://minio:9000
S3_PUBLIC_SERVICE_URL=https://s3-staging.<base-domain>
S3_PUBLIC_DOMAIN=s3-staging.<base-domain>
S3_REGION=us-east-1
S3_MEDIA_BUCKET=hook2stream-staging-media
S3_FORCE_PATH_STYLE=true
S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE=false
MINIO_MEDIA_QUOTA_GIB=180
MINIO_BACKUP_QUOTA_GIB=20
BACKUP_S3_ENDPOINT=http://minio:9000
BACKUP_S3_REGION=us-east-1
BACKUP_S3_BUCKET=hook2stream-staging-pg-backups
BACKUP_S3_PREFIX=hook2stream/staging/postgres
BACKUP_RETENTION_DAYS=7
POSTGRES_MAX_CONNECTIONS=50
POSTGRES_SHARED_BUFFERS=512MB
POSTGRES_EFFECTIVE_CACHE_SIZE=1536MB
POSTGRES_MAINTENANCE_WORK_MEM=64MB
POSTGRES_WORK_MEM=4MB
```

Pin `MINIO_IMAGE` and `MINIO_MC_IMAGE` by digest and add the two conditional
root files documented in `secrets/README.md`. MinIO creates separate identities
from the existing runtime, bootstrap, and backup S3 credential pairs; no
application container receives the root secrets. The media bucket is
unversioned and capped at 180 GiB. The backup bucket is versioned, capped at 20
GiB, and retains encrypted PostgreSQL recovery points for seven days.

The selected MinIO server rejects its standalone abort-incomplete-multipart
lifecycle rule, so the staging profile disables that one external-S3 safety
net. Application error paths still abort failed multipart uploads directly;
the 30-day disposal limit remains the outer cleanup bound.

Community MinIO does not implement per-bucket `PutBucketCors`. In this profile,
its wildcard global CORS default is disabled and `Caddyfile.minio` answers CORS
only for the path-style media bucket, only for the exact application origin.
The backup bucket receives no browser CORS headers. The external-S3 profile
continues to configure native bucket CORS through the bootstrapper.

On this 16 GB host, create a 4 GB swap file as an emergency OOM guard, keep at
least 20% disk space free, and run only one instance of every worker pool. Swap
is not capacity for normal renders. The MinIO overlay lowers PostgreSQL and
non-render worker memory ceilings while preserving 3 GB for the render worker.

The base domain itself is intentionally an operator-provided value. Register
the exact application callback and webhook only after both public hostnames
resolve and Caddy can issue their certificates.

Prepare the persistent layout before changing Docker's `data-root`:

```bash
sudo install -d -o root -g root -m 0755 \
  /srv/hook2stream \
  /srv/hook2stream/docker \
  /srv/hook2stream/releases
sudo install -d -o root -g root -m 0700 \
  /srv/hook2stream/config \
  /srv/hook2stream/release-state
sudo install -d -o root -g 2000 -m 0750 \
  /srv/hook2stream/secrets/current
```

Keep `/srv/hook2stream/release-state` as a real root-owned directory with mode
`0700`; every parent must also be root-owned and non-writable by group/others.
Do not replace it or any parent component with a symlink. The release preflight
fails closed instead of following or repairing an unsafe state path.

On a fresh host, configure Docker Engine's `data-root` as
`/srv/hook2stream/docker` before starting any application containers. Create
the emergency swap once, record it in `/etc/fstab`, and keep swappiness low:

```bash
sudo fallocate -l 4G /swapfile
sudo chmod 0600 /swapfile
sudo mkswap /swapfile
sudo swapon /swapfile
printf '%s\n' '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
printf '%s\n' 'vm.swappiness=10' | sudo tee /etc/sysctl.d/90-hook2stream.conf
sudo sysctl --system
```

Run `sudo ./scripts/validate-staging-host.sh` after Docker Compose v2 and the
UFW firewall are configured. The check fails when the host is undersized,
Docker stores data outside `/srv/hook2stream`, free disk falls below 20%, a
private application/storage port is bound to every host interface, or UFW does
not default-deny inbound traffic with the exact public/restricted rules below.

### IT-Garage host hardening

`my.it-garage.pro` is the provider control panel, not an application hostname.
Confirm that the RZ-W-8 offer, German location, price, and stock still match the
required 8 vCPU / 16 GB / 320 GB profile before ordering. After Ubuntu boots:

1. Create a named operator with `sudo` and install only the operator's public
   key in its `authorized_keys`. Do not add that account to the `docker` group;
   releases intentionally run through `sudo`.
2. In `/etc/ssh/sshd_config.d/90-hook2stream.conf`, set
   `PermitRootLogin no`, `PasswordAuthentication no`,
   `KbdInteractiveAuthentication no`, and `PubkeyAuthentication yes`. Run
   `sudo sshd -t`, keep the original session open, and prove a second key-only
   operator login before reloading SSH.
3. Default-deny inbound traffic. Permit 22/TCP only from the operator CIDR and
   permit 80/TCP, 443/TCP, and 443/UDP publicly. Apply the same policy in the
   provider firewall when it is available; do not open any Compose-private
   port. Configure explicit UFW port rules rather than broad application
   profiles:

   ```bash
   sudo ufw default deny incoming
   sudo ufw default allow outgoing
   sudo ufw default deny routed
   sudo ufw allow from '<operator-cidr>' to any port 22 proto tcp
   sudo ufw allow 80/tcp
   sudo ufw allow 443/tcp
   sudo ufw allow 443/udp
   sudo ufw enable
   ```
4. Install Docker Engine from Docker's official Ubuntu repository with the
   Compose v2 plugin. Set `/etc/docker/daemon.json` to use
   `"data-root": "/srv/hook2stream/docker"` before the first workload starts.
   Enable Ubuntu unattended security upgrades and reboot deliberately when a
   kernel or container-runtime update requires it.
5. Log in to GHCR with a dedicated token that has only `read:packages`; keep it
   in Docker's root credential store, never in `.env` or a secret mounted into
   the application.

Create DNS-only A records for both public names. If an AAAA record is added,
first apply the same SSH/firewall policy to IPv6 and verify that Caddy can reach
the ACME service over that route.

### Staging accounts and first release

- Keep Google OAuth in **Testing** status, add only the owner as a test user,
  and register exactly
  `https://staging.<base-domain>/api/v1/auth/callback`.
- Use Stripe test mode, create the five products/prices listed below, and
  register exactly
  `https://staging.<base-domain>/api/v1/billing/stripe/webhook`.
- Issue a dedicated OpenRouter key, require ZDR, deny data collection, restrict
  it to the configured three models, and cap the account/key at USD 20 monthly.
- Store the non-secret staging environment at
  `/srv/hook2stream/config/staging.env`; keep file secrets under
  `/srv/hook2stream/secrets/current`. The environment sets
  `HOOK2STREAM_RELEASE_STATE_DIR=/srv/hook2stream/release-state`.

Release only a commit whose verify, full-stack, container scan, and publish jobs
are green. Download `release-images-<sha>/release-images.env`, copy its digest
references into the staging environment, resolve the remaining infrastructure
images to reviewed digests, and check out that same SHA on the VPS. Then run:

```bash
test "$(git rev-parse HEAD)" = '<green-commit-sha>'
sudo env \
  HOOK2STREAM_ENV_FILE=/srv/hook2stream/config/staging.env \
  ./src/deploy/scripts/deploy-release.sh
```

Use the same command for upgrades. The script records the previous environment
under the configured release-state directory and prints the rollback command;
rollback changes image digests only and never runs a down-migration.

## One-time setup for the external-S3 alpha

This section describes the existing Hetzner/LUKS/external-S3 profile. For the
temporary IT-Garage staging profile, use the unencrypted single-disk procedure
above and do not mix the two storage contracts.

1. Provision a Hetzner CX43-class x86_64 host in Helsinki with Ubuntu 24.04,
   Docker Engine, and the Docker Compose plugin v2 or newer. Attach a 160 GB Cloud Volume,
   encrypt it with LUKS, and mount it at `/srv/hook2stream`; put Docker's
   `data-root`, release checkouts, release state, and secrets there. Configure
   systemd so Docker requires that mount, and document the manual LUKS unlock
   required after a reboot. Restrict SSH to the operator IP and expose only TCP
   80/443 and UDP 443. Point the `APP_DOMAIN` A record at the host; add AAAA only
   after IPv6 is configured. Use DNS-only mode when the DNS provider offers an
   HTTP proxy. Authenticate private GHCR pulls with a dedicated `read:packages`
   credential in Docker's credential store, never in `.env`.
2. Copy `.env.example` to `.env`. `APP_DOMAIN` and the host in `PUBLIC_ORIGIN`
   must match. Register `${PUBLIC_ORIGIN}/api/v1/auth/callback` in Google and
   `${PUBLIC_ORIGIN}/api/v1/billing/stripe/webhook` in Stripe.
3. Set `SECRET_PROVIDER=file`, `SECRETS_DIR=/srv/hook2stream/secrets/current`,
   and `SECRETS_GID=2000`. Create every file in `secrets/README.md` as
   `root:2000` mode `0640` in a `root:2000` mode `0750` directory. Run releases
   through `sudo`; preflight rejects missing files, symlinks, or weaker/different
   ownership and modes. Keep the runtime media, bucket-bootstrap, and backup
   credentials separate. `ReverseProxy__TrustAllProxies` trusts Docker-network
   peers. This is acceptable here because the API has no published port and
   Caddy is the only
   container accepting external traffic. Use a fixed `KnownProxies` address
   instead if the host's network policy permits stable container addressing.
4. In AWS `eu-north-1`, pre-create separate private media and backup buckets with
   Block Public Access, Bucket Owner Enforced ownership, provider encryption,
   and a policy denying non-TLS requests. Keep media versioning disabled so
   product deletion remains final; allow CORS only from `PUBLIC_ORIGIN`. Enable
   versioning only on the backup bucket and apply `backup/lifecycle-policy.json`
   so current and non-current objects are permanently gone within 35 days. The
   one-shot bootstrapper configures media CORS and staging/multipart lifecycle
   rules. The backup credential also needs
   version-list and permanent version/delete-marker deletion permissions under
   `BACKUP_S3_PREFIX`. Assign the active backup passphrase a non-secret key ID
   such as `2026-q3-01`; an ID is immutable and must never be reused for different
   key material.
5. Confirm that the OpenRouter key or account guardrail enforces Zero Data
   Retention. The compose file acknowledges that external control only after the
   operator has verified it.
6. Create two external HTTPS monitors for `/health/ready` and
   `/health/api-ready`. Put the backup monitor's secret HTTPS URL in
   `backup_heartbeat_url`; use an hourly schedule with a 130-minute grace period.

For an AWS-compatible CLI, backup-bucket protection can be applied with:

```bash
aws --region eu-north-1 s3api put-bucket-versioning \
  --bucket hook2stream-alpha-ACCOUNT-pg-backups \
  --versioning-configuration Status=Enabled
aws --region eu-north-1 s3api put-bucket-lifecycle-configuration \
  --bucket hook2stream-alpha-ACCOUNT-pg-backups \
  --lifecycle-configuration file://backup/lifecycle-policy.json
```

The lifecycle policy matches the whole bucket, so do not apply it to a shared
bucket.

## External account configuration

- Google OAuth: register `${PUBLIC_ORIGIN}/api/v1/auth/callback` as the exact
  redirect URI.
- Stripe: stay in test mode and create USD prices matching the application
  contract: `art_credits_5` = $1, `mini_release` = $5, `release_pack` = $9.90,
  `clean_cover` = $2, and recurring `active_artist` = $29/month. Register
  `${PUBLIC_ORIGIN}/api/v1/billing/stripe/webhook` for
  `checkout.session.completed`, `checkout.session.async_payment_succeeded`,
  `checkout.session.expired`, `checkout.session.async_payment_failed`,
  `invoice.paid`, and `charge.refunded`.
- OpenRouter: allow only `openai/whisper-large-v3`,
  `bytedance-seed/seedream-4.5`, and `openai/gpt-oss-120b`; enforce Zero Data
  Retention, disable prompt logging/data collection, and set an initial
  $20/month account budget. Verify all three with one short, licensed MP3 before
  admitting alpha users.
- Monitoring: create Better Stack monitors for the two readiness URLs and one
  hourly backup heartbeat. Email alerts must go to an inbox watched by the
  operator.

## Build and preflight

CI should build from a clean commit, scan each runtime image, emit an SBOM, push
the commit-SHA tags, and resolve those tags to registry digests. Production
`.env` values should use `image@sha256:...`, not mutable tags. The browser uses
relative API paths behind Caddy, so the same web image can move between
same-origin environments without rebuilding it for a hostname.

`scripts/validate-deployment.sh` syntax-checks every deployment shell script,
runs the focused tests under `tests/`, parses the lifecycle policy, validates
both Caddy configurations, and renders external, MinIO, build, and Vault
Compose models with temporary placeholder secret files. Every rendered service
must use an immutable `image@sha256` reference. The repository workflow runs it
as a dedicated CI step; it can also be run locally before pushing when Docker
Compose v2 and Node 24 are available.

The verify job also boots the audited MinIO image and the production MinIO
Caddyfile, runs the real initializer twice, checks quotas/versioning/lifecycle
and IAM denials, and exercises HTTPS CORS plus signed single-part and multipart
uploads through the public S3 origin.

On a successful `main` publish, CI emits the artifact
`release-images-<commit-sha>/release-images.env`. Review it and copy the
digest-pinned `API_IMAGE`, `WORKER_IMAGE`, `BOOTSTRAPPER_IMAGE`, `WEB_IMAGE`,
`POSTGRES_BACKUP_IMAGE`, `MINIO_IMAGE`, and `RELEASE_VERSION` assignments into
the selected environment file. This artifact is the release handoff; do not
translate its digest references back to mutable tags. `MINIO_IMAGE` is ignored
in external mode.
Resolve and pin `CADDY_IMAGE`, `POSTGRES_IMAGE`, and `PGBOUNCER_IMAGE` by digest
as well. MinIO mode additionally requires a reviewed `MINIO_MC_IMAGE` digest
for the one-shot initializer. The release preflight rejects every mutable
application or infrastructure image reference that the selected mode uses.

The optional build overlay builds the four application artifacts and backup
sidecar from the repository root:

```bash
compose_env=${HOOK2STREAM_ENV_FILE:-.env}
compose_files=(-f compose.yaml)
if grep -qx 'STORAGE_MODE=minio' "$compose_env"; then
  compose_files+=(-f compose.minio.yaml)
fi
compose_files+=(-f compose.build.yaml)
docker compose --env-file "$compose_env" "${compose_files[@]}" \
  build api worker-media bootstrapper web postgres-backup
docker compose --env-file "$compose_env" "${compose_files[@]}" \
  push api worker-media bootstrapper web postgres-backup
```

Before every deployment, validate the fully interpolated model and inspect its
image list. This catches missing required values without printing secret file
contents:

```bash
compose_env=${HOOK2STREAM_ENV_FILE:-.env}
compose_files=(-f compose.yaml)
if grep -qx 'STORAGE_MODE=minio' "$compose_env"; then
  compose_files+=(-f compose.minio.yaml)
fi
docker compose --env-file "$compose_env" "${compose_files[@]}" \
  --profile tools config --quiet
docker compose --env-file "$compose_env" "${compose_files[@]}" \
  --profile tools config --images
```

The mode check is intentional: raw Compose commands do not automatically load
`compose.minio.yaml`. Keep the same `compose_files` selection for every manual
staging inspection or incident command.

The release preflight rejects non-HTTPS application and custom backup S3
endpoints. For native AWS S3, leave `BACKUP_S3_ENDPOINT` empty so the AWS CLI
uses the regional endpoint selected by `BACKUP_S3_REGION`.
It also rejects mutable application and infrastructure image tags; replace all
eight base image values and, in MinIO mode, both additional image values with
reviewed `image@sha256:<digest>` references first.
Exported variables that could override Compose inputs are rejected as well;
make deployment changes in the selected `.env` file so preflight validates the
same values that Compose will use.

## Initial deployment and releases

Use the idempotent release command for both the first install and upgrades:

```bash
sudo ./scripts/deploy-release.sh
```

Use `--no-pull` only for images just built on that host. The script serializes
deployments, validates Compose, records the last successful environment, pulls
all images and, in MinIO mode, starts and checks object storage and runs its
idempotent initializer first. It then starts PostgreSQL/PgBouncer, requires an
encrypted pre-migration backup, runs the bootstrapper exactly once, updates leaf
workers → control → API → web/Caddy, waits on role readiness, and smokes web,
API, and public MinIO readiness through HTTPS. It also requires the persistent
backup daemon to become healthy before recording the release as successful.
Re-running it with the same environment is safe.

Workers receive a 150-second shutdown grace period, longer than the 120-second
lease. Jobs are retryable and fenced, but this Compose topology does not promise
zero-downtime rolling replacement. Drain paid work or schedule a maintenance
window before replacing render/export workers. Database changes must follow
expand/contract compatibility so the previous application images can still run.

After rollout, check service health, the internal dependency-ready endpoint, and
the public web endpoint:

```bash
compose_env=${HOOK2STREAM_ENV_FILE:-.env}
compose_files=(-f compose.yaml)
if grep -qx 'STORAGE_MODE=minio' "$compose_env"; then
  compose_files+=(-f compose.minio.yaml)
fi
docker compose --env-file "$compose_env" "${compose_files[@]}" ps
docker compose --env-file "$compose_env" "${compose_files[@]}" exec api \
  /bin/sh /opt/hook2stream/http-healthcheck.sh
curl --fail --silent --show-error https://app.example.com/health/ready
curl --fail --silent --show-error https://app.example.com/health/api-ready
```

Then run an OAuth/session smoke and one short MP3 flow through upload, all five
worker pools, SSE reconnect, preview/render, ZIP export, and a Stripe test event.
Watch 5xx rate, queue age, retries, render duration, and backup freshness for at
least 30 minutes. The external monitors must alert independently on web and API
availability; the success heartbeat must alert when the recovery-point age
exceeds 130 minutes. Leave OTLP disabled for the alpha unless an approved EU
collector and its data-retention policy are configured.

For the temporary staging profile, acceptance also requires all of the
following before it can be called ready:

- the media bucket accepts HTTPS presigned single-part and multipart uploads
  from the exact application origin, exposes `ETag`, supports read/delete, and
  applies its staging-object expiry rule;
- the backup bucket is versioned, has the seven-day lifecycle and 20 GiB quota,
  while the media bucket is non-versioned with a 180 GiB quota; the console and
  MinIO admin/metrics routes are unavailable publicly;
- Google login → licensed short MP3 → all five worker pools → OpenRouter
  transcription/artwork/campaign → preview → 18 renders → ZIP completes;
  Stripe test checkout succeeds and replaying the same webhook does not grant a
  second entitlement;
- a second identical deploy succeeds, a VPS reboot returns every persistent
  service to healthy, the previous digest snapshot rolls back without a
  down-migration, and one encrypted dump restores into a new empty database;
- no container is OOM-killed and at least 20% of the filesystem remains free
  throughout the 30-minute observation window.

## Backup, restore, and rollback

`postgres-backup` performs an hourly `pg_dump` in custom format and encrypts it
client-side with AES-256-CBC/PBKDF2-HMAC-SHA-256. Every completed recovery point
has three objects whose basename contains its validated encryption key ID:
`.dump.enc`, `.dump.enc.sha256`, and `.dump.enc.manifest.json`. The manifest
records the key ID, cipher/KDF parameters, encrypted-dump checksum, and exact
object keys; it contains no passphrase. It is uploaded last and is the completion
record for the set.

After every upload the backup process lists every object version and delete
marker under its prefix and permanently deletes entries approaching 35 days;
the two-hour safety margin ensures an hourly healthy scheduler removes them
before the boundary. The bucket lifecycle is defense in depth: current versions
expire after 34 days, non-current versions one day later, and expired delete
markers are removed. This avoids the common `35 days current + 35 days
non-current = 70 days` mistake. Monitor health from outside the host; a monitor
on the same failed VM is insufficient.

The freshness marker is replaced atomically only after the dump, checksum, and
manifest uploads and the version/delete-marker purge all succeed. The external
success heartbeat is sent only after that marker is committed; heartbeat
delivery failure is logged but does not invalidate the recovery point. The
endpoint must return a direct 2xx response; redirects count as delivery
failures. The marker records the timestamp and key ID. A later failure preserves
the previous
complete cycle, but the healthcheck fails immediately when the configured key
ID differs from the last successful backup. The marker lives on tmpfs, so a
restarted backup container must complete a fresh cycle before becoming healthy.

Treat each encryption key ID as an immutable lookup key. The mutable Vault
record `hook2stream-kv/data/production/backup-encryption/current` contains the
active ID and passphrase rendered for the backup process. Before CAS-updating
that record, archive the same key material at the create-once path
`hook2stream-kv/data/production/backup-encryption/keys/<key_id>` and never update
that key path. With the file provider, archive a
root-owned `0600` copy under the same ID before replacing the two active files.
Retain each historical passphrase for at least **49 days after the last backup
made with it**: the 35-day backup window plus a 14-day restore/operations margin.
Vault KV v2 version count is not sufficient protection because exceeding
`max_versions` permanently destroys the oldest version. The runtime renderer
may read only `current`; a separate break-glass restore identity may read
`backup-encryption/keys/+`. Before destroying any historical key, prove that no
retained manifest references its ID.

Once a month, restore a selected recovery point into a **new, empty database**:

1. Download its `.manifest.json` first and validate `schemaVersion`,
   `kind`, object prefix, and the SHA-256-shaped digest.
2. Read `encryption.keyId` and fetch that exact immutable passphrase from Vault
   or the protected file-provider archive. Never decrypt an old backup with the
   current/latest key by assumption.
3. Download the manifest's `encryptedDump.objectKey` and `checksum.objectKey`,
   run `sha256sum -c`, and also compare the ciphertext digest with
   `encryptedDump.sha256`.
4. Decrypt with the manifest's cipher/KDF/iteration values, run
   `pg_restore --exit-on-error --no-owner`, and start a temporary API against the
   restored database.

Verify users, projects, entitlements, queued jobs, object references, and
reapplied deletion tombstones; record actual RPO/RTO. Every key rotation drill
must restore one recovery point from before the rotation and one from after it.
Never test `--clean` against the production database.

After each success, the release script stores `last-successful.env` under
`HOOK2STREAM_RELEASE_STATE_DIR` (or under local `.release-state` when that
setting is omitted). Before the next release it snapshots that file and prints
the exact snapshot path.
For an application rollback, first verify the migration was expand/contract
compatible, then run the printed command, for example:

```bash
sudo env \
  HOOK2STREAM_ENV_FILE=/srv/hook2stream/release-state/20260727T120000Z.env \
  ./src/deploy/scripts/deploy-release.sh
```

This restores old image digests through the same ordered and health-checked flow.
Do not run an automatic down-migration. If a migration itself is defective, stop
writes and follow the documented database recovery decision: forward-fix first,
or restore the verified pre-deploy recovery point with explicit data-loss
approval.

Useful incident checks:

```bash
compose_env=${HOOK2STREAM_ENV_FILE:-.env}
compose_files=(-f compose.yaml)
if grep -qx 'STORAGE_MODE=minio' "$compose_env"; then
  compose_files+=(-f compose.minio.yaml)
fi
docker compose --env-file "$compose_env" "${compose_files[@]}" ps
docker compose --env-file "$compose_env" "${compose_files[@]}" \
  logs --since 30m api pgbouncer postgres
docker compose --env-file "$compose_env" "${compose_files[@]}" \
  logs --since 30m \
  worker-media worker-analysis worker-control worker-render worker-export
docker compose --env-file "$compose_env" "${compose_files[@]}" \
  logs --since 2h postgres-backup
```

Docker JSON logs rotate locally. For this alpha, use external web/API synthetics
and the backup heartbeat with email alerts. Persistent centralized logs,
metrics/traces, host-volume snapshots, and recorded off-host restore evidence
remain operator responsibilities.

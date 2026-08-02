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

## One-time setup

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
runs the focused tests under `tests/`, parses the lifecycle policy, and renders
both Compose models with temporary placeholder secret files. The repository
workflow runs it as a dedicated CI step; it can also be run locally before
pushing when Docker Compose v2 and Node 24 are available.

On a successful `main` publish, CI emits the artifact
`release-images-<commit-sha>/release-images.env`. Review it, copy its five
digest-pinned `API_IMAGE`, `WORKER_IMAGE`, `BOOTSTRAPPER_IMAGE`, `WEB_IMAGE`, and
`POSTGRES_BACKUP_IMAGE` assignments and its `RELEASE_VERSION` into the
production `.env`. This artifact is the release handoff; do not translate its
digest references back to mutable tags.
Resolve and pin `CADDY_IMAGE`, `POSTGRES_IMAGE`, and `PGBOUNCER_IMAGE` by digest
as well. The release preflight rejects every mutable application or
infrastructure image reference.

The optional build overlay builds the four application artifacts and backup
sidecar from the repository root:

```bash
docker compose --env-file .env \
  -f compose.yaml -f compose.build.yaml \
  build api worker-media bootstrapper web postgres-backup
docker compose --env-file .env \
  -f compose.yaml -f compose.build.yaml \
  push api worker-media bootstrapper web postgres-backup
```

Before every deployment, validate the fully interpolated model and inspect its
image list. This catches missing required values without printing secret file
contents:

```bash
docker compose --env-file .env --profile tools config --quiet
docker compose --env-file .env --profile tools config --images
```

The release preflight rejects non-HTTPS application and custom backup S3
endpoints. For native AWS S3, leave `BACKUP_S3_ENDPOINT` empty so the AWS CLI
uses the regional endpoint selected by `BACKUP_S3_REGION`.
It also rejects mutable application and infrastructure image tags; replace all
eight image values with reviewed `image@sha256:<digest>` references first.
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
all images, starts PostgreSQL/PgBouncer, requires an encrypted pre-migration
backup, runs the bootstrapper exactly once, updates leaf workers → control → API
→ web/Caddy, waits on role readiness, and smokes both web and API through the
public origin. Re-running it with the same `.env` is safe.

Workers receive a 150-second shutdown grace period, longer than the 120-second
lease. Jobs are retryable and fenced, but this Compose topology does not promise
zero-downtime rolling replacement. Drain paid work or schedule a maintenance
window before replacing render/export workers. Database changes must follow
expand/contract compatibility so the previous application images can still run.

After rollout, check service health, the internal dependency-ready endpoint, and
the public web endpoint:

```bash
docker compose --env-file .env ps
docker compose --env-file .env exec api \
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

After each success, the release script stores `.release-state/last-successful.env`.
Before the next release it snapshots that file and prints the exact snapshot path.
For an application rollback, first verify the migration was expand/contract
compatible, then run the printed command, for example:

```bash
sudo env HOOK2STREAM_ENV_FILE=.release-state/20260727T120000Z.env \
  ./scripts/deploy-release.sh
```

This restores old image digests through the same ordered and health-checked flow.
Do not run an automatic down-migration. If a migration itself is defective, stop
writes and follow the documented database recovery decision: forward-fix first,
or restore the verified pre-deploy recovery point with explicit data-loss
approval.

Useful incident checks:

```bash
docker compose --env-file .env ps
docker compose --env-file .env logs --since 30m api pgbouncer postgres
docker compose --env-file .env logs --since 30m \
  worker-media worker-analysis worker-control worker-render worker-export
docker compose --env-file .env logs --since 2h postgres-backup
```

Docker JSON logs rotate locally. For this alpha, use external web/API synthetics
and the backup heartbeat with email alerts. Persistent centralized logs,
metrics/traces, host-volume snapshots, and recorded off-host restore evidence
remain operator responsibilities.

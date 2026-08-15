# Hook2Stream deployment bundles

The authoritative operating contract is
[`docs/operations/hook2stream-mvp-runbook.md`](../../docs/operations/hook2stream-mvp-runbook.md).
The deployed MVP has four IT-Garage VPS instances:

- an app host and a separate MinIO storage host for staging;
- an app host and a separate MinIO storage host for production.

Provisioning remains manual. GitHub Actions promote immutable container digests
to already bootstrapped hosts through environment-specific Tailscale SSH.

## Directory map

- `compose.yaml`: app, workers, PostgreSQL, PgBouncer, Caddy, backup, and
  role-specific egress proxies for an application host;
- `storage/`: standalone Tailscale-only MinIO/Caddy bundle and storage deploy
  wrapper for a storage host;
- `compose.minio.yaml`: disposable local-development and CI overlay only;
- `environments/`: deployed app environment templates;
- `host/`: app forced-command SSH/sudo templates;
- `scripts/`: app candidate validation, deploy, rollback, host validation,
  backup, and health/E2E contracts;
- `minio/`: pinned source-only MinIO build shared by local CI and the remote
  storage release pipeline;
- `secrets/`: file-secret contract; secret values are never committed;
- `tests/`: offline deployment contract tests.

`compose.minio.yaml` must never run on either deployed app host. Remote staging
and production always use `STORAGE_MODE=external` with their corresponding
`h2s-storage-<environment>.<tailnet>.ts.net` endpoint.

## App-host contract

Copy the matching `environments/*.env.example` into the root-owned release
configuration outside Git. Replace every placeholder and pin every image by
`@sha256:<64 lowercase hex>`. The essential storage values are:

```dotenv
STORAGE_MODE=external
S3_SERVICE_URL=https://h2s-storage-<environment>.<tailnet>.ts.net
S3_PUBLIC_SERVICE_URL=https://h2s-storage-<environment>.<tailnet>.ts.net
S3_ENDPOINT_HOST=h2s-storage-<environment>.<tailnet>.ts.net
S3_REGION=us-east-1
S3_FORCE_PATH_STYLE=true
S3_CONFIGURE_BUCKET_LIFECYCLE=false
S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE=false
STORAGE_PROTOCOL_VERSION=1
EGRESS_CONFIG_DIR=./egress/rendered/<environment>
BACKUP_S3_ENDPOINT=https://h2s-storage-<environment>.<tailnet>.ts.net
BACKUP_S3_REGION=us-east-1
```

`S3_PUBLIC_SERVICE_URL` is retained only for strict legacy configuration
validation. The browser never receives an S3 URL, credentials, capability token,
or object key. Upload and content traffic uses the same-origin Hook2Stream API;
S3 CORS is disabled.

Only Caddy publishes host ports: 80/TCP, 443/TCP, and 443/UDP. All backend and
role networks are internal. The Squid configuration is rendered from the exact
validated `S3_ENDPOINT_HOST`; a wildcard `*.ts.net` allowlist is rejected.

Before backup or database migration, the deploy wrapper requires storage
protocol version 1 at:

```text
https://h2s-storage-<environment>.<tailnet>.ts.net/.well-known/hook2stream-storage-protocol
```

It then performs authenticated PUT, HEAD, one single-range GET, and DELETE on a
disposable S3 key through the S3 egress proxy. Failure stops deployment before a
backup or migration is attempted.

App secret files live below `/srv/hook2stream/secrets/current`, on the encrypted
mount, with the ownership and modes in [`secrets/README.md`](secrets/README.md).
Staging and production credentials and H2SE keyrings must be unrelated.

After manual unlock/mount, start Docker through the encrypted-mount systemd
guard and validate the bootstrapped app host before deploying:

```bash
sudo ./scripts/validate-host.sh app staging
sudo ./scripts/validate-host.sh app production
```

The command is intentionally role- and environment-explicit. It checks the
file-backed LUKS2 chain, 112/176 GiB minimum, allocation and ownership, encrypted
swap, Docker root, free space, UFW, Tailscale, listeners, and secrets.
Install [`host/docker-encrypted-mount.conf.example`](host/docker-encrypted-mount.conf.example)
as the documented Docker systemd drop-in so a reboot cannot start Docker while
the encrypted application mount is absent.

## Storage-host contract

Use [`storage/README.md`](storage/README.md) for installation. In summary, each
storage host runs only:

- a source-built, immutable MinIO digest on an internal Docker network;
- Caddy bound to that host's Tailscale IPv4 TCP 443;
- one-shot idempotent initialization and deployment verification.

MinIO 9000 and console 9001 are never host-published. The console is disabled.
TLS certificate/key files are issued locally with `tailscale cert`, live on the
encrypted storage mount, and are mounted read-only.

The environment topology is fixed:

| Environment | Media | PostgreSQL backup |
|---|---|---|
| staging | private, unversioned, 35 GiB | private, versioned, 10 GiB, 7 days |
| production | private, unversioned, 160 GiB | private, versioned, 30 GiB, 35 days |

Root, bootstrap, runtime, and backup credentials are all distinct. Runtime can
access only media; backup can access only the backup bucket. The bootstrap
identity manages only initialization concerns. Root credentials are unavailable
to app containers and CI.

After manual unlock/mount, start Docker through the encrypted-mount systemd
guard and validate the bootstrapped storage host before deploying:

```bash
sudo ./scripts/validate-host.sh storage staging
sudo ./scripts/validate-host.sh storage production
```

The storage bundle has its own immutable candidate and forced-command wrapper.
It records the on-disk storage format/protocol floor and refuses downgrade after
that floor changes. Recovery is forward-fix only.

## File-backed encrypted mounts

The provider root disk is not treated as encrypted storage. On each app host,
the fully allocated root-owned `0600` backing file is
`/var/lib/hook2stream-data.luks`, mapped as `hook2stream-data`, and mounted at
`/srv/hook2stream`. On each storage host it is
`/var/lib/hook2stream-storage.luks`, mapped as `hook2stream-storage`, and mounted
at `/srv/hook2stream-storage`.

Use LUKS2 and keep every recovery key off-host. Do not configure automatic
unlock. Docker units must refuse to start unless the correct mount is active.
After every reboot, attach the exact file to a loop device, unlock interactively,
mount, enable only encrypted swap, run the role validator, and then start
Docker. See the canonical runbook for sizes and recovery gates.

## Local and CI MinIO overlay

`compose.minio.yaml` remains useful for disposable integration tests. It builds
the audited final source release pinned in `minio/Dockerfile`; it is not the
remote-storage production topology and its data is not a backup.

Use only an explicit local/CI environment with `STORAGE_MODE=minio`. Never copy
its root credentials, bucket limits, public test routing, or Compose overlay to
a deployed host. CI contract tests continue to exercise this overlay so the
application's S3-compatible behavior remains reproducible without production
access.

## App release and rollback

The app candidate contains schema-v1 metadata, digest-only image variables, the
deployment bundle, and `SHA256SUMS`. Staging deploy is automatic after `main` CI;
production consumes the exact staged artifact and signed receipt after protected
Environment approval. Secrets are host-resident and absent from the artifact.

The forced command accepts only a validated candidate or an eligible rollback
SHA. It rejects archive traversal, links, special files, unapproved images,
tags, checksum/schema/repository mismatch, and receipt mismatch. Host `flock` is
the second concurrency lock.

Install the app gate only after `/srv/hook2stream` is the active encrypted
mount. Its root must remain root-owned mode `0755`; configuration, releases,
and release state are root-owned mode `0700`. Install every executable gate
component with exact ownership and mode—the launcher and host validator reject
permission drift instead of repairing it:

`/usr/local`, `/usr/local/sbin`, and `/usr/local/libexec` must be real
root:root `0755` directories; do not use a deploy-user-writable symlink or
parent for the sudo target.

```bash
sudo chown root:root /srv/hook2stream
sudo chmod 0755 /srv/hook2stream
sudo install -d -o root -g root -m 0700 \
  /srv/hook2stream/config \
  /srv/hook2stream/releases \
  /srv/hook2stream/release-state \
  /etc/hook2stream
sudo install -d -o root -g root -m 0755 \
  /usr/local/libexec/hook2stream/lib
sudo install -o root -g root -m 0555 \
  scripts/lib/forced-command-trust.sh \
  /usr/local/libexec/hook2stream/lib/forced-command-trust.sh
sudo install -o root -g root -m 0555 \
  scripts/deploy-forced-command.sh \
  scripts/validate-candidate.sh \
  /usr/local/libexec/hook2stream/
sudo install -o root -g root -m 0500 \
  scripts/post-deploy-e2e.sh \
  /usr/local/libexec/hook2stream/post-deploy-e2e.sh
sudo install -o root -g root -m 0555 \
  scripts/deploy-forced-launcher.sh \
  /usr/local/sbin/hook2stream-deploy-launcher
```

Create `/srv/hook2stream/config/staging.env` or `production.env` from the
matching reviewed environment template and install it root:root `0600`.
Install `host/deploy.conf.example` as `/etc/hook2stream/deploy.conf`, also
root:root `0600`, after replacing every placeholder. Production additionally
installs `host/staging-receipt-allowed-signers.example` as
`/etc/hook2stream/staging-receipt-allowed-signers`, root:root `0600`. Never put
these files or the gate directories in the deploy operator's groups. Run
`scripts/validate-host.sh app staging|production` after installation and before
the first candidate.

Rollback is application-only: it changes API, worker, and web digests, preserves
bootstrap and all infrastructure images, runs no migration/down-migration, and
verifies actual running digests before and after. An incompatible database
schema needs a forward fix or an explicitly approved restore with writes stopped.

## Storage release

The separate storage workflow builds MinIO from the pinned source release,
publishes an immutable GHCR digest with SBOM/provenance, resolves all supporting
images to digests, scans the exact digests, and creates
`storage-candidate-<sha>-<run_id>-<attempt>`.

`storage-staging` deploys automatically and verifies health, protocol version,
policies, credential isolation, quotas, versioning, lifecycle, and actual image
digests. `promote-storage-production.yml` takes the source storage-CI run ID,
requires approval, validates the signed staging receipt, and deploys the same
digests without rebuild.

## Validation

Run the repository deployment validation from this directory:

```bash
./scripts/validate-deployment.sh
```

The validator renders all Compose profiles and runs shell/Node contract suites.
Storage-specific offline tests live below `storage/tests` and `src/ci/tests`.
Action workflows should additionally pass `actionlint`, and release image scans
must run against published digests, not mutable tags.

These checks do not create an IT-Garage account, order servers, unlock LUKS,
issue Tailscale certificates, change Cloudflare, provision GitHub Environments,
or perform the real OAuth/Stripe/render/recovery drills. Those remain explicit
operator gates.

## Accepted MVP risks

Each environment is two single-node failure domains. PostgreSQL is self-managed,
logical backups are not PITR, and each MinIO media disk has no replica. Losing a
storage disk can permanently lose media and the backups stored on that disk.
IT-Garage advertises only 97 percent SLA and may throttle shared CPU/network
under its AUP/Fair Use terms.

This exception expires 90 days after the first paid user. Before public signup,
move PostgreSQL to managed 35-day PITR and media to managed S3 or replicated
MinIO with an independent copy.

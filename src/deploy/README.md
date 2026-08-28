# Hook2Stream deployment bundle

The canonical operating contract is
[`docs/operations/hook2stream-mvp-runbook.md`](../../docs/operations/hook2stream-mvp-runbook.md).
The deployed MVP has two permanent app-only Servers.Guru VPS instances:
staging is `MTL1-3` in Montreal with 4 shared vCPU, 8 GiB RAM, and 80 GB NVMe;
production is `NL1-4` in Amsterdam with 6 shared vCPU, 8 GiB RAM, and 160 GB
NVMe. Media and encrypted backups use separate Storj projects; there is no
deployed object-storage host or storage release pipeline.

## Directory map

- `compose.yaml`: Caddy, app, workers, PostgreSQL, PgBouncer, encrypted backup,
  Storj media janitor, storage probe, and role-specific egress proxies;
- `compose.minio.yaml`: disposable local-development and CI overlay only;
- `environments/`: reviewed staging/production app-host templates;
- `host/`: app forced-command SSH, sudo, and mount-guard templates;
- `scripts/`: candidate validation, app deploy/rollback, host validation,
  backup, Storj probe/janitor, health, and E2E contracts;
- `storj/`: operator-only bucket bootstrap and live acceptance;
- `secrets/`: scalar file-secret contract; values are never committed;
- `vault/`: optional external Vault-to-file renderer;
- `tests/`: offline deployment contracts.

`compose.minio.yaml` must never run on staging or production and is excluded
from immutable candidates. Its `Manage` storage mode, root credentials, bucket
bootstrap, and test routing are local/CI conveniences only.

## Deployed app-host contract

Copy the matching `environments/*.env.example` to the root-owned release
configuration outside Git. Replace every placeholder and pin every image as
`@sha256:<64 lowercase hex>`. The deployed storage contract is:

```dotenv
STORAGE_MODE=external
STORAGE_PROVISIONING_MODE=VerifyOnly
STORAGE_OBJECT_EXPIRATION_MODE=Storj
S3_ENDPOINT_HOST=gateway.storjshare.io
S3_SERVICE_URL=https://gateway.storjshare.io
S3_REGION=global
S3_MEDIA_BUCKET=hook2stream-com-<environment>-media
S3_FORCE_PATH_STYLE=true
S3_CONFIGURE_BUCKET_LIFECYCLE=false
S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE=false
STORAGE_PROTOCOL_VERSION=1
STORAGE_CONTRACT_KEY=.hook2stream/contracts/storage-v1.json
STORAGE_CONTRACT_SHA256=<bootstrap-output-sha256>
BACKUP_S3_ENDPOINT_HOST=gateway.storjshare.io
BACKUP_S3_ENDPOINT=https://gateway.storjshare.io
BACKUP_S3_REGION=global
BACKUP_S3_BUCKET=hook2stream-com-<environment>-pg-backups
BACKUP_S3_FORCE_PATH_STYLE=true
```

Media and backup endpoint/region/path-style settings remain independent even
when their current Storj values are equal. The egress renderer validates each
exact credential-free HTTPS origin and renders separate media and backup proxy
allowlists. Wildcards and arbitrary HTTPS origins are rejected.

`VerifyOnly` checks the pre-created private media bucket and never creates a
bucket or mutates CORS/lifecycle. Browser S3 URLs, object keys, grants, and
credentials are never exposed; uploads and content downloads use same-origin
Hook2Stream APIs. Storj does not support the S3 Lifecycle API, so both deployed
lifecycle flags stay `false`.

Before backup or database migration, the wrapper authenticates with the media
runtime credential, downloads
`.hook2stream/contracts/storage-v1.json`, verifies the root-pinned SHA-256 and
provider/environment/bucket/H2SE/retention fields, and performs a disposable
PUT, HEAD, single-range GET, and DELETE. It also requires a successful encrypted
backup newer than two hours. Any failure stops before migration.

The backup sidecar uses separate bucket-scoped Storj credentials, single
`PutObject` uploads, version IDs, age ciphertext, and manifest-last publication.
Its credential has no Delete permission and carries maximum object TTL 168
hours for staging or 840 hours for production. The daily media janitor uses the
media credential only to abort incomplete multipart uploads older than 24
hours; temporary `staging/` H2SE data and manifest share a 24-hour absolute TTL.

## Storj operator bootstrap

Staging and production use different Storj Standard/global projects and
different escrowed encryption passphrases. Their fixed private buckets are:

| Environment | Media | PostgreSQL backup |
|---|---|---|
| staging | `hook2stream-com-staging-media` | `hook2stream-com-staging-pg-backups` |
| production | `hook2stream-com-production-media` | `hook2stream-com-production-pg-backups` |

Media is unversioned and has no CORS; backup is versioned. The operating
thresholds are 35/160 GiB media and 10/30 GiB backup. Follow
[`storj/README.md`](storj/README.md) to derive role-scoped grants, bootstrap the
buckets and private storage marker, and run live acceptance. Full project,
bootstrap/root, restore-read grants, and project encryption passphrases remain
off every VPS and outside GitHub. Only runtime media, no-Delete backup writer,
and the marker digest reach their corresponding app environment.

## Servers.Guru hosts and encrypted mount

Both VPS instances are provisioned manually in the Servers.Guru control panel
and remain running between releases. The canonical provider checklist and
policy live under `../../deploy/providers/serversguru`. The public API is
optional and read-only for inventory, status, product/image, and wallet checks;
CI never receives an API key and never creates, rebuilds, powers, cancels, or
restores a server. There is no Terraform provider contract, provider lifecycle
receipt, retained address, or per-release staging lifecycle.

Verify the paid records exactly before bootstrap: `MTL1-3` in Montreal for
staging and `NL1-4` in Amsterdam for production, both Ubuntu 24.04 amd64. The
pair's storefront baseline is EUR 32.48 per monthly cycle. A total above EUR
40, a price movement above ten percent, a location/SKU/image substitution, or
an unpaid/ambiguous invoice blocks rollout. Keep two monthly budgets in the
provider wallet because a new account has no documented renewal grace.

Servers.Guru documents KVM, ordinary SSH, and panel VNC but does not document
custom cloud-init, SSH-key injection, a provider firewall, or scoped/expiring
API keys. Use the issued root password through a verified SSH/VNC session.
Install operator keys, enroll Tailscale with Tailscale SSH disabled, and
restrict TCP 22 to `tailscale0`. The operator and deploy accounts stay
password-locked and key-only; as a temporary MVP exception, root retains a
unique active password for ordinary OpenSSH recovery through Tailscale only.
Never place that password in GitHub, shell history, provider API logs, or
deployment artifacts. Retain independent `findmnt`,
backing-device-chain, and `cryptsetup status` evidence.

Install the reviewed SSH policy before host validation:

```sh
sudo install -o root -g root -m 0644 \
  host/sshd-no-public-ssh.conf.example \
  /etc/ssh/sshd_config.d/99-hook2stream-no-public-ssh.conf
sudo sshd -t
sudo systemctl reload ssh.service
```

Both permanent hosts use only `/etc/ssh/ssh_host_ed25519_key` and an exact
pinned ED25519 `known_hosts` record. Neither profile uses SSH host
certificates. Never establish trust through an unauthenticated
`ssh-keyscan` or hand-author a partial drop-in from validator output.

The provider root disk is not treated as protected runtime storage. Both hosts
use a fully allocated root-owned mode `0600`
`/var/lib/hook2stream-data.luks`, mapped as `hook2stream-data`, and mounted at
`/srv/hook2stream`. Staging uses exactly 48 GiB; production uses 64 GiB.
Docker data-root, PostgreSQL, secrets, release state, logs, scratch, and 4 GiB
swap live below the encrypted mount.

Never configure automatic unlock. Docker's systemd unit must require the exact
mount. After a reboot, use the already verified Tailscale ordinary-OpenSSH path
to attach the loop device, unlock LUKS2, mount it, enable encrypted swap, then
start Docker. If network-independent recovery is required, use the Servers.Guru
panel VNC console.
VNC at the boot prompt must be proved by the live provider probe before relying
on it. Finally validate:

```sh
sudo ./scripts/validate-host.sh app staging
sudo ./scripts/validate-host.sh app production
```

Run only the command matching that host. The validator proves the exact
file/loop/LUKS2/mapper/mount chain, capacity/allocation/permissions, at least
20 percent free on root and encrypted filesystems, encrypted swap, Docker root,
the installed and loaded systemd mount guard, UFW, Tailscale-only SSH, listener
policy, and secret modes.

Only Caddy publishes 80/TCP, 443/TCP, and 443/UDP. Servers.Guru provider-level
firewalling is not assumed; these are the only public UFW ingress rules, and
UFW allows SSH only inbound on `tailscale0`.
Application backend networks are
internal. The operator and deploy users are not in `docker` or the secrets
group and their local passwords remain locked. The reviewed global sshd policy
allows password authentication only because `Match` blocks are forbidden; the
exact `AllowUsers` list and account state make root the sole password-capable
SSH identity. This is a temporary MVP risk: store each root password only in
encrypted operator escrow and rotate it after any suspected disclosure. Install
`host/docker-encrypted-mount.conf.example` as the reviewed Docker systemd
drop-in so Docker cannot start while `/srv/hook2stream` is absent.
Use the fail-closed, interactive initialization and reboot procedure in
[`host/README.md`](host/README.md). The bootstrap interface formats only a new
backing file created by the same invocation, never stores an unlock key, and
refuses to format an existing path or an existing unlocked volume.

## App release gate

Install the app gate only after `/srv/hook2stream` is the active encrypted
mount. The mount root remains `root:root` mode `0755`; configuration, releases,
state, and `/etc/hook2stream` are root-private. The deploy user's parent paths
must not be writable or symlinked. Install the reviewed forced-command launcher,
wrapper, candidate validator, libraries, and E2E script with the exact ownership
and modes enforced by `validate-host.sh`.

Create `/srv/hook2stream/config/staging.env` or `production.env` from the
matching environment template as `root:root` mode `0600`. Install
`host/deploy.conf.example` as `/etc/hook2stream/deploy.conf`, also `0600`.
Replace both public-key fingerprint placeholders with the exact OpenSSH SHA-256
fingerprints for the single operator key and the single environment-specific CI
deploy key. `validate-host.sh` rejects extra/unrestricted keys and any sudoers
content other than `host/sudoers.example`, including effective grants from a
different drop-in or group. Run `sudo tailscale set --ssh=false` before host
validation; the deployment contract uses ordinary OpenSSH only. The validator
also rejects named/default POSIX ACLs on trusted files, secrets, and the Docker
socket.
Production installs the staging-receipt allowed-signers file with exactly one
named ED25519 authority; additional or wildcard records are rejected. An
accepted promotion carries and verifies the signed staging receipt before
GitHub Environment approval and again on the host. No provider lifecycle
marker is part of release evidence.
Secret scalar files live below `/srv/hook2stream/secrets/current` according to
[`secrets/README.md`](secrets/README.md).

The candidate contains schema-v1 metadata, digest-only image variables, this
application deploy bundle, and checksums. Protected `main` CI publishes it but
does not deploy staging. On the already accepted permanent staging host, the
`Stage candidate` workflow takes a selected successful `source_ci_run_id`,
deploys the existing artifact without rebuild, runs acceptance plus the
60-minute soak, and signs a receipt. The soak is a separate staging-only forced
command for the exact current candidate. It holds the deployment lock for
3600--3900 seconds, invokes the root-owned E2E hook in `soak-60m` mode, and
requires a completed render, at least 3300 active render seconds, concurrency
one, at least 60 successful network checks, no throttling/OOM, and one healthy
candidate-digest `worker-render`. Its strict result is included in the signed
application receipt; hook diagnostics are not exposed to CI logs. Production
takes the successful staging workflow
run ID and accepts only its exact candidate after GitHub Environment approval.
All GitHub-runner policy and sourced helpers are pinned to the current
`github.workflow_sha`; the selected release SHA is data only and is never
checked out or executed in a credential-bearing job. A separate job/artifact
boundary precedes Environment secrets, and current policy revalidates the data
after that boundary.
The forced command rejects
archive traversal, links, special files, unknown images, tags, checksum/schema
or receipt mismatch; host `flock` is the second concurrency lock.

Before the first candidate, finish the deploy-key, host trust, MagicDNS,
rollback-marker, integration callback, and DNS handoff in
[`DEPLOYMENT.md`](../../.github/DEPLOYMENT.md). The host and matching GitHub
Environment must contain the same first H2SE-capable release SHA. Production
and staging each use an exact, distinct pinned ED25519 host key for their stable
MagicDNS names.

Rollback is application-only. It changes API, workers, and web to a previously
successful H2SE-compatible target, preserves bootstrap and infrastructure
digests, and runs no migration. Incompatible schema rollback requires a forward
fix or separately approved write-stop/restore.

## Local and CI MinIO

`compose.minio.yaml` provides reproducible disposable S3 integration tests. Use
it only with an explicit local/CI environment and `STORAGE_MODE=minio`. Local
MinIO may use `StorageProvisioningMode=Manage`, test CORS/lifecycle behavior,
and local bootstrap/root secrets. It is not published as a production runtime
image, is not included in a release candidate, and never stores a backup claim.

## Validation and external gates

Run the offline deployment validation from this directory:

```sh
./scripts/validate-deployment.sh
```

It renders Compose and egress configurations and executes the shell/Node
contract suites. Action workflows also require workflow contract checks and
published image scans against exact digests.

Repository tests do not register, order, rebuild, cancel, or fund Servers.Guru
VPS instances or provider/Storj balances,
create Storj projects/grants, register a domain, change Cloudflare/GitHub Pages,
unlock LUKS, provision host secrets, or perform live OAuth/Stripe/render/recovery
drills. Those remain explicit operator gates in the canonical runbook.

External observability and alerting are not configured;
`OTEL_EXPORTER_OTLP_ENDPOINT` remains empty. Local healthchecks, structured
logs, backup-age gate, deployment E2E, the 60-minute staging soak, recovery
drills, and manual post-release observation remain mandatory.

## Accepted MVP risks

Production is one shared-CPU `NL1-4` VPS in Amsterdam; staging is one permanent
shared-CPU `MTL1-3` VPS in Montreal. The MVP does not configure or rely on
IPv6, provider backup, snapshots, monitoring agents, provider firewalling,
provider DDoS protection, or private-network routing.
PostgreSQL is self-managed without
PITR, and Storj media has no independent application-level replica or EU-only
placement guarantee. Both permanent VPS instances renew from the provider
wallet; keep at least two full pair budgets funded and review invoices before
their due dates. Regular-vCPU FFmpeg use remains a written-support and soak-test
gate. This 90-day closed-pilot exception expires after the first paid user.
Before public signup, use managed 35-day PostgreSQL PITR, at least two app
instances behind a load balancer, and an independent media and backup copy.

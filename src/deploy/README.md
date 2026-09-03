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
- `compose.billing-stripe.yaml`: Stripe-only API configuration, price IDs, and
  secret mounts; selected for staging and omitted from billing-disabled production;
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
bootstrap, and test routing are local/CI conveniences only. The final MinIO
Community source release is archived and no longer receives OSS security
fixes. CI therefore records its vulnerability inventory without treating that
test-only image as deployable; all images that can enter a candidate retain the
blocking High/Critical gate. A MinIO finding can never be waived into staging
or production.

Caddy, PgBouncer, and the four Squid egress proxies are repository-owned
hardened release images, not mutable external runtime tags. CI builds Caddy
2.11.4 from its exact upstream commit on Go 1.27.0 with reviewed patched Go
modules and a scratch runtime; PgBouncer 1.25.2 from its checksummed release
tarball on patched Alpine 3.24; and Squid 7.6 from exact patched Alpine
packages. Each image is published to the repository GHCR namespace with
SBOM/provenance, scanned at its published digest with the same blocking
High/Critical (`only-fixed=false`) policy, and then allowlisted only under its
`ghcr.io/<owner>/hook2stream-*` repository. External Caddy, edoburu/PgBouncer,
and Ubuntu/Squid digests are rejected by candidate validation.

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

Billing is an explicit deployment capability. Staging sets
`BILLING_MODE=stripe` and includes `compose.billing-stripe.yaml`; production
sets `BILLING_MODE=disabled` and uses only the base Compose file. The disabled
base starts the API with `Stripe__Mode=Disabled`, mounts no Stripe secrets,
accepts no Stripe price identifiers, and omits `api.stripe.com` from the API
egress allowlist. Any other environment/mode pairing fails before mutation.

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

Staging and production use different Storj Standard/global projects. Before
creating either project, record an explicit, irreversible encryption-model
decision. Managed encryption is recommended for the MVP because newly created
Self-Managed projects cannot use Storj exhaustive S3 listing; H2SE and age still
ensure Storj receives only ciphertext. Self-Managed remains available only with
a distinct escrowed passphrase per environment and live proof that all required
list operations work. Their fixed private buckets are:

| Environment | Media | PostgreSQL backup |
|---|---|---|
| staging | `hook2stream-com-staging-media` | `hook2stream-com-staging-pg-backups` |
| production | `hook2stream-com-production-media` | `hook2stream-com-production-pg-backups` |

Media is unversioned and browser S3 access/CORS stay disabled; backup is
versioned. Storj bucket CORS operations are unsupported and must not be called.
The operating thresholds are 35/160 GiB media and 10/30 GiB backup. Follow
[`storj/README.md`](storj/README.md) to derive role-scoped grants, bootstrap the
buckets and private storage marker, and run live acceptance. Full project,
restore-read grants, any Self-Managed project passphrases, and the temporary
bootstrap credential remain off every VPS and outside GitHub. Revoke the
temporary bootstrap credential after live acceptance. Only runtime media,
no-Delete backup writer, and the marker digest reach their corresponding app
environment. Storj's 30-day minimum object charge and 50-kB minimum billable
object size apply even when shorter TTLs enforce the MVP retention contract.

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
/usr/local/libexec/hook2stream/validate-host.sh app staging
/usr/local/libexec/hook2stream/validate-host.sh app production
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
wrapper, candidate validator, libraries, E2E script, and
`/usr/local/libexec/hook2stream/rollback-application.sh` with the exact
ownership and modes enforced by `validate-host.sh`. The rollback orchestrator
is `root:root` mode `0555` and never executes the target candidate's copy. A
successful forward deploy writes protocol-v2 capability state and
`active-infrastructure-release.json`; rollback preserves that marker and uses
only its validated root-private Compose/helper bundle. Pre-v2 current or target
release capability records are rejected.

Create `/srv/hook2stream/config/staging.env` or `production.env` from the
matching environment template as `root:root` mode `0600`. Install
`host/deploy.conf.example` as `/etc/hook2stream/deploy.conf`, also `0600`.
Replace both public-key fingerprint placeholders with the exact OpenSSH SHA-256
fingerprints for the single operator key and the single environment-specific CI
deploy key. `validate-host.sh` rejects extra/unrestricted keys and any sudoers
content other than `host/sudoers.example`, including effective grants from a
different drop-in or group.

Run `deploy/providers/serversguru/configure-ghcr-pull-auth.sh` with a unique
32-hex operator identity suffix and distinct GitHub credential per environment,
then copy all four printed non-secret pins to `deploy.conf`. Docker login proves
only credential usability: GitHub does not expose PAT scopes here. The
root-only `identity.attestation` therefore records the operator's
`read:packages`-only and environment-exclusive assertions, and its SHA-256 is
enforced by the launcher, deploy scripts, and host validator.

Run `sudo tailscale set --ssh=false` before host
validation; the deployment contract uses ordinary OpenSSH only. The validator
also rejects named/default POSIX ACLs on trusted files, secrets, and the Docker
socket.

Install `host/authenticated-e2e.sh` unchanged as the root-owned mode `0500`
path configured by `HOOK2STREAM_AUTHENTICATED_E2E_HOOK`. Provision its
environment-specific expected-email, licensed MP3 and staging soak baseline
inputs under `/srv/hook2stream/e2e` as documented in
[`host/README.md`](host/README.md). On a cold host, install the OAuth session
only after `prepare-pending` has made the public origin available; established
hosts must have a valid session before `deploy-and-finalize`. The script calls
the public API and fails closed. Staging performs
upload/OpenRouter/test-billing/render; production performs deterministic
upload/OpenRouter/preview, proves `checkoutEnabled=false` and
`503 billing.disabled` for checkout/webhook, and performs no final render.
Printing either
capability line without its environment-specific checks is not an installation
option.
Production installs the staging-receipt allowed-signers file with exactly one
named ED25519 authority; additional or wildcard records are rejected. An
accepted promotion carries and verifies the signed staging receipt before the
production Environment boundary and again on the host. No provider lifecycle
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
requires the prior completed 18-item render, at least 3300 seconds of synthetic
FFmpeg load in an isolated ephemeral container from the exact worker digest
with the same CPU/memory limits, concurrency one, at least 60
successful network checks, no meaningful cgroup/host throttling or OOM, and one
healthy candidate-digest `worker-render`. Its strict result is included in the signed
application receipt; hook diagnostics are not exposed to CI logs. Production
takes the successful staging workflow run ID and accepts only its exact
candidate. Each phase is dispatched only by `Tassadar2499`, requires repository
variable `PRODUCTION_DEPLOY_ACTORS=Tassadar2499`, and requires the exact input
`production_confirmation=DEPLOY hook2stream.com` before and after the
production Environment boundary. No second reviewer is required for this
solo-developer MVP.
All GitHub-runner policy and sourced helpers are pinned to the current
`github.workflow_sha`; the selected release SHA is data only and is never
checked out or executed in a credential-bearing job. A separate job/artifact
boundary precedes Environment secrets, and current policy revalidates the data
after that boundary.
The forced command rejects
archive traversal, links, special files, unknown images, tags, checksum/schema
or receipt mismatch; host `flock` is the second concurrency lock.

The staging and production workflows expose three explicit deployment phases:

- `prepare-pending` is permitted only for the first, cold database bootstrap,
  when no `last-successful.env` exists. It starts the candidate runtime through
  the host `prepare` transaction and returns a pending receipt, but does not
  declare the release successful, run the soak, or make it promotable. The
  operator then completes invite-only OAuth onboarding against that running
  origin, creates the QA workspace, and installs the resulting cookie jar as a
  root-owned private E2E input.
- `finalize-pending` sends only `finalize <candidate-id>`. It must name the exact
  prepared candidate and reuses its persisted E2E operation identity; it
  revalidates the running digests and all bound evidence before authenticated
  E2E and successful-state publication. A cold authenticated-E2E failure leaves
  that exact pending transaction available for a corrected retry.
- `deploy-and-finalize` is the normal path after any successful release exists.
  The host `deploy` command performs runtime deployment, authenticated E2E,
  digest verification, and state publication as one locked host transaction.
  It must not be split into an operator handoff, and it is rejected for the
  first cold deployment.

`release-state/pending-deploy.json` binds more than the commit SHA: it records
the full `release-candidate-<sha>-<run_id>-<attempt>` artifact name, environment,
previous successful SHA, release-images, bundle and derived environment hashes,
the 32-hex E2E operation ID, and, in production, the receipt, signature and
allowed-signer hashes. Only an exact replay may reuse it; a different artifact,
same-SHA attempt, changed approval, or other drift is rejected. The operation
ID remains stable within that transaction and makes its mutating E2E requests
idempotent, while a later candidate attempt receives a new ID. A successful
remote result is stored per full candidate and may be re-emitted after lost SSH
output only after the host revalidates the successful environment, active
infrastructure marker, live health, and actual running digests.

If normal deployment or finalization fails after a previous release existed,
the gate compensates by restoring that previous application image set. It does
not down-migrate the database or restore old infrastructure images: the
candidate infrastructure bundle remains the active infrastructure ledger, and
the published state records that coherent combination. Compensation is itself
signal-safe. If application restoration, digest verification, or state
publication cannot be proven, the gate writes root-owned
`release-state/recovery-required.json`, stops public Caddy ingress where it can
prove ownership, and blocks every automated deploy, finalize, soak, and
rollback until an operator reconciles runtime and ledger state manually.

Before the first candidate, finish the deploy-key, host trust, MagicDNS,
rollback-marker, integration callback, and DNS handoff in
[`DEPLOYMENT.md`](../../.github/DEPLOYMENT.md). The host and matching GitHub
Environment must contain the same first H2SE-capable release SHA. Production
and staging each use an exact, distinct pinned ED25519 host key for their stable
MagicDNS names.

Rollback is application-only. It changes API, workers, and web to a previously
successful H2SE-compatible target, preserves bootstrap and infrastructure
digests, and runs no migration. Before committing rollback state, the host runs
the bounded, non-mutating `rollback-verify` authenticated gate for OAuth,
H2SE Range reads, worker state, preview/export reads, and denied egress. A gate,
digest, or signal failure reverses the application to the original release; if
that reversal cannot be proved, the same recovery-required marker and ingress
shutdown boundary apply. Incompatible schema rollback requires a forward fix
or separately approved write-stop/restore.

## Local and CI MinIO

`compose.minio.yaml` provides reproducible disposable S3 integration tests. Use
it only with an explicit local/CI environment and `STORAGE_MODE=minio`. Local
MinIO may use `StorageProvisioningMode=Manage`, test CORS/lifecycle behavior,
and local bootstrap/root secrets. It is not published as a production runtime
image, is not included in a release candidate, and never stores a backup claim.
Its CI scan is inventory-only because upstream Community Edition is archived;
the release-candidate exclusions are the fail-closed deployment boundary.

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

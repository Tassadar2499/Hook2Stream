# Hook2Stream MVP operations runbook

This is the operator contract for the invite-only paid MVP. Production runs on
one permanent Servers.Guru `NL1-4` app VPS in Amsterdam. Staging runs on one
permanent Servers.Guru `MTL1-3` app VPS in Montreal. Media ciphertext and
age-encrypted PostgreSQL backups live in
two isolated Storj Standard/global projects. There is no production MinIO
host, automatic failover, PITR, or external monitoring. The exception expires
90 days after the first paid user.

## Fixed environment matrix

| Contract | Staging | Production |
|---|---|---|
| Public URL | `https://staging.hook2stream.com` | `https://hook2stream.com` |
| Servers.Guru lifecycle | permanent monthly VPS | permanent monthly VPS |
| Location | Montreal, Canada | Amsterdam, Netherlands |
| Servers.Guru SKU | `MTL1-3` | `NL1-4` |
| Compute / root disk | 4 shared vCPU / 8 GiB / 80 GB NVMe | 6 shared vCPU / 8 GiB / 160 GB NVMe |
| Public IPv4 | primary VPS IPv4 | primary VPS IPv4 |
| LUKS2 container | 48 GiB | 64 GiB |
| Reviewed price | EUR 14.99/month | EUR 17.49/month |
| Storj project | `hook2stream-staging` | `hook2stream-production` |
| Media threshold | 35 GiB | 160 GiB |
| Backup threshold / retention | 10 GiB / 7 days | 30 GiB / 35 days |
| Integrations | Google test, Stripe test, dedicated OpenRouter key | Google production, Stripe live, dedicated OpenRouter key |
| Deployment | manual selection of a protected-main candidate | exact staging artifact after approval |

Both application hosts are persistent. Storj Standard is global and is not an
EU-only data-location guarantee.

## Servers.Guru provider and cost gate

The operator-only contract is `deploy/providers/serversguru`. Provisioning,
rebuild, power, cancellation, provider backup/snapshot restore, and IP mutation
are manual control-panel actions. GitHub workflows never receive a provider
credential and never mutate Servers.Guru resources. The public API may be used
only for optional read-only inventory, status, product/image, and wallet checks;
the published API has no documented key scopes or expiry controls.

Before host bootstrap, verify the paid records exactly:

- staging: `MTL1-3`, Montreal, 4 shared vCPU, 8 GiB RAM, 80 GB NVMe;
- production: `NL1-4`, Amsterdam, 6 shared vCPU, 8 GiB RAM, 160 GB NVMe;
- both: KVM, Ubuntu 24.04 amd64, one primary IPv4, monthly billing.

The reviewed storefront total is EUR 32.48 per month. A total above EUR 40,
an increase above ten percent for either SKU, wrong location/image/resources,
an overdue invoice, or an ambiguous server record blocks rollout. Enable 2FA,
review the renewal invoice issued seven days before its due date, and keep at
least two complete monthly pair budgets in the wallet. A new account must not
assume the seven-day grace that Servers.Guru grants only after three successful
invoices. Crypto funding is manual and requires provider confirmations.

Servers.Guru documents KVM, SSH, and panel VNC, but does not document
custom cloud-init, SSH-key injection, provider firewalling, or a numeric
regular-VPS CPU duty cycle. Treat provider backups, snapshots, IPv6, and DDoS
protection as unavailable unless the exact server record proves otherwise.
Storj plus age encryption remains the canonical off-host recovery boundary.

Before deployment, run live acceptance on the exact paid hosts and retain:

```sh
sudo src/deploy/scripts/validate-serversguru-probe.sh staging
sudo src/deploy/scripts/validate-serversguru-probe.sh production
```

The live gate requires panel VNC access before guest networking, `/dev/net/tun`,
Tailscale, loop devices, dm-crypt/LUKS2, Docker Compose v2, static primary IPv4
after reboot, access to Storj/Google/Stripe/OpenRouter, and independent
`findmnt`/`cryptsetup status` evidence. Obtain written support confirmation that
one regular-VPS FFmpeg process may use up to three vCPU during the 60-minute
soak; any throttling, OOM, or refusal blocks production.

Initialize both PostgreSQL databases once. Restore environment-specific OAuth,
Stripe, OpenRouter, Storj, invite allowlist, public age recipient, and H2SE
keyring files from encrypted operator escrow only after LUKS is mounted.
Produce and verify the first age-encrypted backup before the first candidate.
The permanent staging database and diagnostics survive releases unless an
explicit test reset is approved. Staging and production escrow material never
overlap.

Production receives exactly one
`hook2stream-staging ssh-ed25519 ...` record at
`/etc/hook2stream/staging-receipt-allowed-signers` mode `0600`; extra, wildcard,
stale, or non-ED25519 staging authorities block promotion. The staging-receipt
authority, operator-login key, and forced-command deploy key must be different
from one another. Host validation treats
any cross-role key reuse as a release-blocking privilege boundary failure.
The ED25519 SSH host private key is additionally required to be a root-owned,
mode-`0600`, non-symlink file without extended ACLs. Each permanent host has a
different exact pinned ED25519 host key, distinct from all user and
receipt-authority keys.

Create one Storj account without a bank card, but do not create either
Standard/global project until the user records an explicit encryption-model
decision. The choice is irreversible. Managed encryption is recommended for
this MVP because projects created after 2025-11-30 cannot use exhaustive S3
listing with Self-Managed encryption; H2SE and age still ensure Storj receives
only ciphertext. Self-Managed requires a separate escrowed passphrase per
environment and live proof that every required list/prefix operation works.
Fund with STORJ on zkSync Era when the operator wallet supports it; use Ethereum
L1 only when L2 is unavailable. Keep at least USD 50 or three months of forecast
Storj cost, whichever is greater. Storj crypto balances do not auto-recharge,
so Servers.Guru and Storj billing email reviews remain an operator duty.

## Domain and public edge

Immediately before purchase, perform an ICANN RDAP lookup for
`hook2stream.com`. If it is already registered, stop the rollout and ask the
owner for a replacement; never select one automatically. Register it through
Cloudflare Registrar and enable 2FA, auto-renew, registrar lock, DNSSEC, and
billing/domain-expiry email.

Create only DNS-only records:

- `A @` -> the primary IPv4 of the permanent Amsterdam `NL1-4` production VPS;
- `A staging` -> the primary IPv4 of the permanent Montreal `MTL1-3` staging VPS;
- `CNAME www` -> `Tassadar2499.github.io`;
- the GitHub domain-verification TXT record returned for
  `hook2stream.com`.

Verify each paid server's primary IPv4 before publishing its A record. UFW must
deny non-Caddy public traffic until that host passes its bootstrap gate. Confirm
both application answers through a public resolver. Publish `A @` only after
the Amsterdam production host
has passed Tailscale, LUKS, Storj, environment-secret, and host validation
gates. Perform the apex change and production promotion inside an announced
MVP maintenance window; record the prior apex value so a failed promotion can
restore DNS while the application is forward-fixed. During a provider
migration, record and verify both A-record values independently.

Do not create AAAA records until equivalent IPv6 UFW rules are tested. Caddy
terminates TLS only for the apex and staging. GitHub Pages owns
`www.hook2stream.com`; Caddy must not claim or redirect it. Keep the GitHub TXT
record permanently, set the Pages custom domain, verify TXT and CNAME through a
public resolver, wait for the Pages certificate, and only then enable Enforce
HTTPS. The checked-in `site/CNAME` asserts the intended domain but does not
configure the GitHub repository setting.

Staging returns `X-Robots-Tag: noindex, nofollow, noarchive`. Register Google
callbacks as `https://staging.hook2stream.com/api/v1/auth/callback` and
`https://hook2stream.com/api/v1/auth/callback`. Register the corresponding
Stripe webhooks at `/api/v1/billing/stripe/webhook` on those exact two origins.
Unknown Google accounts fail closed; production accepts only pre-issued invites.

## Servers.Guru host bootstrap and LUKS2

Servers.Guru issues an initial root credential and provides panel VNC. It does
not document custom cloud-init or SSH-key injection. Use that credential
through a verified initial SSH/VNC session and create the locked-password,
key-only operator and deploy users. For this temporary MVP exception, root
retains its active password for ordinary OpenSSH recovery, but TCP 22 is
reachable only through `tailscale0`; the password must never enter GitHub,
logs, shell history, candidates, or another account. If public SSH is
temporarily required, add a TCP 22 UFW
rule limited to the operator's fixed `/32`
in UFW. Provider-level firewalling is not assumed. Install and interactively enroll Tailscale,
run `sudo tailscale set --ssh=false`, add only `sudo ufw allow in on tailscale0
to any port 22 proto tcp`, then run `sudo systemctl unmask --runtime
ssh.service ssh.socket` and `sudo systemctl enable --now ssh.service` from the
console. Prove MagicDNS ordinary OpenSSH for both the operator key and the root
password, then remove both temporary `/32`
rules from the Tailscale session and verify public IPv4 TCP 22 is closed. Never open TCP 22 to
`0.0.0.0/0`; if the exact `/32` procedure is unavailable, keep the rollout
blocked and finish through the out-of-band console.

Before host acceptance, install the checked-in SSH policy with exact paths and
modes:

```sh
sudo install -o root -g root -m 0644 \
  src/deploy/host/sshd-no-public-ssh.conf.example \
  /etc/ssh/sshd_config.d/99-hook2stream-no-public-ssh.conf
sudo sshd -t
sudo systemctl reload ssh.service
```

Neither permanent host uses SSH host certificates. Pin each
host's exact ED25519 key after reading it through VNC or an already authenticated
Tailscale session; do not hand-author substitute drop-ins.

Both servers use the same exact paths:

- backing file `/var/lib/hook2stream-data.luks`;
- mapper `hook2stream-data`;
- mount `/srv/hook2stream`.

Create a fully allocated file of exactly 48 GiB on staging or 64 GiB on
production. It must be a non-symlink owned by `root:root` with mode `0600` and
allocated blocks equal to its logical size. Attach that exact file to a loop
device, format the loop device as LUKS2, and create the filesystem on the
dm-crypt mapper. The unlock key stays only with the operator; it is never saved
on the VPS, in GitHub, Servers.Guru, Storj, or an installation payload.

Place Docker data-root, all named volumes including PostgreSQL, release state,
host secrets, application logs, worker scratch, and a 4 GiB swap file below
`/srv/hook2stream`. The encrypted swap file is `root:root` mode `0600`; do not
enable root-filesystem swap. Docker and Hook2Stream units must use
`RequiresMountsFor`, `After`, and `ConditionPathIsMountPoint` so neither starts
without the mount.

Before the first host validation, also install the Docker mount-guard template,
persist `/proc` with `hidepid=2` or `hidepid=invisible`, create root-private
`config`, `releases`, and `release-state` directories, install the exact
environment file and scalar secrets, and install
`src/deploy/host/authenticated-e2e.sh` unchanged as the root-owned mode `0500`
authenticated hook. Before the first release, provision its
environment-specific expected-email scalar, licensed MP3 and staging soak
baseline below the encrypted `/srv/hook2stream/e2e` directory exactly as
documented in the host README. The OAuth cookie jar is installed later, during
the explicit cold-bootstrap handoff after the candidate origin is running; do
not use a placeholder. At finalization, missing, linked, loosely permissioned,
stale or wrong-account inputs block deployment. Create
`hook2stream-deploy` separately from the operator, without Docker/secrets-group
membership, and install the forced-command launcher, `authorized_keys`,
sudoers, validation libraries, and production signer file with the modes
enforced by `validate-host.sh`. Each account has exactly one approved ED25519
key. Put their OpenSSH SHA-256 fingerprints in the root-owned `deploy.conf` as
`HOOK2STREAM_OPERATOR_PUBLIC_KEY_SHA256` and
`HOOK2STREAM_DEPLOY_PUBLIC_KEY_SHA256`; install the deploy key only through the
exact `restrict,command="/usr/bin/sudo -n /usr/local/sbin/hook2stream-deploy-launcher"`
record and the exact two-line `sudoers.example` drop-in. Extra keys, options, or
sudo grants block host acceptance.

Automatic LUKS unlock is forbidden. After every reboot, prefer the already
verified ordinary OpenSSH session over Tailscale to attach the backing file to
a loop device, unlock it interactively, mount it, enable encrypted swap, and
only then start Docker. If guest networking is unavailable, perform those same
installed-OS steps through the verified Servers.Guru panel VNC console. Prove
VNC reaches the boot prompt before depending on this path. Run the environment-specific
validator after bootstrap and every reboot:

```sh
/usr/local/libexec/hook2stream/validate-host.sh app staging
/usr/local/libexec/hook2stream/validate-host.sh app production
```

It must prove the exact backing file -> loop -> LUKS2 -> mapper -> mount chain,
exact capacity, full allocation, permissions, encrypted swap, Docker data-root,
the installed and loaded Docker systemd mount guard, Tailscale, UFW, secret
modes, listener policy, and at least 20 percent free space on both the root
filesystem and encrypted filesystem. Downtime until manual unlock completes is
accepted for this MVP.

## Network and access policy

The named operator and `hook2stream-deploy` users remain password-locked and
key-only and are not in `docker` or any secret-reader group. Because this
configuration tree intentionally forbids `Match` blocks, the global SSH policy
uses `PasswordAuthentication yes`, `AuthenticationMethods any`,
`PermitRootLogin yes`, and the exact `AllowUsers root hook2stream-operator
hook2stream-deploy` list. The host validator compensates fail-closed by
requiring locked local passwords for both named users and an active password
only for root. Keyboard-interactive, host-based, GSSAPI, Kerberos, and
empty-password authentication remain disabled. Public TCP 22 may exist only
during initial bootstrap; after Tailscale is ready, SSH is allowed exclusively
on `tailscale0`.
Immediately after joining the tailnet run `sudo tailscale set --ssh=false`.
Tailscale SSH is forbidden because it intercepts tailnet port 22 before the
pinned OpenSSH keys and forced command. Both validators accept only a disabled
`tailscale get --json ssh` response: the legacy JSON literal `false` or an
object whose only member is the boolean `"ssh": false`; extra keys, enabled or
malformed values, and every other shape fail closed.

Retaining a remotely usable root password increases the impact of tailnet or
credential compromise and is an explicit temporary MVP risk. Use a unique,
high-entropy root password per host, keep it only in encrypted operator escrow,
rotate it immediately after suspected disclosure, and review removal of this
exception before public signup. UFW's exact Tailscale-interface rule, the
absence of public TCP 22, and the validator's separately resolved root
effective policy are mandatory; failure of any one blocks host acceptance.

Only Caddy publishes 80/TCP, 443/TCP, and 443/UDP. After the documented initial
`/32` SSH bootstrap, those are the only public ingress rules in UFW. UFW allows SSH only inbound
on `tailscale0`; public TCP 22 is removed after bootstrap. PostgreSQL,
PgBouncer, API, workers, Docker, and every tool container stay private.
Default-deny inbound, routed, and unreviewed IPv6 traffic. Mount `/proc` with
`hidepid=2` or the equivalent invisible mode before installing runtime secrets.
Both Servers.Guru profiles use regular shared vCPU. This MVP does not configure
or rely on IPv6, provider backups, snapshots, monitoring agents, provider DDoS
protection, or private-network-only
routing, even if the current platform offers those features. This is an
accepted closed-MVP risk, not a reason to expose additional listeners.

GitHub-hosted runners join Tailscale as ephemeral OIDC-federated nodes. ACLs
allow `tag:hook2stream-ci-staging` only to staging TCP 22 and
`tag:hook2stream-ci-production` only to production TCP 22. Staging identities
never reach production. Do not store reusable Tailscale auth keys in GitHub.

Enroll the permanent staging host as `h2s-app-staging` and the permanent
production host as `h2s-app-production`. Use those MagicDNS names as the
corresponding `DEPLOY_HOST` values. Generate unrelated ED25519 CI deploy keys
for the two environments and install only their public halves in the
forced-command `authorized_keys`.

Each host retains a different exact pinned ED25519 host-key record obtained
through panel VNC or an already authenticated Tailscale session. An
unauthenticated `ssh-keyscan` result is not trust evidence. Configure the
GitHub Environments exactly as described in
[`DEPLOYMENT.md`](../../.github/DEPLOYMENT.md), then prove the
environment-specific CI tag cannot reach the other host.

Role-specific Squid proxies are the only application egress paths. API and
workers may reach only their required Google, Stripe, OpenRouter, and media
Storj endpoints. `postgres-backup` uses a separate proxy that permits only the
exact `BACKUP_S3_ENDPOINT_HOST`. Wildcards and arbitrary HTTPS origins are
forbidden. The currently pinned endpoint for both roles is
`gateway.storjshare.io`, but media and backup endpoint variables remain
independent and must be validated separately.

## Storj storage contract

Create the following private buckets with location `global-1`:

| Environment | Media bucket | Backup bucket |
|---|---|---|
| staging | `hook2stream-com-staging-media` | `hook2stream-com-staging-pg-backups` |
| production | `hook2stream-com-production-media` | `hook2stream-com-production-pg-backups` |

Media buckets are unversioned and browser S3 access/CORS stay disabled. Backup
buckets are versioned. Storj bucket CORS operations are unsupported and must
not be called.
Storj does not support the S3 Lifecycle API used by AWS/MinIO, so deployed code
must never call `PutBucketLifecycle`. The 35/160 GiB media and 10/30 GiB backup
values are operating thresholds, not a claim that Storj enforces a bucket
quota.

For each environment, derive from one escrowed full project grant. A
Self-Managed project also has its own off-host escrowed passphrase:

- media runtime: Read, Write, List, Delete, restricted to its media bucket;
- backup writer: Read, Write, List, no Delete, restricted to its backup bucket,
  with `MaxObjectTTL=168h` on staging or `840h` on production;
- restore read-only: backup bucket only, kept off both VPS instances;
- root: operator-held only and never installed on an app host;
- bootstrap: a separate temporary full-project S3 credential, revoked after
  bootstrap and live acceptance with its denial recorded as evidence.

Use these exact `uplink` permission shapes, substituting values only inside the
operator's encrypted session and capturing registered credentials directly into
the encrypted secret store:

```sh
# Media runtime
uplink share --access <environment-root-grant> \
  --readonly=false --register sj://<environment-media-bucket>/

# Backup writer (choose 168h for staging or 840h for production)
uplink share --access <environment-root-grant> \
  --readonly=false --disallow-deletes --max-object-ttl <168h-or-840h> \
  --register sj://<environment-backup-bucket>/

# Off-host restore reader
uplink share --access <environment-root-grant> \
  --readonly --register sj://<environment-backup-bucket>/
```

These are syntax placeholders, not real grants. Do not paste an access grant,
passphrase, registered access key, or secret key into Git, shell history,
terminal capture, CI output, or this documentation.

Credentials and passphrases never cross environment or role boundaries. Follow
[`src/deploy/storj/README.md`](../../src/deploy/storj/README.md) to create grants,
install the exact hash-locked `boto3==1.35.99` operator client, bootstrap
buckets, and run live acceptance. The bootstrap requires the approved
`STORJ_ENCRYPTION_MODEL=managed|self-managed` and publishes the private
`.hook2stream/contracts/storage-v1.json` marker last and prints its SHA-256.
Store only that digest in the root-owned environment file. Before backup or a
migration, the host gate fetches the marker with runtime authentication, proves
the expected provider/environment/project/buckets/H2SE/retention contract, then
performs a disposable media PUT, HEAD, single-range GET, and DELETE.

Deployed environments must use:

```dotenv
STORAGE_MODE=external
STORAGE_PROVISIONING_MODE=VerifyOnly
STORAGE_OBJECT_EXPIRATION_MODE=Storj
S3_ENDPOINT_HOST=gateway.storjshare.io
S3_SERVICE_URL=https://gateway.storjshare.io
S3_REGION=global
S3_FORCE_PATH_STYLE=true
S3_CONFIGURE_BUCKET_LIFECYCLE=false
S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE=false
BACKUP_S3_ENDPOINT_HOST=gateway.storjshare.io
BACKUP_S3_ENDPOINT=https://gateway.storjshare.io
BACKUP_S3_REGION=global
BACKUP_S3_FORCE_PATH_STYLE=true
STORAGE_PROTOCOL_VERSION=1
STORAGE_CONTRACT_KEY=.hook2stream/contracts/storage-v1.json
```

`VerifyOnly` verifies a pre-created bucket and performs no bucket, CORS, or
lifecycle mutation. `Manage` is permitted only in local/CI MinIO. Browser S3
URLs and CORS stay disabled; public upload/download APIs remain same-origin.

H2SE temporary data and manifests under `staging/` receive the same absolute
24-hour object TTL. A daily janitor uses the media credential to abort
incomplete multipart uploads older than 24 hours. Storj does not expose the
upload-ID pagination markers needed for another page, so the janitor requests
at most 1000 uploads and fails before any abort when that response is truncated.
Backup uploads do not use multipart. Storj still bills objects deleted before
30 days for the 30-day minimum and bills objects smaller than 50 kB as 50 kB;
TTL remains a retention control rather than a guaranteed cost reduction. There
is no storage host, MinIO deployment workflow, storage
GitHub Environment, storage Tailscale node, or storage forced command. The
checked-in `compose.minio.yaml` remains solely for disposable local development
and CI and is excluded from release candidates. Because the final MinIO
Community release is archived, its CI vulnerability scan is inventory-only;
the image is never published or deployed, while every candidate image retains
the blocking High/Critical scan.

## Secrets and encryption

Each VPS has a separate root-owned secrets directory on its encrypted mount.
Files are non-symlinks, owned by `root:<service-group>`, and mode `0640`. OAuth,
Stripe, OpenRouter, runtime media S3, backup S3, PostgreSQL, session, invite,
age, and H2SE keyring values never enter Git, Servers.Guru provider records,
candidates, Compose output, or CI logs. Storj root and restore grants, any
Self-Managed project passphrases, and the temporary bootstrap credential are
operator-held off-host; the bootstrap credential is revoked after acceptance.

Media objects use H2SE v1 ciphertext; Storj never receives plaintext or an H2SE
KEK. Rotate the active environment-specific KEK every 90 days and retain old
KEKs until their inventory is zero. Keep an encrypted escrow copy outside
Servers.Guru, Storj, and GitHub. Set `AllowLegacyPlaintextReads=false` from the
first object.
After any H2SE v1 write, rollback to a release without the v1 reader is
forbidden.

PostgreSQL backups are age-encrypted before leaving the host. Only the public
age recipient is installed on a VPS; the recovery identity stays on an operator
recovery device. See `src/deploy/secrets/README.md` for the scalar file contract.

## Release promotion and rollback

Protect `main`; require pull requests, required checks, review, conversation
resolution, and prohibit force-push/deletion/bypass. Only the `staging`,
`production`, and `github-pages` deployment Environments are required. Any old
`storage-staging` or `storage-production` Environment must be removed after
verifying it has no consumers.

The `CI` workflow creates one immutable
`release-candidate-<sha>-<run_id>-<attempt>` containing schema-v1 metadata,
digest-only images, the app deploy bundle, and checksums. It never creates or
deploys staging automatically. After the permanent staging host has passed
provider and host acceptance, the separately dispatched staging workflow takes
a successful protected-main `source_ci_run_id`, verifies its attestations and
candidate, deploys it without rebuild, runs smoke/E2E/storage gates plus the
60-minute soak, and publishes a signed staging receipt. Production proves that
receipt refers to the exact candidate through `workflow_dispatch` with the
staging run ID, waits for two-reviewer/no-self-review GitHub Environment
approval, and deploys the same digests without rebuild.

The first release in each environment is an explicit two-dispatch cold
bootstrap:

1. Dispatch the selected workflow with
   `deployment_phase=prepare-pending`. The host accepts `prepare` only when
   `last-successful.env` does not exist, starts the candidate runtime, and
   returns a pending receipt. It does not mark the candidate successful, run
   soak, or issue promotion evidence.
2. Against that running origin, sign in with the dedicated pre-invited Google
   QA account, complete onboarding, and verify/create the intended QA workspace.
   Export the short-lived OAuth cookie jar and atomically install it below
   `/srv/hook2stream/e2e` as the configured non-symlink `root:root` private
   file. Do not place Cookie or CSRF values in shell history or logs.
3. Dispatch the same source run and full artifact with
   `deployment_phase=finalize-pending`. The host revalidates the pending
   candidate and running digests, runs authenticated E2E, then publishes the
   successful release state. A cold E2E failure retains the exact pending
   transaction for a corrected retry; it does not authorize another candidate.

After a successful release exists, use only the default
`deployment_phase=deploy-and-finalize`. Its `deploy` forced command performs
runtime transition, authenticated E2E, digest verification, and successful
state publication as one host-side transaction under the deployment `flock`.
There is no operator handoff between deployment and validation, and `prepare`
is rejected on an established host.

The root-owned `release-state/pending-deploy.json` binds the full candidate
artifact, not merely its commit: environment, previous successful SHA,
release-images, bundle and derived environment hashes, a generated 32-hex E2E
operation ID, and, in production, staging receipt/signature/allowed-signer
hashes. Only an exact replay may reuse this state. A same-SHA artifact from a
different run/attempt, a changed signer file, or any other drift is rejected.
The persisted operation ID provides stable idempotency keys within an exact
attempt, while different attempts receive different IDs. The successful remote
result is also stored per full artifact; after a lost SSH response it may be
re-emitted only after live health, the active-infrastructure ledger, the
successful environment, and all running digests are revalidated.

All candidate runtimes come from repository-owned GHCR builds. In particular,
Caddy 2.11.4 is rebuilt from its exact upstream commit on the pinned Go 1.27.0
builder with reviewed security dependency updates and a scratch runtime under
UID/GID 10001. Before Caddy starts, a no-network, capability-limited one-shot
job uses the pinned PostgreSQL image to initialize/chown only the named
`/data` and `/config` volumes; the public Caddy container retains only
`NET_BIND_SERVICE` and cannot run as root;
PgBouncer 1.25.2 is built from its checksummed upstream release; and Squid 7.6
uses exact patched Alpine 3.24 packages and runs as UID/GID 31. Their published
digests carry SBOM/provenance and must pass the blocking High/Critical scan with
`only-fixed=false`. Candidate validation rejects the former external Caddy,
edoburu/PgBouncer, and Ubuntu/Squid repositories.

Freeze protected `main` from the staging workflow dispatch through its signed
application receipt, production approval, and
the production SSH deployment. A merge during deploy or the 60-minute soak
intentionally makes the workflow policy SHA stale and invalidates that rollout;
select and stage a new candidate instead of bypassing the live-main checks.

Runner policy is never taken from the selected release commit. Verification,
host-key and receipt validation, and all sourced helpers execute
only from the exact current `github.workflow_sha`, which must equal the
protected-main dispatch SHA. The historical candidate crosses the job boundary
only as attested data and is revalidated by current policy after the staging or
production Environment boundary; it cannot set workflow environment/PATH
state before deploy or signing credentials are used.

The signed staging receipt binds the exact workflow run/attempt, source CI
run/attempt/SHA, immutable image digests, bundle hash, host-observed digests,
Storj probe, backup freshness, smoke/E2E, and 60-minute soak result. Promotion
verifies its dedicated ED25519 signature before and after approval; production
repeats the same receipt check. Missing, stale, failed, cross-environment, or
replay-mutated evidence blocks approval.

For the first H2SE v1 deployment, preselect a protected-main candidate whose
build, test, security, and candidate-publication jobs succeeded. Before
dispatch, establish its exact commit SHA as the shared immutable
`MIN_ROLLBACK_RELEASE_SHA` in both staging and production host settings and in
both GitHub Environment variables. Hostnames, SSH keys, WIF/Tailscale
identities, and all other environment-specific values remain distinct. A
placeholder or any candidate older than this H2SE-reader floor blocks rollout.
Later staging releases preserve that original floor; they never reset it to
each selected candidate, and every selected candidate must be a protected-main
descendant of it. Production promotes the exact staging-tested descendant.

`hook2stream-deploy` has no Docker or secret access. Its restricted SSH key
invokes only the root-owned forced command. Candidate validation rejects tags,
unknown images, checksum/schema/repository mismatch, archive traversal, links,
special files, and receipt mismatch. GitHub concurrency and host `flock` both
serialize deployment. After `deploy <candidate-id>` or the first successful
`finalize <candidate-id>` completes on staging, CI opens a separate
`soak <candidate-id>` SSH operation. The wrapper accepts it only for the exact
currently successful candidate and holds
the same `flock` for the entire sustained test; rollback invalidates this
eligibility.

If a normal deploy or finalization fails after a previous successful release,
the host compensates by restoring that previous application image set. It does
not perform a down migration and does not restore old Caddy, PostgreSQL,
PgBouncer, backup, proxy, or bootstrap images: the candidate infrastructure
bundle remains active, and the durable ledger must describe the candidate
infrastructure plus restored application. Compensation handles termination
signals as part of the transaction. If application restoration, digest
verification, or state publication cannot be proven, the host writes
`/srv/hook2stream/release-state/recovery-required.json`, stops the owned public
Caddy container where possible, and blocks all automated deploy, finalize,
soak, and rollback commands. Treat that marker as a manual incident: reconcile
database, runtime digests, `last-successful.env`, and
`active-infrastructure-release.json` before clearing it. Never delete the
marker merely to unblock CI.

Before any migration, the wrapper requires the signed Storj marker, authenticated
media probe, and a backup newer than two hours. Rollback changes only API,
workers, and web to previously successful H2SE-compatible digests. It never
runs a down migration or rolls back PostgreSQL/Caddy/PgBouncer/backup/proxy
images. Before it commits rollback state, the root-owned orchestrator runs the
bounded, non-mutating `rollback-verify` gate: exact OAuth identity, H2SE
single-range read, worker state, preview/export reads, and denied egress. It
does not upload media, create billing events, start renders, or migrate the
database. A gate, digest, or signal failure reverses the app to its original
release; an unprovable reversal enters the same recovery-required and
closed-ingress state. An incompatible database schema requires a forward fix
or separately approved write-stop and restore.

## Backup and recovery drills

The backup sidecar runs hourly, streams a custom-format `pg_dump` through
`age`, and uploads ciphertext and checksum with single `PutObject` requests.
It records version IDs and publishes the authenticated manifest last. The writer
has no Delete permission, and retention comes from the grant's 168/840-hour
maximum object TTL rather than a lifecycle or delete job. The local success
marker must be newer than two hours before deploy/migration.

Before live Stripe, complete and record all of these drills:

1. Reboot both permanent Servers.Guru VPS instances and prove manual panel VNC
   loop/LUKS/mount/swap/Docker recovery plus host
   validation.
2. Treat the production app host as lost and build an empty temporary contour.
3. Use the off-host restore grant to download a real versioned production
   manifest, checksum, and age ciphertext; verify and decrypt off-host.
4. Restore into empty PostgreSQL with writes isolated.
5. Return the escrowed H2SE keyring, fetch a real media object, and prove
   authenticated decryption plus HTTP Range playback.
6. Record measured RPO/RTO and destroy temporary plaintext, state, and
   credentials.

Repeat recovery monthly and before risky migrations. A restore never overwrites
the live database without explicit approval and stopped writes.

## Go-live and decommission gates

Live Storj acceptance must pass in both projects: privacy, SigV4, marker hash,
cross-environment and cross-role denial, media PUT/HEAD/206 Range/DELETE,
multipart list/abort, backup versioning and version IDs, writer Delete denial,
and the delayed 168/840-hour TTL-expiry proof. Verify that uploaded H2SE objects
contain no plaintext MP3/PNG/ZIP markers.

A staging release must prove invite-only Google test login, Stripe test checkout and
duplicate webhook, licensed MP3 upload, all workers, OpenRouter analysis and
artwork, preview seek, 18 renders, ZIP, idempotent deploy, concurrency lock,
compatible rollback, exact running digests, egress denial, no OOM or sustained
shared-CPU throttling, and at least 20 percent free disk. The render/network
soak is a separate root-owned `HOOK2STREAM_E2E_HOOK ... soak-60m` operation,
not a readiness loop. It must run for 3600--3900 measured seconds and return one
strict `hook2stream-soak-hook-result-v1` JSON line proving at least one completed
18-item staging render, at least 3300 active FFmpeg load seconds, maximum
render concurrency one, at
least 60 network checks with zero failures, no CPU throttling, and no OOM. The
checked-in authenticated hook records the real initial render duration during
the staging release gate, rejects only a slowdown over 20 percent against a
retained same-SKU baseline no older than 90 days, and then starts one bounded
3600-second `lavfi` to null FFmpeg process in a dedicated networkless,
read-only container built from the exact running worker digest with the same
three-vCPU/1536-MiB limits. The labeled container is removed in every exit
path. It does not consume a content rerender or another billing
entitlement. Each minute contributes a network check only after
both public/API readiness and an authenticated small Storj HEAD/Range cycle
succeed. The operator hook samples `/proc/stat` once per minute and retains the
raw deltas privately: `cpuThrottled=false` is permitted only when no five-minute
window exceeds 10 percent steal time, cgroup `nr_throttled`/`throttled_usec`
remain within policy, and real render throughput is no more than 20 percent
slower than the accepted same-SKU probe baseline. A faster host passes.
Missing samples or a missing baseline fails closed. The
wrapper then independently proves exactly one healthy `worker-render`, its
candidate digest, and `OOMKilled=false`; the signed receipt binds the result to
the candidate and commit. Hook stderr and diagnostics
remain in a root-private temporary directory and are not copied to CI logs.
Retain the accepted same-SKU shared-CPU probe and soak evidence for the tested
FFmpeg workload. Any throttling, network failure, OOM, unexpected
disk growth, missing written FFmpeg approval, projected pair cost above EUR 40,
or a price increase above ten percent blocks promotion. Record the candidate,
source CI run, signed receipt, backup freshness, and diagnostics. A failed
staging run is diagnosed on the persistent host; capture logs and a fresh
encrypted backup without deleting or rebuilding it. Production must promote
the same accepted candidate without rebuild.

Production additionally requires two GitHub reviewers, the exact staged
digests, successful recovery evidence, TLS and security headers, OAuth, a
controlled live Stripe payment/refund, encrypted upload/range/download, and at
least 30 minutes of manual observation. Lack of the second reviewer blocks live
payments but not staging.

Before enabling live Stripe, retain evidence that the operating company and
Stripe account are approved in a supported country. Hosting the application in
Servers.Guru Amsterdam does not establish Stripe eligibility and must not be treated as a
substitute for Stripe approval.

External observability and alerting are intentionally not configured. Keep
`OTEL_EXPORTER_OTLP_ENDPOINT` empty. Operators must manually inspect
Docker health, backup age, root and encrypted disk usage, OOM events, queue age,
gateway 5xx/GCM failures, denied egress, TLS, and provider balances. There are
no automatic downtime, backup-age, OOM, TLS, or disk-full alerts; this accepted
risk does not relax any gate above.

Keep legacy provider app/storage hosts, storage Tailscale identities, GitHub
storage Environments, and credentials untouched for seven stable days after
production cutover. No media/data transfer is expected. After that period,
obtain explicit operator confirmation, prove no consumer or recoverable data
remains, revoke old credentials, remove old DNS/ACL/GitHub configuration, and
only then delete the old resources. Never fold legacy-provider destruction into
Servers.Guru bootstrap or application promotion.

This remains a single-node-per-environment closed pilot without HA, PITR,
contracted DDoS protection, or an independent media copy. Production uses a
shared-CPU `NL1-4` VPS in Amsterdam; permanent staging uses a shared-CPU
`MTL1-3` VPS in Montreal. Before public signup, move PostgreSQL to managed
35-day PITR, run at
least two application instances behind a load balancer, and add an independent
media/backup replica.

# Hook2Stream release workflow setup

The workflows are fail-closed until the repository, GitHub Environments,
Tailscale identities, Servers.Guru app hosts, and Storj storage contract below
exist.
Release candidates contain no application, provider, or infrastructure secret.

## Repository controls

Protect `main`: require pull requests, the complete `CI` check set,
conversation resolution, and block force pushes, deletion, and bypass.
Deployment Environments are only:

- `staging`, restricted to protected `main`;
- `production`, restricted to protected `main`;
- `github-pages`, restricted to protected `main`.

Delete legacy `storage-staging` and `storage-production` Environments only after
confirming no workflow or secret consumer remains. Production MinIO/storage
workflows, candidate receipts, deploy keys, and Tailscale tags do not belong to
this architecture. This solo-maintainer MVP intentionally has no second
production reviewer; workflow actor and explicit-confirmation gates apply
instead.

Set the repository variable `PRODUCTION_DEPLOY_ACTORS` to exactly
`Tassadar2499`. The production workflow accepts only a dispatch whose actor and
triggering actor both equal that sole entry, and whose
`production_confirmation` input is exactly `DEPLOY hook2stream.com`. It repeats
these checks after entering the `production` Environment and before any
credential-bearing or host-mutating action.

## Application deployment Environments

Create `staging` and `production`. Keep these values distinct between the two
Environments:

- `DEPLOY_HOST`: the full MagicDNS name
  `h2s-app-staging.<tailnet>.ts.net` or
  `h2s-app-production.<tailnet>.ts.net`; never use a public Servers.Guru IPv4;
- `DEPLOY_SSH_PRIVATE_KEY`: its ED25519 deploy key;
- `DEPLOY_SSH_KNOWN_HOSTS`: the exact pinned ED25519 record for that
  environment's `DEPLOY_HOST`;
- `TS_OAUTH_CLIENT_ID` and `TS_AUDIENCE`: environment-specific Tailscale
  workload-identity federation values. Despite the action input name,
  `TS_OAUTH_CLIENT_ID` is the federated identity client ID, not a reusable
  OAuth client credential; neither Environment stores an OAuth secret or auth
  key.

Set `MIN_ROLLBACK_RELEASE_SHA` to the same full H2SE-capable baseline SHA in
both Environments for one rollout, and mirror that exact value in both
root-owned host wrapper configurations. Staging signs the host-observed value;
production rejects a different baseline, a placeholder, or a candidate that is
not that commit or its descendant.

Staging also supplies `STAGING_RECEIPT_SIGNING_KEY`, a dedicated ED25519 private
key used only for successful staging receipts. Store the corresponding public
key as repository variable `STAGING_RECEIPT_ALLOWED_SIGNERS` in OpenSSH
allowed-signers form. The value must contain exactly this one non-comment
ED25519 record; extra, wildcard, stale, and non-ED25519 authorities fail both
pre-boundary and post-boundary validation:

```text
hook2stream-staging ssh-ed25519 AAAA...
```

GitHub-hosted runners use OIDC workload federation and ephemeral Tailscale
nodes. Create two separate Tailscale OpenID Connect trust credentials with
issuer `https://token.actions.githubusercontent.com`. Give each credential
only the writable `auth_keys` scope and exactly its one requested tag. The
live GitHub issuer returns immutable repository subjects for this public
repository. Use these exact subjects:

```text
repo:Tassadar2499@34176883/Hook2Stream@1295804906:environment:staging
repo:Tassadar2499@34176883/Hook2Stream@1295804906:environment:production
```

Do not infer the emitted token shape from the repository OIDC customization
compatibility flag: the Tailscale trust-credential validator is authoritative
and records the subject received from the issuer. A rename, transfer, or
repository recreation can change the immutable IDs. If that happens, stop
deployment and replace both environment subjects with the newly observed exact
values; never leave name-based, wildcard, and immutable credentials active in
parallel.

Use the generated client ID and audience only in the matching GitHub
Environment. `tag:hook2stream-ci-staging` may reach only staging TCP 22 and
`tag:hook2stream-ci-production` may reach only production TCP 22. Apply
[`deploy/providers/serversguru/tailscale-policy.hujson`](../deploy/providers/serversguru/tailscale-policy.hujson)
as the complete tailnet policy, not as an addition to a default allow-all
grant. It binds the accepted live Tailscale IPv4 addresses, grants the tailnet
owner ordinary OpenSSH access to both hosts, contains cross-environment deny
tests, uses no wildcard grant, and intentionally has no `ssh` section because
Tailscale SSH remains disabled on both hosts. Do not store a reusable
Tailscale auth key or OAuth client secret in GitHub. The two deploy keys, host
trust roots, and workload identities must not overlap.

Enroll the two permanent hosts as `h2s-app-staging` and
`h2s-app-production`. Generate each CI deploy key off-host, install only its public half using
`src/deploy/host/authorized_keys.example`, and place its private half only in
the matching Environment secret. Read each host key through the Servers.Guru
VNC console or an already verified Tailscale operator session and pin it
exactly.

Never establish trust with an unauthenticated `ssh-keyscan`. Before enabling
deployment, prove each CI tag reaches only its matching host and that strict
OpenSSH rejects a deliberately wrong host key.

Storj runtime/backup credentials, the storage marker digest, any optional
Servers.Guru read-only API key, OAuth/OpenRouter values, staging-only Stripe
test values, database/session secrets, age material, and H2SE keyrings are
host/operator concerns and never GitHub Environment secrets.

## Initial provider-to-CI handoff

Complete bootstrap once for each permanent host:

1. In the Servers.Guru panel verify the already paid staging `MTL1-3` record in
   Montreal and production `NL1-4` record in Amsterdam. Both must show Ubuntu
   24.04 amd64, the expected primary IPv4 and monthly term. Provisioning,
   rebuild, power, cancellation, snapshot restore, and backup restore remain
   manual operator actions. An optional provider API key is read-only and stays
   outside GitHub and both VPS instances.
2. Through a verified SSH or panel VNC session, use the issued root password
   to bootstrap and as the temporary MVP recovery credential. Install operator keys, enroll Tailscale with
   Tailscale SSH disabled, allow TCP 22 only on `tailscale0`, install
   `src/deploy/host/sshd-no-public-ssh.conf.example`, and require `sshd -t`.
   Remove public SSH; keep operator and deploy local passwords locked. Root is
   the only account with an active password, which must be unique per host and
   held only in encrypted operator escrow. Prove both operator-key and
   root-password ordinary OpenSSH through MagicDNS before installing any
   environment secret.
3. Run `validate-serversguru-probe.sh staging|production` and the matching
   `validate-host.sh app staging|production`. Prove KVM, `/dev/net/tun`,
   Tailscale, loop/dm-crypt/LUKS2, VNC recovery, Docker Compose v2, static IPv4,
   exact resource capacity, UFW, and required outbound integrations. Stripe
   egress is required only on staging; production must reject it. Production
   also requires written support acceptance of one FFmpeg job using up to three
   vCPU for the 60-minute soak.
4. Bootstrap and accept the environment's separate Storj contract, install its
   marker digest and runtime secrets, and prove the authenticated storage probe.
   Initialize each PostgreSQL database once and require its first encrypted
   backup. The permanent staging database is preserved between candidates
   unless an explicit test reset is approved.
5. Register the exact Google callback for each environment and the Stripe test
   webhook only for staging. Production starts with `BILLING_MODE=disabled` and
   has no Stripe credentials, Price IDs, webhook registration, or Stripe egress.
6. Configure the environment-specific deploy key, exact pinned ED25519 host
   key, Tailscale OIDC values, tailnet policy, and `DEPLOY_HOST`. Mirror the
   same first H2SE-capable `MIN_ROLLBACK_RELEASE_SHA` in both hosts and
   Environments.
7. Point Cloudflare DNS-only `A staging` to the `MTL1-3` IPv4 and `A @` to the
   `NL1-4` IPv4. Leave `www`, GitHub verification TXT, and AAAA unchanged; AAAA
   remains absent because these locations do not currently offer IPv6.
8. Select a successful protected-main candidate and dispatch `Stage candidate`
   with its `source_ci_run_id`. After the signed staging receipt and 60-minute
   soak pass, dispatch `Promote production` with that successful staging run
   ID, the intended deployment phase, and the exact confirmation
   `DEPLOY hook2stream.com`. Only `Tassadar2499` may dispatch it.

The staging dispatch remains fail-closed until every bootstrap item is
complete. Do not weaken Environment, host trust, Tailscale, storage, backup, or
forced-command controls to make a deployment pass. Production accepts only the
exact immutable candidate recorded by a successful signed staging receipt.

The currently successful staging release predates
`compose.billing-stripe.yaml`. Do not copy the overlay into that immutable
release and do not install control-plane checks that require it before the first
new candidate is active. Deploy the first overlay-bearing candidate through the
previous reviewed staging wrapper, confirm that candidate is the active
infrastructure release, then run the separate commit- and SHA-256-pinned staging
one-shot updater for the complete installed control plane. Production is cold
and uses its distinct updater before its first candidate. Verify both updater
signatures and offline self-tests; never reuse an updater across environments.

## Candidate promotion

After successful CI on protected `main`, build exactly one immutable
`release-candidate-<sha>-<run_id>-<attempt>` retained for 90 days. It contains:

- schema-v1 `release-metadata.json`;
- digest-only `release-images.env`;
- the application-only `deploy-bundle.tar.gz` from `src/deploy`;
- `SHA256SUMS`.

The bundle excludes production MinIO/storage-plane material. The checked-in
MinIO overlay remains local/CI only.

The main `CI` workflow only publishes the candidate. Once the permanent staging
host is accepted, manually dispatch `Stage candidate` with `source_ci_run_id`. That workflow
verifies the selected run is a successful protected-main run, downloads its
exact candidate and attestations, streams it to staging, performs the signed
Storj marker and authenticated storage probe, requires a fresh encrypted backup
before migration, runs smoke/E2E plus the 60-minute soak, verifies actual
digests, and publishes a signed `staging-receipt` artifact.

Freeze protected `main` from the `Stage candidate` dispatch until the staging
receipt is signed and production finishes its SSH
promotion (or the rollout is explicitly abandoned). Every secret boundary,
host mutation, and staging-receipt signature re-reads protected `main` and
requires it to remain the dispatch policy SHA. A merge during the 60-minute
soak intentionally invalidates that rollout; stage a new candidate from the
new protected-main policy instead of weakening this check.

Both the secretless verification job and every Environment-secret-bearing job
check out policy/helpers only at the exact current `github.workflow_sha` and
require it to equal the protected-main dispatch SHA. The selected historical
release SHA is treated only as attested candidate data: no helper, validator,
shell profile, or workflow command from that checkout is executed on a GitHub
runner. The verified candidate crosses into the credential-bearing job through
a new job/artifact boundary and is revalidated there with current policy.

Production starts only from `workflow_dispatch` with the required inputs
`source_staging_run_id`, `deployment_phase`, and `production_confirmation`.
Promotion verifies that exact successful `Stage candidate` run belongs to
protected `main`, extracts the source CI run/attempt/SHA from its signed receipt,
downloads that exact candidate, verifies the sole allowed actor and exact
`DEPLOY hook2stream.com` confirmation, and verifies the dedicated
staging-receipt ED25519 signature before and after the production Environment
boundary. The production host checks the same receipt again and streams the
same digest-only artifact without rebuild. A stale, failed, unsigned,
cross-environment, or mismatched receipt is rejected.
The staging and production concurrency groups are distinct and both use
`cancel-in-progress:false`; host `flock` is the second lock.

## Host command protocol

`hook2stream-deploy` is not in `docker` or any secret-reader group. Its
`authorized_keys` contains exactly one environment-specific ED25519 key with
`restrict` and the root-owned forced command through absolute
`/usr/bin/sudo`; the operator key file likewise
contains exactly one ED25519 key. Record both `ssh-keygen -lf ... -E sha256`
fingerprints as `HOOK2STREAM_OPERATOR_PUBLIC_KEY_SHA256` and
`HOOK2STREAM_DEPLOY_PUBLIC_KEY_SHA256` in root-owned `deploy.conf`. The host
validator also requires the exact two-line `/etc/sudoers.d/hook2stream-deploy`
grant from `src/deploy/host/sudoers.example`; extra keys, options, or sudo rules
fail validation. Operator, forced-command deploy, and staging-receipt trust
roles must all use different ED25519 keys; reusing the deploy
key for the password-locked operator account is a release-blocking privilege
escalation. The operator account has no sudo grant; host acceptance runs only
from a root shell through the installed root-owned validator. The private
ED25519 host key must be a non-symlink `root:root 0600`
file without extended ACLs; its `root:root 0644` public key must match, and both
must be distinct from every user/receipt authority. The SSH client command is
exactly cold-bootstrap-only `prepare <candidate-id>`, `finalize <candidate-id>`
for that same prepared candidate, normal transactional
`deploy <candidate-id>`, staging-only `soak <candidate-id>`, or
`rollback <40-character-sha> H2SEv1`.

`prepare` is accepted only before any successful release exists. It publishes
only a root-owned runtime-ready pending record so the operator can complete the
invited OAuth workspace/session bootstrap; it never emits a successful receipt.
`finalize` reuses the host-generated operation ID bound to that pending record
and publishes success only after authenticated E2E and exact running-digest
verification. Every later `deploy` performs rollout and finalization inside one
forced-command transaction. A failed or interrupted upgrade/rollback restores
the previous application digests when that reversal can be proven. Otherwise
the host writes `recovery-required.json`, stops its owned public Caddy, and
blocks all automated operations until explicit operator reconciliation.

Deploy receives one uncompressed tar stream. It contains `candidate/` with the
four immutable files; production also carries
`approval/staging-receipt.json` and `approval/staging-receipt.sig`. The host
rejects checksum/schema/repository/image allowlist mismatch, tags, duplicate or
unknown variables, archive traversal, links, special files, invalid staging
approval, and any mismatch between expected and running digests. Its final
success line is the base64-encoded `hook2stream-remote-deploy-result` record
prefixed with `HOOK2STREAM_REMOTE_RECEIPT=`.

Extracted releases remain `root:root 0700`. Before Compose may consume a
release, the forced command validates an exact allowlist of non-secret config
sources and changes only those files to read-only `0444` or executable
read-only `0555`. This is required because local file-backed Compose configs
preserve the source mode instead of applying the declared config `mode`. A
symlink, missing allowlisted file, owner mismatch, writable result, or unknown
path fails closed; secrets remain outside the release and are unaffected.

Use ordinary OpenSSH only. Bootstrap every host with
`sudo tailscale set --ssh=false`; validation fails unless
`tailscale get --json ssh` returns either the legacy JSON literal `false` or
an object whose only member is the boolean `"ssh": false`. Extra keys, enabled
or malformed values, and any other shape fail closed because Tailscale SSH would
intercept the tailnet listener before these key and forced-command checks.

After a successful staging deploy, the workflow opens a separate SSH command
`soak <candidate-id>` with no input stream. The forced command accepts only the
exact current successful candidate, holds the host lock, and invokes the
trusted root-owned E2E hook with fourth argument
`soak-60m`. The hook must run for 3600--3900 measured seconds and emit one
strict `hook2stream-soak-hook-result-v1` JSON line proving a completed render,
at least 3300 active render seconds, render concurrency exactly one, at least 60
network checks with zero failures, no throttling, and no OOM. The wrapper also
requires one healthy non-OOM `worker-render` on the candidate digest. It returns
only the bound base64 `hook2stream-remote-soak-result` line prefixed with
`HOOK2STREAM_REMOTE_SOAK_RECEIPT=`; hook stderr/logs are never mirrored into
Actions output. The signed staging receipt includes this result and the
`render-network-soak` check. The soak does not spend another render
entitlement: it reuses the completed real staging render only as throughput
evidence and runs a 3600-second synthetic FFmpeg load in a dedicated
networkless/read-only container from the running immutable worker image, with
the same three-vCPU/1536-MiB limits, while checking public/storage paths,
cgroup `cpu.stat`, OOM and restarts. The exact labeled container is force-
removed on every success or failure path.

The canonical implementation is `src/deploy/host/authenticated-e2e.sh`.
Install it as `root:root` mode `0500` and provision only root-owned inputs below
the encrypted `/srv/hook2stream/e2e` directory. It accepts a pre-issued OAuth
Mozilla cookie jar, verifies that session and its CSRF token through the public
`/api/v1/auth/session` endpoint, and never logs authentication or provider
secrets. Staging signs and repeats a Stripe test webhook only after proving test
API/Checkout identifiers. Production is a distinct non-billing gate: its
dedicated QA identity performs deterministic encrypted upload/range,
OpenRouter pipeline and preview verification, but it never creates a live
Checkout or starts a final render. The accepted staging receipt proves the
same digests' Stripe test, 18-render/ZIP and soak behavior. Production also
proves that checkout and webhook calls fail with `billing.disabled`, purchase
controls are disabled, Stripe secrets are absent, and `api.stripe.com` is
denied. Live billing is a separate future release after Stripe onboarding.
See the host README for the file and soak-baseline contracts.

Every successful forward release gets a root-owned protocol-v2 capability
document and becomes the root-owned active infrastructure marker. Rollback is
eligible only when that host recorded both the current application and target
as protocol-v2 successful releases supporting `H2SEv1`. A pre-v2 target or
current release fails closed. GitHub additionally requires the target to equal
the configured `MIN_ROLLBACK_RELEASE_SHA` or be its Git descendant, both before
tailnet access and immediately before SSH mutation. A SHA is an identity, not
an ordering primitive. Once an H2SE v1 object exists, a release without the
reader remains ineligible.

Rollback is application-image-only and is executed only by the installed
`/usr/local/libexec/hook2stream/rollback-application.sh`; target bundle scripts
are never executed or sourced. The active infrastructure marker is preserved,
and only its validated root-private Compose/helper bundle is used. Rollback
replaces API, worker, web, and release version while preserving the current bootstrapper and all infrastructure
digests. It runs no migration, down-migration, or storage mutation. Before and
after mutation, the host proves PostgreSQL, PgBouncer, Caddy, the backup sidecar,
storage janitor, and every egress proxy are unchanged. Incompatible schema
rollback requires a forward fix or a separately approved write-stop/restore.

## Storj deployment gate

There is no storage deployment workflow. An operator creates two Storj projects
and four private buckets outside GitHub, derives environment- and role-scoped
access grants, runs `src/deploy/storj/bootstrap-buckets.sh`, and stores only the
printed marker SHA-256 in each root-owned Servers.Guru environment configuration.

Staging/production use `STORAGE_MODE=external`,
`STORAGE_PROVISIONING_MODE=VerifyOnly`, and exact media/backup endpoints. The
host gate authenticates to the media bucket, verifies
`.hook2stream/contracts/storage-v1.json` and its pinned digest, then performs
PUT/HEAD/single-range GET/DELETE. `VerifyOnly` must not create buckets or mutate
CORS/lifecycle. The backup writer is bucket-scoped, has no Delete permission,
and carries the 168/840-hour maximum object TTL in its Storj access grant.

Never place a Storj full project grant, encryption passphrase, restore grant, or
bootstrap credential on a Servers.Guru host or in GitHub. See the canonical
operations runbook and `src/deploy/storj/README.md` for the operator flow.

## GitHub Pages landing

The static landing in `site/` owns only `www.hook2stream.com`. The production
application remains at the apex and staging remains at
`staging.hook2stream.com`; Caddy must never claim or redirect `www`.

For first publication:

1. verify `hook2stream.com` in the GitHub account using Cloudflare's DNS-only
   TXT challenge and keep that record permanently;
2. configure the repository Pages custom domain as `www.hook2stream.com`;
3. create DNS-only `CNAME www` -> `Tassadar2499.github.io`;
4. confirm TXT and CNAME with a public resolver;
5. wait for the Pages certificate and enable Enforce HTTPS;
6. run the manual-only `Pages` workflow from protected `main` and smoke `/`,
   `/styles.css`, and a nested missing URL for the styled custom 404.

The workflow intentionally does not run on push. Application CI publishes a
candidate, while deployment to permanent staging is a separate
operator-selected dispatch. A
checked-in `site/CNAME` alone does not configure the repository's custom domain.

## External operator boundaries

GitHub workflows do not register the domain, edit Cloudflare DNS, order,
rebuild, cancel, power, or restore Servers.Guru VPS instances, fund provider or
Storj balances, create Storj
projects/grants, unlock
LUKS, provision host secrets, or execute live OAuth/Stripe/render/recovery
drills. Those are explicit operator gates.

Servers.Guru account 2FA, wallet funding, renewal review, VNC access, manual
provider mutations, and shared-CPU acceptance are operator-only. The API is
optional read-only inventory/status evidence because published keys have no
documented scopes or expiry controls. The rollout gate stops above EUR 40 per
month for the pair, any ten-percent SKU price movement, a wrong location/image,
an overdue invoice, or an ambiguous provider record. Keep at least two complete
monthly pair budgets funded. The operator retains independent `findmnt`,
backing-device, and `cryptsetup status` evidence for each active host.

Crypto payment and wallet replenishment remain manual. Renewal invoices arrive
seven days before the due date, and a new account must not assume a grace
period. Provider backup, snapshots, IPv6, provider firewalling, and DDoS
protection are not release dependencies.

External observability and alerting integrations are deferred. Keep
`OTEL_EXPORTER_OTLP_ENDPOINT` empty. The operator must manually review
local health, backup age, disk/OOM/queue state, TLS, and provider balances during
the 60-minute staging soak and at least 30 minutes after production deploy.

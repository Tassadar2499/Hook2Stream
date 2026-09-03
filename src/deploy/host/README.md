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

After provisioning secrets and the reviewed host policies, install the
root-owned release control plane below before starting Docker or validating the
host. Do not run a checkout-local validator as root.

## Installed release control plane and GHCR identity

Install the forced-command control plane from the reviewed current checkout;
never run its rollback program from a release candidate directory:

```sh
sudo install -d -o root -g root -m 0755 /usr/local/libexec/hook2stream/lib
sudo install -o root -g root -m 0555 scripts/validate-host.sh \
  /usr/local/libexec/hook2stream/validate-host.sh
sudo install -o root -g root -m 0555 scripts/deploy-forced-command.sh \
  /usr/local/libexec/hook2stream/deploy-forced-command.sh
sudo install -o root -g root -m 0555 scripts/rollback-application.sh \
  /usr/local/libexec/hook2stream/rollback-application.sh
sudo install -o root -g root -m 0555 scripts/validate-candidate.sh \
  /usr/local/libexec/hook2stream/validate-candidate.sh
sudo install -o root -g root -m 0555 scripts/lib/forced-command-trust.sh \
  /usr/local/libexec/hook2stream/lib/forced-command-trust.sh
sudo install -o root -g root -m 0555 scripts/lib/host-validation-common.sh \
  /usr/local/libexec/hook2stream/lib/host-validation-common.sh
sudo install -o root -g root -m 0500 scripts/post-deploy-e2e.sh \
  /usr/local/libexec/hook2stream/post-deploy-e2e.sh
sudo install -o root -g root -m 0500 host/authenticated-e2e.sh \
  /usr/local/libexec/hook2stream/authenticated-e2e.sh
sudo install -o root -g root -m 0555 scripts/deploy-forced-launcher.sh \
  /usr/local/sbin/hook2stream-deploy-launcher
```

An existing successful release created before
`compose.billing-stripe.yaml` must not be modified to add that file and must
not receive the new unconditional overlay trust checks first. On staging,
deploy the first candidate that already contains the overlay through the
previous reviewed wrapper, confirm it became the active infrastructure release,
and only then install the complete new control plane with a commit-pinned,
SHA-256-pinned one-shot updater. A cold production host may install that updater
before its first candidate because it has no legacy active release. Use distinct
staging and production updaters, verify their signatures and offline self-tests,
and update all installed files as one transaction under the forced-command
lock. Never backfill or mutate an extracted historical release bundle.

Run acceptance from a root shell through the installed root-owned copy; never
source validator libraries from an operator-writable checkout. Start the
mount-guarded Docker daemon first because validation inspects its root and
socket:

```sh
systemctl start docker.service
/usr/local/libexec/hook2stream/validate-host.sh app staging
/usr/local/libexec/hook2stream/validate-host.sh app production
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

The expected-email, MP3, and staging baseline files can and must be provisioned
before the first release. The OAuth cookie jar is the deliberate cold-bootstrap
exception: it cannot be minted until the application origin is running. On a
new host, install it only during the `prepare-pending` operator handoff described
below. On an established host, refresh it before an ordinary deployment if it
will expire during the gate. Never create a dummy cookie file merely to satisfy
a path check.

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

Staging must set `BILLING_MODE=stripe` and use Stripe test credentials. The gate creates a real test Checkout
Session, signs one bounded `checkout.session.completed` payload with the
root-managed test webhook secret, submits the exact bytes twice, and proves the
second delivery is a duplicate. This path is cryptographically blocked when
the API key or Checkout Session is live. Production uses its dedicated invited
QA account for deterministic encrypted upload/range, OpenRouter pipeline and
preview verification. It requires `BILLING_MODE=disabled`, proves the billing
summary has `checkoutEnabled=false`, proves checkout and webhook return
`503 billing.disabled`, and confirms the API egress proxy rejects
`api.stripe.com`; it performs no final-render POST. The same
immutable digest's staging receipt supplies Stripe test, 18-render/ZIP and soak
evidence. Live payment is a separate future rollout and no Stripe material is
installed on production in this release.

## First release and normal deployment transactions

Use the workflow `deployment_phase=prepare-pending` exactly once on a cold host
with no `/srv/hook2stream/release-state/last-successful.env`. The forced command
accepts `prepare <candidate-id>`, brings up the candidate runtime, and writes
the root-only `release-state/pending-deploy.json`. The returned pending receipt
is evidence only that this exact runtime is ready for operator onboarding; it is
not a successful deployment receipt and cannot authorize soak or promotion.
`prepare` is rejected as soon as a successful release exists.

While that exact pending runtime remains active, the operator must:

1. complete Google OAuth with the dedicated pre-invited QA account at the
   environment's public origin;
2. verify or create the intended QA workspace without enabling public signup;
3. export the short-lived Mozilla/Netscape cookie jar without printing it, and
   install it atomically at the configured path as a non-symlink root-owned
   `0400` or `0600` file; and
4. dispatch the same source run and full candidate with
   `deployment_phase=finalize-pending`.

The resulting `finalize <candidate-id>` command does not accept new candidate
bytes. It revalidates the persisted bundle, environment file, running image
digests, production approval material where applicable, and the original
32-hex E2E operation identity before authenticated E2E. If cold E2E fails, fix
only the onboarding/input defect and retry `finalize-pending` for that exact
candidate. Selecting another candidate while one is pending is intentionally
rejected.

After the first release, always use the default
`deployment_phase=deploy-and-finalize`. The host `deploy <candidate-id>` keeps
candidate install, runtime transition, authenticated E2E, digest verification,
and successful-state publication inside one `flock`-serialized transaction;
there is no operator OAuth pause. The operation ID remains stable for exact
retries of that full artifact but differs for a new run/attempt even when its
commit SHA is identical. The pending marker also binds the environment,
previous successful SHA, release-images, bundle and derived environment hashes,
and all production staging-receipt authority hashes, so a drifted replay fails
closed. If SSH output is lost after success, an exact retry may return the
stored result only after rechecking live health, the active infrastructure
ledger, successful environment, and running digests.

For a failed normal deployment, the control plane restores the previous
application images when it can prove a protocol-v2 target. Database migrations
are never reversed, and candidate infrastructure images remain active; the
ledger is atomically rewritten to describe that candidate infrastructure plus
the restored application release. A compensation interruption, failed digest
check, or failed ledger publication writes
`/srv/hook2stream/release-state/recovery-required.json` and stops the owned
Caddy container when possible. The existence of that root-owned marker blocks
all automated deploy, finalize, soak, and rollback commands. Inspect the marker,
container digests, database, `last-successful.env`, and
`active-infrastructure-release.json`; reconcile them through the provider
console or verified operator path before manually clearing the marker. Do not
delete it simply to make automation continue.

Application rollback uses the installed root-owned orchestrator and the active
infrastructure bundle, never code from the target candidate. After switching
the app images it runs the bounded, non-mutating `rollback-verify` gate, which
checks the exact OAuth account, H2SE single-range reads, worker state,
preview/export reads, and denied egress without creating uploads, billing
events, renders, or migrations. Failure or interruption restores the original
application release; inability to prove that reversal enters the same
recovery-required/closed-ingress state.

## Every reboot

Docker is guarded and cannot start while `/srv/hook2stream` is absent. Use the
verified operator access path or the provider VNC recovery console, then run:

```sh
./scripts/bootstrap-encrypted-host.sh unlock app staging
# or, on the production host only:
./scripts/bootstrap-encrypted-host.sh unlock app production

systemctl start docker.service
/usr/local/libexec/hook2stream/validate-host.sh app staging   # choose the matching environment
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

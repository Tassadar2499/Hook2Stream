# Hook2Stream MVP operations runbook

This runbook is the operator contract for the invite-only staging and production
single-node environments. It does not claim high availability. The accepted risk
expires 90 days after the first paid user.

## Environment matrix

| Setting | Staging | Production |
|---|---|---|
| Domain | `staging.hook2stream.com` | `hook2stream.com` |
| Hetzner project/location | isolated project, NBG1 | isolated project, HEL1 |
| Server | CX43, Ubuntu 24.04 amd64 | CX43, Ubuntu 24.04 amd64 |
| LUKS volume | at least 64 GB | at least 128 GB |
| Backup retention | 7 days | 35 days |
| Integrations | Google test, Stripe test, dedicated OpenRouter key | Google production, Stripe live, dedicated OpenRouter key |

Use `src/deploy/environments/{staging,production}.env.example` as the profile
overlay. `S3_PUBLIC_SERVICE_URL` remains populated only because older application
configuration validates it; browser presigned URLs and S3 CORS are forbidden.

## Domain and DNS gate

Immediately before purchase, look up `hook2stream.com` in ICANN Lookup. If it is
registered, stop and ask for a replacement; do not choose one automatically.
Register through Cloudflare Registrar and enable account 2FA, auto-renew,
registrar lock, and DNSSEC. Create DNS-only records:

- `A @` to production IPv4;
- `A staging` to staging IPv4;
- `CNAME www` to `@`.

Do not create AAAA records until the provider firewall and UFW contain equivalent
IPv6 rules. Caddy owns TLS. Production redirects `www` with status 308; staging
emits `X-Robots-Tag: noindex, nofollow, noarchive`. Check Hetzner's current order
total before purchase; domain, provider, Stripe, OpenRouter, and traffic charges
are external operations and are not created by this repository.

## Host bootstrap and LUKS recovery

Attach the volume, create LUKS2 with an operator-held recovery key, open it as
`hook2stream-data`, create a filesystem, and mount it at `/srv/hook2stream`.
Create Docker data, PostgreSQL-backed Docker volumes, release state, root-only
configuration, secrets, logs, and scratch below that mount. Create the 4 GB swap
file below `/srv/hook2stream`; never use an unencrypted root-disk swapfile.

Docker must have `"data-root": "/srv/hook2stream/docker"`. Add systemd drop-ins
for `docker.service` and the Hook2Stream deployment unit with:

```ini
[Unit]
RequiresMountsFor=/srv/hook2stream
After=srv-hook2stream.mount
ConditionPathIsMountPoint=/srv/hook2stream
```

Automatic volume unlock is deliberately not configured. After reboot, unlock
from the console, mount `/srv/hook2stream`, activate its swap, start Docker, then
run `sudo src/deploy/scripts/validate-host.sh staging|production` before starting
services. Downtime until manual unlock is an accepted MVP limitation.

Create a named sudo operator using key-only SSH and do not add it to `docker` or
the secrets group. Disable root/password/keyboard-interactive SSH. Public SSH is
allowed only from the operator CIDR. Add a distinct UFW rule for port 22 on
`tailscale0`; expose only 80/TCP, 443/TCP, and 443/UDP publicly. Mirror this in
the Hetzner firewall. CI Tailscale ACLs must permit
`tag:hook2stream-ci-staging` only to the staging host's port 22 and
`tag:hook2stream-ci-production` only to production port 22.

## Secrets and encrypted storage

Each environment has a separate `/srv/hook2stream/secrets/current`, root-owned
`0750`, containing root:`2000` `0640` files listed in
`src/deploy/secrets/README.md`. Never copy staging OAuth, Stripe, OpenRouter, S3,
database, invite, media keyring, or age values to production. `invited_emails` is
newline-delimited, accepts `#` comments, contains no example or default account,
and fails closed when absent. Unknown Google accounts are not provisioned.

`media_keyring` is a separately escrowed environment-specific H2SE v1 keyring.
Only API/workers mount it. The active 256-bit KEK wraps new DEKs; retired KEKs
remain readable until inventory reaches zero. Rotate every 90 days. Store an
encrypted escrow copy outside the VPS and Object Storage. `AllowLegacyPlaintextReads`
is always false. After the first H2SE v1 object, never roll back to a release that
cannot read H2SE v1.

The API, every worker, and the backup sidecar have encrypted Docker named
volumes mounted at `/tmp`; `TMPDIR=/tmp` keeps H2SE scratch and encrypted backup
staging on LUKS without a fixed 512 MiB tmpfs ceiling. Eight upload-part
encryptions and four downloads are contour-wide ceilings enforced across API
and worker containers with PostgreSQL advisory-lock slots.

## Hetzner Object Storage

Use one FSN1 location with four private, environment-distinct buckets and three
credential classes per environment: runtime media, bootstrap media, and backup.
Hetzner endpoint configuration uses region `fsn1`, endpoint
`https://fsn1.your-objectstorage.com`, and `S3_FORCE_PATH_STYLE=false`. Disable
public access and media CORS. Enable versioning only on backup buckets. Apply 7-
and 35-day retention respectively and verify expired versions/delete markers are
actually purged. Object keys must be server-generated and contain no filenames.

The role-specific Squid proxies are the only application egress route. Their
allowlists permit S3 for API/workers/backups, Google and Stripe for API, and
OpenRouter for the control worker. The Compose backend/edge/role networks are
internal. Before go-live, run a deployment test from each role that succeeds to
its allowed providers and fails to an unrelated HTTPS origin; alert on proxy
denials. Changing a provider hostname requires an audited allowlist change.

## CI deploy user

Create `hook2stream-deploy` without Docker/secrets access. Its public key line
uses `restrict` and a forced command that invokes only a root-owned wrapper via
an exact passwordless sudoers entry. Install audited copies of
`deploy-forced-command.sh` and `validate-candidate.sh` under
`/usr/local/libexec/hook2stream`,
set the host environment variables in that wrapper's root-owned service file,
and pin ED25519 host keys in GitHub Environment secrets. Set
`HOOK2STREAM_E2E_HOOK` to the installed root-owned, non-symlink copy of
`post-deploy-e2e.sh` with mode `0500`. Also install an environment-specific
root-owned `0500` scenario at `HOOK2STREAM_AUTHENTICATED_E2E_HOOK`; start from
the fail-closed `src/deploy/host/authenticated-e2e.example` contract. The public
hook checks exact HTTP 200 for root/readiness/API readiness, the anonymous
session JSON shape, and staging noindex without printing response bodies. It
then invokes the authenticated scenario, which owns its OAuth/billing test
credentials and must verify H2SE upload/range, every worker, OpenRouter,
preview/18 renders/ZIP, and Stripe duplicate delivery. A successful receipt and
provider allowlist success plus unrelated-origin denial. A successful receipt
and H2SE rollback capability are impossible until that scenario emits the exact
capability line documented in the template; a health-only hook is not accepted.

Use the exact templates in `src/deploy/host`. Install the launcher at
`/usr/local/sbin/hook2stream-deploy-launcher`, the wrapper/validator/hook at
`/usr/local/libexec/hook2stream/`, and the per-host config at
`/etc/hook2stream/deploy.conf` as root mode `0600`. Sudo's default `env_reset`
drops `SSH_ORIGINAL_COMMAND`; the sudoers template preserves only that variable
for `hook2stream-deploy`. All other wrapper settings are loaded by the root-owned
launcher, never accepted from the SSH account environment. Validate sudoers with
`visudo -cf` before ending the bootstrap session.

The wrapper accepts only `deploy release-candidate-...` with a maximum 256 MiB
POSIX tar on stdin, or `rollback <40-hex-sha> H2SEv1`. It rejects checksum/schema/repo/
digest mismatch, duplicate or unknown image variables, path traversal, links,
special files, unapproved production receipts, and candidates not built from
`main`. Production receipt verification uses a root-owned SSH allowed-signers
file. Host `flock` remains the final concurrency lock. Rollback copies only the
target `API_IMAGE`, `WORKER_IMAGE`, `WEB_IMAGE`, and `RELEASE_VERSION` into the
current environment, preserving current bootstrapper and infrastructure
digests. It pulls/recreates API, all workers and web with `--no-deps`; it never
runs bootstrap/migrations or pulls/recreates Caddy, PostgreSQL, PgBouncer,
backup, or egress services. Exact running infrastructure digests are checked
before and after the application change. Incompatible schemas require a forward
fix or a separately approved write-stop/restore procedure. Every verified success records
an H2SE capability marker. `MIN_ROLLBACK_RELEASE_SHA` is only the operator's
identity marker for the first H2SE release; SHA text is never treated as an
ordering relation, and every rollback target must independently record H2SEv1.
Set it to the first candidate's exact 40-hex commit before the first deployment;
the forced wrapper rejects both deploy and rollback before mutation when it is
missing or malformed, and every rollback receipt repeats the exact host value.

## Backups and recovery

The backup sidecar runs hourly, pipes a custom-format `pg_dump` directly to
`age` using only the off-host public recipient, uploads dump and checksum, then
publishes the authenticated manifest last. The private age identity is never on
either server or Object Storage. Better Stack heartbeat failure never makes a
completed backup fail; alert if the newest successful marker exceeds two hours.

Before enabling live Stripe, perform this drill in an empty temporary network:

1. Download one manifest, checksum, and `.dump.age`; verify SHA-256.
2. Decrypt using the operator-held age identity and restore into empty PostgreSQL.
3. Restore the matching media keyring escrow and fetch a real H2SE object.
4. Prove decrypt/range playback and record measured RPO/RTO and object IDs.
5. Destroy the temporary plaintext and credentials.

Repeat monthly and before risky migrations. A restore may not overwrite the live
database without explicit approval and stopped writes.

## Go-live and monitoring gates

Staging must pass OAuth, licensed MP3 upload, every worker, OpenRouter analysis
and artwork, preview seeking, 18 renders, ZIP, Stripe test checkout plus duplicate
webhook, reboot/LUKS recovery, concurrent deploy lock, idempotent same-SHA deploy,
compatible old-new-old application-only rollback, including proof that no
migration/bootstrap/infra service was invoked, egress-denial tests, and 30 minutes without OOM
or disk below 20%.

Production requires a second GitHub Environment reviewer, protected Environment,
the successful signed staging receipt, recovery drill, TLS/security headers,
308 `www`, OAuth, controlled live payment/refund, encrypted upload/download/range,
actual digest verification, and 30-minute observation. Without the second
reviewer/protection, live payments remain blocked.

Configure Better Stack web/API monitors for both environments and backup
heartbeats. Alert on backup age over two hours, disk/OOM, queue age, gateway 5xx,
GCM failures, TLS/domain expiry, and sustained CPU/network saturation. At 80%
volume use, resize. Cost above the accepted monthly threshold or persistent
saturation triggers architecture review.

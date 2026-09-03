# Servers.Guru operator contour

This directory records the exact two already purchased Hook2Stream VPS. It is
a manual, operator-only provider boundary: GitHub Actions must not receive a
Servers.Guru session, password, payment credential, API credential, or recovery
console credential. There is no Terraform, create, reinstall, resize, renew, or
delete automation in this contour.

`configure-ghcr-pull-auth.sh` is the only operator helper here. It mutates no
Servers.Guru resource: run it locally as root on an already accepted app host,
after the encrypted mount and installed trust helper are active. Pass
`staging|production`, the environment-specific GitHub username, and an
independently generated 32-hex identity suffix. GitHub login proves usability,
not PAT scope; the script records and pins the operator's `read:packages`-only,
environment-exclusive attestation without placing the PAT in argv, logs,
GitHub, or artifacts.

## Purchased inventory

| Environment | Region | Plan | Resources | Monthly catalogue price |
|---|---|---|---|---:|
| staging | `MTL1` | `MTL1-3` | 4 vCPU / 8 GiB / 80 GB | EUR 14.99 |
| production | `NL1` | `NL1-4` | 6 vCPU / 8 GiB / 160 GB | EUR 17.49 |

The exact machine-readable contract is [`policy.json`](policy.json). A plan,
region, CPU count, memory class, or disk class outside that contract is drift;
do not silently substitute another SKU or location. Keep invoice, service ID,
assigned IPv4, console screenshots, support correspondence, and probe evidence
in encrypted operator storage outside Git and GitHub.

## Manual acceptance

Install Ubuntu 24.04 amd64 and bootstrap the host without application secrets.
Before creating the permanent LUKS backing file, run the matching provider probe
from the repository checkout. The probe is intentionally fail-closed and must
be run through `sudo` after the exact UFW, OpenSSH, Tailscale, and Docker
policies are installed:

```sh
sudo env \
  SERVERS_GURU_PLAN_CODE=MTL1-3 \
  SERVERS_GURU_EXPECTED_REGION=MTL1 \
  SERVERS_GURU_EXPECTED_IPV4=REPLACE_WITH_STAGING_IPV4 \
  SERVERS_GURU_VNC_CONSOLE_VERIFIED=true \
  SERVERS_GURU_RESCUE_BOOT_VERIFIED=true \
  SERVERS_GURU_LUKS_BOOT_CONSOLE_VERIFIED=true \
  SERVERS_GURU_STATIC_IPV4_REBOOT_VERIFIED=true \
  SERVERS_GURU_FFMPEG_APPROVAL_FILE=/root/evidence/serversguru-ffmpeg-approval.txt \
  SERVERS_GURU_FFMPEG_APPROVAL_SHA256=REPLACE_WITH_SHA256 \
  SERVERS_GURU_FFMPEG_SOAK_EVIDENCE_FILE=/root/evidence/serversguru-staging-soak.json \
  SERVERS_GURU_FFMPEG_SOAK_EVIDENCE_SHA256=REPLACE_WITH_SHA256 \
  src/deploy/scripts/validate-serversguru-probe.sh staging
```

Use `NL1-4`, `NL1`, the production IPv4, and independently captured
production evidence for `production`. Evidence files must be regular,
non-symlink, `root:root 0600` files and their action-time approved SHA-256
values must match. The FFmpeg approval evidence must explicitly cover one job
using at most three vCPU for a 60-minute interval; the measured soak must use
the same limit.

The operator and deploy accounts are password-locked and use their exact
ED25519 keys. Root retains a unique active password as a temporary MVP recovery
exception, but the exact UFW policy permits TCP 22 only inbound on
`tailscale0`; public root SSH is forbidden. Keep the password only in encrypted
operator escrow, never in provider evidence or CI, and rotate it immediately
after suspected disclosure. Host acceptance resolves and checks root's
effective sshd policy separately.

The probe checks the exact visible CPU/RAM/disk class, KVM/QEMU, one expected
public IPv4, absence of global IPv6, at least 20 percent free root space,
`/dev/net/tun`, ordinary OpenSSH over Tailscale, the application UFW policy,
Docker Compose v2, a temporary loop/dm-crypt/LUKS2 round trip, direct HTTPS to
Storj/Google/OpenRouter plus staging-only Stripe, and a three-thread FFmpeg
workload. Production does not probe or allow Stripe while
`BILLING_MODE=disabled`. Its
temporary LUKS image is removed on exit.

Provider console actions remain manual and separately reviewed. Never
reinstall, resize, cancel, or delete either paid VPS as part of application
deployment or rollback.

After the provider probe passes, use the reviewed manual encrypted-volume
interface and reboot procedure in
[`src/deploy/host/README.md`](../../../src/deploy/host/README.md). It creates
only the exact 48/64 GiB environment profile, never stores an unlock key, and
refuses to format any pre-existing backing file or existing LUKS mapping.

# Provider operations

[`serversguru`](serversguru) is the canonical operator contract for the two
permanent Servers.Guru application VPS instances:

| Environment | Product | Location | Capacity | Monthly storefront price |
|---|---|---|---|---:|
| staging | `MTL1-3` | Montreal, Canada | 4 shared vCPU / 8 GiB / 80 GB NVMe | EUR 14.99 |
| production | `NL1-4` | Amsterdam, Netherlands | 6 shared vCPU / 8 GiB / 160 GB NVMe | EUR 17.49 |

The expected pair is EUR 32.48 per monthly billing cycle. Both hosts are
app-only Ubuntu 24.04 KVM VPS instances with a dedicated IPv4. Media and
age-encrypted PostgreSQL backups stay in separate Storj projects. There is no
remote MinIO host, provider storage plane, per-release staging lifecycle,
or external observability service.

Provisioning and cancellation are manual operator actions in the Servers.Guru
control panel. The public API may be used only for optional read-only
inventory, status, product/image, and wallet checks. Provider API keys never
enter GitHub, CI, candidates, or a VPS. Hook2Stream does not automate
`/servers/vps/create`, cancellation, rebuild, backup restore, snapshot restore,
power, IP mutation, or any other provider write operation.

The provider policy and exact manual checklist live in
[`serversguru/README.md`](serversguru/README.md) and
[`serversguru/policy.json`](serversguru/policy.json). Live acceptance uses
`src/deploy/scripts/validate-serversguru-probe.sh staging|production`. Run it
only after confirming the exact paid SKU, location, Ubuntu image, primary IPv4,
KVM/VNC access, and the guest host prerequisites.

Servers.Guru does not publicly document cloud-init/user-data, SSH-key injection,
a provider firewall, API-key scopes, or numeric regular-VPS CPU duty-cycle
limits. Initial bootstrap therefore uses the issued root credential to install
operator keys and enroll Tailscale. As a temporary MVP exception, root password
SSH remains available through ordinary OpenSSH on `tailscale0` only; the
operator and deploy passwords remain locked. UFW is the authoritative ingress
boundary and public TCP 22 remains denied. Production remains blocked
until support confirms that one FFmpeg process may use up to three vCPU for a
60-minute soak and live probes prove `/dev/net/tun`, loop/dm-crypt/LUKS2, VNC
recovery, Docker Compose v2, and required outbound integrations.

Treat provider backup, snapshots, IPv6, and DDoS protection as unavailable
unless the exact two server records prove otherwise. Storj plus age encryption
remains the canonical off-host recovery path. Keep at least two monthly budgets
in the provider wallet, review the renewal invoice issued seven days before its
due date, and block rollout if the pair exceeds EUR 40 per month or either SKU
changes by more than ten percent.

Runtime artifacts live in [`src/deploy`](../../src/deploy). The local/CI
`compose.minio.yaml` overlay never runs on either Servers.Guru VPS.

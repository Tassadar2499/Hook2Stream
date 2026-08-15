# Provider operations

The authoritative four-VPS IT-Garage operating contract is
[`docs/operations/hook2stream-mvp-runbook.md`](../../docs/operations/hook2stream-mvp-runbook.md).
It defines two application hosts, two separate Tailscale-only MinIO hosts,
file-backed LUKS2 layouts, manual provisioning, release promotion, recovery, and
the 90-day risk acceptance.

Use the adjacent
[`it-garage-support-checklist.md`](../../docs/operations/it-garage-support-checklist.md)
before ordering. This repository does not provision the provider account, VPS,
Cloudflare zone, Tailscale ACLs, GitHub Environments, or monitoring accounts.

Runtime artifacts live in [`src/deploy`](../../src/deploy): the base Compose
bundle is for app hosts, `src/deploy/storage` is for remote MinIO hosts, and
`compose.minio.yaml` remains local/CI-only.

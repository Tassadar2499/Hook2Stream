#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
repository_root=$(CDPATH= cd -- "$deployment_dir/../.." && pwd)
provider=$repository_root/deploy/providers/serversguru
policy=$provider/policy.json
fail() { printf '%s\n' "Servers.Guru provider contract test: $*" >&2; exit 1; }

[ -d "$provider" ] || fail 'Servers.Guru provider contour is missing'
[ "$(find "$provider" -mindepth 1 -maxdepth 1 -type f -printf '%f\n' | sort)" = \
    "$(printf '%s\n' README.md configure-ghcr-pull-auth.sh policy.json tailscale-policy.hujson)" ] \
    || fail 'provider contour may contain only docs, policy, tailnet policy, and the host-local GHCR installer'
[ -z "$(find "$provider" -mindepth 1 -type d -print -quit)" ] \
    || fail 'manual provider contour must not contain automation subdirectories'
[ ! -e "$repository_root/deploy/providers/digitalocean" ] \
    || fail 'retired DigitalOcean provider contour remains'

node - "$policy" <<'JS' || fail 'policy is not the exact purchased inventory contract'
const assert = require("node:assert/strict");
const fs = require("node:fs");
const policy = JSON.parse(fs.readFileSync(process.argv[2], "utf8"));
assert.deepStrictEqual(policy, {
  schema: "hook2stream-serversguru-policy-v1",
  provider: "servers.guru",
  provisioningMode: "manual-existing-services",
  billing: {
    currency: "EUR",
    cycle: "monthly",
    stagingMonthly: "14.99",
    productionMonthly: "17.49",
    totalMonthly: "32.48",
  },
  image: {
    distribution: "Ubuntu",
    version: "24.04",
    architecture: "amd64",
  },
  environments: {
    staging: {
      hostname: "h2s-app-staging",
      plan: "MTL1-3",
      region: "MTL1",
      vcpus: 4,
      memoryMiB: 8192,
      diskGb: 80,
      luksGiB: 48,
    },
    production: {
      hostname: "h2s-app-production",
      plan: "NL1-4",
      region: "NL1",
      vcpus: 6,
      memoryMiB: 8192,
      diskGb: 160,
      luksGiB: 64,
    },
  },
  network: {
    publicIpv4PerHost: 1,
    publicIpv6Required: false,
    publicTcpPorts: [80, 443],
    publicUdpPorts: [443],
    sshInterface: "tailscale0",
  },
  acceptance: {
    virtualization: ["kvm", "qemu"],
    tunRequired: true,
    vncConsoleEvidenceRequired: true,
    rescueBootEvidenceRequired: true,
    manualLuksUnlockEvidenceRequired: true,
    staticIpv4RebootEvidenceRequired: true,
    ffmpegApprovalEvidenceRequired: true,
    ffmpegSoakSeconds: 3600,
    ffmpegThreads: 3,
  },
});
JS

grep -Fq 'There is no Terraform, create, reinstall, resize, renew, or' "$provider/README.md" \
    || fail 'manual no-mutation boundary is not documented'
grep -Fq 'GitHub Actions must not receive' "$provider/README.md" \
    || fail 'provider credential isolation is not documented'
grep -Fq 'configure-ghcr-pull-auth.sh' "$provider/README.md" \
    || fail 'host-local GHCR credential installation is not documented'
if grep -Eqi '(password|secret|token|api[_-]?key)[[:space:]]*[:=][[:space:]]*[^[:space:]]+' "$policy"; then
    fail 'policy contains credential-shaped material'
fi

printf '%s\n' 'Servers.Guru provider contract tests passed'

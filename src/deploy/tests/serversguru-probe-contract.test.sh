#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
probe=$deployment_dir/scripts/validate-serversguru-probe.sh
fail() { printf '%s\n' "Servers.Guru probe contract test: $*" >&2; exit 1; }

[ -x "$probe" ] || fail 'validate-serversguru-probe.sh must be executable'
sh -n "$probe" || fail 'probe has invalid shell syntax'

for contract in \
    MTL1-3 MTL1 NL1-4 NL1 \
    'expected_vcpus=4' 'expected_vcpus=6' \
    80000000000 90000000000 160000000000 180000000000 \
    7500000 8600000 \
    SERVERS_GURU_VNC_CONSOLE_VERIFIED \
    SERVERS_GURU_RESCUE_BOOT_VERIFIED \
    SERVERS_GURU_LUKS_BOOT_CONSOLE_VERIFIED \
    SERVERS_GURU_STATIC_IPV4_REBOOT_VERIFIED \
    SERVERS_GURU_FFMPEG_APPROVAL_SHA256 \
    SERVERS_GURU_FFMPEG_SOAK_EVIDENCE_SHA256 \
    '/dev/net/tun' 'Docker Compose v2' 'tailscale get --json ssh' \
    'cryptsetup luksFormat --type luks2' \
    gateway.storjshare.io accounts.google.com api.stripe.com openrouter.ai \
    'libx264 -threads 3'; do
    grep -Fq "$contract" "$probe" || fail "probe omits contract: $contract"
done

grep -Fq 'hook2stream_validate_ufw_status app "$ufw_status"' "$probe" \
    || fail 'probe does not reuse the exact app UFW policy'
grep -Fq 'hook2stream_validate_sshd_config_tree' "$probe" \
    || fail 'probe does not reject alternate SSH trust paths'
grep -Fq 'hook2stream_validate_sshd_effective "$sshd_effective"' "$probe" \
    || fail 'probe does not validate both SSH users'
grep -Fq "stat -c '%u:%g:%a'" "$probe" \
    || fail 'probe evidence is not protected by exact owner/group/mode checks'
grep -Fq 'hook2stream_host_no_extended_acl "$evidence_path"' "$probe" \
    || fail 'probe evidence does not reject extended ACL access'
grep -Fq 'no unexpected public IPv4' "$probe" \
    || fail 'probe does not reject an unexpected public IPv4 address'
grep -Fq '/sys/class/net/$provider_interface/device' "$probe" \
    || fail 'probe does not exclude Docker/bridge addresses through physical NIC evidence'

if grep -Eqi '(terraform|digitalocean|cloudzy|timeweb|cherry|it-garage|provider API|API token)' "$probe"; then
    fail 'manual Servers.Guru probe contains retired provider or provisioning automation'
fi

printf '%s\n' 'Servers.Guru probe contract tests passed'

#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_dir/lib/host-validation-common.sh"

fail() { printf '%s\n' "Servers.Guru probe: $*" >&2; exit 1; }
require_command() { command -v "$1" >/dev/null 2>&1 || fail "$1 is required"; }

validate_expected_ipv4_topology() {
    expected_public=$1
    observed_topology=$2
    default_interface=$3
    printf '%s\n' "$observed_topology" | awk \
        -v expected="$expected_public" -v default_interface="$default_interface" '
      function valid_ipv4(value, octets, part) {
        if (value !~ /^[0-9]+(\.[0-9]+){3}$/) return 0
        split(value, octets, ".")
        for (part = 1; part <= 4; part++) {
          if (octets[part] < 0 || octets[part] > 255 ||
              (octets[part] != "0" && octets[part] ~ /^0/)) return 0
        }
        return 1
      }
      function private_ipv4(value, octets) {
        split(value, octets, ".")
        return octets[1] == 10 ||
          (octets[1] == 172 && octets[2] >= 16 && octets[2] <= 31) ||
          (octets[1] == 192 && octets[2] == 168)
      }
      BEGIN {
        if (!valid_ipv4(expected) || private_ipv4(expected) ||
            default_interface !~ /^[A-Za-z0-9_.:-]+$/) invalid = 1
      }
      /^$/ { next }
      {
        if ($1 ~ /^docker[0-9]*$/ || $1 ~ /^br-/ || $1 ~ /^veth/) next
        if (NF != 2 || $1 == "tailscale0" || $1 !~ /^[A-Za-z0-9_.:-]+$/ ||
            !valid_ipv4($2) || seen_address[$2]++) {
          invalid = 1
          next
        }
        if ($2 == expected) {
          public_count++
          public_interface = $1
        } else if (!private_ipv4($2)) {
          invalid = 1
        }
      }
      END {
        if (public_count != 1 || public_interface != default_interface) invalid = 1
        exit invalid ? 1 : 0
      }
    '
}

require_approved_evidence() {
    evidence_path=$1
    approved_sha=$2
    evidence_label=$3
    case "$evidence_path" in /*) ;; *) fail "$evidence_label path must be absolute" ;; esac
    [ -f "$evidence_path" ] && [ ! -L "$evidence_path" ] \
        || fail "$evidence_label must be a regular non-symlink file"
    [ "$(stat -c '%u:%g:%a' "$evidence_path")" = 0:0:600 ] \
        || fail "$evidence_label must be root:root mode 0600"
    hook2stream_host_no_extended_acl "$evidence_path" \
        || fail "$evidence_label must not grant access through POSIX ACLs"
    printf '%s\n' "$approved_sha" | grep -Eq '^[0-9a-f]{64}$' \
        || fail "$evidence_label approved digest must be lowercase SHA-256"
    evidence_sha=$(sha256sum "$evidence_path" | awk '{print $1}')
    [ "$evidence_sha" = "$approved_sha" ] \
        || fail "$evidence_label does not match its action-time approved SHA-256"
}

environment=${1:-}
[ "$#" -eq 1 ] || fail 'usage: validate-serversguru-probe.sh staging|production'
case "$environment" in
    staging)
        expected_plan=MTL1-3
        expected_region=MTL1
        expected_vcpus=4
        minimum_disk_bytes=80000000000
        maximum_disk_bytes=90000000000
        ;;
    production)
        expected_plan=NL1-4
        expected_region=NL1
        expected_vcpus=6
        minimum_disk_bytes=160000000000
        maximum_disk_bytes=180000000000
        ;;
    *) fail 'usage: validate-serversguru-probe.sh staging|production' ;;
esac
[ "$(id -u)" -eq 0 ] || fail 'run through sudo on the target Servers.Guru VPS'

for command_name in awk cryptsetup curl df docker fallocate ffmpeg getfacl grep ip losetup lsblk mktemp nproc sed sha256sum sort sshd stat systemd-detect-virt tailscale tr ufw uname wc; do
    require_command "$command_name"
done

[ -r /etc/os-release ] || fail '/etc/os-release is unreadable'
. /etc/os-release
[ "${ID:-}" = ubuntu ] && [ "${VERSION_ID:-}" = 24.04 ] || fail 'Ubuntu 24.04 is required'
case "$(uname -m)" in x86_64|amd64) ;; *) fail 'amd64 is required' ;; esac
case "$(systemd-detect-virt)" in kvm|qemu) ;; *) fail 'Servers.Guru VPS must expose KVM/QEMU virtualization' ;; esac

[ "${SERVERS_GURU_PLAN_CODE:-}" = "$expected_plan" ] || fail "provider plan must be $expected_plan"
[ "${SERVERS_GURU_EXPECTED_REGION:-}" = "$expected_region" ] || fail "provider region must be $expected_region"
[ "${SERVERS_GURU_VNC_CONSOLE_VERIFIED:-}" = true ] || fail 'operator must verify out-of-band VNC console access'
[ "${SERVERS_GURU_RESCUE_BOOT_VERIFIED:-}" = true ] || fail 'operator must verify rescue boot access'
[ "${SERVERS_GURU_LUKS_BOOT_CONSOLE_VERIFIED:-}" = true ] || fail 'manual LUKS unlock through VNC must be proven'
[ "${SERVERS_GURU_STATIC_IPV4_REBOOT_VERIFIED:-}" = true ] || fail 'assigned IPv4 persistence across reboot must be proven'

[ "$(nproc)" -eq "$expected_vcpus" ] || fail "reviewed $expected_plan requires exactly $expected_vcpus visible vCPU"
memory_kib=$(awk '/^MemTotal:/ {print $2}' /proc/meminfo)
[ "${memory_kib:-0}" -ge 7500000 ] && [ "$memory_kib" -le 8600000 ] || fail 'reviewed plan requires an 8-GiB RAM class'
largest_disk_bytes=$(lsblk -bndo SIZE,TYPE | awk '$2 == "disk" && $1 > largest {largest=$1} END {print largest+0}')
[ "$largest_disk_bytes" -ge "$minimum_disk_bytes" ] && [ "$largest_disk_bytes" -le "$maximum_disk_bytes" ] \
    || fail "reviewed $expected_plan exposes an unexpected disk class"
[ "$(df -Pk / | awk 'NR == 2 {print int($4 * 100 / $2)}')" -ge 20 ] \
    || fail 'root filesystem must retain at least 20 percent free'

[ -c /dev/net/tun ] || fail '/dev/net/tun is unavailable'
tailscale status --json | grep -q '"BackendState"[[:space:]]*:[[:space:]]*"Running"' || fail 'Tailscale is not running'
tailscale_ssh_preference=$(tailscale get --json ssh 2>/dev/null) || fail 'cannot read Tailscale SSH preference'
hook2stream_validate_tailscale_ssh_preference "$tailscale_ssh_preference" || fail 'ordinary OpenSSH, not Tailscale SSH, is required'
docker compose version >/dev/null 2>&1 || fail 'Docker Compose v2 is required'

default_ipv4_interfaces=$(ip -4 route show default | awk '
  $1 == "default" {
    for (field = 1; field < NF; field++) if ($field == "dev") print $(field + 1)
  }
')
[ "$(printf '%s\n' "$default_ipv4_interfaces" | sed '/^$/d' | wc -l | tr -d ' ')" -eq 1 ] \
    || fail 'exactly one IPv4 default-route interface is required'
default_ipv4_interface=$(printf '%s\n' "$default_ipv4_interfaces" | sed '/^$/d')
provider_ipv4_topology=
for provider_interface in $(ip -4 -o addr show scope global | awk '$2 != "tailscale0" { print $2 }' | sort -u); do
    case "$provider_interface" in *[!A-Za-z0-9_.:-]*|'') fail 'provider interface name is unsafe' ;; esac
    [ -e "/sys/class/net/$provider_interface/device" ] || continue
    provider_interface_addresses=$(ip -4 -o addr show dev "$provider_interface" scope global \
        | awk '{split($4,a,"/"); print $2, a[1]}')
    [ -n "$provider_interface_addresses" ] || continue
    provider_ipv4_topology="${provider_ipv4_topology}${provider_ipv4_topology:+
}${provider_interface_addresses}"
done
validate_expected_ipv4_topology \
    "${SERVERS_GURU_EXPECTED_IPV4:-}" "$provider_ipv4_topology" "$default_ipv4_interface" \
    || fail 'the host must expose exactly the expected public IPv4 and no unexpected public IPv4'
[ -z "$(ip -6 -o addr show scope global | awk '$2 != "tailscale0" {print $4}')" ] \
    || fail 'IPv6 must remain disabled until an explicit firewall contract is added'

ufw_status=$(LC_ALL=C ufw status verbose) || fail 'cannot inspect UFW'
hook2stream_validate_ufw_status app "$ufw_status" || fail 'UFW must expose only TCP 80/443, UDP 443 and tailscale0 SSH'
hook2stream_validate_sshd_config_tree /etc/ssh/sshd_config /etc/ssh/sshd_config.d 0:0 \
    || fail 'SSH config has an alternate trust path'
for ssh_policy_user in hook2stream-operator hook2stream-deploy; do
    sshd_effective=$(sshd -T -C "user=$ssh_policy_user,host=h2s-app-$environment,addr=100.64.0.1,laddr=${SERVERS_GURU_EXPECTED_IPV4:-},lport=22" 2>/dev/null) \
        || fail "sshd config is invalid for $ssh_policy_user"
    hook2stream_validate_sshd_effective "$sshd_effective" || fail "SSH policy is not exact for $ssh_policy_user"
done

require_approved_evidence \
    "${SERVERS_GURU_FFMPEG_APPROVAL_FILE:-}" \
    "${SERVERS_GURU_FFMPEG_APPROVAL_SHA256:-}" \
    'FFmpeg provider approval evidence'
require_approved_evidence \
    "${SERVERS_GURU_FFMPEG_SOAK_EVIDENCE_FILE:-}" \
    "${SERVERS_GURU_FFMPEG_SOAK_EVIDENCE_SHA256:-}" \
    '60-minute FFmpeg soak evidence'

probe_dir=$(mktemp -d /var/tmp/hook2stream-serversguru-probe.XXXXXX)
probe_loop=
probe_mapper=hook2stream-serversguru-probe-$$
cleanup_probe() {
    cryptsetup close "$probe_mapper" >/dev/null 2>&1 || true
    if [ -n "$probe_loop" ]; then losetup -d "$probe_loop" >/dev/null 2>&1 || true; fi
    rm -rf -- "$probe_dir"
}
trap cleanup_probe EXIT
trap 'exit 130' HUP INT TERM
chmod 700 "$probe_dir"
fallocate -l 32M "$probe_dir/luks.img"
chmod 600 "$probe_dir/luks.img"
printf '%s\n' "hook2stream-serversguru-probe-$PPID-$$" > "$probe_dir/key"
chmod 600 "$probe_dir/key"
probe_loop=$(losetup --find --show "$probe_dir/luks.img")
cryptsetup luksFormat --type luks2 --batch-mode --key-file "$probe_dir/key" "$probe_loop"
cryptsetup open --key-file "$probe_dir/key" "$probe_loop" "$probe_mapper"
cryptsetup status "$probe_mapper" | grep -Eq '^[[:space:]]*type:[[:space:]]+LUKS2$' \
    || fail 'loop/dm-crypt/LUKS2 round trip failed'
cryptsetup close "$probe_mapper"
losetup -d "$probe_loop"
probe_loop=

for https_origin in https://gateway.storjshare.io https://accounts.google.com https://api.stripe.com https://openrouter.ai; do
    curl -q --proxy '' --noproxy '*' --silent --show-error --output /dev/null \
        --connect-timeout 10 --max-time 20 --max-redirs 0 --proto '=https' --tlsv1.2 \
        "$https_origin" || fail "direct HTTPS probe failed: $https_origin"
done
ffmpeg -hide_banner -loglevel error -f lavfi -i testsrc2=size=1920x1080:rate=30 \
    -t 15 -c:v libx264 -threads 3 -preset veryfast -f null - \
    || fail 'three-thread FFmpeg acceptance workload failed'

printf '%s\n' \
    "Servers.Guru probe: $environment $expected_plan ${expected_vcpus}/8 in $expected_region passed" \
    'VNC/rescue/LUKS boot, static IPv4, no IPv6, firewall, TUN, Tailscale, Docker, FFmpeg and egress passed'

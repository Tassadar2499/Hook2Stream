#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_dir/lib/staging-host-common.sh"

fail() { printf '%s\n' "host validation: $*" >&2; exit 1; }
require_command() { command -v "$1" >/dev/null 2>&1 || fail "$1 is required"; }

environment=${1:-}
case "$environment" in
    staging) minimum_volume_gib=64 ;;
    production) minimum_volume_gib=128 ;;
    *) fail "usage: validate-host.sh staging|production" ;;
esac

[ "$(id -u)" -eq 0 ] || fail "run through sudo"
[ -r /etc/os-release ] || fail "/etc/os-release is not readable"
. /etc/os-release
[ "${ID:-}" = ubuntu ] && [ "${VERSION_ID:-}" = 24.04 ] \
    || fail "Ubuntu 24.04 is required"
case "$(uname -m)" in x86_64|amd64) ;; *) fail "amd64 is required" ;; esac

for tool in awk cryptsetup df docker findmnt lsblk nproc ss stat swapon tailscale ufw; do
    require_command "$tool"
done
docker compose version >/dev/null 2>&1 || fail "Docker Compose v2 is required"

host_root=${HOOK2STREAM_HOST_ROOT:-/srv/hook2stream}
secrets_dir=${SECRETS_DIR:-${host_root}/secrets/current}
case "$host_root:$secrets_dir" in /*:/*) ;; *) fail "host and secrets paths must be absolute" ;; esac
[ -d "$host_root" ] || fail "$host_root does not exist (unlock and mount LUKS first)"

mount_source=$(findmnt -n -o SOURCE --target "$host_root")
case "$mount_source" in /dev/mapper/*) ;; *) fail "$host_root must be mounted from a dm-crypt mapper" ;; esac
mapper_name=${mount_source#/dev/mapper/}
cryptsetup status "$mapper_name" >/dev/null 2>&1 || fail "$mount_source is not an active LUKS mapping"

volume_kib=$(df -Pk "$host_root" | awk 'NR == 2 {print $2}')
available_kib=$(df -Pk "$host_root" | awk 'NR == 2 {print $4}')
minimum_kib=$((minimum_volume_gib * 1024 * 1024 * 95 / 100))
[ "$volume_kib" -ge "$minimum_kib" ] || fail "$environment requires a ${minimum_volume_gib} GB-class LUKS volume"
[ $((available_kib * 100 / volume_kib)) -ge 20 ] || fail "less than 20 percent disk space is free"

docker_root=$(docker info --format '{{.DockerRootDir}}')
case "$docker_root" in "$host_root"/*) ;; *) fail "Docker data-root must be below $host_root" ;; esac
for scratch in api_scratch worker_media_scratch worker_analysis_scratch worker_control_scratch worker_render_scratch worker_export_scratch backup_scratch; do
    case "$(docker volume inspect --format '{{.Mountpoint}}' "hook2stream_${scratch}" 2>/dev/null || true)" in
        ""|"$docker_root"/*) ;;
        *) fail "$scratch is outside the encrypted Docker data-root" ;;
    esac
done

swap_total_kib=$(awk '/^SwapTotal:/ {print $2}' /proc/meminfo)
[ "$swap_total_kib" -ge 4000000 ] || fail "at least 4 GB swap is required"
swapon --noheadings --show=NAME | while IFS= read -r swap_path; do
    [ -n "$swap_path" ] || continue
    case "$swap_path" in "$host_root"/*|/dev/mapper/*) ;; *) fail "swap must be encrypted or stored on $host_root" ;; esac
done

ufw_status=$(LC_ALL=C ufw status verbose) || fail "cannot read UFW status"
staging_host_validate_ufw_status "$ufw_status" || fail "UFW public policy is not default-deny with restricted SSH and only 80/443 exposed"
printf '%s\n' "$ufw_status" | grep -Eq '22/tcp.*tailscale0.*(ALLOW|LIMIT)[[:space:]]+IN' \
    || fail "CI SSH must be allowed only through tailscale0"
tailscale status --json | grep -q '"BackendState"[[:space:]]*:[[:space:]]*"Running"' \
    || fail "Tailscale is not running"

sshd_effective=$(sshd -T 2>/dev/null) || fail "sshd configuration is invalid"
printf '%s\n' "$sshd_effective" | grep -qx 'passwordauthentication no' || fail "SSH passwords must be disabled"
printf '%s\n' "$sshd_effective" | grep -qx 'kbdinteractiveauthentication no' || fail "SSH keyboard-interactive auth must be disabled"
printf '%s\n' "$sshd_effective" | grep -qx 'permitrootlogin no' || fail "root SSH login must be disabled"

[ -d "$secrets_dir" ] && [ ! -L "$secrets_dir" ] || fail "secret directory is missing or a symlink"
secret_gid=${SECRETS_GID:-2000}
[ "$(stat -c '%u:%g:%a' "$secrets_dir")" = "0:${secret_gid}:750" ] || fail "secret directory must be root:${secret_gid} mode 0750"
for secret in postgres_password media_keyring invited_emails backup_age_recipient; do
    secret_path=$secrets_dir/$secret
    [ -s "$secret_path" ] && [ ! -L "$secret_path" ] || fail "$secret is missing, empty, or a symlink"
    [ "$(stat -c '%u:%g:%a' "$secret_path")" = "0:${secret_gid}:640" ] || fail "$secret must be root:${secret_gid} mode 0640"
done

for private_port in 3000 3128 5432 6432 8080 9000 9001; do
    if ss -ltn | awk -v port=":$private_port" 'NR > 1 && $4 ~ port "$" && ($4 ~ /^0\.0\.0\.0:/ || $4 ~ /^\[::\]:/) {found=1} END {exit found ? 0 : 1}'; then
        fail "private port $private_port listens publicly"
    fi
done

printf '%s\n' "host validation: $environment LUKS, encrypted swap/scratch, Docker, secrets, firewall, SSH, and Tailscale passed"

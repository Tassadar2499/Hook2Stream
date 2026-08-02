#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_dir/lib/staging-host-common.sh"

fail() {
    printf '%s\n' "staging host validation: $*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "$1 is required"
}

[ "$(id -u)" -eq 0 ] || fail "run this validation through sudo"
[ -r /etc/os-release ] || fail "/etc/os-release is not readable"
. /etc/os-release
[ "${ID:-}" = ubuntu ] && [ "${VERSION_ID:-}" = 24.04 ] \
    || fail "Ubuntu 24.04 is required"

case "$(uname -m)" in
    x86_64|amd64) ;;
    *) fail "the published release images require an amd64 host" ;;
esac

require_command awk
require_command df
require_command docker
require_command nproc
require_command ss
require_command ufw

cpu_count=$(nproc)
[ "$cpu_count" -ge 8 ] || fail "at least 8 vCPUs are required; found $cpu_count"

memory_kib=$(awk '/^MemTotal:/ { print $2 }' /proc/meminfo)
case "$memory_kib" in
    *[!0-9]*|'') fail "could not read total memory" ;;
esac
[ "$memory_kib" -ge 15000000 ] \
    || fail "at least 16 GB-class RAM is required; found ${memory_kib} KiB"

swap_kib=$(awk '/^SwapTotal:/ { print $2 }' /proc/meminfo)
case "$swap_kib" in
    *[!0-9]*|'') fail "could not read total swap" ;;
esac
[ "$swap_kib" -ge 4000000 ] \
    || fail "configure a 4 GB emergency swap file before deployment"

host_root=${HOOK2STREAM_HOST_ROOT:-/srv/hook2stream}
case "$host_root" in
    /*) ;;
    *) fail "HOOK2STREAM_HOST_ROOT must be an absolute path" ;;
esac
[ -d "$host_root" ] || fail "$host_root does not exist"

disk_total_kib=$(df -Pk "$host_root" | awk 'NR == 2 { print $2 }')
disk_available_kib=$(df -Pk "$host_root" | awk 'NR == 2 { print $4 }')
case "$disk_total_kib:$disk_available_kib" in
    *[!0-9:]*) fail "could not determine staging disk capacity" ;;
esac
[ "$disk_total_kib" -ge 285000000 ] \
    || fail "the staging filesystem must provide roughly 320 GB-class storage"
[ $((disk_available_kib * 100 / disk_total_kib)) -ge 20 ] \
    || fail "less than 20 percent of the staging filesystem is free"

docker compose version >/dev/null 2>&1 \
    || fail "the Docker Compose v2 plugin is required"
docker_root=$(docker info --format '{{.DockerRootDir}}')
case "$docker_root" in
    "$host_root"|"$host_root"/*) ;;
    *) fail "Docker data-root must be inside $host_root; found $docker_root" ;;
esac

ufw_status=$(LC_ALL=C ufw status verbose) \
    || fail "could not read the UFW firewall policy"
staging_host_validate_ufw_status "$ufw_status" \
    || fail "UFW must default-deny inbound traffic, restrict 22/tcp to an operator address, expose only 80/tcp and 443/tcp+udp, and mirror public rules when IPv6 rules are enabled"

for private_port in 3000 5432 6432 8080 9000 9001; do
    if ss -ltn | awk -v port=":${private_port}" '
        NR > 1 && $4 ~ port "$" && ($4 ~ /^0\.0\.0\.0:/ || $4 ~ /^\[::\]:/) { found = 1 }
        END { exit found ? 0 : 1 }
    '; then
        fail "private service port ${private_port} is listening on every host interface"
    fi
done

printf '%s\n' \
    "staging host validation: Ubuntu, amd64, CPU, RAM, swap, disk, Docker, and private ports are valid"

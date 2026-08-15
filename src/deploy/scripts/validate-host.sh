#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_dir/lib/host-validation-common.sh"
. "$script_dir/lib/forced-command-trust.sh"

fail() { printf '%s\n' "host validation: $*" >&2; exit 1; }
require_command() { command -v "$1" >/dev/null 2>&1 || fail "$1 is required"; }
require_trusted_directory() {
    hook2stream_trusted_directory "$1" 0:0 "$2" || fail "$3 must be root:root mode 0$2"
}
require_trusted_file() {
    hook2stream_trusted_file "$1" 0:0 "$2" || fail "$3 must be root:root mode 0$2"
}
require_encrypted_subpath() {
    encrypted_path=$1
    encrypted_label=$2
    [ -e "$encrypted_path" ] && [ ! -L "$encrypted_path" ] \
        || fail "$encrypted_label is missing or a symlink: $encrypted_path"
    encrypted_source=$(findmnt -n -o SOURCE --target "$encrypted_path") \
        || fail "cannot resolve the mount source for $encrypted_label"
    encrypted_target=$(findmnt -n -o TARGET --target "$encrypted_path") \
        || fail "cannot resolve the mount target for $encrypted_label"
    hook2stream_subpath_mount_matches \
        "$mount_source" "$host_root" "$encrypted_source" "$encrypted_target" \
        || fail "$encrypted_label must resolve directly to the encrypted role mount"
}

role=${1:-}
environment=${2:-}
hook2stream_host_profile "$role" "$environment" \
    || fail "usage: validate-host.sh app|storage staging|production"

[ "$#" -eq 2 ] || fail "usage: validate-host.sh app|storage staging|production"
[ "$(id -u)" -eq 0 ] || fail "run through sudo"
[ -r /etc/os-release ] || fail "/etc/os-release is not readable"
. /etc/os-release
[ "${ID:-}" = ubuntu ] && [ "${VERSION_ID:-}" = 24.04 ] \
    || fail "Ubuntu 24.04 is required"
case "$(uname -m)" in x86_64|amd64) ;; *) fail "amd64 is required" ;; esac

for tool in awk cryptsetup df docker findmnt getent id losetup lsblk ss stat swapon tailscale ufw; do
    require_command "$tool"
done
docker compose version >/dev/null 2>&1 || fail "Docker Compose v2 is required"
[ "$(findmnt -n -o FSTYPE --target /proc)" = proc ] \
    || fail "/proc must be a procfs mount"
proc_options=$(findmnt -n -o OPTIONS --target /proc)
hook2stream_validate_proc_options "$proc_options" \
    || fail "/proc must use hidepid=2 or hidepid=invisible to protect process credentials"

host_root=${HOOK2STREAM_HOST_ROOT:-$hook2stream_profile_mount}
[ "$host_root" = "$hook2stream_profile_mount" ] \
    || fail "$role must use the exact mount $hook2stream_profile_mount"
secrets_dir=${SECRETS_DIR:-${host_root}/secrets/current}
case "$secrets_dir" in /*) ;; *) fail "SECRETS_DIR must be absolute" ;; esac
case "$secrets_dir" in
    "$host_root"/*) ;;
    *) fail "SECRETS_DIR must be below the encrypted role mount $host_root" ;;
esac
[ -d "$host_root" ] && [ ! -L "$host_root" ] \
    || fail "$host_root does not exist or is a symlink (unlock and mount LUKS first)"
require_trusted_directory "$host_root" 755 "encrypted role mount root"

mount_source=$(findmnt -n -o SOURCE --target "$host_root")
[ "$(findmnt -n -o TARGET --target "$host_root")" = "$host_root" ] \
    || fail "$host_root must be a mount point, not a directory inside another mount"
[ "$mount_source" = "/dev/mapper/$hook2stream_profile_mapper" ] \
    || fail "$host_root must be mounted from /dev/mapper/$hook2stream_profile_mapper"
luks_status=$(cryptsetup status "$hook2stream_profile_mapper" 2>/dev/null) \
    || fail "$mount_source is not an active LUKS mapping"
loop_device=$(hook2stream_luks_loop_from_status "$luks_status") \
    || fail "$mount_source must be a LUKS2 mapping backed by a loop device"
[ "$(lsblk -dn -o TYPE "$loop_device" | tr -d '[:space:]')" = loop ] \
    || fail "$loop_device is not a loop device"
cryptsetup isLuks --type luks2 "$loop_device" >/dev/null 2>&1 \
    || fail "$loop_device does not contain a LUKS2 header"

loop_backing_file=$(losetup --noheadings --output BACK-FILE "$loop_device" \
    | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')
[ "$loop_backing_file" = "$hook2stream_profile_backing_file" ] \
    || fail "$loop_device must be backed by $hook2stream_profile_backing_file"
[ -f "$hook2stream_profile_backing_file" ] && [ ! -L "$hook2stream_profile_backing_file" ] \
    || fail "$hook2stream_profile_backing_file must be a regular non-symlink file"
backing_metadata=$(stat -c '%u:%g:%a:%s:%b:%B' "$hook2stream_profile_backing_file")
hook2stream_validate_backing_metadata \
    "$backing_metadata" "$hook2stream_profile_minimum_gib" \
    || fail "$hook2stream_profile_backing_file must be root:root 0600, fully allocated, and exactly ${hook2stream_profile_minimum_gib} GiB"

volume_kib=$(df -Pk "$host_root" | awk 'NR == 2 {print $2}')
available_kib=$(df -Pk "$host_root" | awk 'NR == 2 {print $4}')
minimum_kib=$((hook2stream_profile_minimum_gib * 1024 * 1024 * 95 / 100))
[ "$volume_kib" -ge "$minimum_kib" ] \
    || fail "$role $environment requires a ${hook2stream_profile_minimum_gib} GiB-class LUKS filesystem"
[ "$volume_kib" -gt 0 ] || fail "could not determine encrypted filesystem size"
[ $((available_kib * 100 / volume_kib)) -ge 20 ] \
    || fail "less than 20 percent disk space is free"

docker_root=$(docker info --format '{{.DockerRootDir}}')
case "$docker_root" in "$host_root"/*) ;; *) fail "Docker data-root must be below $host_root" ;; esac
require_encrypted_subpath "$docker_root" "Docker data-root"
if [ "$role" = app ]; then
    for volume_name in caddy_data caddy_config postgres_data api_scratch worker_media_scratch worker_analysis_scratch worker_control_scratch worker_render_scratch worker_export_scratch backup_scratch; do
        volume_mount=$(docker volume inspect --format '{{.Mountpoint}}' \
            "hook2stream-${environment}_${volume_name}" 2>/dev/null || true)
        [ -z "$volume_mount" ] || require_encrypted_subpath "$volume_mount" "Docker volume $volume_name"
    done
fi

swap_total_kib=$(awk '/^SwapTotal:/ {print $2}' /proc/meminfo)
[ "${swap_total_kib:-0}" -ge 4000000 ] || fail "at least 4 GB encrypted swap is required"
swapon --noheadings --show=NAME | while IFS= read -r swap_path; do
    [ -n "$swap_path" ] || continue
    case "$swap_path" in
        "$host_root"/*)
            [ -f "$swap_path" ] && [ ! -L "$swap_path" ] \
                && [ "$(stat -c '%a' "$swap_path")" = 600 ] \
                || fail "swap file $swap_path must be a regular non-symlink mode 0600 file"
            [ "$(findmnt -n -o SOURCE --target "$swap_path")" = "$mount_source" ] \
                || fail "swap file $swap_path does not resolve to the encrypted role mount"
            ;;
        /dev/mapper/*)
            swap_mapper=${swap_path#/dev/mapper/}
            cryptsetup status "$swap_mapper" >/dev/null 2>&1 \
                || fail "swap mapper $swap_path is not encrypted"
            ;;
        *) fail "swap must be encrypted or stored on $host_root" ;;
    esac
done

ufw_status=$(LC_ALL=C ufw status verbose) || fail "cannot read UFW status"
hook2stream_validate_ufw_status "$role" "$ufw_status" \
    || fail "UFW does not match the exact $role policy"
tailscale status --json | grep -q '"BackendState"[[:space:]]*:[[:space:]]*"Running"' \
    || fail "Tailscale is not running"
tailscale_ipv4=$(tailscale ip -4) || fail "cannot determine the Tailscale IPv4 address"
case "$tailscale_ipv4" in
    ''|*'
'*|*:*|*[!0-9.]*) fail "Tailscale must expose exactly one IPv4 address" ;;
esac

sshd_effective=$(sshd -T 2>/dev/null) || fail "sshd configuration is invalid"
printf '%s\n' "$sshd_effective" | grep -qx 'passwordauthentication no' || fail "SSH passwords must be disabled"
printf '%s\n' "$sshd_effective" | grep -qx 'kbdinteractiveauthentication no' || fail "SSH keyboard-interactive auth must be disabled"
printf '%s\n' "$sshd_effective" | grep -qx 'permitrootlogin no' || fail "root SSH login must be disabled"

[ -d "$secrets_dir" ] && [ ! -L "$secrets_dir" ] \
    || fail "secret directory is missing or a symlink"
require_encrypted_subpath "$secrets_dir" "secrets directory"
secret_gid=${SECRETS_GID:-2000}
[ "$(stat -c '%u:%g:%a' "$secrets_dir")" = "0:${secret_gid}:750" ] \
    || fail "secret directory must be root:${secret_gid} mode 0750"
for secret in $(hook2stream_required_secret_files "$role"); do
    secret_path=$secrets_dir/$secret
    [ -f "$secret_path" ] && [ ! -L "$secret_path" ] \
        || fail "$secret is missing or a symlink"
    [ "$(stat -c '%u:%g:%a' "$secret_path")" = "0:${secret_gid}:640" ] \
        || fail "$secret must be root:${secret_gid} mode 0640"
    [ -s "$secret_path" ] || fail "$secret is empty"
done

operator_user=${SUDO_USER:-}
[ -n "$operator_user" ] && [ "$operator_user" != root ] \
    || fail "run the validator through sudo from the named operator account"
case "$role" in
    app) deploy_user=hook2stream-deploy ;;
    storage) deploy_user=hook2stream-storage-deploy ;;
esac
docker_group=$(getent group docker) || fail "docker group is missing"
docker_gid=$(printf '%s\n' "$docker_group" | awk -F: 'NF == 4 {print $3}')
[ -n "$docker_gid" ] || fail "docker group has an invalid record"
for unprivileged_user in "$operator_user" "$deploy_user"; do
    unprivileged_uid=$(id -u "$unprivileged_user" 2>/dev/null) \
        || fail "required account is missing: $unprivileged_user"
    case "$unprivileged_uid" in
        0|10001|10002|10003) fail "$unprivileged_user has a reserved or privileged UID" ;;
    esac
    unprivileged_groups=$(id -G "$unprivileged_user") \
        || fail "cannot inspect groups for $unprivileged_user"
    hook2stream_gid_list_contains "$unprivileged_groups" "$secret_gid" \
        && fail "$unprivileged_user must not belong to the secrets group"
    hook2stream_gid_list_contains "$unprivileged_groups" "$docker_gid" \
        && fail "$unprivileged_user must not belong to the docker group"
done
[ "$(id -u "$operator_user")" != "$(id -u "$deploy_user")" ] \
    || fail "operator and deploy accounts must have different UIDs"

for trusted_system_directory in /usr/local /usr/local/sbin /usr/local/libexec; do
    require_trusted_directory "$trusted_system_directory" 755 "deploy gate system directory"
done

case "$role" in
    app)
        require_trusted_file /etc/hook2stream/deploy.conf 600 "app deploy launcher config"
        require_trusted_file "/srv/hook2stream/config/${environment}.env" 600 "app environment"
        require_trusted_file /usr/local/sbin/hook2stream-deploy-launcher 555 "app deploy launcher"
        require_trusted_directory /usr/local/libexec/hook2stream 755 "app deploy gate directory"
        require_trusted_directory /usr/local/libexec/hook2stream/lib 755 "app deploy gate library directory"
        for trusted_gate in \
            /usr/local/libexec/hook2stream/deploy-forced-command.sh \
            /usr/local/libexec/hook2stream/validate-candidate.sh \
            /usr/local/libexec/hook2stream/lib/forced-command-trust.sh; do
            require_trusted_file "$trusted_gate" 555 "app deploy gate program"
        done
        require_trusted_file /usr/local/libexec/hook2stream/post-deploy-e2e.sh 500 "app post-deploy E2E hook"
        if [ "$environment" = production ]; then
            require_trusted_file /etc/hook2stream/staging-receipt-allowed-signers 600 \
                "app staging allowed-signers file"
        fi
        ;;
    storage)
        require_trusted_file /etc/hook2stream-storage/deploy.conf 600 "storage deploy launcher config"
        require_trusted_file "/etc/hook2stream-storage/${environment}.env" 600 "storage environment"
        require_trusted_file /usr/local/sbin/hook2stream-storage-deploy-launcher 555 \
            "storage deploy launcher"
        require_trusted_directory /usr/local/libexec/hook2stream-storage 755 \
            "storage deploy gate directory"
        require_trusted_directory /usr/local/libexec/hook2stream-storage/lib 755 \
            "storage deploy gate library directory"
        for trusted_gate in \
            /usr/local/libexec/hook2stream-storage/storage-forced-command.sh \
            /usr/local/libexec/hook2stream-storage/validate-candidate.sh \
            /usr/local/libexec/hook2stream-storage/validate-production-approval.sh \
            /usr/local/libexec/hook2stream-storage/lib/storage-common.sh; do
            require_trusted_file "$trusted_gate" 555 "storage deploy gate program"
        done
        if [ "$environment" = production ]; then
            require_trusted_file \
                /etc/hook2stream-storage/storage-staging-receipt-allowed-signers 600 \
                "storage staging allowed-signers file"
        fi
        ;;
esac

if [ "$role" = storage ]; then
    for service_identity in \
        hook2stream-minio:10001:10001 \
        hook2stream-storage-caddy:10002:10002 \
        hook2stream-storage-init:10003:10003; do
        service_name=${service_identity%%:*}
        service_numbers=${service_identity#*:}
        service_uid=${service_numbers%%:*}
        service_gid=${service_numbers#*:}
        service_passwd=$(getent passwd "$service_name") \
            || fail "dedicated storage account is missing: $service_name"
        hook2stream_service_identity_matches \
            "$service_passwd" "$service_name" "$service_uid" "$service_gid" \
            || fail "$service_name must use reserved UID:GID and /usr/sbin/nologin"
        service_group=$(getent group "$service_name") \
            || fail "dedicated storage group is missing: $service_name"
        [ "$(printf '%s\n' "$service_group" | awk -F: 'NF == 4 {print $3}')" = "$service_gid" ] \
            || fail "$service_name primary group has the wrong GID"
        [ -z "$(printf '%s\n' "$service_group" | awk -F: 'NF == 4 {print $4}')" ] \
            || fail "$service_name group must not contain supplemental members"
        hook2stream_gid_list_is_exact "$(id -G "$service_name")" "$service_gid" \
            || fail "$service_name must not have supplementary host groups"
        for unprivileged_user in "$operator_user" "$deploy_user"; do
            hook2stream_gid_list_contains "$(id -G "$unprivileged_user")" "$service_gid" \
                && fail "$unprivileged_user must not belong to $service_name"
        done
    done

    require_command jq
    minio_security_policy=/etc/hook2stream-storage/minio-security-policy.json
    [ -f "$minio_security_policy" ] && [ ! -L "$minio_security_policy" ] \
        && [ "$(stat -c '%u:%g:%a' "$minio_security_policy")" = 0:0:600 ] \
        || fail "MinIO security policy must be $minio_security_policy, root:root mode 0600"
    jq -e '
        def exactKeys($expected):
            type == "object" and (keys | sort) == ($expected | sort);
        exactKeys(["schemaVersion","kind","reviewedAt","approvedSourceReleases","blockingAdvisories"]) and
        .schemaVersion == 1 and .kind == "hook2stream-minio-security-policy" and
        (.reviewedAt | type == "string" and test("^[0-9]{4}-[0-9]{2}-[0-9]{2}$")) and
        (.approvedSourceReleases | type == "array") and
        ([.approvedSourceReleases[] | (.release + ":" + .commit)] | length == (unique | length)) and
        ([.approvedSourceReleases[].securitySequence] | length == (unique | length)) and
        all(.approvedSourceReleases[];
            exactKeys(["release","commit","source","reviewedAt","securitySequence"]) and
            (.release | type == "string" and test("^RELEASE\\.[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}-[0-9]{2}-[0-9]{2}Z$")) and
            (.commit | type == "string" and test("^[0-9a-f]{40}$")) and
            .source == "https://github.com/minio/minio" and
            (.reviewedAt | type == "string" and test("^[0-9]{4}-[0-9]{2}-[0-9]{2}$")) and
            (.securitySequence | type == "number" and . >= 1 and floor == .)) and
        (.blockingAdvisories | type == "array" and length > 0) and
        ([.blockingAdvisories[].id] | length == (unique | length)) and
        all(.blockingAdvisories[];
            exactKeys(["id","severity","url","patchedOssRelease"]) and
            (.id | type == "string" and test("^CVE-[0-9]{4}-[0-9]{4,}$")) and
            (.severity == "high" or .severity == "critical") and
            (.url | type == "string" and test("^https://github\\.com/advisories/GHSA-[a-z0-9-]+$")) and
            .patchedOssRelease == null)
    ' "$minio_security_policy" >/dev/null \
        || fail "MinIO security policy schema is invalid"
fi

docker_binding_table=
for running_container in $(docker ps --quiet); do
    container_bindings=$(docker inspect --format \
        '{{range $port, $bindings := .HostConfig.PortBindings}}{{range $binding := $bindings}}{{printf "%s\t%s\t%s\t%s\t%s\n" (index $.Config.Labels "com.docker.compose.project") (index $.Config.Labels "com.docker.compose.service") $port $binding.HostIp $binding.HostPort}}{{end}}{{end}}{{range $port, $bindings := .NetworkSettings.Ports}}{{range $binding := $bindings}}{{printf "%s\t%s\t%s\t%s\t%s\n" (index $.Config.Labels "com.docker.compose.project") (index $.Config.Labels "com.docker.compose.service") $port $binding.HostIp $binding.HostPort}}{{end}}{{end}}' \
        "$running_container") || fail "cannot inspect Docker port bindings for $running_container"
    [ -z "$container_bindings" ] || docker_binding_table="${docker_binding_table}${docker_binding_table:+
}${container_bindings}"
done
hook2stream_validate_docker_bindings \
    "$role" "$environment" "$tailscale_ipv4" "$docker_binding_table" \
    || fail "Docker publishes a port outside the exact $role $environment policy"

for encrypted_runtime_dir in "$host_root/releases" "$host_root/release-state"; do
    require_trusted_directory "$encrypted_runtime_dir" 700 "runtime state directory"
    require_encrypted_subpath "$encrypted_runtime_dir" "runtime state directory"
done
if [ "$role" = app ]; then
    require_trusted_directory "$host_root/config" 700 "app configuration directory"
    require_encrypted_subpath "$host_root/config" "app configuration directory"
fi
if [ "$role" = storage ]; then
    require_encrypted_subpath "$host_root/minio-data" "MinIO data directory"
fi

case "$role" in
    app) private_ports='3000 3128 5432 6432 8080 9000 9001' ;;
    storage) private_ports='9000 9001' ;;
esac
tcp_listeners=$(ss -H -ltn) || fail "cannot inspect host TCP listeners"
for private_port in $private_ports; do
    if hook2stream_has_tcp_listener "$tcp_listeners" "$private_port"; then
        fail "private port $private_port must not have any host listener"
    fi
done
if [ "$role" = storage ]; then
    hook2stream_validate_storage_https_listeners "$tcp_listeners" "$tailscale_ipv4" \
        || fail "storage HTTPS must listen only on the exact Tailscale IPv4 address"
    udp_listeners=$(ss -H -lun) || fail "cannot inspect host UDP listeners"
    if hook2stream_has_tcp_listener "$udp_listeners" 443; then
        fail "storage must not publish HTTPS over UDP"
    fi
fi

printf '%s\n' \
    "host validation: $role $environment file-backed LUKS2, encrypted swap, Docker, role secrets, UFW, SSH, and Tailscale passed"

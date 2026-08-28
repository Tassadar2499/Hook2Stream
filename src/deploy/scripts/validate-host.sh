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
require_minimum_free_percent() {
    filesystem_path=$1
    filesystem_label=$2
    filesystem_total_kib=$(df -Pk "$filesystem_path" | awk 'NR == 2 {print $2}')
    filesystem_available_kib=$(df -Pk "$filesystem_path" | awk 'NR == 2 {print $4}')
    [ "${filesystem_total_kib:-0}" -gt 0 ] \
        || fail "could not determine $filesystem_label filesystem size"
    [ $((filesystem_available_kib * 100 / filesystem_total_kib)) -ge 20 ] \
        || fail "$filesystem_label has less than 20 percent free space"
}

role=${1:-}
environment=${2:-}
hook2stream_host_profile "$role" "$environment" \
    || fail "usage: validate-host.sh app staging|production"

[ "$#" -eq 2 ] || fail "usage: validate-host.sh app staging|production"
[ "$(id -u)" -eq 0 ] || fail "run through sudo"
[ -r /etc/os-release ] || fail "/etc/os-release is not readable"
. /etc/os-release
[ "${ID:-}" = ubuntu ] && [ "${VERSION_ID:-}" = 24.04 ] \
    || fail "Ubuntu 24.04 is required"
case "$(uname -m)" in x86_64|amd64) ;; *) fail "amd64 is required" ;; esac

for tool in awk cat cryptsetup cvtsudoers date df docker ffprobe findmnt getent getfacl grep id jq losetup lsblk passwd python3 ss sshd ssh-keygen stat swapon systemctl tailscale timeout ufw visudo; do
    require_command "$tool"
done
docker compose version >/dev/null 2>&1 || fail "Docker Compose v2 is required"
docker_mount_guard=/etc/systemd/system/docker.service.d/10-hook2stream-encrypted-mount.conf
require_trusted_file "$docker_mount_guard" 644 "Docker encrypted-mount guard"
encrypted_mount_unit=/etc/systemd/system/srv-hook2stream.mount
encrypted_swap_unit=/etc/systemd/system/hook2stream-encrypted-swap.service
docker_daemon_config=/etc/docker/daemon.json
require_trusted_file "$encrypted_mount_unit" 644 "encrypted application mount unit"
require_trusted_file "$encrypted_swap_unit" 644 "encrypted swap unit"
require_trusted_file "$docker_daemon_config" 644 "Docker daemon configuration"
for exact_mount_directive in \
    'What=/dev/mapper/hook2stream-data' \
    'Where=/srv/hook2stream' \
    'Type=ext4'; do
    grep -Fxq "$exact_mount_directive" "$encrypted_mount_unit" \
        || fail "encrypted mount unit omits $exact_mount_directive"
done
for exact_swap_directive in \
    'RequiresMountsFor=/srv/hook2stream' \
    'ConditionPathIsMountPoint=/srv/hook2stream' \
    'ExecStart=/sbin/swapon /srv/hook2stream/swap/hook2stream.swap'; do
    grep -Fxq "$exact_swap_directive" "$encrypted_swap_unit" \
        || fail "encrypted swap unit omits $exact_swap_directive"
done
jq -e '
    type == "object" and
    .["data-root"] == "/srv/hook2stream/docker"
' "$docker_daemon_config" >/dev/null \
    || fail "Docker daemon data-root must be /srv/hook2stream/docker"
loaded_docker_unit=$(systemctl cat docker.service) \
    || fail "cannot inspect the loaded Docker systemd unit"
for mount_guard_directive in \
    'RequiresMountsFor=/srv/hook2stream' \
    'After=srv-hook2stream.mount' \
    'ConditionPathIsMountPoint=/srv/hook2stream'; do
    grep -Fxq "$mount_guard_directive" "$docker_mount_guard" \
        || fail "Docker encrypted-mount guard omits $mount_guard_directive"
    printf '%s\n' "$loaded_docker_unit" | grep -Fxq "$mount_guard_directive" \
        || fail "Docker has not loaded encrypted-mount guard directive $mount_guard_directive"
done
systemctl is-active --quiet hook2stream-encrypted-swap.service \
    || fail "encrypted swap service is not active"
[ "$(findmnt -n -o FSTYPE --target /proc)" = proc ] \
    || fail "/proc must be a procfs mount"
proc_options=$(findmnt -n -o OPTIONS --target /proc)
hook2stream_validate_proc_options "$proc_options" \
    || fail "/proc must use hidepid=2 or hidepid=invisible to protect process credentials"
[ -f /etc/fstab ] && [ ! -L /etc/fstab ] \
    || fail "/etc/fstab must be a regular non-symlink file"
proc_fstab_status=$(awk '
    /^[[:space:]]*($|#)/ { next }
    $2 == "/proc" {
        count++
        if (NF != 6 || $1 != "proc" || $3 != "proc" ||
            $4 != "nosuid,nodev,noexec,hidepid=2" || $5 != 0 || $6 != 0) invalid = 1
    }
    END {
        if (count == 1 && !invalid) print "ok"
    }
' /etc/fstab)
[ "$proc_fstab_status" = ok ] \
    || fail "/etc/fstab must contain exactly one canonical persistent hidepid=2 proc mount"

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
require_minimum_free_percent / "root filesystem"

volume_kib=$(df -Pk "$host_root" | awk 'NR == 2 {print $2}')
minimum_kib=$((hook2stream_profile_minimum_gib * 1024 * 1024 * 95 / 100))
[ "$volume_kib" -ge "$minimum_kib" ] \
    || fail "$role $environment requires a ${hook2stream_profile_minimum_gib} GiB-class LUKS filesystem"
require_minimum_free_percent "$host_root" "encrypted filesystem"

docker_root=$(docker info --format '{{.DockerRootDir}}')
case "$docker_root" in "$host_root"/*) ;; *) fail "Docker data-root must be below $host_root" ;; esac
require_encrypted_subpath "$docker_root" "Docker data-root"
for volume_name in caddy_data caddy_config postgres_data api_scratch worker_media_scratch worker_analysis_scratch worker_control_scratch worker_render_scratch worker_export_scratch backup_scratch; do
    volume_mount=$(docker volume inspect --format '{{.Mountpoint}}' \
        "hook2stream-${environment}_${volume_name}" 2>/dev/null || true)
    [ -z "$volume_mount" ] || require_encrypted_subpath "$volume_mount" "Docker volume $volume_name"
done

swap_total_kib=$(awk '/^SwapTotal:/ {print $2}' /proc/meminfo)
[ "${swap_total_kib:-0}" -ge 4194304 ] || fail "at least 4 GiB encrypted swap is required"
swap_paths=$(swapon --noheadings --show=NAME) \
    || fail "cannot enumerate active swap"
[ -n "$swap_paths" ] || fail "no active swap was reported"
while IFS= read -r swap_path; do
    [ -n "$swap_path" ] || continue
    case "$swap_path" in
        "$host_root"/*) ;;
        *) fail "every active swap must be a file below the encrypted role mount $host_root" ;;
    esac
    [ -f "$swap_path" ] && [ ! -L "$swap_path" ] \
        && [ "$(stat -c '%a' "$swap_path")" = 600 ] \
        || fail "swap file $swap_path must be a regular non-symlink mode 0600 file"
    [ "$(findmnt -n -o SOURCE --target "$swap_path")" = "$mount_source" ] \
        || fail "swap file $swap_path does not resolve to the encrypted role mount"
done <<EOF
$swap_paths
EOF

ufw_status=$(LC_ALL=C ufw status verbose) || fail "cannot read UFW status"
hook2stream_validate_ufw_status "$role" "$ufw_status" \
    || fail "UFW does not match the exact $role policy"
tailscale_status=$(tailscale status --json) || fail "cannot read Tailscale status"
printf '%s\n' "$tailscale_status" | jq -e '.BackendState == "Running"' >/dev/null \
    || fail "Tailscale is not running"
tailscale_dns_name=$(printf '%s\n' "$tailscale_status" | jq -er '.Self.DNSName') \
    || fail "cannot determine the Tailscale MagicDNS identity"
tailscale_dns_name=${tailscale_dns_name%.}
printf '%s\n' "$tailscale_dns_name" \
    | grep -Eq "^h2s-app-${environment}\\.[a-z0-9-]+\\.ts\\.net$" \
    || fail "Tailscale MagicDNS identity does not match $environment"
tailscale_ipv4=$(tailscale ip -4) || fail "cannot determine the Tailscale IPv4 address"
case "$tailscale_ipv4" in
    ''|*'
'*|*:*|*[!0-9.]*) fail "Tailscale must expose exactly one IPv4 address" ;;
esac
tailscale_ssh_preference=$(tailscale get --json ssh 2>/dev/null) \
    || fail "cannot read the Tailscale SSH preference"
hook2stream_validate_tailscale_ssh_preference "$tailscale_ssh_preference" \
    || fail "Tailscale SSH must remain disabled; ordinary OpenSSH is required"

hook2stream_validate_sshd_config_tree \
    /etc/ssh/sshd_config /etc/ssh/sshd_config.d 0:0 \
    || fail "SSH config must use one canonical include tree without Match or nested Include directives"
require_trusted_file /etc/ssh/sshd_config.d/99-hook2stream-no-public-ssh.conf 644 \
    "Hook2Stream SSH policy drop-in"
ssh_host_private_key=/etc/ssh/ssh_host_ed25519_key
ssh_host_public_key=/etc/ssh/ssh_host_ed25519_key.pub
require_trusted_file "$ssh_host_private_key" 600 "private ED25519 SSH host key"
require_trusted_file "$ssh_host_public_key" 644 "public ED25519 SSH host key"
ssh_host_private_details=$(ssh-keygen -lf "$ssh_host_private_key" -E sha256 2>/dev/null) \
    || fail "cannot fingerprint the private ED25519 SSH host key"
ssh_host_public_details=$(ssh-keygen -lf "$ssh_host_public_key" -E sha256 2>/dev/null) \
    || fail "cannot fingerprint the public ED25519 SSH host key"
for ssh_host_key_details in "$ssh_host_private_details" "$ssh_host_public_details"; do
    [ "$(printf '%s\n' "$ssh_host_key_details" | awk 'NF { count++ } END { print count + 0 }')" -eq 1 ] \
        && [ "$(printf '%s\n' "$ssh_host_key_details" | awk '{ print $1 }')" = 256 ] \
        && [ "$(printf '%s\n' "$ssh_host_key_details" | awk '{ print $NF }')" = '(ED25519)' ] \
        || fail "SSH host key must contain exactly one 256-bit ED25519 identity"
done
ssh_host_key_fingerprint=$(printf '%s\n' "$ssh_host_private_details" | awk '{ print $2 }')
[ "$ssh_host_key_fingerprint" = "$(printf '%s\n' "$ssh_host_public_details" | awk '{ print $2 }')" ] \
    || fail "SSH host public key does not match its private ED25519 key"
for ssh_policy_user in hook2stream-operator hook2stream-deploy; do
    sshd_effective=$(sshd -T -C \
        "user=${ssh_policy_user},host=h2s-app-${environment},addr=100.64.0.1,laddr=${tailscale_ipv4},lport=22" \
        2>/dev/null) || fail "sshd configuration is invalid for $ssh_policy_user"
    hook2stream_validate_sshd_effective "$sshd_effective" \
        || fail "SSH must use the canonical root-password/key-user policy for $ssh_policy_user"
    host_certificate_lines=$(printf '%s\n' "$sshd_effective" \
        | awk 'tolower($1) == "hostcertificate" { print tolower($1), $2 }')
    [ -z "$host_certificate_lines" ] \
        || fail "$environment must use only its pinned raw ED25519 host key"
done
sshd_root_effective=$(sshd -T -C \
    "user=root,host=h2s-app-${environment},addr=100.64.0.1,laddr=${tailscale_ipv4},lport=22" \
    2>/dev/null) || fail "sshd configuration is invalid for root"
hook2stream_validate_sshd_root_effective "$sshd_root_effective" \
    || fail "root password SSH must be enabled only behind the Tailscale-only UFW boundary"
root_host_certificate_lines=$(printf '%s\n' "$sshd_root_effective" \
    | awk 'tolower($1) == "hostcertificate" { print tolower($1), $2 }')
[ -z "$root_host_certificate_lines" ] \
    || fail "$environment root SSH must use only its pinned raw ED25519 host key"

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
    hook2stream_no_extended_acl "$secret_path" \
        || fail "$secret must not grant access through POSIX ACLs"
    [ -s "$secret_path" ] || fail "$secret is empty"
done
hook2stream_no_extended_acl "$secrets_dir" \
    || fail "secret directory must not grant access through POSIX ACLs"

operator_user=${SUDO_USER:-}
[ "$operator_user" = hook2stream-operator ] \
    || fail "run the validator through sudo from hook2stream-operator"
deploy_user=hook2stream-deploy
docker_group=$(getent group docker) || fail "docker group is missing"
docker_gid=$(printf '%s\n' "$docker_group" | awk -F: 'NF == 4 {print $3}')
[ -n "$docker_gid" ] || fail "docker group has an invalid record"
sudo_group=$(getent group sudo) || fail "sudo group is missing"
sudo_gid=$(printf '%s\n' "$sudo_group" | awk -F: 'NF == 4 {print $3}')
[ -n "$sudo_gid" ] || fail "sudo group has an invalid record"
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
    [ "$unprivileged_user" != "$deploy_user" ] \
        || ! hook2stream_gid_list_contains "$unprivileged_groups" "$sudo_gid" \
        || fail "$deploy_user must not belong to the sudo group"
    password_status=$(passwd -S "$unprivileged_user" 2>/dev/null | awk 'NF >= 2 { print $2 }')
    hook2stream_validate_locked_password_status "$password_status" \
        || fail "$unprivileged_user local password must remain locked"
done
[ "$(id -u "$operator_user")" != "$(id -u "$deploy_user")" ] \
    || fail "operator and deploy accounts must have different UIDs"
root_password_status=$(passwd -S root 2>/dev/null | awk 'NF >= 2 { print $2 }')
hook2stream_validate_root_password_status "$root_password_status" \
    || fail "root must be the only SSH account with an active local password"

for trusted_system_directory in /usr/local /usr/local/sbin /usr/local/libexec; do
    require_trusted_directory "$trusted_system_directory" 755 "deploy gate system directory"
done

require_trusted_file /etc/hook2stream/deploy.conf 600 "app deploy launcher config"
read_deploy_config_value() {
    deploy_config_key=$1
    [ "$(awk -F= -v key="$deploy_config_key" '$1 == key { count++ } END { print count + 0 }' \
        /etc/hook2stream/deploy.conf)" -eq 1 ] \
        || fail "deploy config must contain exactly one $deploy_config_key"
    awk -F= -v key="$deploy_config_key" \
        '$1 == key { print substr($0, index($0, "=") + 1) }' \
        /etc/hook2stream/deploy.conf
}
operator_key_fingerprint=$(read_deploy_config_value HOOK2STREAM_OPERATOR_PUBLIC_KEY_SHA256)
deploy_key_fingerprint=$(read_deploy_config_value HOOK2STREAM_DEPLOY_PUBLIC_KEY_SHA256)
registry_auth_dir=$(read_deploy_config_value DOCKER_CONFIG)
registry_auth_username=$(read_deploy_config_value HOOK2STREAM_GHCR_USERNAME)
registry_auth_sha256=$(read_deploy_config_value HOOK2STREAM_GHCR_AUTH_SHA256)
registry_credential_identity=$(read_deploy_config_value HOOK2STREAM_GHCR_CREDENTIAL_IDENTITY)
registry_identity_sha256=$(read_deploy_config_value HOOK2STREAM_GHCR_IDENTITY_SHA256)
[ "$registry_auth_dir" = /srv/hook2stream/registry-auth ] \
    || fail "deploy config DOCKER_CONFIG must use the canonical encrypted registry-auth path"
hook2stream_validate_ghcr_pull_auth \
    "$registry_auth_dir" "$registry_auth_username" "$registry_auth_sha256" 0:0 \
    || fail "GHCR pull authentication is missing, unsafe, malformed, or differs from the pinned environment credential"
hook2stream_validate_ghcr_identity_attestation \
    "$registry_auth_dir" "$environment" "$registry_auth_username" \
    "$registry_credential_identity" "$registry_identity_sha256" 0:0 \
    || fail "GHCR credential identity attestation is missing, unsafe, malformed, or differs from its environment pin"
require_encrypted_subpath "$registry_auth_dir" "GHCR pull-auth directory"
for key_fingerprint in "$operator_key_fingerprint" "$deploy_key_fingerprint"; do
    printf '%s\n' "$key_fingerprint" | grep -Eq '^SHA256:[A-Za-z0-9+/]{43}$' \
        || fail "deploy config public-key fingerprints must use OpenSSH SHA256 form"
done
hook2stream_validate_distinct_ed25519_fingerprints \
    "$operator_key_fingerprint" "$deploy_key_fingerprint" \
    || fail "operator and forced-command deploy accounts must use different ED25519 keys"
for ssh_access_fingerprint in "$operator_key_fingerprint" "$deploy_key_fingerprint"; do
    hook2stream_validate_distinct_ed25519_fingerprints \
        "$ssh_access_fingerprint" "$ssh_host_key_fingerprint" \
        || fail "SSH user and host identities must use different ED25519 keys"
done
for ssh_identity in operator deploy; do
    case "$ssh_identity" in
        operator)
            ssh_identity_user=hook2stream-operator
            ssh_identity_fingerprint=$operator_key_fingerprint
            ;;
        deploy)
            ssh_identity_user=hook2stream-deploy
            ssh_identity_fingerprint=$deploy_key_fingerprint
            ;;
    esac
    ssh_identity_home=$(getent passwd "$ssh_identity_user" | awk -F: 'NF == 7 { print $6 }')
    [ "$ssh_identity_home" = "/home/$ssh_identity_user" ] \
        || fail "$ssh_identity_user must use the canonical home directory"
    ssh_identity_owner=$(id -u "$ssh_identity_user"):$(id -g "$ssh_identity_user")
    [ -d "$ssh_identity_home/.ssh" ] && [ ! -L "$ssh_identity_home/.ssh" ] \
        && [ "$(stat -c '%u:%g:%a' "$ssh_identity_home/.ssh")" = "$ssh_identity_owner:700" ] \
        || fail "$ssh_identity_user .ssh directory has unsafe ownership or mode"
    hook2stream_no_extended_acl "$ssh_identity_home/.ssh" \
        || fail "$ssh_identity_user .ssh directory must not have extended ACLs"
    ssh_identity_authorized_keys=$ssh_identity_home/.ssh/authorized_keys
    [ -f "$ssh_identity_authorized_keys" ] && [ ! -L "$ssh_identity_authorized_keys" ] \
        && [ "$(stat -c '%u:%g:%a' "$ssh_identity_authorized_keys")" = "$ssh_identity_owner:600" ] \
        || fail "$ssh_identity_user authorized_keys has unsafe ownership or mode"
    hook2stream_no_extended_acl "$ssh_identity_authorized_keys" \
        || fail "$ssh_identity_user authorized_keys must not have extended ACLs"
    hook2stream_validate_exact_authorized_key \
        "$ssh_identity_authorized_keys" "$ssh_identity" "$ssh_identity_fingerprint" \
        || fail "$ssh_identity_user authorized_keys is not the exact approved ED25519 identity"
done
sudoers_dropin=/etc/sudoers.d/hook2stream-deploy
require_trusted_directory /etc/sudoers.d 755 "sudoers drop-in directory"
require_trusted_file "$sudoers_dropin" 440 "deploy forced-command sudoers rule"
hook2stream_validate_exact_deploy_sudoers "$sudoers_dropin" \
    || fail "deploy sudoers rule is not the exact forced-command grant"
visudo -cf "$sudoers_dropin" >/dev/null 2>&1 \
    || fail "deploy forced-command sudoers rule is invalid"
effective_deploy_sudoers=$(LC_ALL=C cvtsudoers -f sudoers -e -M -p \
    -m 'user=hook2stream-deploy' -s defaults,aliases /etc/sudoers 2>/dev/null) \
    || fail "cannot resolve the effective deploy sudoers policy"
hook2stream_validate_effective_deploy_sudoers "$effective_deploy_sudoers" \
    || fail "effective deploy sudoers policy contains an extra user, group, host, runas, option, or command grant"
docker_socket=/var/run/docker.sock
[ -S "$docker_socket" ] && [ ! -L "$docker_socket" ] \
    && [ "$(stat -c '%U:%G:%a' "$docker_socket")" = root:docker:660 ] \
    || fail "Docker socket must be root:docker mode 0660"
hook2stream_no_extended_acl "$docker_socket" \
    || fail "Docker socket must not grant access through POSIX ACLs"
require_trusted_file "/srv/hook2stream/config/${environment}.env" 600 "app environment"
require_trusted_file /usr/local/sbin/hook2stream-deploy-launcher 555 "app deploy launcher"
require_trusted_directory /usr/local/libexec/hook2stream 755 "app deploy gate directory"
require_trusted_directory /usr/local/libexec/hook2stream/lib 755 "app deploy gate library directory"
for trusted_gate in \
    /usr/local/libexec/hook2stream/deploy-forced-command.sh \
    /usr/local/libexec/hook2stream/rollback-application.sh \
    /usr/local/libexec/hook2stream/validate-candidate.sh \
    /usr/local/libexec/hook2stream/lib/forced-command-trust.sh; do
    require_trusted_file "$trusted_gate" 555 "app deploy gate program"
done
require_trusted_file /usr/local/libexec/hook2stream/post-deploy-e2e.sh 500 "app post-deploy E2E hook"
require_trusted_file /usr/local/libexec/hook2stream/authenticated-e2e.sh 500 \
    "app authenticated E2E and sustained soak hook"
last_successful=/srv/hook2stream/release-state/last-successful.env
active_infrastructure=/srv/hook2stream/release-state/active-infrastructure-release.json
if [ -e "$active_infrastructure" ] || [ -L "$active_infrastructure" ]; then
    require_trusted_file "$last_successful" 600 "last successful release environment"
    require_trusted_file "$active_infrastructure" 600 "active infrastructure release marker"
    current_release=$(awk -F= '
      $1 == "RELEASE_VERSION" { count++; value=substr($0,index($0,"=")+1) }
      END { if (count != 1) exit 1; print value }
    ' "$last_successful") || fail "last successful environment has no unique RELEASE_VERSION"
    rollback_protocol=hook2stream-application-rollback-v2
    hook2stream_validate_rollback_capability \
        "/srv/hook2stream/release-state/successful/$current_release.capabilities.json" \
        "$current_release" "$rollback_protocol" 0:0 \
        || fail "current application release is not rollback protocol v2 capable"
    active_infrastructure_state=$(jq -ce --arg protocol "$rollback_protocol" 'select(
      (keys | sort) == ["deployBundleSha256","kind","releaseSha","rollbackProtocol","schemaVersion"] and
      .schemaVersion == 2 and .kind == "hook2stream-active-infrastructure-release" and
      .rollbackProtocol == $protocol and
      (.releaseSha | type == "string" and test("^[0-9a-f]{40}$")) and
      (.deployBundleSha256 | type == "string" and test("^[0-9a-f]{64}$"))
    )' "$active_infrastructure") || fail "active infrastructure marker is not rollback protocol v2"
    active_infrastructure_release=$(printf '%s' "$active_infrastructure_state" | jq -r '.releaseSha')
    active_infrastructure_bundle=$(printf '%s' "$active_infrastructure_state" | jq -r '.deployBundleSha256')
    hook2stream_validate_rollback_capability \
        "/srv/hook2stream/release-state/successful/$active_infrastructure_release.capabilities.json" \
        "$active_infrastructure_release" "$rollback_protocol" 0:0 \
        || fail "active infrastructure release is not rollback protocol v2 capable"
    require_trusted_file \
        "/srv/hook2stream/release-state/successful/$active_infrastructure_release.env" 600 \
        "active infrastructure release environment"
    active_infrastructure_dir=/srv/hook2stream/releases/$active_infrastructure_release
    require_trusted_directory "$active_infrastructure_dir" 700 "active infrastructure release directory"
    require_trusted_file "$active_infrastructure_dir/.deploy-bundle.sha256" 600 \
        "active infrastructure bundle digest"
    require_trusted_file "$active_infrastructure_dir/deploy/compose.yaml" 600 \
        "active infrastructure Compose source"
    require_trusted_file "$active_infrastructure_dir/deploy/scripts/lib/deployment-common.sh" 600 \
        "active infrastructure deployment helper"
    require_trusted_file "$active_infrastructure_dir/deploy/scripts/lib/forced-command-trust.sh" 700 \
        "active infrastructure trust helper"
    [ "$(cat "$active_infrastructure_dir/.deploy-bundle.sha256")" = "$active_infrastructure_bundle" ] \
        || fail "active infrastructure bundle differs from its forward-deploy marker"
elif [ -e "$last_successful" ] || [ -L "$last_successful" ]; then
    # A pre-v2 installation may have a successful environment but no active
    # infrastructure marker. It remains forward-deployable so the next
    # successful release can establish v2 state; rollback itself fails closed.
    require_trusted_file "$last_successful" 600 "legacy last successful release environment"
fi
if [ "$environment" = production ]; then
    require_trusted_file /etc/hook2stream/staging-receipt-allowed-signers 600 \
        "app staging allowed-signers file"
    hook2stream_validate_exact_allowed_signer \
        /etc/hook2stream/staging-receipt-allowed-signers hook2stream-staging \
        || fail "app staging allowed-signers must contain exactly one hook2stream-staging ED25519 key"
    staging_receipt_signer_details=$(ssh-keygen -lf \
        /etc/hook2stream/staging-receipt-allowed-signers -E sha256 2>/dev/null) \
        || fail "cannot fingerprint the staging receipt authority"
    staging_receipt_signer_fingerprint=$(printf '%s\n' "$staging_receipt_signer_details" | awk '{ print $2 }')
    for ssh_access_fingerprint in \
        "$operator_key_fingerprint" "$deploy_key_fingerprint" "$ssh_host_key_fingerprint"; do
        hook2stream_validate_distinct_ed25519_fingerprints \
            "$ssh_access_fingerprint" "$staging_receipt_signer_fingerprint" \
            || fail "SSH access and staging receipt authorities must use different ED25519 keys"
    done
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
require_trusted_directory "$host_root/config" 700 "app configuration directory"
require_encrypted_subpath "$host_root/config" "app configuration directory"

private_ports='2375 2376 3000 3128 5432 6432 8080 9000 9001'
tcp_listeners=$(ss -H -ltn) || fail "cannot inspect host TCP listeners"
for private_port in $private_ports; do
    if hook2stream_has_tcp_listener "$tcp_listeners" "$private_port"; then
        fail "private port $private_port must not have any host listener"
    fi
done
printf '%s\n' \
    "host validation: app $environment file-backed LUKS2, encrypted swap, Docker, app secrets, UFW, SSH, and Tailscale passed"

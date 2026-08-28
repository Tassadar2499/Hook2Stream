#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
bootstrap=$deployment_dir/scripts/bootstrap-encrypted-host.sh
host_dir=$deployment_dir/host

fail_test() {
    printf '%s\n' "encrypted host bootstrap contract: $*" >&2
    exit 1
}

[ -x "$bootstrap" ] || fail_test "bootstrap script is missing or not executable"
sh -n "$bootstrap" || fail_test "bootstrap script has invalid shell syntax"

for required_literal in \
    'hook2stream_host_profile "$role" "$environment"' \
    'backing_file=$hook2stream_profile_backing_file' \
    'mapper=$hook2stream_profile_mapper' \
    'mount_path=$hook2stream_profile_mount' \
    'hook2stream_validate_backing_metadata' \
    'cryptsetup isLuks --type luks2' \
    'refusing to format an existing LUKS volume' \
    'refusing to create or format it' \
    'cryptsetup luksFormat --type luks2 --verify-passphrase' \
    'cryptsetup open --type luks2' \
    'Type INITIALIZE %s %s GiB to continue:' \
    'require_docker_stopped' \
    'systemctl start srv-hook2stream.mount' \
    'systemctl start hook2stream-encrypted-swap.service'; do
    grep -Fq "$required_literal" "$bootstrap" \
        || fail_test "missing fail-closed operation: $required_literal"
done

if grep -Eq -- '--key-file|/etc/crypttab|/etc/fstab|systemctl[[:space:]]+enable|HOOK2STREAM_.*(KEY|PASSPHRASE)' "$bootstrap"; then
    fail_test "bootstrap script contains an automatic or stored-key unlock path"
fi

luks_format_line=$(grep -n 'cryptsetup luksFormat --type luks2 --verify-passphrase' "$bootstrap" | cut -d: -f1)
new_file_guard_line=$(grep -n '\[ ! -e "$backing_file" \]' "$bootstrap" | cut -d: -f1)
[ -n "$luks_format_line" ] && [ -n "$new_file_guard_line" ] \
    && [ "$new_file_guard_line" -lt "$luks_format_line" ] \
    || fail_test "LUKS formatting is not guarded by an absent-backing-file check"

mount_unit=$host_dir/srv-hook2stream.mount.example
swap_unit=$host_dir/hook2stream-encrypted-swap.service.example
docker_guard=$host_dir/docker-encrypted-mount.conf.example
docker_config=$host_dir/docker-daemon.json.example
for template in "$mount_unit" "$swap_unit" "$docker_guard" "$docker_config"; do
    [ -f "$template" ] && [ ! -L "$template" ] \
        || fail_test "missing trusted host template: $template"
done

grep -Fxq 'What=/dev/mapper/hook2stream-data' "$mount_unit" \
    || fail_test "mount unit uses the wrong mapper"
grep -Fxq 'Where=/srv/hook2stream' "$mount_unit" \
    || fail_test "mount unit uses the wrong mount"
grep -Fxq 'Type=ext4' "$mount_unit" || fail_test "mount unit is not ext4"
if grep -Fq '[Install]' "$mount_unit"; then
    fail_test "manual mount unit is enableable"
fi
grep -Fxq 'ExecStart=/sbin/swapon /srv/hook2stream/swap/hook2stream.swap' "$swap_unit" \
    || fail_test "swap unit does not use encrypted-mount swap"
grep -Fxq 'ConditionPathIsMountPoint=/srv/hook2stream' "$swap_unit" \
    || fail_test "swap unit is not mount guarded"
grep -Fxq 'RequiresMountsFor=/srv/hook2stream' "$docker_guard" \
    || fail_test "Docker is not mount guarded"
grep -Fq '"data-root": "/srv/hook2stream/docker"' "$docker_config" \
    || fail_test "Docker data-root is not encrypted"
for validator_gate in \
    '/etc/systemd/system/srv-hook2stream.mount' \
    '/etc/systemd/system/hook2stream-encrypted-swap.service' \
    '/etc/docker/daemon.json' \
    'systemctl is-active --quiet hook2stream-encrypted-swap.service'; do
    grep -Fq "$validator_gate" "$deployment_dir/scripts/validate-host.sh" \
        || fail_test "host validator omits installed bootstrap gate: $validator_gate"
done

readme=$host_dir/README.md
grep -Fq 'Every reboot' "$readme" || fail_test "manual reboot procedure is missing"
grep -Fq 'never accepts a passphrase in an argument' "$readme" \
    || fail_test "off-host key contract is not documented"
grep -Fq 'Do not enable the mount or swap unit' "$readme" \
    || fail_test "manual-unlock availability contract is not documented"

printf '%s\n' 'encrypted host bootstrap contract: ok'

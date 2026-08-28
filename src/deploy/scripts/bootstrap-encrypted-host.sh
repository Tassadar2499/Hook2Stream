#!/bin/sh
set -eu

# Manual, fail-closed interface for the Servers.Guru app-host encrypted volume.
# It intentionally has no key-file, crypttab, fstab, or automatic-unlock path.

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
deployment_dir=$(CDPATH= cd -- "$script_dir/.." && pwd)
. "$script_dir/lib/host-validation-common.sh"

fail() {
    printf '%s\n' "encrypted host bootstrap: $*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "$1 is required"
}

usage() {
    printf '%s\n' \
        "usage: bootstrap-encrypted-host.sh install-guards app staging|production" \
        "       bootstrap-encrypted-host.sh initialize app staging|production" \
        "       bootstrap-encrypted-host.sh unlock app staging|production" \
        "       bootstrap-encrypted-host.sh status app staging|production" >&2
    exit 2
}

[ "$#" -eq 3 ] || usage
action=$1
role=$2
environment=$3
case "$action" in install-guards|initialize|unlock|status) ;; *) usage ;; esac
hook2stream_host_profile "$role" "$environment" || usage
[ "$(id -u)" -eq 0 ] || fail "run through sudo"

backing_file=$hook2stream_profile_backing_file
mapper=$hook2stream_profile_mapper
mapper_path=/dev/mapper/$mapper
mount_path=$hook2stream_profile_mount
swap_path=$mount_path/swap/hook2stream.swap
expected_bytes=$((hook2stream_profile_minimum_gib * 1024 * 1024 * 1024))

for tool in blkid cryptsetup df findmnt install losetup lsblk stat systemctl; do
    require_command "$tool"
done

validate_backing_file() {
    [ -f "$backing_file" ] && [ ! -L "$backing_file" ] \
        || fail "$backing_file must be a regular non-symlink file"
    backing_metadata=$(stat -c '%u:%g:%a:%s:%b:%B' "$backing_file")
    hook2stream_validate_backing_metadata \
        "$backing_metadata" "$hook2stream_profile_minimum_gib" \
        || fail "$backing_file must be root:root 0600, fully allocated, and exactly ${hook2stream_profile_minimum_gib} GiB"
}

single_backing_loop() {
    backing_loops=$(losetup --noheadings --output NAME -j "$backing_file" \
        | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//' -e '/^$/d')
    loop_count=$(printf '%s\n' "$backing_loops" | awk 'NF { count++ } END { print count + 0 }')
    [ "$loop_count" -le 1 ] || fail "$backing_file is attached to more than one loop device"
    printf '%s\n' "$backing_loops"
}

attach_backing_loop() {
    loop_device=$(single_backing_loop)
    if [ -z "$loop_device" ]; then
        loop_device=$(losetup --find --show "$backing_file") \
            || fail "could not attach $backing_file to a loop device"
        attached_here=true
    else
        attached_here=false
    fi
    [ "$(lsblk -dn -o TYPE "$loop_device" | tr -d '[:space:]')" = loop ] \
        || fail "$loop_device is not a loop device"
}

verify_active_mapper() {
    luks_status=$(cryptsetup status "$mapper" 2>/dev/null) \
        || fail "$mapper_path is not an active LUKS mapping"
    active_loop=$(hook2stream_luks_loop_from_status "$luks_status") \
        || fail "$mapper_path is not a LUKS2 mapping over a loop device"
    active_backing=$(losetup --noheadings --output BACK-FILE "$active_loop" \
        | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')
    [ "$active_backing" = "$backing_file" ] \
        || fail "$mapper_path is backed by an unexpected file"
}

verify_mount() {
    [ "$(findmnt -n -o TARGET --target "$mount_path" 2>/dev/null || true)" = "$mount_path" ] \
        || return 1
    [ "$(findmnt -n -o SOURCE --target "$mount_path")" = "$mapper_path" ] \
        || fail "$mount_path is mounted from an unexpected source"
    [ "$(findmnt -n -o FSTYPE --target "$mount_path")" = ext4 ] \
        || fail "$mount_path is not ext4"
}

require_docker_stopped() {
    if systemctl is-active --quiet docker.service \
        || systemctl is-active --quiet docker.socket; then
        fail "stop docker.service and docker.socket before attaching or mounting the encrypted data-root"
    fi
}

install_exact_file() {
    source_file=$1
    target_file=$2
    target_mode=$3
    if [ -e "$target_file" ]; then
        [ -f "$target_file" ] && [ ! -L "$target_file" ] \
            || fail "$target_file exists but is not a regular file"
        cmp -s "$source_file" "$target_file" \
            || fail "$target_file already exists with different content; review it manually"
        [ "$(stat -c '%u:%g:%a' "$target_file")" = "0:0:$target_mode" ] \
            || fail "$target_file must be root:root mode 0$target_mode"
        return
    fi
    install -o root -g root -m "0$target_mode" "$source_file" "$target_file"
}

install_guards() {
    require_command cmp
    install -d -o root -g root -m 0755 /etc/systemd/system/docker.service.d
    install -d -o root -g root -m 0755 /etc/docker
    install_exact_file \
        "$deployment_dir/host/srv-hook2stream.mount.example" \
        /etc/systemd/system/srv-hook2stream.mount 644
    install_exact_file \
        "$deployment_dir/host/hook2stream-encrypted-swap.service.example" \
        /etc/systemd/system/hook2stream-encrypted-swap.service 644
    install_exact_file \
        "$deployment_dir/host/docker-encrypted-mount.conf.example" \
        /etc/systemd/system/docker.service.d/10-hook2stream-encrypted-mount.conf 644
    install_exact_file \
        "$deployment_dir/host/docker-daemon.json.example" \
        /etc/docker/daemon.json 644
    systemctl daemon-reload
}

create_runtime_layout() {
    verify_mount || fail "$mount_path is not the encrypted mount"
    [ "$(stat -c '%u:%g:%a' "$mount_path")" = 0:0:755 ] \
        || { chown root:root "$mount_path"; chmod 0755 "$mount_path"; }
    for private_dir in config releases release-state logs scratch; do
        install -d -o root -g root -m 0700 "$mount_path/$private_dir"
    done
    install -d -o root -g root -m 0711 "$mount_path/docker"
    install -d -o root -g root -m 0700 "$mount_path/swap"

    if [ ! -e "$swap_path" ]; then
        require_command fallocate
        require_command mkswap
        fallocate -l 4G "$swap_path"
        chown root:root "$swap_path"
        chmod 0600 "$swap_path"
        mkswap "$swap_path" >/dev/null
    else
        [ -f "$swap_path" ] && [ ! -L "$swap_path" ] \
            || fail "$swap_path exists but is not a regular non-symlink file"
        swap_metadata=$(stat -c '%u:%g:%a:%s:%b:%B' "$swap_path")
        hook2stream_validate_backing_metadata "$swap_metadata" 4 \
            || fail "$swap_path must be root:root 0600, fully allocated, and exactly 4 GiB"
        [ "$(blkid -p -s TYPE -o value "$swap_path" 2>/dev/null || true)" = swap ] \
            || fail "$swap_path exists but does not contain a swap signature"
    fi
    systemctl start hook2stream-encrypted-swap.service
}

unlock_existing() {
    require_docker_stopped
    validate_backing_file
    attach_backing_loop
    cryptsetup isLuks --type luks2 "$loop_device" >/dev/null 2>&1 \
        || fail "$backing_file does not contain a valid LUKS2 header; refusing to format it"
    if cryptsetup status "$mapper" >/dev/null 2>&1; then
        verify_active_mapper
    else
        printf '%s\n' "Unlocking $environment interactively; the passphrase is never stored by this script."
        cryptsetup open --type luks2 "$loop_device" "$mapper"
        verify_active_mapper
    fi
    filesystem_type=$(blkid -p -s TYPE -o value "$mapper_path" 2>/dev/null || true)
    [ "$filesystem_type" = ext4 ] \
        || fail "$mapper_path does not contain ext4; refusing to format an existing LUKS volume"
    install -d -o root -g root -m 0755 "$mount_path"
    if ! verify_mount; then
        systemctl start srv-hook2stream.mount
        verify_mount || fail "systemd did not mount $mount_path from $mapper_path"
    fi
    create_runtime_layout
}

initialize_new() {
    require_command fallocate
    require_command mkfs.ext4
    require_docker_stopped
    [ ! -e "$backing_file" ] \
        || fail "$backing_file already exists; refusing to create or format it (use unlock for a valid LUKS2 volume)"
    [ ! -e "$mapper_path" ] \
        || fail "$mapper_path already exists; refusing initialization"
    if [ "$(findmnt -n -o TARGET --target "$mount_path" 2>/dev/null || true)" = "$mount_path" ]; then
        fail "$mount_path is already a mount point; refusing initialization"
    fi
    [ -c /dev/tty ] || fail "an interactive terminal is required"

    filesystem_kib=$(df -Pk "$(dirname "$backing_file")" | awk 'NR == 2 { print $2 }')
    available_kib=$(df -Pk "$(dirname "$backing_file")" | awk 'NR == 2 { print $4 }')
    required_kib=$((expected_bytes / 1024))
    [ "$available_kib" -ge $((required_kib + filesystem_kib / 5)) ] \
        || fail "insufficient space to allocate ${hook2stream_profile_minimum_gib} GiB and retain 20 percent free"

    printf '%s\n' \
        "This will create and LUKS2-format a NEW ${hook2stream_profile_minimum_gib} GiB file:" \
        "  $backing_file" \
        "No passphrase or recovery key will be stored on the VPS, in Git, or in GitHub."
    printf 'Type INITIALIZE %s %s GiB to continue: ' \
        "$environment" "$hook2stream_profile_minimum_gib" >/dev/tty
    IFS= read -r confirmation </dev/tty
    [ "$confirmation" = "INITIALIZE $environment $hook2stream_profile_minimum_gib GiB" ] \
        || fail "confirmation did not match; nothing was created"

    old_umask=$(umask)
    umask 077
    fallocate -l "${hook2stream_profile_minimum_gib}G" "$backing_file"
    chown root:root "$backing_file"
    chmod 0600 "$backing_file"
    umask "$old_umask"
    validate_backing_file
    attach_backing_loop
    cryptsetup isLuks "$loop_device" >/dev/null 2>&1 \
        && fail "the newly created backing file unexpectedly contains a LUKS header"

    printf '%s\n' "Choose a unique $environment passphrase and escrow it outside this VPS."
    cryptsetup luksFormat --type luks2 --verify-passphrase "$loop_device"
    cryptsetup isLuks --type luks2 "$loop_device" >/dev/null 2>&1 \
        || fail "LUKS2 formatting did not produce a valid header"
    cryptsetup open --type luks2 "$loop_device" "$mapper"
    verify_active_mapper
    [ -z "$(blkid -p -s TYPE -o value "$mapper_path" 2>/dev/null || true)" ] \
        || fail "the new mapper unexpectedly contains a filesystem signature"
    mkfs.ext4 -L hook2stream-data "$mapper_path" >/dev/null
    install -d -o root -g root -m 0755 "$mount_path"
    systemctl start srv-hook2stream.mount
    verify_mount || fail "systemd did not mount the new encrypted filesystem"
    create_runtime_layout
}

show_status() {
    printf '%s\n' \
        "environment=$environment" \
        "backing_file=$backing_file" \
        "expected_gib=$hook2stream_profile_minimum_gib" \
        "mapper=$mapper_path" \
        "mount=$mount_path"
    if [ -e "$backing_file" ]; then
        validate_backing_file
        if loop_device=$(single_backing_loop) && [ -n "$loop_device" ]; then
            printf '%s\n' "loop=$loop_device"
        else
            printf '%s\n' 'loop=detached'
        fi
    else
        printf '%s\n' 'backing=absent'
    fi
    if cryptsetup status "$mapper" >/dev/null 2>&1; then
        verify_active_mapper
        printf '%s\n' 'luks=unlocked'
    else
        printf '%s\n' 'luks=locked'
    fi
    if verify_mount; then
        printf '%s\n' 'mounted=yes'
    else
        printf '%s\n' 'mounted=no'
    fi
}

case "$action" in
    install-guards)
        install_guards
        ;;
    initialize)
        install_guards
        initialize_new
        printf '%s\n' "Encrypted $environment host storage initialized. Docker remains stopped until explicitly started."
        ;;
    unlock)
        install_guards
        unlock_existing
        printf '%s\n' "Encrypted $environment host storage is mounted and swap is active. Start Docker explicitly after validation."
        ;;
    status)
        show_status
        ;;
esac

#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
. "$deployment_dir/scripts/lib/host-validation-common.sh"

fail_test() {
    printf '%s\n' "host profile test: $*" >&2
    exit 1
}

assert_profile() {
    expected_role=$1
    expected_environment=$2
    expected_gib=$3
    expected_file=$4
    expected_mapper=$5
    expected_mount=$6
    hook2stream_host_profile "$expected_role" "$expected_environment" \
        || fail_test "$expected_role/$expected_environment was rejected"
    [ "$hook2stream_profile_minimum_gib" = "$expected_gib" ] \
        || fail_test "$expected_role/$expected_environment minimum changed"
    [ "$hook2stream_profile_backing_file" = "$expected_file" ] \
        || fail_test "$expected_role backing path changed"
    [ "$hook2stream_profile_mapper" = "$expected_mapper" ] \
        || fail_test "$expected_role mapper changed"
    [ "$hook2stream_profile_mount" = "$expected_mount" ] \
        || fail_test "$expected_role mount changed"
}

assert_profile app staging 112 /var/lib/hook2stream-data.luks hook2stream-data /srv/hook2stream
assert_profile app production 176 /var/lib/hook2stream-data.luks hook2stream-data /srv/hook2stream
assert_profile storage staging 64 /var/lib/hook2stream-storage.luks hook2stream-storage /srv/hook2stream-storage
assert_profile storage production 256 /var/lib/hook2stream-storage.luks hook2stream-storage /srv/hook2stream-storage

if hook2stream_host_profile database production; then
    fail_test "unknown role was accepted"
fi

one_gib=$((1024 * 1024 * 1024))
size_112=$((112 * one_gib))
blocks_112=$((size_112 / 512))
hook2stream_validate_backing_metadata "0:0:600:${size_112}:${blocks_112}:512" 112 \
    || fail_test "fully allocated 112 GiB root backing file was rejected"
if hook2stream_validate_backing_metadata "0:0:600:${size_112}:1:512" 112; then
    fail_test "sparse backing file was accepted"
fi
if hook2stream_validate_backing_metadata \
    "0:0:600:$((size_112 + one_gib)):$(((size_112 + one_gib) / 512)):512" 112; then
    fail_test "oversized backing file was accepted for the exact host profile"
fi
if hook2stream_validate_backing_metadata "1000:0:600:${size_112}:${blocks_112}:512" 112; then
    fail_test "non-root backing file was accepted"
fi
if hook2stream_validate_backing_metadata "0:0:640:${size_112}:${blocks_112}:512" 112; then
    fail_test "weak backing-file mode was accepted"
fi

luks_status='hook2stream-data is active and is in use.
  type:    LUKS2
  cipher:  aes-xts-plain64
  device:  /dev/loop7'
[ "$(hook2stream_luks_loop_from_status "$luks_status")" = /dev/loop7 ] \
    || fail_test "valid LUKS2 loop chain was rejected"
if hook2stream_luks_loop_from_status "$(printf '%s\n' "$luks_status" | sed s/LUKS2/PLAIN/)" >/dev/null; then
    fail_test "non-LUKS2 mapping was accepted"
fi
if hook2stream_luks_loop_from_status "$(printf '%s\n' "$luks_status" | sed s#/dev/loop7#/dev/sdb#)" >/dev/null; then
    fail_test "non-loop LUKS backing device was accepted"
fi

hook2stream_validate_proc_options 'rw,nosuid,nodev,noexec,relatime,hidepid=2' \
    || fail_test "hidepid=2 procfs was rejected"
hook2stream_validate_proc_options 'rw,nosuid,nodev,noexec,relatime,hidepid=invisible' \
    || fail_test "hidepid=invisible procfs was rejected"
if hook2stream_validate_proc_options 'rw,nosuid,nodev,noexec,relatime'; then
    fail_test "world-readable process metadata was accepted"
fi
if hook2stream_validate_proc_options 'rw,nosuid,nodev,noexec,relatime,hidepid=2,gid=2000'; then
    fail_test "procfs hidepid bypass group was accepted"
fi

listener_fixture='LISTEN 0 4096 0.0.0.0:9000 0.0.0.0:*
LISTEN 0 4096 203.0.113.10:9001 0.0.0.0:*
LISTEN 0 4096 [fd7a:115c:a1e0::1]:5432 [::]:*
LISTEN 0 4096 100.64.0.8:6432 0.0.0.0:*
LISTEN 0 4096 127.0.0.1:8080 0.0.0.0:*'
for forbidden_port in 9000 9001 5432 6432 8080; do
    hook2stream_has_tcp_listener "$listener_fixture" "$forbidden_port" \
        || fail_test "listener on private port $forbidden_port was missed"
done
if hook2stream_has_tcp_listener "$listener_fixture" 443; then
    fail_test "unrelated public web listener was classified as private"
fi
hook2stream_validate_storage_https_listeners \
    'LISTEN 0 4096 100.64.0.8:443 0.0.0.0:*' 100.64.0.8 \
    || fail_test "exact Tailscale HTTPS listener was rejected"
for bad_https_fixture in \
    'LISTEN 0 4096 0.0.0.0:443 0.0.0.0:*' \
    'LISTEN 0 4096 203.0.113.10:443 0.0.0.0:*' \
    'LISTEN 0 4096 [::]:443 [::]:*' \
    'LISTEN 0 4096 100.64.0.9:443 0.0.0.0:*'; do
    if hook2stream_validate_storage_https_listeners "$bad_https_fixture" 100.64.0.8; then
        fail_test "non-Tailscale storage HTTPS listener was accepted"
    fi
done
hook2stream_validate_storage_https_listeners '' 100.64.0.8 \
    || fail_test "pre-deployment host without storage HTTPS was rejected"

app_docker_bindings='hook2stream-staging caddy 80/tcp 0.0.0.0 80
hook2stream-staging caddy 443/tcp 0.0.0.0 443
hook2stream-staging caddy 443/udp 0.0.0.0 443'
hook2stream_validate_docker_bindings app staging 100.64.0.8 "$app_docker_bindings" \
    || fail_test "exact app Caddy bindings were rejected"
storage_docker_bindings='hook2stream-storage-staging caddy 443/tcp 100.64.0.8 443'
hook2stream_validate_docker_bindings storage staging 100.64.0.8 "$storage_docker_bindings" \
    || fail_test "exact storage Caddy binding was rejected"
hook2stream_validate_docker_bindings storage staging 100.64.0.8 '' \
    || fail_test "pre-deployment storage host was rejected by the Docker binding gate"
for bad_storage_docker_bindings in \
    'manual minio 9000/tcp 0.0.0.0 9000' \
    'hook2stream-storage-staging minio 9000/tcp 100.64.0.8 9000' \
    'hook2stream-storage-staging caddy 443/tcp 0.0.0.0 443' \
    'hook2stream-storage-staging caddy 443/udp 100.64.0.8 443'; do
    if hook2stream_validate_docker_bindings \
        storage staging 100.64.0.8 "$bad_storage_docker_bindings"; then
        fail_test "unsafe storage Docker binding was accepted: $bad_storage_docker_bindings"
    fi
done
if hook2stream_validate_docker_bindings app staging 100.64.0.8 \
    'manual postgres 5432/tcp 0.0.0.0 5432'; then
    fail_test "unsafe app Docker binding was accepted"
fi

hook2stream_subpath_mount_matches \
    /dev/mapper/hook2stream-data /srv/hook2stream \
    /dev/mapper/hook2stream-data /srv/hook2stream \
    || fail_test "same encrypted filesystem subpath was rejected"
if hook2stream_subpath_mount_matches \
    /dev/mapper/hook2stream-data /srv/hook2stream \
    /dev/vda1 /srv/hook2stream/docker; then
    fail_test "nested unencrypted Docker bind mount was accepted"
fi

hook2stream_service_identity_matches \
    'hook2stream-storage-caddy:x:10002:10002::/nonexistent:/usr/sbin/nologin' \
    hook2stream-storage-caddy 10002 10002 \
    || fail_test "valid non-login storage identity was rejected"
if hook2stream_service_identity_matches \
    'operator:x:1000:1000::/home/operator:/bin/bash' \
    hook2stream-storage-caddy 10002 10002; then
    fail_test "operator identity collision was accepted"
fi
hook2stream_gid_list_contains '1000 27 2000' 2000 \
    || fail_test "supplemental secrets-group membership was missed"
if hook2stream_gid_list_contains '1000 27 998' 2000; then
    fail_test "unrelated supplemental group was classified as secrets access"
fi
hook2stream_gid_list_is_exact '10001' 10001 \
    || fail_test "exact dedicated service group was rejected"
if hook2stream_gid_list_is_exact '10001 998' 10001; then
    fail_test "dedicated service account supplementary group was accepted"
fi
if hook2stream_subpath_mount_matches \
    /dev/mapper/hook2stream-storage /srv/hook2stream-storage \
    /dev/mapper/hook2stream-storage /srv/hook2stream-storage/minio-data; then
    fail_test "nested MinIO bind mount was accepted despite a matching source"
fi

app_secrets=$(hook2stream_required_secret_files app)
printf '%s\n' "$app_secrets" | grep -qx postgres_password \
    || fail_test "app database secret is missing"
printf '%s\n' "$app_secrets" | grep -qx media_keyring \
    || fail_test "app media keyring is missing"
if printf '%s\n' "$app_secrets" | grep -qx backup_heartbeat_url; then
    fail_test "removed app backup heartbeat secret is still required"
fi
if printf '%s\n' "$app_secrets" | grep -qx minio_root_password; then
    fail_test "app host unexpectedly requires MinIO root credentials"
fi
storage_secrets=$(hook2stream_required_secret_files storage)
printf '%s\n' "$storage_secrets" | grep -qx minio_root_password \
    || fail_test "storage MinIO root secret is missing"
printf '%s\n' "$storage_secrets" | grep -qx storage-tls.crt \
    || fail_test "storage TLS certificate is missing"
printf '%s\n' "$storage_secrets" | grep -qx storage-tls.key \
    || fail_test "storage TLS private key is missing"
if printf '%s\n' "$storage_secrets" | grep -qx storage_heartbeat_url; then
    fail_test "removed storage heartbeat secret is still required"
fi
if printf '%s\n' "$storage_secrets" | grep -qx postgres_password; then
    fail_test "storage host unexpectedly requires the application database secret"
fi

grep -Fq 'losetup --noheadings --output BACK-FILE' "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "mount-to-loop backing chain validation is missing"
grep -Fq 'cryptsetup isLuks --type luks2' "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "explicit LUKS2 header validation is missing"
grep -Fq 'SECRETS_DIR must be below the encrypted role mount' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "role secrets are not pinned to the encrypted mount"
grep -Fq '/proc must use hidepid=2 or hidepid=invisible' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "process metadata protection is not enforced"
grep -Fq 'private port $private_port must not have any host listener' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "concrete-address private listeners are not rejected"
grep -Fq 'hook2stream_validate_docker_bindings' "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "Docker DNAT port bindings are not validated"
grep -Fq 'minio-security-policy.json' "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "root-owned current MinIO security policy is not validated"
grep -Fq 'hook2stream_subpath_mount_matches' "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "nested unencrypted mounts are not rejected"
grep -Fq 'hook2stream-${environment}_${volume_name}' "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "environment-specific Docker volumes are not checked"
grep -Fq 'require_trusted_directory "$encrypted_runtime_dir" 700' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "release and release-state directories are not root-private"
for trusted_host_gate in \
    /usr/local/sbin/hook2stream-deploy-launcher \
    /usr/local/sbin/hook2stream-storage-deploy-launcher \
    /usr/local/libexec/hook2stream/lib/forced-command-trust.sh \
    /usr/local/libexec/hook2stream-storage/lib/storage-common.sh; do
    grep -Fq "$trusted_host_gate" "$deployment_dir/scripts/validate-host.sh" \
        || fail_test "host validator omitted installed gate $trusted_host_gate"
done
grep -Fq '/etc/hook2stream/staging-receipt-allowed-signers' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "production app signer trust file is not host-validated"
app_mount_guard=$deployment_dir/host/docker-encrypted-mount.conf.example
grep -Fxq 'RequiresMountsFor=/srv/hook2stream' "$app_mount_guard" \
    || fail_test "app Docker mount dependency is missing"
grep -Fxq 'ConditionPathIsMountPoint=/srv/hook2stream' "$app_mount_guard" \
    || fail_test "app Docker mount condition is missing"

printf '%s\n' \
    "host profile test: all four profiles, full allocation, LUKS2 loop chain, and role secrets passed"

#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
. "$deployment_dir/scripts/lib/host-validation-common.sh"

fail_test() {
    printf '%s\n' "host profile test: $*" >&2
    exit 1
}

test_scratch=$(mktemp -d)
cleanup_test_scratch() { rm -rf "$test_scratch"; }
trap cleanup_test_scratch EXIT HUP INT TERM

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

assert_profile app staging 48 /var/lib/hook2stream-data.luks hook2stream-data /srv/hook2stream
assert_profile app production 64 /var/lib/hook2stream-data.luks hook2stream-data /srv/hook2stream

for removed_profile in 'storage staging' 'storage production' 'database production'; do
    set -- $removed_profile
    if hook2stream_host_profile "$1" "$2"; then
        fail_test "$1/$2 was accepted"
    fi
done

one_gib=$((1024 * 1024 * 1024))
size_48=$((48 * one_gib))
blocks_48=$((size_48 / 512))
hook2stream_validate_backing_metadata "0:0:600:${size_48}:${blocks_48}:512" 48 \
    || fail_test "fully allocated 48 GiB root backing file was rejected"
if hook2stream_validate_backing_metadata "0:0:600:${size_48}:1:512" 48; then
    fail_test "sparse backing file was accepted"
fi
if hook2stream_validate_backing_metadata \
    "0:0:600:$((size_48 + one_gib)):$(((size_48 + one_gib) / 512)):512" 48; then
    fail_test "oversized backing file was accepted for the exact host profile"
fi
if hook2stream_validate_backing_metadata "1000:0:600:${size_48}:${blocks_48}:512" 48; then
    fail_test "non-root backing file was accepted"
fi
if hook2stream_validate_backing_metadata "0:0:640:${size_48}:${blocks_48}:512" 48; then
    fail_test "weak backing-file mode was accepted"
fi
size_64=$((64 * one_gib))
blocks_64=$((size_64 / 512))
hook2stream_validate_backing_metadata "0:0:600:${size_64}:${blocks_64}:512" 64 \
    || fail_test "fully allocated 64 GiB root backing file was rejected"
if hook2stream_validate_backing_metadata "0:0:600:${size_48}:${blocks_48}:512" 64; then
    fail_test "undersized staging backing file was accepted for production"
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

for locked_password_status in L LK; do
    hook2stream_validate_locked_password_status "$locked_password_status" \
        || fail_test "locked account status $locked_password_status was rejected"
done
for active_password_status in P NP ''; do
    if hook2stream_validate_locked_password_status "$active_password_status"; then
        fail_test "non-locked service account status was accepted: ${active_password_status:-empty}"
    fi
done
hook2stream_validate_root_password_status P \
    || fail_test "active root password status was rejected"
for invalid_root_password_status in L LK NP ''; do
    if hook2stream_validate_root_password_status "$invalid_root_password_status"; then
        fail_test "inactive root password status was accepted: ${invalid_root_password_status:-empty}"
    fi
done

sshd_root_password_key_users='pubkeyauthentication yes
passwordauthentication yes
kbdinteractiveauthentication no
authenticationmethods any
hostbasedauthentication no
gssapiauthentication no
kerberosauthentication no
permitemptypasswords no
permitrootlogin yes
allowusers root hook2stream-operator hook2stream-deploy
authorizedkeysfile .ssh/authorized_keys
authorizedkeyscommand none
authorizedkeyscommanduser none
trustedusercakeys none
strictmodes yes
permituserenvironment no
permituserrc no
forcecommand none
disableforwarding yes
hostkey /etc/ssh/ssh_host_ed25519_key
acceptenv LANG
acceptenv LC_*'
sshd_policy_template=$deployment_dir/host/sshd-no-public-ssh.conf.example
[ -f "$sshd_policy_template" ] && [ ! -L "$sshd_policy_template" ] \
    || fail_test "reviewed SSH policy template is missing"
[ "$(cat "$sshd_policy_template")" = "$sshd_root_password_key_users" ] \
    || fail_test "reviewed SSH policy template differs from the validator's exact effective policy"
hook2stream_validate_sshd_effective "$(cat "$sshd_policy_template")" \
    || fail_test "reviewed SSH policy template does not satisfy the effective-policy validator"
hook2stream_validate_sshd_effective "$sshd_root_password_key_users" \
    || fail_test "exact root-password/key-user SSH policy was rejected"
hook2stream_validate_sshd_root_effective "$sshd_root_password_key_users" \
    || fail_test "exact root effective SSH policy was rejected"
sshd_split_allowusers=$(printf '%s\n' "$sshd_root_password_key_users" | sed \
    's/^allowusers root hook2stream-operator hook2stream-deploy$/allowusers root\
allowusers hook2stream-operator\
allowusers hook2stream-deploy/')
hook2stream_validate_sshd_effective "$sshd_split_allowusers" \
    || fail_test "OpenSSH split AllowUsers effective output was rejected"
hook2stream_validate_sshd_root_effective "$sshd_split_allowusers" \
    || fail_test "OpenSSH split AllowUsers root effective output was rejected"
for unsafe_sshd_replacement in \
    'pubkeyauthentication yes|pubkeyauthentication no' \
    'passwordauthentication yes|passwordauthentication no' \
    'authenticationmethods any|authenticationmethods publickey' \
    'hostbasedauthentication no|hostbasedauthentication yes' \
    'gssapiauthentication no|gssapiauthentication yes' \
    'kerberosauthentication no|kerberosauthentication yes' \
    'permitemptypasswords no|permitemptypasswords yes' \
    'authorizedkeysfile .ssh/authorized_keys|authorizedkeysfile .ssh/authorized_keys2' \
    'authorizedkeyscommand none|authorizedkeyscommand /usr/local/bin/lookup-key' \
    'trustedusercakeys none|trustedusercakeys /etc/ssh/trusted-user-ca.pub' \
    'strictmodes yes|strictmodes no' \
    'permituserenvironment no|permituserenvironment yes' \
    'permituserrc no|permituserrc yes' \
    'forcecommand none|forcecommand /bin/sh' \
    'disableforwarding yes|disableforwarding no' \
    'permitrootlogin yes|permitrootlogin prohibit-password' \
    'allowusers root hook2stream-operator hook2stream-deploy|allowusers hook2stream-operator hook2stream-deploy'; do
    safe_directive=${unsafe_sshd_replacement%%|*}
    unsafe_directive=${unsafe_sshd_replacement#*|}
    unsafe_sshd=$(printf '%s\n' "$sshd_root_password_key_users" | sed "s#^${safe_directive}\$#${unsafe_directive}#")
    if hook2stream_validate_sshd_effective "$unsafe_sshd"; then
        fail_test "unsafe SSH directive was accepted: $unsafe_directive"
    fi
done
if hook2stream_validate_sshd_effective "$sshd_root_password_key_users
acceptenv PATH"; then
    fail_test "SSH AcceptEnv PATH injection was accepted"
fi
if hook2stream_validate_sshd_effective "$sshd_root_password_key_users
setenv PATH=/tmp"; then
    fail_test "SSH SetEnv PATH injection was accepted"
fi
if hook2stream_validate_sshd_effective "$sshd_root_password_key_users
hostbasedauthentication yes"; then
    fail_test "duplicate conflicting SSH authentication directives were accepted"
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
app_docker_bindings='hook2stream-staging caddy 80/tcp 0.0.0.0 80
hook2stream-staging caddy 443/tcp 0.0.0.0 443
hook2stream-staging caddy 443/udp 0.0.0.0 443'
hook2stream_validate_docker_bindings app staging 100.64.0.8 "$app_docker_bindings" \
    || fail_test "exact app Caddy bindings were rejected"
if hook2stream_validate_docker_bindings storage staging 100.64.0.8 ''; then
    fail_test "removed storage Docker profile was accepted"
fi
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

hook2stream_gid_list_contains '1000 27 2000' 2000 \
    || fail_test "supplemental secrets-group membership was missed"
if hook2stream_gid_list_contains '1000 27 998' 2000; then
    fail_test "unrelated supplemental group was classified as secrets access"
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
if printf '%s\n' "$app_secrets" | grep -Eq '^s3_bootstrap_(access_key|secret_key)$'; then
    fail_test "app host unexpectedly requires operator-only bootstrap credentials"
fi
if hook2stream_required_secret_files storage >/dev/null; then
    fail_test "removed storage host secret profile was accepted"
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
if grep -Eq 'hook2stream-storage|MinIO security policy|role" = storage' \
    "$deployment_dir/scripts/validate-host.sh"; then
    fail_test "removed remote-storage host validation is still installed"
fi
grep -Fq 'require_minimum_free_percent / "root filesystem"' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "root filesystem free-space gate is missing"
grep -Fq 'require_minimum_free_percent "$host_root" "encrypted filesystem"' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "encrypted filesystem free-space gate is missing"
grep -Fq 'every active swap must be a file below the encrypted role mount' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "active swap is not restricted to a file inside the provider host LUKS mount"
grep -Fq '$4 != "nosuid,nodev,noexec,hidepid=2"' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "host validator does not require the canonical persistent proc hidepid options"
grep -Fq 'exactly one canonical persistent hidepid=2 proc mount' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "host validator does not reject missing or duplicate proc fstab entries"
[ "$(cat "$deployment_dir/host/proc-hidepid.fstab.example")" = \
    'proc /proc proc nosuid,nodev,noexec,hidepid=2 0 0' ] \
    || fail_test "canonical proc hidepid fstab template is missing or malformed"
grep -Fq '[ "${swap_total_kib:-0}" -ge 4194304 ]' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "the provider host validator accepts less than 4 GiB of active swap"
grep -Fq 'swap_paths=$(swapon --noheadings --show=NAME)' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "swapon enumeration failures are not captured fail-closed"
grep -Fq '|| fail "cannot enumerate active swap"' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "swapon enumeration failure does not stop host validation"
if grep -Fq 'swap_mapper=' "$deployment_dir/scripts/validate-host.sh"; then
    fail_test "a separate dm-crypt swap outside the provider host LUKS mount is still accepted"
fi
grep -Fq 'hook2stream_subpath_mount_matches' "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "nested unencrypted mounts are not rejected"
grep -Fq 'hook2stream-${environment}_${volume_name}' "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "environment-specific Docker volumes are not checked"
grep -Fq 'require_trusted_directory "$encrypted_runtime_dir" 700' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "release and release-state directories are not root-private"
for trusted_host_gate in \
    /usr/local/sbin/hook2stream-deploy-launcher \
    /usr/local/libexec/hook2stream/lib/forced-command-trust.sh \
    /usr/local/libexec/hook2stream/authenticated-e2e.sh; do
    grep -Fq "$trusted_host_gate" "$deployment_dir/scripts/validate-host.sh" \
        || fail_test "host validator omitted installed gate $trusted_host_gate"
done
for exact_access_gate in \
    'HOOK2STREAM_OPERATOR_PUBLIC_KEY_SHA256' \
    'HOOK2STREAM_DEPLOY_PUBLIC_KEY_SHA256' \
    'hook2stream_validate_exact_authorized_key' \
    'hook2stream_validate_exact_deploy_sudoers' \
    'hook2stream_validate_effective_deploy_sudoers' \
    'tailscale get --json ssh' \
    'hook2stream_no_extended_acl' \
    '/var/run/docker.sock' \
    '/etc/sudoers.d/hook2stream-deploy'; do
    grep -Fq "$exact_access_gate" "$deployment_dir/scripts/validate-host.sh" \
        || fail_test "host validator omitted exact access gate $exact_access_gate"
done
grep -Fq 'local password must remain locked' "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "host validator does not require locked SSH account passwords"
grep -Fq 'root must be the only SSH account with an active local password' "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "host validator does not require an active password only for root"
grep -Fq 'must not belong to the sudo group' "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "deploy account can still inherit broad sudo access"
grep -Fq "private_ports='2375 2376" "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "unauthenticated Docker TCP endpoints are not rejected"
grep -Fq 'tailscale timeout ufw' "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "host validator does not require the sustained-soak timeout runtime"
grep -Fq '/etc/hook2stream/staging-receipt-allowed-signers' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "production app signer trust file is not host-validated"
if grep -Eq 'provider-window|provider-lifecycle|HOOK2STREAM_STAGING_HOST_CA|host_ed25519_key-cert' \
    "$deployment_dir/scripts/validate-host.sh" \
    "$deployment_dir/host/deploy.conf.example"; then
    fail_test "permanent Servers.Guru hosts still require ephemeral provider or host-certificate state"
fi
grep -Fq 'hook2stream_validate_sshd_effective "$sshd_effective"' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "host SSH access is not restricted to the exact root-password/key-user policy"
grep -Fq 'hook2stream_validate_sshd_config_tree' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "host SSH config can still add Match or nested Include trust paths"
grep -Fq 'sshd -T -C' "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "host validator does not resolve per-user effective SSH policy"
grep -Fq 'hook2stream_validate_sshd_root_effective "$sshd_root_effective"' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "host validator does not resolve and validate root effective SSH policy"
for host_key_gate in \
    'require_trusted_file "$ssh_host_private_key" 600' \
    'require_trusted_file "$ssh_host_public_key" 644' \
    'SSH host public key does not match its private ED25519 key' \
    'SSH user and host identities must use different ED25519 keys'; do
    grep -Fq "$host_key_gate" "$deployment_dir/scripts/validate-host.sh" \
        || fail_test "host validator omitted private host-key gate: $host_key_gate"
done
app_mount_guard=$deployment_dir/host/docker-encrypted-mount.conf.example
grep -Fxq 'RequiresMountsFor=/srv/hook2stream' "$app_mount_guard" \
    || fail_test "app Docker mount dependency is missing"
grep -Fxq 'ConditionPathIsMountPoint=/srv/hook2stream' "$app_mount_guard" \
    || fail_test "app Docker mount condition is missing"
grep -Fq '/etc/systemd/system/docker.service.d/10-hook2stream-encrypted-mount.conf' \
    "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "host validator does not verify the installed Docker mount guard"
grep -Fq 'systemctl cat docker.service' "$deployment_dir/scripts/validate-host.sh" \
    || fail_test "host validator does not verify the loaded Docker mount guard"
staging_env=$deployment_dir/environments/staging.env.example
production_env=$deployment_dir/environments/production.env.example
grep -Fxq 'HOOK2STREAM_MIN_VOLUME_GIB=48' "$staging_env" \
    || fail_test "Servers.Guru staging environment does not require exactly 48 GiB"
grep -Fxq 'HOOK2STREAM_MIN_VOLUME_GIB=64' "$production_env" \
    || fail_test "Servers.Guru production environment does not require exactly 64 GiB"
grep -Fq 'Servers.Guru `MTL1-3` staging VPS has 4 shared vCPU, 8 GiB RAM' "$staging_env" \
    || fail_test "staging environment is not documented as an 8 GiB Servers.Guru MTL1-3 VPS"
grep -Fq 'Servers.Guru `NL1-4` production VPS has 6 shared vCPU, 8 GiB RAM' "$production_env" \
    || fail_test "production environment is not documented as an 8 GiB Servers.Guru NL1-4 VPS"
if grep -Eiq 'Cherry|Cloud VDS' "$staging_env" "$production_env"; then
    fail_test "retired Cherry host wording remains in runtime environments"
fi

printf '%s\n' \
    "host profile test: two Servers.Guru app profiles, full allocation, LUKS2 loop chain, and app secrets passed"

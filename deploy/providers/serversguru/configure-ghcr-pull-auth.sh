#!/bin/sh
set -eu
set -f
umask 077
PATH=/usr/sbin:/usr/bin:/sbin:/bin
export PATH
unset CDPATH ENV BASH_ENV DOCKER_CONFIG

fail() {
    printf '%s\n' "configure GHCR pull auth: $*" >&2
    exit 1
}

[ "$#" -eq 3 ] \
    || fail "usage: configure-ghcr-pull-auth.sh staging|production GITHUB_USERNAME 32_HEX_ID"
environment=$1
username=$2
identity_suffix=$3
case "$environment" in staging|production) ;; *) fail "environment must be staging or production" ;; esac
printf '%s\n' "$username" \
    | grep -Eq '^[A-Za-z0-9]([A-Za-z0-9-]{0,37}[A-Za-z0-9])?$' \
    || fail "GitHub username is invalid"
printf '%s\n' "$identity_suffix" | grep -Eq '^[0-9a-f]{32}$' \
    || fail "credential identity suffix must be 32 lowercase hex characters"
credential_identity=hook2stream-$environment-$identity_suffix
[ "$(id -u)" -eq 0 ] || fail "run through sudo"
for required_command in docker find findmnt flock getfacl jq sha256sum; do
    command -v "$required_command" >/dev/null 2>&1 \
        || fail "$required_command is required"
done

no_extended_acl() {
    [ "$#" -eq 1 ] || return 1
    LC_ALL=C getfacl -cp -- "$1" 2>/dev/null | awk '
      /^$/ { next }
      /^user::[rwx-][rwx-][rwx-]$/ { users++; next }
      /^group::[rwx-][rwx-][rwx-]$/ { groups++; next }
      /^other::[rwx-][rwx-][rwx-]$/ { others++; next }
      { invalid = 1 }
      END { exit (users == 1 && groups == 1 && others == 1 && !invalid) ? 0 : 1 }
    '
}
for trusted_parent in \
    /usr/local \
    /usr/local/libexec \
    /usr/local/libexec/hook2stream \
    /usr/local/libexec/hook2stream/lib; do
    [ -d "$trusted_parent" ] && [ ! -L "$trusted_parent" ] \
        && [ "$(stat -c '%u:%g:%a' "$trusted_parent")" = 0:0:755 ] \
        && no_extended_acl "$trusted_parent" \
        || fail "untrusted installed helper parent: $trusted_parent"
done
trust_library=/usr/local/libexec/hook2stream/lib/forced-command-trust.sh
[ -f "$trust_library" ] && [ ! -L "$trust_library" ] \
    && [ "$(stat -c '%u:%g:%a' "$trust_library")" = 0:0:555 ] \
    && no_extended_acl "$trust_library" \
    || fail "installed trust helper must be root:root mode 0555 without ACLs"
. "$trust_library"

mount_root=/srv/hook2stream
auth_dir=$mount_root/registry-auth
state_dir=$mount_root/release-state
lock_file=$state_dir/forced-command.lock
environment_file=$mount_root/config/$environment.env
hook2stream_trusted_directory "$mount_root" 0:0 755 \
    || fail "$mount_root must be the unlocked root-owned encrypted mount"
[ "$(findmnt -n -o SOURCE --target "$mount_root")" = /dev/mapper/hook2stream-data ] \
    || fail "$mount_root is not mounted from the Hook2Stream LUKS mapper"
hook2stream_trusted_directory "$state_dir" 0:0 700 \
    || fail "$state_dir must be root:root mode 0700"
if [ -e "$environment_file" ] || [ -L "$environment_file" ]; then
    hook2stream_trusted_file "$environment_file" 0:0 600 \
        || fail "$environment_file must be root:root mode 0600"
    configured_environment=$(awk -F= '
      $1 == "DEPLOYMENT_ENVIRONMENT" { count++; value=substr($0,index($0,"=")+1) }
      END { if (count != 1) exit 1; print value }
    ' "$environment_file") || fail "deployment environment is missing or duplicated"
    [ "$configured_environment" = "$environment" ] \
        || fail "requested environment differs from the host configuration"
fi

[ ! -L "$lock_file" ] || fail "$lock_file must not be a symlink"
if [ ! -e "$lock_file" ]; then
    : > "$lock_file"
    chown root:root "$lock_file"
    chmod 0600 "$lock_file"
fi
hook2stream_trusted_file "$lock_file" 0:0 600 \
    || fail "$lock_file must be root:root mode 0600"
exec 9<>"$lock_file"
flock -n 9 || fail "a deployment, rollback, or credential rotation is already running"

if [ -e "$auth_dir" ] || [ -L "$auth_dir" ]; then
    hook2stream_trusted_directory "$auth_dir" 0:0 700 \
        || fail "$auth_dir must be a root-owned mode 0700 non-symlink directory"
else
    install -d -o root -g root -m 0700 "$auth_dir"
fi
hook2stream_remove_stale_ghcr_auth_temporaries "$auth_dir" 0:0 \
    || fail "$auth_dir contains an unsafe interrupted-rotation temporary"
[ "$(find "$auth_dir" -mindepth 1 -maxdepth 1 \
    ! -name config.json ! -name identity.attestation -print -quit)" = "" ] \
    || fail "$auth_dir contains an unexpected file"
temporary_dir=$(mktemp -d "$mount_root/.registry-auth.XXXXXX")
pending_config=
pending_identity=
cleanup() {
    stty echo </dev/tty 2>/dev/null || true
    unset token
    [ -z "$pending_config" ] || rm -f -- "$pending_config"
    [ -z "$pending_identity" ] || rm -f -- "$pending_identity"
    rm -rf "$temporary_dir"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

printf '%s\n' \
    'GitHub does not expose PAT scopes to this installer.' \
    'By continuing, the operator attests this is an environment-exclusive credential with only read:packages.' >/dev/tty
printf 'Enter that environment-specific GitHub PAT classic: ' >/dev/tty
stty -echo </dev/tty
IFS= read -r token </dev/tty
stty echo </dev/tty
printf '\n' >/dev/tty
[ -n "$token" ] || fail "token must not be empty"
case "$token" in *[!A-Za-z0-9_]* ) fail "token contains an unexpected character" ;; esac
if ! printf '%s\n' "$token" \
    | docker --config "$temporary_dir" login ghcr.io \
        --username "$username" --password-stdin >/dev/null 2>&1; then
    fail "GHCR rejected the supplied credential"
fi
unset token

temporary_config=$temporary_dir/config.json
[ -f "$temporary_config" ] && [ ! -L "$temporary_config" ] \
    || fail "docker login did not create a regular config.json"
chown root:root "$temporary_config"
chmod 0600 "$temporary_config"
auth_sha256=$(jq -jr '.auths["ghcr.io"].auth // empty' "$temporary_config" \
    | sha256sum | awk '{ print $1 }')
# The temporary login directory must satisfy the same exact-entry contract.
# Add its operator attestation before validating it as a complete credential set.
printf '%s\n' \
    'schema=hook2stream-ghcr-pull-identity-v1' \
    "environment=$environment" \
    "username=$username" \
    "credential_identity=$credential_identity" \
    'operator_attests_read_packages_only=true' \
    'operator_attests_environment_exclusive=true' \
    'scope_verification=provider-unavailable' \
    > "$temporary_dir/identity.attestation"
chmod 0600 "$temporary_dir/identity.attestation"
identity_sha256=$(sha256sum "$temporary_dir/identity.attestation" | awk '{ print $1 }')
hook2stream_validate_ghcr_pull_auth \
    "$temporary_dir" "$username" "$auth_sha256" 0:0 \
    || fail "docker login produced an unsafe or unexpected config"
hook2stream_validate_ghcr_identity_attestation \
    "$temporary_dir" "$environment" "$username" "$credential_identity" \
    "$identity_sha256" 0:0 \
    || fail "operator GHCR identity attestation is invalid"
pending_config=$(mktemp "$auth_dir/.config.json.tmp.XXXXXX")
pending_identity=$(mktemp "$auth_dir/.identity.attestation.tmp.XXXXXX")
install -o root -g root -m 0600 "$temporary_config" "$pending_config"
install -o root -g root -m 0600 "$temporary_dir/identity.attestation" "$pending_identity"
mv -f "$pending_config" "$auth_dir/config.json"
pending_config=
mv -f "$pending_identity" "$auth_dir/identity.attestation"
pending_identity=
hook2stream_validate_ghcr_pull_auth \
    "$auth_dir" "$username" "$auth_sha256" 0:0 \
    || fail "published GHCR config did not pass the fail-closed contract"
hook2stream_validate_ghcr_identity_attestation \
    "$auth_dir" "$environment" "$username" "$credential_identity" \
    "$identity_sha256" 0:0 \
    || fail "published GHCR identity attestation did not pass the fail-closed contract"

printf '%s\n' \
    "GHCR pull auth configured for $environment." \
    "Set these non-secret pins in /etc/hook2stream/deploy.conf:" \
    "DOCKER_CONFIG=$auth_dir" \
    "HOOK2STREAM_GHCR_USERNAME=$username" \
    "HOOK2STREAM_GHCR_AUTH_SHA256=$auth_sha256" \
    "HOOK2STREAM_GHCR_CREDENTIAL_IDENTITY=$credential_identity" \
    "HOOK2STREAM_GHCR_IDENTITY_SHA256=$identity_sha256"

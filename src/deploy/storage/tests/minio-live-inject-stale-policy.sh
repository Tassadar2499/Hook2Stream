#!/bin/sh
set -eu
set -f

fail() { printf '%s\n' "storage stale-policy fixture: $*" >&2; exit 1; }
read_secret() {
    secret_path=$1
    secret_value=
    secret_extra=
    [ -f "$secret_path" ] && [ ! -L "$secret_path" ] && [ -r "$secret_path" ] \
        || fail "secret is unreadable"
    exec 8< "$secret_path"
    IFS= read -r secret_value <&8 || [ -n "$secret_value" ] || fail "secret is empty"
    if IFS= read -r secret_extra <&8 || [ -n "$secret_extra" ]; then
        exec 8<&-
        fail "secret is multiline"
    fi
    exec 8<&-
    case "$secret_value" in ''|*[!A-Za-z0-9._+-]*) fail "credential is unsafe for MC_HOST" ;; esac
    printf '%s' "$secret_value"
}
mc_host_value() {
    fixture_access=$1
    fixture_secret=$2
    printf 'http://%s:%s@minio:9000' "$fixture_access" "$fixture_secret"
}

: "${DEPLOYMENT_ENVIRONMENT:?DEPLOYMENT_ENVIRONMENT is required}"
: "${MINIO_ENDPOINT:?MINIO_ENDPOINT is required}"
: "${MINIO_BACKUP_BUCKET:?MINIO_BACKUP_BUCKET is required}"
: "${MINIO_ROOT_USER_FILE:?MINIO_ROOT_USER_FILE is required}"
: "${MINIO_ROOT_PASSWORD_FILE:?MINIO_ROOT_PASSWORD_FILE is required}"
: "${S3_RUNTIME_ACCESS_KEY_FILE:?S3_RUNTIME_ACCESS_KEY_FILE is required}"
: "${S3_RUNTIME_SECRET_KEY_FILE:?S3_RUNTIME_SECRET_KEY_FILE is required}"
: "${MC_CONFIG_DIR:=/tmp/mc-stale-policy-fixture}"
[ "$MINIO_ENDPOINT" = http://minio:9000 ] || fail "endpoint is not internal"

root_user=$(read_secret "$MINIO_ROOT_USER_FILE")
root_password=$(read_secret "$MINIO_ROOT_PASSWORD_FILE")
runtime_access=$(read_secret "$S3_RUNTIME_ACCESS_KEY_FILE")
runtime_secret=$(read_secret "$S3_RUNTIME_SECRET_KEY_FILE")
umask 077
mkdir -p "$MC_CONFIG_DIR"
printf '%s' hook2stream-stale-policy-fixture > "$MC_CONFIG_DIR/probe"
MC_HOST_fixture_root=$(mc_host_value "$root_user" "$root_password")
MC_HOST_fixture_runtime=$(mc_host_value "$runtime_access" "$runtime_secret")
export MC_HOST_fixture_root MC_HOST_fixture_runtime

mc --config-dir "$MC_CONFIG_DIR" admin policy attach fixture_root readwrite --user "$runtime_access" >/dev/null
mc --config-dir "$MC_CONFIG_DIR" admin user info --json fixture_root "$runtime_access" \
    > "$MC_CONFIG_DIR/broad-user.json"
grep -F 'readwrite' "$MC_CONFIG_DIR/broad-user.json" >/dev/null \
    || fail "broad fixture policy was not attached"
fixture_key=policy-reconciliation/stale-broad-policy-$$
mc --config-dir "$MC_CONFIG_DIR" cp "$MC_CONFIG_DIR/probe" \
    "fixture_runtime/$MINIO_BACKUP_BUCKET/$fixture_key" >/dev/null
mc --config-dir "$MC_CONFIG_DIR" stat \
    "fixture_runtime/$MINIO_BACKUP_BUCKET/$fixture_key" >/dev/null \
    || fail "broad fixture policy was not effective"
mc --config-dir "$MC_CONFIG_DIR" rm --force \
    "fixture_root/$MINIO_BACKUP_BUCKET/$fixture_key" >/dev/null

unset MC_HOST_fixture_root MC_HOST_fixture_runtime root_user root_password runtime_access runtime_secret
printf '%s\n' "storage stale-policy fixture: broad policy attached and effective" >&2

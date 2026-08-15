#!/bin/sh
set -eu
set -f

fail() { printf '%s\n' "storage authenticated health: $*" >&2; exit 1; }
read_secret() {
    path=$1
    [ -f "$path" ] && [ ! -L "$path" ] && [ -r "$path" ] || fail "secret is unreadable"
    value=
    extra=
    exec 8< "$path"
    IFS= read -r value <&8 || [ -n "$value" ] || fail "secret is empty"
    if IFS= read -r extra <&8 || [ -n "$extra" ]; then exec 8<&-; fail "secret is multiline"; fi
    exec 8<&-
    case "$value" in ''|*[[:space:]]*) fail "secret is empty or contains whitespace" ;; esac
    printf '%s' "$value"
}
mc_host_value() {
    access_key=$1
    secret_key=$2
    for credential in "$access_key" "$secret_key"; do
        case "$credential" in *[!A-Za-z0-9._+-]*) fail "credential is unsafe for MC_HOST" ;; esac
    done
    printf 'http://%s:%s@minio:9000' "$access_key" "$secret_key"
}

: "${DEPLOYMENT_ENVIRONMENT:?DEPLOYMENT_ENVIRONMENT is required}"
: "${MINIO_ENDPOINT:?MINIO_ENDPOINT is required}"
: "${MINIO_MEDIA_BUCKET:?MINIO_MEDIA_BUCKET is required}"
: "${MINIO_BACKUP_BUCKET:?MINIO_BACKUP_BUCKET is required}"
: "${MINIO_ROOT_USER_FILE:?MINIO_ROOT_USER_FILE is required}"
: "${MINIO_ROOT_PASSWORD_FILE:?MINIO_ROOT_PASSWORD_FILE is required}"
: "${MC_CONFIG_DIR:=/tmp/mc-health}"
[ "$MINIO_ENDPOINT" = http://minio:9000 ] || fail "endpoint is not internal"
marker=/etc/hook2stream/markers/$DEPLOYMENT_ENVIRONMENT-storage-protocol-v1.json
[ -f "$marker" ] && [ ! -L "$marker" ] || fail "local protocol marker is unavailable"
root_user=$(read_secret "$MINIO_ROOT_USER_FILE")
root_password=$(read_secret "$MINIO_ROOT_PASSWORD_FILE")
umask 077
mkdir -p "$MC_CONFIG_DIR"
MC_HOST_health=$(mc_host_value "$root_user" "$root_password")
export MC_HOST_health
mc --config-dir "$MC_CONFIG_DIR" ready health >/dev/null
for bucket in "$MINIO_MEDIA_BUCKET" "$MINIO_BACKUP_BUCKET"; do
    mc --config-dir "$MC_CONFIG_DIR" stat "health/$bucket" >/dev/null
    mc --config-dir "$MC_CONFIG_DIR" cat "health/$bucket/hook2stream-system/storage-protocol.json" \
        > "$MC_CONFIG_DIR/$bucket-marker.json"
    cmp -s "$marker" "$MC_CONFIG_DIR/$bucket-marker.json" || fail "remote protocol marker differs in $bucket"
done
unset MC_HOST_health root_user root_password
printf '%s\n' "storage authenticated health: ready" >&2

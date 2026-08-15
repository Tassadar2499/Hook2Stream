#!/bin/sh
set -eu
set -f

fail() { printf '%s\n' "storage policy probe: $*" >&2; exit 1; }
read_secret() {
    path=$1
    value=
    extra=
    [ -f "$path" ] && [ ! -L "$path" ] && [ -r "$path" ] || fail "secret is unreadable"
    exec 8< "$path"
    IFS= read -r value <&8 || [ -n "$value" ] || fail "secret is empty"
    if IFS= read -r extra <&8 || [ -n "$extra" ]; then exec 8<&-; fail "secret is multiline"; fi
    exec 8<&-
    case "$value" in ''|*[[:space:]]*) fail "secret is empty or contains whitespace" ;; esac
    printf '%s' "$value"
}
mc_cmd() { mc --config-dir "$MC_CONFIG_DIR" "$@"; }
mc_host_value() {
    access_key=$1
    secret_key=$2
    for credential in "$access_key" "$secret_key"; do
        case "$credential" in *[!A-Za-z0-9._+-]*) fail "credential is unsafe for MC_HOST" ;; esac
    done
    printf 'http://%s:%s@minio:9000' "$access_key" "$secret_key"
}
set_alias() {
    alias_name=$1
    access_file=$2
    secret_file=$3
    access=$(read_secret "$access_file")
    secret=$(read_secret "$secret_file")
    case "$alias_name" in
        runtime) MC_HOST_runtime=$(mc_host_value "$access" "$secret"); export MC_HOST_runtime ;;
        bootstrap) MC_HOST_bootstrap=$(mc_host_value "$access" "$secret"); export MC_HOST_bootstrap ;;
        backup) MC_HOST_backup=$(mc_host_value "$access" "$secret"); export MC_HOST_backup ;;
        *) fail "unknown mc alias" ;;
    esac
    unset access secret
}
must_deny() {
    if "$@" >/dev/null 2>&1; then fail "an isolation-deny operation unexpectedly succeeded"; fi
}

: "${MINIO_ENDPOINT:?MINIO_ENDPOINT is required}"
: "${MINIO_MEDIA_BUCKET:?MINIO_MEDIA_BUCKET is required}"
: "${MINIO_BACKUP_BUCKET:?MINIO_BACKUP_BUCKET is required}"
: "${MINIO_BACKUP_PREFIX:?MINIO_BACKUP_PREFIX is required}"
: "${S3_BOOTSTRAP_ACCESS_KEY_FILE:?S3_BOOTSTRAP_ACCESS_KEY_FILE is required}"
: "${S3_BOOTSTRAP_SECRET_KEY_FILE:?S3_BOOTSTRAP_SECRET_KEY_FILE is required}"
: "${S3_RUNTIME_ACCESS_KEY_FILE:?S3_RUNTIME_ACCESS_KEY_FILE is required}"
: "${S3_RUNTIME_SECRET_KEY_FILE:?S3_RUNTIME_SECRET_KEY_FILE is required}"
: "${BACKUP_S3_ACCESS_KEY_FILE:?BACKUP_S3_ACCESS_KEY_FILE is required}"
: "${BACKUP_S3_SECRET_KEY_FILE:?BACKUP_S3_SECRET_KEY_FILE is required}"
: "${MC_CONFIG_DIR:=/tmp/mc-policy-probe}"
[ "$MINIO_ENDPOINT" = http://minio:9000 ] || fail "endpoint is not internal"
umask 077
mkdir -p "$MC_CONFIG_DIR"
printf '%s' hook2stream-storage-policy-probe > "$MC_CONFIG_DIR/probe"
set_alias runtime "$S3_RUNTIME_ACCESS_KEY_FILE" "$S3_RUNTIME_SECRET_KEY_FILE"
set_alias bootstrap "$S3_BOOTSTRAP_ACCESS_KEY_FILE" "$S3_BOOTSTRAP_SECRET_KEY_FILE"
set_alias backup "$BACKUP_S3_ACCESS_KEY_FILE" "$BACKUP_S3_SECRET_KEY_FILE"

probe_id=policy-probe-$$
media_key=policy-probes/$probe_id
backup_key=$MINIO_BACKUP_PREFIX/policy-probes/$probe_id
mc_cmd cp "$MC_CONFIG_DIR/probe" "runtime/$MINIO_MEDIA_BUCKET/$media_key" >/dev/null
mc_cmd cp "runtime/$MINIO_MEDIA_BUCKET/$media_key" "$MC_CONFIG_DIR/runtime-read" >/dev/null
cmp -s "$MC_CONFIG_DIR/probe" "$MC_CONFIG_DIR/runtime-read" || fail "runtime media read/write verification failed"
mc_cmd cat "bootstrap/$MINIO_MEDIA_BUCKET/hook2stream-system/storage-protocol.json" >/dev/null
must_deny mc_cmd cp "$MC_CONFIG_DIR/probe" "bootstrap/$MINIO_MEDIA_BUCKET/policy-probes/$probe_id-bootstrap"
must_deny mc_cmd cp "$MC_CONFIG_DIR/probe" "runtime/$MINIO_MEDIA_BUCKET/hook2stream-system/storage-protocol.json"
must_deny mc_cmd cp "$MC_CONFIG_DIR/probe" "runtime/$MINIO_BACKUP_BUCKET/$backup_key-runtime"

mc_cmd cp "$MC_CONFIG_DIR/probe" "backup/$MINIO_BACKUP_BUCKET/$backup_key" >/dev/null
mc_cmd cp "backup/$MINIO_BACKUP_BUCKET/$backup_key" "$MC_CONFIG_DIR/backup-read" >/dev/null
cmp -s "$MC_CONFIG_DIR/probe" "$MC_CONFIG_DIR/backup-read" || fail "backup read/write verification failed"
must_deny mc_cmd cp "runtime/$MINIO_BACKUP_BUCKET/$backup_key" "$MC_CONFIG_DIR/runtime-backup-read"
must_deny mc_cmd cp "$MC_CONFIG_DIR/probe" "backup/$MINIO_MEDIA_BUCKET/policy-probes/$probe_id-backup"

mc_cmd rm --force "runtime/$MINIO_MEDIA_BUCKET/$media_key" >/dev/null
mc_cmd rm --force "backup/$MINIO_BACKUP_BUCKET/$backup_key" >/dev/null
unset MC_HOST_runtime MC_HOST_bootstrap MC_HOST_backup
printf '%s\n' "storage policy probe: effective allow/deny isolation verified" >&2

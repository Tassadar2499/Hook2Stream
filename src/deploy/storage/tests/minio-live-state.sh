#!/bin/sh
set -eu
set -f

fail() { printf '%s\n' "storage live state: $*" >&2; exit 1; }
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
    case "$secret_value" in ''|*[[:space:]]*) fail "secret is empty or contains whitespace" ;; esac
    printf '%s' "$secret_value"
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
: "${S3_BOOTSTRAP_ACCESS_KEY_FILE:?S3_BOOTSTRAP_ACCESS_KEY_FILE is required}"
: "${S3_RUNTIME_ACCESS_KEY_FILE:?S3_RUNTIME_ACCESS_KEY_FILE is required}"
: "${BACKUP_S3_ACCESS_KEY_FILE:?BACKUP_S3_ACCESS_KEY_FILE is required}"
: "${MC_CONFIG_DIR:=/tmp/mc-live-state}"
[ "$MINIO_ENDPOINT" = http://minio:9000 ] || fail "endpoint is not internal"
[ -d /results ] && [ -w /results ] || fail "result directory is unavailable"

root_user=$(read_secret "$MINIO_ROOT_USER_FILE")
root_password=$(read_secret "$MINIO_ROOT_PASSWORD_FILE")
bootstrap_access=$(read_secret "$S3_BOOTSTRAP_ACCESS_KEY_FILE")
runtime_access=$(read_secret "$S3_RUNTIME_ACCESS_KEY_FILE")
backup_access=$(read_secret "$BACKUP_S3_ACCESS_KEY_FILE")
umask 077
mkdir -p "$MC_CONFIG_DIR"
MC_HOST_state=$(mc_host_value "$root_user" "$root_password")
export MC_HOST_state
mc --config-dir "$MC_CONFIG_DIR" ready state >/dev/null
mc --config-dir "$MC_CONFIG_DIR" version info "state/$MINIO_MEDIA_BUCKET" > /results/media-version.txt
mc --config-dir "$MC_CONFIG_DIR" version info "state/$MINIO_BACKUP_BUCKET" > /results/backup-version.txt
mc --config-dir "$MC_CONFIG_DIR" quota info "state/$MINIO_MEDIA_BUCKET" > /results/media-quota.txt
mc --config-dir "$MC_CONFIG_DIR" quota info "state/$MINIO_BACKUP_BUCKET" > /results/backup-quota.txt
mc --config-dir "$MC_CONFIG_DIR" ilm export "state/$MINIO_MEDIA_BUCKET" > /results/media-ilm.json
mc --config-dir "$MC_CONFIG_DIR" ilm export "state/$MINIO_BACKUP_BUCKET" > /results/backup-ilm.json
mc --config-dir "$MC_CONFIG_DIR" admin user info --json state "$runtime_access" > /results/runtime-user.json
mc --config-dir "$MC_CONFIG_DIR" admin user info --json state "$bootstrap_access" > /results/bootstrap-user.json
mc --config-dir "$MC_CONFIG_DIR" admin user info --json state "$backup_access" > /results/backup-user.json
{
    mc --config-dir "$MC_CONFIG_DIR" anonymous get "state/$MINIO_MEDIA_BUCKET"
    mc --config-dir "$MC_CONFIG_DIR" anonymous get "state/$MINIO_BACKUP_BUCKET"
} > /results/anonymous.txt
chmod 0644 /results/media-version.txt /results/backup-version.txt \
    /results/media-quota.txt /results/backup-quota.txt \
    /results/media-ilm.json /results/backup-ilm.json /results/anonymous.txt \
    /results/runtime-user.json /results/bootstrap-user.json /results/backup-user.json
unset MC_HOST_state root_user root_password bootstrap_access runtime_access backup_access
printf '%s\n' "storage live state: captured $DEPLOYMENT_ENVIRONMENT" >&2

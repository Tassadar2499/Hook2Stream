#!/bin/sh
set -eu

readonly alias_name=hook2stream
readonly policy_dir=${MINIO_POLICY_DIR:-/etc/hook2stream/minio}

fail() {
    printf '%s\n' "minio init: $*" >&2
    exit 1
}

read_secret() {
    secret_path=$1
    [ -r "$secret_path" ] || fail "required secret file is not readable: $secret_path"
    secret_value=
    secret_extra=
    exec 8< "$secret_path"
    if ! IFS= read -r secret_value <&8; then
        [ -n "$secret_value" ] || fail "required secret file is empty: $secret_path"
    fi
    if IFS= read -r secret_extra <&8 || [ -n "$secret_extra" ]; then
        exec 8<&-
        fail "required secret file must contain exactly one line: $secret_path"
    fi
    exec 8<&-
    [ -n "$secret_value" ] || fail "required secret file is empty: $secret_path"
    carriage_return=$(printf '\r')
    case "$secret_value" in
        *"$carriage_return"*)
            fail "required secret file must contain exactly one line: $secret_path"
            ;;
        [[:space:]]*|*[[:space:]])
            fail "required secret must not start or end with whitespace: $secret_path"
            ;;
    esac
    printf '%s' "$secret_value"
}

validate_bucket_name() {
    bucket_name=$1
    bucket_length=${#bucket_name}
    [ "$bucket_length" -ge 3 ] && [ "$bucket_length" -le 63 ] \
        || fail "bucket names must contain between 3 and 63 characters"
    case "$bucket_name" in
        [a-z0-9]*[a-z0-9]) ;;
        *) fail "bucket names must start and end with a lowercase letter or digit" ;;
    esac
    case "$bucket_name" in
        *[!a-z0-9.-]*|*..*|*.-*|*-.*)
            fail "invalid S3 bucket name: $bucket_name"
            ;;
    esac
}

validate_quota() {
    quota_name=$1
    quota_value=$2
    case "$quota_value" in
        ''|*[!0-9]*) fail "$quota_name must be a positive integer in GiB" ;;
    esac
    [ "${#quota_value}" -le 5 ] \
        || fail "$quota_name is outside the supported range"
    [ "$quota_value" -ge 1 ] && [ "$quota_value" -le 10240 ] \
        || fail "$quota_name must be between 1 and 10240 GiB"
}

validate_credential() {
    credential_name=$1
    access_key=$2
    secret_key=$3
    [ "${#access_key}" -ge 3 ] \
        || fail "$credential_name access key must contain at least 3 characters"
    [ "${#secret_key}" -ge 8 ] \
        || fail "$credential_name secret key must contain at least 8 characters"
}

mc_command() {
    mc --config-dir "$MC_CONFIG_DIR" "$@"
}

upsert_identity() {
    access_key=$1
    secret_key=$2
    policy_name=$3
    mc_command admin user add "$alias_name" "$access_key" "$secret_key"
    mc_command admin user enable "$alias_name" "$access_key"
    mc_command admin policy attach "$alias_name" "$policy_name" --user "$access_key"
}

[ "${STORAGE_MODE:-}" = "minio" ] \
    || fail "STORAGE_MODE must be exactly 'minio' for the MinIO overlay"
: "${MINIO_ENDPOINT:?MINIO_ENDPOINT is required}"
: "${MINIO_REGION:?MINIO_REGION is required}"
: "${MINIO_MEDIA_BUCKET:?MINIO_MEDIA_BUCKET is required}"
: "${MINIO_BACKUP_BUCKET:?MINIO_BACKUP_BUCKET is required}"
: "${MINIO_BACKUP_PREFIX:?MINIO_BACKUP_PREFIX is required}"
: "${MINIO_MEDIA_QUOTA_GIB:?MINIO_MEDIA_QUOTA_GIB is required}"
: "${MINIO_BACKUP_QUOTA_GIB:?MINIO_BACKUP_QUOTA_GIB is required}"
: "${MINIO_ROOT_USER_FILE:?MINIO_ROOT_USER_FILE is required}"
: "${MINIO_ROOT_PASSWORD_FILE:?MINIO_ROOT_PASSWORD_FILE is required}"
: "${S3_RUNTIME_ACCESS_KEY_FILE:?S3_RUNTIME_ACCESS_KEY_FILE is required}"
: "${S3_RUNTIME_SECRET_KEY_FILE:?S3_RUNTIME_SECRET_KEY_FILE is required}"
: "${S3_BOOTSTRAP_ACCESS_KEY_FILE:?S3_BOOTSTRAP_ACCESS_KEY_FILE is required}"
: "${S3_BOOTSTRAP_SECRET_KEY_FILE:?S3_BOOTSTRAP_SECRET_KEY_FILE is required}"
: "${BACKUP_S3_ACCESS_KEY_FILE:?BACKUP_S3_ACCESS_KEY_FILE is required}"
: "${BACKUP_S3_SECRET_KEY_FILE:?BACKUP_S3_SECRET_KEY_FILE is required}"
: "${MC_CONFIG_DIR:=/tmp/mc}"

[ "$MINIO_ENDPOINT" = "http://minio:9000" ] \
    || fail "MINIO_ENDPOINT must be exactly http://minio:9000"
case "$MINIO_REGION" in
    ''|*[!A-Za-z0-9-]*) fail "MINIO_REGION contains invalid characters" ;;
esac
validate_bucket_name "$MINIO_MEDIA_BUCKET"
validate_bucket_name "$MINIO_BACKUP_BUCKET"
[ "$MINIO_MEDIA_BUCKET" != "$MINIO_BACKUP_BUCKET" ] \
    || fail "media and backup buckets must be different"
validate_quota MINIO_MEDIA_QUOTA_GIB "$MINIO_MEDIA_QUOTA_GIB"
validate_quota MINIO_BACKUP_QUOTA_GIB "$MINIO_BACKUP_QUOTA_GIB"

# The bundled IAM policies are intentionally concrete rather than templates:
# this keeps the init job compatible with the minimal, immutable mc image and
# prevents an environment override from widening access to another bucket.
[ "$MINIO_MEDIA_BUCKET" = "hook2stream-staging-media" ] \
    || fail "MINIO_MEDIA_BUCKET must be exactly hook2stream-staging-media"
[ "$MINIO_BACKUP_BUCKET" = "hook2stream-staging-pg-backups" ] \
    || fail "MINIO_BACKUP_BUCKET must be exactly hook2stream-staging-pg-backups"
[ "$MINIO_MEDIA_QUOTA_GIB" = "180" ] \
    || fail "MINIO_MEDIA_QUOTA_GIB must be exactly 180"
[ "$MINIO_BACKUP_QUOTA_GIB" = "20" ] \
    || fail "MINIO_BACKUP_QUOTA_GIB must be exactly 20"

backup_prefix=${MINIO_BACKUP_PREFIX#/}
backup_prefix=${backup_prefix%/}
[ -n "$backup_prefix" ] || fail "MINIO_BACKUP_PREFIX must not be empty"
case "$backup_prefix" in
    *[!A-Za-z0-9._/-]*|*..*) fail "MINIO_BACKUP_PREFIX contains invalid characters" ;;
esac
[ "$backup_prefix" = "hook2stream/staging/postgres" ] \
    || fail "MINIO_BACKUP_PREFIX must be exactly hook2stream/staging/postgres"

root_user=$(read_secret "$MINIO_ROOT_USER_FILE")
root_password=$(read_secret "$MINIO_ROOT_PASSWORD_FILE")
runtime_access_key=$(read_secret "$S3_RUNTIME_ACCESS_KEY_FILE")
runtime_secret_key=$(read_secret "$S3_RUNTIME_SECRET_KEY_FILE")
bootstrap_access_key=$(read_secret "$S3_BOOTSTRAP_ACCESS_KEY_FILE")
bootstrap_secret_key=$(read_secret "$S3_BOOTSTRAP_SECRET_KEY_FILE")
backup_access_key=$(read_secret "$BACKUP_S3_ACCESS_KEY_FILE")
backup_secret_key=$(read_secret "$BACKUP_S3_SECRET_KEY_FILE")

validate_credential root "$root_user" "$root_password"
validate_credential runtime "$runtime_access_key" "$runtime_secret_key"
validate_credential bootstrap "$bootstrap_access_key" "$bootstrap_secret_key"
validate_credential backup "$backup_access_key" "$backup_secret_key"

for managed_access_key in \
    "$runtime_access_key" \
    "$bootstrap_access_key" \
    "$backup_access_key"; do
    [ "$managed_access_key" != "$root_user" ] \
        || fail "a managed access key must not equal the MinIO root access key"
done
[ "$runtime_access_key" != "$bootstrap_access_key" ] \
    || fail "runtime and bootstrap access keys must be distinct"
[ "$runtime_access_key" != "$backup_access_key" ] \
    || fail "runtime and backup access keys must be distinct"
[ "$bootstrap_access_key" != "$backup_access_key" ] \
    || fail "bootstrap and backup access keys must be distinct"

umask 077
mkdir -p "$MC_CONFIG_DIR"
for policy_path in \
    "$policy_dir/runtime-media.json" \
    "$policy_dir/bootstrap-media.json" \
    "$policy_dir/postgres-backup.json"; do
    [ -r "$policy_path" ] || fail "policy is not readable: $policy_path"
done

mc_command alias set \
    "$alias_name" \
    "$MINIO_ENDPOINT" \
    "$root_user" \
    "$root_password" \
    --api S3v4 \
    --path on

mc_command mb \
    --ignore-existing \
    --region "$MINIO_REGION" \
    "$alias_name/$MINIO_MEDIA_BUCKET"
mc_command mb \
    --ignore-existing \
    --region "$MINIO_REGION" \
    "$alias_name/$MINIO_BACKUP_BUCKET"

mc_command anonymous set none "$alias_name/$MINIO_MEDIA_BUCKET"
mc_command anonymous set none "$alias_name/$MINIO_BACKUP_BUCKET"
mc_command version suspend "$alias_name/$MINIO_MEDIA_BUCKET"
mc_command version enable "$alias_name/$MINIO_BACKUP_BUCKET"
mc_command quota set \
    "$alias_name/$MINIO_MEDIA_BUCKET" \
    --size "${MINIO_MEDIA_QUOTA_GIB}GiB"
mc_command quota set \
    "$alias_name/$MINIO_BACKUP_BUCKET" \
    --size "${MINIO_BACKUP_QUOTA_GIB}GiB"
mc_command ilm import \
    "$alias_name/$MINIO_BACKUP_BUCKET" \
    < "$policy_dir/backup-lifecycle.json"

mc_command admin policy create \
    "$alias_name" hook2stream-runtime-media "$policy_dir/runtime-media.json"
mc_command admin policy create \
    "$alias_name" hook2stream-bootstrap-media "$policy_dir/bootstrap-media.json"
mc_command admin policy create \
    "$alias_name" hook2stream-postgres-backup "$policy_dir/postgres-backup.json"

upsert_identity \
    "$runtime_access_key" \
    "$runtime_secret_key" \
    hook2stream-runtime-media
upsert_identity \
    "$bootstrap_access_key" \
    "$bootstrap_secret_key" \
    hook2stream-bootstrap-media
upsert_identity \
    "$backup_access_key" \
    "$backup_secret_key" \
    hook2stream-postgres-backup

unset \
    root_user root_password \
    runtime_access_key runtime_secret_key \
    bootstrap_access_key bootstrap_secret_key \
    backup_access_key backup_secret_key
printf '%s\n' "minio init: buckets, quotas, lifecycle, and identities are configured"

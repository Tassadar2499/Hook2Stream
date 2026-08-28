#!/bin/bash
set -euo pipefail

fail() {
    printf '%s\n' "postgres backup: $*" >&2
    exit 1
}

read_secret() {
    secret_path=$1
    [ -r "$secret_path" ] || fail "required secret file is not readable: $secret_path"
    secret_value=$(sed -e 's/[[:space:]]*$//' "$secret_path")
    [ -n "$secret_value" ] || fail "required secret file is empty: $secret_path"
    printf '%s' "$secret_value"
}

read_age_recipient() {
    age_recipient=$(read_secret "$1")
    case "$age_recipient" in
        age1[0-9a-z]*) ;;
        *) fail "backup age recipient must be a public X25519 age1 recipient" ;;
    esac
    case "$age_recipient" in
        *[!0-9a-z]*) fail "backup age recipient contains invalid characters" ;;
    esac
    printf '%s' "$age_recipient"
}

aws_command() {
    if [ -n "$BACKUP_S3_ENDPOINT" ]; then
        aws --endpoint-url "$BACKUP_S3_ENDPOINT" "$@"
    else
        aws "$@"
    fi
}

cleanup() {
    rm -f \
        "${encrypted_file:-}" \
        "${checksum_file:-}" \
        "${manifest_file:-}" \
        "${pgpass_file:-}" \
        "${put_response_file:-}" \
        "${success_marker_tmp:-}"
}

cleanup_process() {
    cleanup
    rm -f "${aws_config_file:-}"
}

on_signal() {
    signal_status=$1
    trap - EXIT HUP INT TERM
    cleanup_process
    exit "$signal_status"
}

put_versioned_object() {
    put_body=$1
    put_key=$2
    put_response_file=/tmp/hook2stream-backup-put-response.json
    aws_command s3api put-object \
        --bucket "$BACKUP_S3_BUCKET" \
        --key "$put_key" \
        --body "$put_body" \
        --output json > "$put_response_file"
    put_version_id=$(jq -er '.VersionId | select(type == "string" and length > 0)' \
        "$put_response_file") \
        || fail "versioned backup bucket did not return a VersionId for ${put_key}"
    rm -f "$put_response_file"
    put_response_file=
    printf '%s' "$put_version_id"
}

with_backup_lock() {
    exec 9>"$BACKUP_LOCK_FILE"
    flock -w "$BACKUP_LOCK_TIMEOUT_SECONDS" 9 \
        || fail "another backup did not release the shared lock within ${BACKUP_LOCK_TIMEOUT_SECONDS} seconds"
    perform_backup
    flock -u 9
    exec 9>&-
}

perform_backup() {
    created_at=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    timestamp=$(printf '%s' "$created_at" | tr -d ':-')
    run_id=$(od -An -N8 -tx1 /dev/urandom | tr -d ' \n')
    case "$run_id" in
        [0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]) ;;
        *) fail "could not generate a collision-resistant backup run ID" ;;
    esac
    date_path=$(printf '%s' "${created_at%%T*}" | tr '-' '/')
    age_recipient=$(read_age_recipient "$BACKUP_AGE_RECIPIENT_FILE")
    recipient_fingerprint=$(printf '%s' "$age_recipient" | sha256sum | cut -c1-16)
    base_name="${POSTGRES_DB}-${timestamp}-${run_id}-age-${recipient_fingerprint}.dump.age"
    encrypted_file="/tmp/${base_name}"
    checksum_file="${encrypted_file}.sha256"
    manifest_file="${encrypted_file}.manifest.json"
    pgpass_file="/tmp/.pgpass"
    object_prefix=${BACKUP_S3_PREFIX%/}
    object_key="${object_prefix}/${date_path}/${base_name}"
    checksum_object_key="${object_key}.sha256"
    manifest_object_key="${object_key}.manifest.json"

    postgres_password=$(read_secret "$POSTGRES_PASSWORD_FILE")
    escaped_postgres_password=$(printf '%s' "$postgres_password" \
        | sed -e 's/\\/\\\\/g' -e 's/:/\\:/g')
    umask 077
    printf '%s:%s:%s:%s:%s\n' \
        "$POSTGRES_HOST" "$POSTGRES_PORT" "$POSTGRES_DB" "$POSTGRES_USER" "$escaped_postgres_password" \
        > "$pgpass_file"
    unset postgres_password escaped_postgres_password
    export PGPASSFILE=$pgpass_file

    printf '%s\n' "postgres backup: creating encrypted logical backup at ${timestamp}"
    pg_dump \
        --host "$POSTGRES_HOST" \
        --port "$POSTGRES_PORT" \
        --username "$POSTGRES_USER" \
        --dbname "$POSTGRES_DB" \
        --format custom \
        --compress 9 \
        --no-owner \
        --no-privileges \
        | age --recipient "$age_recipient" --output "$encrypted_file"

    [ -s "$encrypted_file" ] || fail "pg_dump produced an empty backup"
    (
        cd /tmp
        sha256sum "$base_name" > "${base_name}.sha256"
    )
    ciphertext_sha256=$(sed -e 's/[[:space:]].*$//' "$checksum_file")

    dump_version_id=$(put_versioned_object "$encrypted_file" "$object_key")
    checksum_version_id=$(put_versioned_object "$checksum_file" "$checksum_object_key")

    jq -n \
        --arg kind "hook2stream-postgresql-logical-backup" \
        --arg createdAt "$created_at" \
        --arg database "$POSTGRES_DB" \
        --arg recipientFingerprint "$recipient_fingerprint" \
        --arg dumpObjectKey "$object_key" \
        --arg dumpVersionId "$dump_version_id" \
        --arg checksumObjectKey "$checksum_object_key" \
        --arg checksumVersionId "$checksum_version_id" \
        --arg ciphertextSha256 "$ciphertext_sha256" \
        --argjson maxObjectTtlHours "$BACKUP_MAX_OBJECT_TTL_HOURS" \
        '{
            schemaVersion: 3,
            kind: $kind,
            createdAt: $createdAt,
            database: $database,
            encryption: {
                format: "age",
                recipientType: "X25519",
                recipientFingerprint: $recipientFingerprint
            },
            encryptedDump: {
                objectKey: $dumpObjectKey,
                versionId: $dumpVersionId,
                sha256: $ciphertextSha256
            },
            checksum: {
                objectKey: $checksumObjectKey,
                versionId: $checksumVersionId
            },
            retention: {
                mode: "storj-access-grant-max-object-ttl",
                maxObjectTtlHours: $maxObjectTtlHours
            }
        }' > "$manifest_file"

    # The manifest is the completion record for this backup set and is uploaded last.
    manifest_version_id=$(put_versioned_object "$manifest_file" "$manifest_object_key")

    success_marker_tmp="${BACKUP_SUCCESS_MARKER}.tmp"
    {
        date -u +%s
        printf '%s\n' "$recipient_fingerprint"
        printf '%s\n' "$manifest_object_key"
        printf '%s\n' "$manifest_version_id"
    } > "$success_marker_tmp"
    mv -f "$success_marker_tmp" "$BACKUP_SUCCESS_MARKER"
    printf '%s\n' \
        "postgres backup: uploaded s3://${BACKUP_S3_BUCKET}/${manifest_object_key}"
    cleanup
}

: "${POSTGRES_HOST:=postgres}"
: "${POSTGRES_PORT:=5432}"
: "${POSTGRES_DB:=hook2stream}"
: "${POSTGRES_USER:=hook2stream}"
: "${POSTGRES_PASSWORD_FILE:?POSTGRES_PASSWORD_FILE is required}"
: "${BACKUP_S3_ENDPOINT:=}"
: "${BACKUP_S3_REGION:?BACKUP_S3_REGION is required}"
: "${BACKUP_S3_BUCKET:?BACKUP_S3_BUCKET is required}"
: "${BACKUP_S3_PREFIX:=hook2stream/production/postgres}"
: "${BACKUP_S3_FORCE_PATH_STYLE:?BACKUP_S3_FORCE_PATH_STYLE is required}"
: "${BACKUP_S3_ACCESS_KEY_FILE:?BACKUP_S3_ACCESS_KEY_FILE is required}"
: "${BACKUP_S3_SECRET_KEY_FILE:?BACKUP_S3_SECRET_KEY_FILE is required}"
: "${BACKUP_AGE_RECIPIENT_FILE:?BACKUP_AGE_RECIPIENT_FILE is required}"
: "${BACKUP_INTERVAL_SECONDS:=3600}"
: "${BACKUP_RETENTION_DAYS:=35}"
: "${BACKUP_MAX_OBJECT_TTL_HOURS:=$((BACKUP_RETENTION_DAYS * 24))}"
: "${BACKUP_SUCCESS_MARKER:=/tmp/last-successful-backup}"
: "${BACKUP_LOCK_FILE:=/tmp/hook2stream-postgres-backup.lock}"
: "${BACKUP_LOCK_TIMEOUT_SECONDS:=1800}"

for integer_value in \
    "$BACKUP_INTERVAL_SECONDS" \
    "$BACKUP_RETENTION_DAYS" \
    "$BACKUP_MAX_OBJECT_TTL_HOURS" \
    "$BACKUP_LOCK_TIMEOUT_SECONDS"; do
    case "$integer_value" in
        *[!0-9]*|'') fail "backup interval, retention, and TTL values must be integers" ;;
    esac
done
[ "$BACKUP_INTERVAL_SECONDS" -ge 300 ] || fail "BACKUP_INTERVAL_SECONDS must be at least 300"
[ "$BACKUP_RETENTION_DAYS" -ge 2 ] || fail "BACKUP_RETENTION_DAYS must be at least 2"
[ "$BACKUP_MAX_OBJECT_TTL_HOURS" -eq "$((BACKUP_RETENTION_DAYS * 24))" ] \
    || fail "BACKUP_MAX_OBJECT_TTL_HOURS must equal BACKUP_RETENTION_DAYS * 24"
[ "$BACKUP_LOCK_TIMEOUT_SECONDS" -ge 60 ] \
    || fail "BACKUP_LOCK_TIMEOUT_SECONDS must be at least 60"
case "$BACKUP_LOCK_FILE" in
    /tmp/*) ;;
    *) fail "BACKUP_LOCK_FILE must be below the encrypted backup scratch mount" ;;
esac
[ "$BACKUP_S3_FORCE_PATH_STYLE" = true ] \
    || fail "BACKUP_S3_FORCE_PATH_STYLE must be true for backup object storage"
command -v flock >/dev/null 2>&1 || fail "flock is required"

umask 077
aws_config_file=$(mktemp /tmp/hook2stream-backup-aws-config.XXXXXX)
AWS_CONFIG_FILE=$aws_config_file
export AWS_CONFIG_FILE
printf '%s\n' \
    '[default]' \
    "region = $BACKUP_S3_REGION" \
    'request_checksum_calculation = when_required' \
    'response_checksum_validation = when_required' \
    's3 =' \
    '    addressing_style = path' > "$AWS_CONFIG_FILE"
AWS_SHARED_CREDENTIALS_FILE=/dev/null
export AWS_SHARED_CREDENTIALS_FILE

export AWS_ACCESS_KEY_ID
AWS_ACCESS_KEY_ID=$(read_secret "$BACKUP_S3_ACCESS_KEY_FILE")
export AWS_SECRET_ACCESS_KEY
AWS_SECRET_ACCESS_KEY=$(read_secret "$BACKUP_S3_SECRET_KEY_FILE")
export AWS_DEFAULT_REGION="$BACKUP_S3_REGION"
export AWS_REGION="$BACKUP_S3_REGION"
export AWS_EC2_METADATA_DISABLED=true
export AWS_PAGER=

trap cleanup_process EXIT
trap 'on_signal 129' HUP
trap 'on_signal 130' INT
trap 'on_signal 143' TERM

printf '%s\n' "postgres backup: waiting for PostgreSQL"
until pg_isready -q -h "$POSTGRES_HOST" -p "$POSTGRES_PORT" -U "$POSTGRES_USER" -d "$POSTGRES_DB"; do
    sleep 5
done

case "${1:-daemon}" in
    backup-once)
        with_backup_lock
        ;;
    daemon)
        while :; do
            with_backup_lock
            sleep "$BACKUP_INTERVAL_SECONDS"
        done
        ;;
    *)
        fail "expected 'daemon' or 'backup-once'"
        ;;
esac

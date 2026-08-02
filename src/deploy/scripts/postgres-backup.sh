#!/bin/sh
set -eu
set -o pipefail

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

read_key_id() {
    key_id=$(read_secret "$1")
    case "$key_id" in
        [A-Za-z0-9]*) ;;
        *) fail "backup encryption key ID must start with an ASCII letter or digit" ;;
    esac
    case "$key_id" in
        *[!A-Za-z0-9._-]*)
            fail "backup encryption key ID may contain only ASCII letters, digits, '.', '_', and '-'"
            ;;
    esac
    [ "${#key_id}" -le 64 ] \
        || fail "backup encryption key ID must not exceed 64 characters"
    printf '%s' "$key_id"
}

aws_command() {
    if [ -n "$BACKUP_S3_ENDPOINT" ]; then
        aws --endpoint-url "$BACKUP_S3_ENDPOINT" "$@"
    else
        aws "$@"
    fi
}

send_success_heartbeat() {
    [ -n "$BACKUP_HEARTBEAT_URL_FILE" ] || return 0

    if [ ! -r "$BACKUP_HEARTBEAT_URL_FILE" ]; then
        printf '%s\n' \
            "postgres backup: warning: heartbeat secret file is not readable; skipping notification" >&2
        return 0
    fi

    if ! heartbeat_url=$(sed -e 's/[[:space:]]*$//' "$BACKUP_HEARTBEAT_URL_FILE"); then
        printf '%s\n' \
            "postgres backup: warning: heartbeat secret file could not be read; skipping notification" >&2
        return 0
    fi
    if [ -z "$heartbeat_url" ]; then
        unset heartbeat_url
        return 0
    fi
    case "$heartbeat_url" in
        https://?*) ;;
        *)
            printf '%s\n' \
                "postgres backup: warning: heartbeat URL must use HTTPS; skipping notification" >&2
            unset heartbeat_url
            return 0
            ;;
    esac
    case "$heartbeat_url" in
        *[[:space:]]*)
            printf '%s\n' \
                "postgres backup: warning: heartbeat URL is invalid; skipping notification" >&2
            unset heartbeat_url
            return 0
            ;;
    esac

    heartbeat_status=
    if heartbeat_status=$(curl \
        --fail \
        --silent \
        --show-error \
        --proto '=https' \
        --connect-timeout 3 \
        --max-time 10 \
        --retry 2 \
        --retry-delay 1 \
        --retry-max-time 20 \
        --retry-connrefused \
        --output /dev/null \
        --write-out '%{http_code}' \
        "$heartbeat_url" 2>/dev/null); then
        case "$heartbeat_status" in
            2[0-9][0-9])
                printf '%s\n' "postgres backup: success heartbeat delivered"
                unset heartbeat_status heartbeat_url
                return 0
                ;;
        esac
    fi
    printf '%s\n' \
        "postgres backup: warning: success heartbeat delivery failed" >&2
    unset heartbeat_status heartbeat_url
    return 0
}

cleanup() {
    rm -f \
        "${encrypted_file:-}" \
        "${checksum_file:-}" \
        "${manifest_file:-}" \
        "${passphrase_snapshot_file:-}" \
        "${pgpass_file:-}" \
        "${versions_file:-}" \
        "${expired_versions_file:-}" \
        "${success_marker_tmp:-}"
}

on_signal() {
    signal_status=$1
    trap - EXIT HUP INT TERM
    cleanup
    exit "$signal_status"
}

purge_expired_versions() {
    retention_seconds=$((BACKUP_RETENTION_DAYS * 86400))
    purge_age_seconds=$((retention_seconds - BACKUP_RETENTION_SAFETY_SECONDS))
    cutoff_epoch=$(($(date -u +%s) - purge_age_seconds))
    cutoff_timestamp=$(date -u -d "@${cutoff_epoch}" +%Y-%m-%dT%H:%M:%SZ)
    versions_file=/tmp/backup-object-versions.json
    expired_versions_file=/tmp/expired-backup-object-versions.tsv

    aws_command s3api list-object-versions \
        --bucket "$BACKUP_S3_BUCKET" \
        --prefix "${BACKUP_S3_PREFIX%/}/" \
        --output json > "$versions_file"
    jq -r --arg cutoff "$cutoff_timestamp" '
        ((.Versions // []) + (.DeleteMarkers // []))[]
        | select(.LastModified <= $cutoff)
        | [.Key, .VersionId]
        | @tsv
    ' "$versions_file" > "$expired_versions_file"

    deleted_versions=0
    tab=$(printf '\t')
    while IFS="$tab" read -r expired_key expired_version_id; do
        [ -n "$expired_key" ] || continue
        [ -n "$expired_version_id" ] || fail "object storage returned an empty backup VersionId"
        aws_command s3api delete-object \
            --bucket "$BACKUP_S3_BUCKET" \
            --key "$expired_key" \
            --version-id "$expired_version_id" \
            --output json >/dev/null
        deleted_versions=$((deleted_versions + 1))
    done < "$expired_versions_file"

    printf '%s\n' \
        "postgres backup: permanently removed ${deleted_versions} expired object versions/delete markers"
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
    encryption_key_id=$(read_key_id "$BACKUP_ENCRYPTION_KEY_ID_FILE")
    base_name="${POSTGRES_DB}-${timestamp}-${run_id}-key-${encryption_key_id}.dump.enc"
    encrypted_file="/tmp/${base_name}"
    checksum_file="${encrypted_file}.sha256"
    manifest_file="${encrypted_file}.manifest.json"
    passphrase_snapshot_file=/tmp/.backup-encryption-passphrase
    pgpass_file="/tmp/.pgpass"
    object_prefix=${BACKUP_S3_PREFIX%/}
    object_key="${object_prefix}/${date_path}/${base_name}"
    checksum_object_key="${object_key}.sha256"
    manifest_object_key="${object_key}.manifest.json"

    postgres_password=$(read_secret "$POSTGRES_PASSWORD_FILE")
    backup_encryption_passphrase=$(read_secret "$BACKUP_ENCRYPTION_PASSPHRASE_FILE")
    escaped_postgres_password=$(printf '%s' "$postgres_password" \
        | sed -e 's/\\/\\\\/g' -e 's/:/\\:/g')
    umask 077
    printf '%s' "$backup_encryption_passphrase" > "$passphrase_snapshot_file"
    printf '%s:%s:%s:%s:%s\n' \
        "$POSTGRES_HOST" "$POSTGRES_PORT" "$POSTGRES_DB" "$POSTGRES_USER" "$escaped_postgres_password" \
        > "$pgpass_file"
    unset postgres_password escaped_postgres_password backup_encryption_passphrase
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
        | openssl enc -aes-256-cbc -salt -pbkdf2 \
            -md sha256 \
            -iter "$BACKUP_KDF_ITERATIONS" \
            -pass "file:${passphrase_snapshot_file}" \
            -out "$encrypted_file"

    [ -s "$encrypted_file" ] || fail "pg_dump produced an empty backup"
    (
        cd /tmp
        sha256sum "$base_name" > "${base_name}.sha256"
    )
    ciphertext_sha256=$(sed -e 's/[[:space:]].*$//' "$checksum_file")

    jq -n \
        --arg kind "hook2stream-postgresql-logical-backup" \
        --arg createdAt "$created_at" \
        --arg database "$POSTGRES_DB" \
        --arg keyId "$encryption_key_id" \
        --arg dumpObjectKey "$object_key" \
        --arg checksumObjectKey "$checksum_object_key" \
        --arg ciphertextSha256 "$ciphertext_sha256" \
        --argjson kdfIterations "$BACKUP_KDF_ITERATIONS" \
        '{
            schemaVersion: 1,
            kind: $kind,
            createdAt: $createdAt,
            database: $database,
            encryption: {
                keyId: $keyId,
                cipher: "aes-256-cbc",
                kdf: "pbkdf2-hmac-sha256",
                kdfIterations: $kdfIterations
            },
            encryptedDump: {
                objectKey: $dumpObjectKey,
                sha256: $ciphertextSha256
            },
            checksum: {
                objectKey: $checksumObjectKey
            }
        }' > "$manifest_file"

    aws_command s3 cp --only-show-errors \
        "$encrypted_file" "s3://${BACKUP_S3_BUCKET}/${object_key}"
    aws_command s3 cp --only-show-errors \
        "$checksum_file" "s3://${BACKUP_S3_BUCKET}/${checksum_object_key}"
    # The manifest is the completion record for this backup set and is uploaded last.
    aws_command s3 cp --only-show-errors \
        "$manifest_file" "s3://${BACKUP_S3_BUCKET}/${manifest_object_key}"

    purge_expired_versions
    success_marker_tmp="${BACKUP_SUCCESS_MARKER}.tmp"
    {
        date -u +%s
        printf '%s\n' "$encryption_key_id"
    } > "$success_marker_tmp"
    mv -f "$success_marker_tmp" "$BACKUP_SUCCESS_MARKER"
    printf '%s\n' \
        "postgres backup: uploaded s3://${BACKUP_S3_BUCKET}/${manifest_object_key}"
    send_success_heartbeat
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
: "${BACKUP_S3_ACCESS_KEY_FILE:?BACKUP_S3_ACCESS_KEY_FILE is required}"
: "${BACKUP_S3_SECRET_KEY_FILE:?BACKUP_S3_SECRET_KEY_FILE is required}"
: "${BACKUP_ENCRYPTION_PASSPHRASE_FILE:?BACKUP_ENCRYPTION_PASSPHRASE_FILE is required}"
: "${BACKUP_ENCRYPTION_KEY_ID_FILE:?BACKUP_ENCRYPTION_KEY_ID_FILE is required}"
: "${BACKUP_INTERVAL_SECONDS:=3600}"
: "${BACKUP_RETENTION_DAYS:=35}"
: "${BACKUP_RETENTION_SAFETY_SECONDS:=7200}"
: "${BACKUP_KDF_ITERATIONS:=200000}"
: "${BACKUP_SUCCESS_MARKER:=/tmp/last-successful-backup}"
: "${BACKUP_HEARTBEAT_URL_FILE:=}"

for integer_value in \
    "$BACKUP_INTERVAL_SECONDS" \
    "$BACKUP_RETENTION_DAYS" \
    "$BACKUP_RETENTION_SAFETY_SECONDS" \
    "$BACKUP_KDF_ITERATIONS"; do
    case "$integer_value" in
        *[!0-9]*|'') fail "backup interval, retention, safety, and KDF values must be integers" ;;
    esac
done
[ "$BACKUP_INTERVAL_SECONDS" -ge 300 ] || fail "BACKUP_INTERVAL_SECONDS must be at least 300"
[ "$BACKUP_RETENTION_DAYS" -ge 2 ] || fail "BACKUP_RETENTION_DAYS must be at least 2"
retention_seconds=$((BACKUP_RETENTION_DAYS * 86400))
[ "$BACKUP_RETENTION_SAFETY_SECONDS" -ge "$BACKUP_INTERVAL_SECONDS" ] \
    || fail "BACKUP_RETENTION_SAFETY_SECONDS must cover at least one backup interval"
[ "$BACKUP_RETENTION_SAFETY_SECONDS" -lt "$retention_seconds" ] \
    || fail "BACKUP_RETENTION_SAFETY_SECONDS must be shorter than retention"
[ "$BACKUP_KDF_ITERATIONS" -ge 100000 ] \
    || fail "BACKUP_KDF_ITERATIONS must be at least 100000"

export AWS_ACCESS_KEY_ID
AWS_ACCESS_KEY_ID=$(read_secret "$BACKUP_S3_ACCESS_KEY_FILE")
export AWS_SECRET_ACCESS_KEY
AWS_SECRET_ACCESS_KEY=$(read_secret "$BACKUP_S3_SECRET_KEY_FILE")
export AWS_DEFAULT_REGION=$BACKUP_S3_REGION
export AWS_REGION=$BACKUP_S3_REGION
export AWS_EC2_METADATA_DISABLED=true
export AWS_PAGER=

trap cleanup EXIT
trap 'on_signal 129' HUP
trap 'on_signal 130' INT
trap 'on_signal 143' TERM

printf '%s\n' "postgres backup: waiting for PostgreSQL"
until pg_isready -q -h "$POSTGRES_HOST" -p "$POSTGRES_PORT" -U "$POSTGRES_USER" -d "$POSTGRES_DB"; do
    sleep 5
done

case "${1:-daemon}" in
    backup-once)
        perform_backup
        ;;
    daemon)
        while :; do
            perform_backup
            sleep "$BACKUP_INTERVAL_SECONDS"
        done
        ;;
    *)
        fail "expected 'daemon' or 'backup-once'"
        ;;
esac

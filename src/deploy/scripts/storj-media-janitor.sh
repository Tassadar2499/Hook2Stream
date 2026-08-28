#!/bin/sh
set -eu

fail() { printf '%s\n' "Storj media janitor: $*" >&2; exit 1; }

: "${S3_ENDPOINT:?S3_ENDPOINT is required}"
: "${S3_REGION:?S3_REGION is required}"
: "${S3_BUCKET:?S3_BUCKET is required}"
: "${S3_ACCESS_KEY_FILE:?S3_ACCESS_KEY_FILE is required}"
: "${S3_SECRET_KEY_FILE:?S3_SECRET_KEY_FILE is required}"
: "${MEDIA_MULTIPART_MAX_AGE_SECONDS:=86400}"
: "${MEDIA_JANITOR_INTERVAL_SECONDS:=86400}"
: "${MEDIA_JANITOR_SUCCESS_MARKER:=/tmp/last-successful-media-janitor}"

for janitor_integer in "$MEDIA_MULTIPART_MAX_AGE_SECONDS" "$MEDIA_JANITOR_INTERVAL_SECONDS"; do
    case "$janitor_integer" in *[!0-9]*|'') fail "age and interval must be integers" ;; esac
done
[ "$MEDIA_MULTIPART_MAX_AGE_SECONDS" -eq 86400 ] \
    || fail "incomplete media multipart uploads must expire after exactly 24 hours"
[ "$MEDIA_JANITOR_INTERVAL_SECONDS" -ge 3600 ] \
    || fail "media janitor interval must be at least one hour"
printf '%s\n' "$S3_ENDPOINT" \
    | grep -Eq '^(https://[a-z0-9]([a-z0-9.-]*[a-z0-9])?|http://minio:9000)$' \
    || fail "S3 endpoint must be a credential-free HTTPS origin or exact local/CI MinIO origin"

for janitor_credential_file in "$S3_ACCESS_KEY_FILE" "$S3_SECRET_KEY_FILE"; do
    [ -r "$janitor_credential_file" ] && [ -s "$janitor_credential_file" ] \
        || fail "required credential file is missing or empty: $janitor_credential_file"
done
command -v hook2stream-storage-tool >/dev/null 2>&1 \
    || fail "hook2stream-storage-tool is required"
command -v jq >/dev/null 2>&1 || fail "jq is required"

storage_janitor() {
    janitor_operation=$1
    shift
    hook2stream-storage-tool "$janitor_operation" \
        --endpoint "$S3_ENDPOINT" \
        --region "$S3_REGION" \
        --bucket "$S3_BUCKET" \
        --access-key-file "$S3_ACCESS_KEY_FILE" \
        --secret-key-file "$S3_SECRET_KEY_FILE" \
        "$@"
}

run_janitor() {
    janitor_result=$(storage_janitor abort-multipart-older-than \
        --older-than-seconds "$MEDIA_MULTIPART_MAX_AGE_SECONDS") \
        || fail "could not abort expired incomplete multipart uploads"
    aborted=$(printf '%s\n' "$janitor_result" \
        | jq -er '.aborted | select(type == "number" and . >= 0)') \
        || fail "storage helper returned an invalid multipart result"

    marker_tmp=${MEDIA_JANITOR_SUCCESS_MARKER}.tmp
    date -u +%s > "$marker_tmp"
    mv -f "$marker_tmp" "$MEDIA_JANITOR_SUCCESS_MARKER"
    printf '%s\n' "Storj media janitor: aborted ${aborted} incomplete multipart upload(s) older than 24 hours"
}

case "${1:-daemon}" in
    run-once) run_janitor ;;
    daemon)
        while :; do
            run_janitor
            sleep "$MEDIA_JANITOR_INTERVAL_SECONDS"
        done
        ;;
    *) fail "expected 'daemon' or 'run-once'" ;;
esac

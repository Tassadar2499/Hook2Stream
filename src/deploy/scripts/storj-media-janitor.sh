#!/bin/sh
set -eu

fail() { printf '%s\n' "Storj media janitor: $*" >&2; exit 1; }

read_secret() {
    janitor_secret_path=$1
    [ -r "$janitor_secret_path" ] && [ -s "$janitor_secret_path" ] \
        || fail "required secret file is missing or empty: $janitor_secret_path"
    janitor_secret=$(sed -e 's/[[:space:]]*$//' "$janitor_secret_path")
    [ -n "$janitor_secret" ] || fail "required secret file is empty: $janitor_secret_path"
    case "$janitor_secret" in *[[:space:]]*) fail "credential files must contain one unpadded line" ;; esac
    printf '%s' "$janitor_secret"
}

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
    | grep -Eq '^https://[a-z0-9]([a-z0-9.-]*[a-z0-9])?$' \
    || fail "S3 endpoint must be a credential-free HTTPS origin"

umask 077
janitor_dir=$(mktemp -d)
cleanup() {
    rm -rf -- "$janitor_dir"
    unset AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY AWS_CONFIG_FILE AWS_SHARED_CREDENTIALS_FILE
}
trap cleanup EXIT HUP INT TERM

AWS_CONFIG_FILE=$janitor_dir/aws-config
AWS_SHARED_CREDENTIALS_FILE=/dev/null
export AWS_CONFIG_FILE AWS_SHARED_CREDENTIALS_FILE
printf '%s\n' \
    '[default]' \
    "region = $S3_REGION" \
    'request_checksum_calculation = when_required' \
    'response_checksum_validation = when_required' \
    's3 =' \
    '    addressing_style = path' > "$AWS_CONFIG_FILE"
AWS_ACCESS_KEY_ID=$(read_secret "$S3_ACCESS_KEY_FILE")
AWS_SECRET_ACCESS_KEY=$(read_secret "$S3_SECRET_KEY_FILE")
AWS_DEFAULT_REGION=$S3_REGION
AWS_REGION=$S3_REGION
AWS_EC2_METADATA_DISABLED=true
AWS_PAGER=
export AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY AWS_DEFAULT_REGION AWS_REGION AWS_EC2_METADATA_DISABLED AWS_PAGER

aws_janitor() {
    aws --endpoint-url "$S3_ENDPOINT" --region "$S3_REGION" "$@"
}

run_janitor() {
    cutoff_epoch=$(($(date -u +%s) - MEDIA_MULTIPART_MAX_AGE_SECONDS))
    key_marker=
    upload_id_marker=
    aborted=0

    while :; do
        page=$janitor_dir/multipart-page.json
        if [ -n "$key_marker" ]; then
            aws_janitor s3api list-multipart-uploads \
                --bucket "$S3_BUCKET" \
                --key-marker "$key_marker" \
                --upload-id-marker "$upload_id_marker" \
                --output json > "$page"
        else
            aws_janitor s3api list-multipart-uploads \
                --bucket "$S3_BUCKET" \
                --output json > "$page"
        fi

        jq -r --argjson cutoff "$cutoff_epoch" '
            (.Uploads // [])[]
            | select((.Initiated | fromdateiso8601) <= $cutoff)
            | [.Key, .UploadId]
            | @tsv
        ' "$page" > "$janitor_dir/expired.tsv"
        tab=$(printf '\t')
        while IFS="$tab" read -r multipart_key multipart_upload_id; do
            [ -n "$multipart_key" ] || continue
            [ -n "$multipart_upload_id" ] || fail "Storj returned an empty multipart upload ID"
            aws_janitor s3api abort-multipart-upload \
                --bucket "$S3_BUCKET" \
                --key "$multipart_key" \
                --upload-id "$multipart_upload_id" >/dev/null
            aborted=$((aborted + 1))
        done < "$janitor_dir/expired.tsv"

        is_truncated=$(jq -r '.IsTruncated // false' "$page")
        [ "$is_truncated" = true ] || break
        key_marker=$(jq -er '.NextKeyMarker | select(type == "string" and length > 0)' "$page") \
            || fail "truncated multipart response omitted NextKeyMarker"
        upload_id_marker=$(jq -er '.NextUploadIdMarker | select(type == "string" and length > 0)' "$page") \
            || fail "truncated multipart response omitted NextUploadIdMarker"
    done

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

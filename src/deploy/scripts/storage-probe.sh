#!/bin/sh
set -eu

fail() { printf '%s\n' "storage probe: $*" >&2; exit 1; }

: "${S3_ENDPOINT:?S3_ENDPOINT is required}"
: "${S3_REGION:?S3_REGION is required}"
: "${S3_BUCKET:?S3_BUCKET is required}"
: "${BACKUP_S3_BUCKET:?BACKUP_S3_BUCKET is required}"
: "${STORAGE_CONTRACT_KEY:?STORAGE_CONTRACT_KEY is required}"
: "${STORAGE_CONTRACT_SHA256:?STORAGE_CONTRACT_SHA256 is required}"
: "${STORAGE_PROTOCOL_VERSION:?STORAGE_PROTOCOL_VERSION is required}"
: "${S3_ACCESS_KEY_FILE:?S3_ACCESS_KEY_FILE is required}"
: "${S3_SECRET_KEY_FILE:?S3_SECRET_KEY_FILE is required}"

[ "$STORAGE_PROTOCOL_VERSION" = 1 ] || fail "only storage protocol version 1 is supported"
printf '%s\n' "$S3_ENDPOINT" \
    | grep -Eq '^(https://[a-z0-9]([a-z0-9.-]*[a-z0-9])?|http://minio:9000)$' \
    || fail "storage endpoint must be a credential-free HTTPS origin or exact local/CI MinIO origin"
[ "$STORAGE_CONTRACT_KEY" = .hook2stream/contracts/storage-v1.json ] \
    || fail "STORAGE_CONTRACT_KEY must be the canonical storage-v1 marker key"
printf '%s\n' "$STORAGE_CONTRACT_SHA256" | grep -Eq '^[0-9a-f]{64}$' \
    || fail "STORAGE_CONTRACT_SHA256 must be a lowercase SHA-256 digest"
[ -r "$S3_ACCESS_KEY_FILE" ] && [ -s "$S3_ACCESS_KEY_FILE" ] \
    || fail "S3 access-key file is missing or empty"
[ -r "$S3_SECRET_KEY_FILE" ] && [ -s "$S3_SECRET_KEY_FILE" ] \
    || fail "S3 secret-key file is missing or empty"

validate_single_line_secret() {
    probe_secret_path=$1
    awk '
        NR == 1 {
            if ($0 == "" || $0 ~ /^[[:space:]]/ || $0 ~ /[[:space:]]$/) exit 1
            next
        }
        { exit 1 }
        END { if (NR != 1) exit 1 }
    ' "$probe_secret_path" \
        || fail "S3 credential files must contain exactly one non-whitespace-padded line"
}
validate_single_line_secret "$S3_ACCESS_KEY_FILE"
validate_single_line_secret "$S3_SECRET_KEY_FILE"

for probe_tool in hook2stream-storage-tool cmp date grep jq mktemp sha256sum; do
    command -v "$probe_tool" >/dev/null 2>&1 || fail "$probe_tool is required"
done

umask 077
probe_dir=$(mktemp -d)
probe_key=".hook2stream-storage-probe/${DEPLOYMENT_ENVIRONMENT:-unknown}/$(date -u +%Y%m%dT%H%M%SZ)-$$"
object_created=false

storage_probe() {
    probe_operation=$1
    shift
    hook2stream-storage-tool "$probe_operation" \
        --endpoint "$S3_ENDPOINT" \
        --region "$S3_REGION" \
        --bucket "$S3_BUCKET" \
        --access-key-file "$S3_ACCESS_KEY_FILE" \
        --secret-key-file "$S3_SECRET_KEY_FILE" \
        "$@"
}

cleanup() {
    cleanup_status=$?
    trap - EXIT
    if [ "$object_created" = true ]; then
        storage_probe delete-object --key "$probe_key" >/dev/null 2>&1 || true
    fi
    rm -rf -- "$probe_dir"
    exit "$cleanup_status"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

contract_body=$probe_dir/storage-v1.json
storage_probe get-object \
    --key "$STORAGE_CONTRACT_KEY" \
    --output "$contract_body" \
    || fail "authenticated storage contract download failed"
actual_contract_sha256=$(sha256sum "$contract_body" | cut -d' ' -f1)
[ "$actual_contract_sha256" = "$STORAGE_CONTRACT_SHA256" ] \
    || fail "storage contract SHA-256 does not match root-owned host configuration"
jq -e \
    --arg environment "${DEPLOYMENT_ENVIRONMENT:-}" \
    --arg mediaBucket "$S3_BUCKET" \
    --arg backupBucket "$BACKUP_S3_BUCKET" \
    --argjson protocolVersion "$STORAGE_PROTOCOL_VERSION" \
    '
        .schemaVersion == 1 and
        .provider == "storj" and
        .environment == $environment and
        (.projectId | type == "string" and length > 0) and
        (.encryptionModel == "managed" or .encryptionModel == "self-managed") and
        .mediaBucket == $mediaBucket and
        .backupBucket == $backupBucket and
        .bucketLocation == "global-1" and
        .retentionMode == "storj-object-ttl-v1" and
        (.h2seReadVersions | type == "array" and index($protocolVersion) != null)
    ' "$contract_body" >/dev/null \
    || fail "storage contract does not match this environment or protocol"

payload=$probe_dir/payload
range_body=$probe_dir/range
printf '%s' 'hook2stream-storage-probe-v1' > "$payload"

storage_probe put-object --key "$probe_key" --body "$payload" >/dev/null \
    || fail "authenticated PUT probe failed"
object_created=true

content_length=$(storage_probe head-object --key "$probe_key" \
    | jq -er '.contentLength | select(type == "number")') \
    || fail "authenticated HEAD probe failed"
[ "$content_length" = 28 ] || fail "HEAD returned an unexpected object length"

storage_probe get-object \
    --key "$probe_key" \
    --range bytes=12-18 \
    --output "$range_body" \
    || fail "authenticated single-Range probe failed"
printf '%s' storage | cmp -s - "$range_body" \
    || fail "single-Range probe returned unexpected bytes"

storage_probe delete-object --key "$probe_key" >/dev/null \
    || fail "authenticated DELETE probe failed"
object_created=false

printf '%s\n' "storage probe: pinned Storj contract v1 and authenticated PUT/HEAD/single-Range/DELETE passed"

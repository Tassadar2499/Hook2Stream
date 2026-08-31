#!/bin/sh
set -eu

case "$0" in
    /*) entrypoint_path=$0 ;;
    */*) entrypoint_path=$PWD/$0 ;;
    *) entrypoint_path=$PWD/$0 ;;
esac
entrypoint_parent=${entrypoint_path%/*}
script_dir=$(CDPATH= cd -P -- "$entrypoint_parent" && pwd -P)
. "$script_dir/strict-probes.sh"

fail() { printf '%s\n' "Storj bootstrap: $*" >&2; exit 1; }
storj_initialize_operator_runtime || fail "trusted operator runtime validation failed"

: "${DEPLOYMENT_ENVIRONMENT:?DEPLOYMENT_ENVIRONMENT is required}"
: "${STORJ_PROJECT_ID:?STORJ_PROJECT_ID is required}"
: "${STORJ_ENCRYPTION_MODEL:?STORJ_ENCRYPTION_MODEL is required}"
: "${STORJ_S3_ACCESS_KEY_FILE:?STORJ_S3_ACCESS_KEY_FILE is required}"
: "${STORJ_S3_SECRET_KEY_FILE:?STORJ_S3_SECRET_KEY_FILE is required}"
: "${STORJ_S3_ENDPOINT:=https://gateway.storjshare.io}"
: "${STORJ_S3_REGION:=global}"
: "${STORAGE_CONTRACT_KEY:=.hook2stream/contracts/storage-v1.json}"

case "$DEPLOYMENT_ENVIRONMENT" in
    staging)
        media_bucket=hook2stream-com-staging-media
        backup_bucket=hook2stream-com-staging-pg-backups
        media_threshold_gib=35
        backup_threshold_gib=10
        backup_ttl_hours=168
        ;;
    production)
        media_bucket=hook2stream-com-production-media
        backup_bucket=hook2stream-com-production-pg-backups
        media_threshold_gib=160
        backup_threshold_gib=30
        backup_ttl_hours=840
        ;;
    *) fail "DEPLOYMENT_ENVIRONMENT must be staging or production" ;;
esac
[ "$STORJ_S3_ENDPOINT" = https://gateway.storjshare.io ] \
    || fail "the MVP bootstrap is pinned to https://gateway.storjshare.io"
[ "$STORJ_S3_REGION" = global ] || fail "Storj signing region must be global"
case "$STORJ_ENCRYPTION_MODEL" in
    managed|self-managed) ;;
    *) fail "STORJ_ENCRYPTION_MODEL must be managed or self-managed" ;;
esac
[ "$STORAGE_CONTRACT_KEY" = .hook2stream/contracts/storage-v1.json ] \
    || fail "storage contract key must remain canonical"
storj_validate_credential_file "$STORJ_S3_ACCESS_KEY_FILE" 'bootstrap access key' \
    || fail "bootstrap access-key file validation failed"
storj_validate_credential_file "$STORJ_S3_SECRET_KEY_FILE" 'bootstrap secret key' \
    || fail "bootstrap secret-key file validation failed"
[ "$STORJ_S3_ACCESS_KEY_FILE" != "$STORJ_S3_SECRET_KEY_FILE" ] \
    || fail "bootstrap access and secret keys must use different files"
operator_secret_dir=${STORJ_S3_ACCESS_KEY_FILE%/*}
[ "${STORJ_S3_SECRET_KEY_FILE%/*}" = "$operator_secret_dir" ] \
    || fail "bootstrap credentials must share one private operator directory"

umask 077
bootstrap_dir=$("$STORJ_ENV_BIN" -i \
    PATH="$STORJ_TRUSTED_PATH" LC_ALL=C LANG=C \
    "$STORJ_MKTEMP_BIN" -d -- \
        "$operator_secret_dir/.hook2stream-storj-bootstrap.XXXXXXXXXX") \
    || fail "cannot create private bootstrap workspace"
STORJ_OPERATOR_HOME=$bootstrap_dir/home
"$STORJ_MKDIR_BIN" -m 700 -- "$STORJ_OPERATOR_HOME"
cleanup() {
    cleanup_status=$?
    trap - EXIT HUP INT TERM
    "$STORJ_RM_BIN" -rf -- "$bootstrap_dir" || cleanup_status=1
    exit "$cleanup_status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

STORJ_AWS_CONFIG_FILE=$bootstrap_dir/aws-config
bootstrap_credentials=$bootstrap_dir/aws-credentials
printf '%s\n' \
    '[default]' \
    "region = $STORJ_S3_REGION" \
    's3 =' \
    '    addressing_style = path' > "$STORJ_AWS_CONFIG_FILE"
storj_write_aws_credentials_file \
    "$STORJ_S3_ACCESS_KEY_FILE" \
    "$STORJ_S3_SECRET_KEY_FILE" \
    "$bootstrap_credentials" \
    bootstrap || fail "operator credential validation failed"

storj_s3() { storj_run_s3_client "$bootstrap_credentials" "$@"; }
storj_jq() {
    "$STORJ_ENV_BIN" -i \
        PATH="$STORJ_TRUSTED_PATH" HOME="$STORJ_OPERATOR_HOME" \
        LC_ALL=C LANG=C \
        "$STORJ_JQ_BIN" "$@"
}

ensure_bucket() {
    bucket_name=$1
    head_stdout=$bootstrap_dir/head-${bucket_name}.stdout
    head_stderr=$bootstrap_dir/head-${bucket_name}.stderr
    if storj_s3 s3api head-bucket --bucket "$bucket_name" \
        >"$head_stdout" 2>"$head_stderr"; then
        [ ! -s "$head_stdout" ] \
            || fail "head-bucket returned unexpected output for ${bucket_name}"
        return
    else
        head_status=$?
    fi
    [ "$head_status" -ne 0 ] || fail "head-bucket failed without a failure status"
    [ ! -s "$head_stdout" ] \
        || fail "failed head-bucket returned output for ${bucket_name}"
    head_error_code=$(storj_aws_error_code_from_file \
        "$head_stderr" HeadBucket) \
        || fail "head-bucket failed without an exact AWS error for ${bucket_name}"
    storj_error_is_missing_bucket "$head_error_code" \
        || fail "head-bucket for ${bucket_name} failed with ${head_error_code}; refusing create"
    storj_s3 s3api create-bucket \
        --bucket "$bucket_name" \
        --create-bucket-configuration LocationConstraint=global-1 \
        --output json >/dev/null
    storj_s3 s3api head-bucket --bucket "$bucket_name" >/dev/null
}

ensure_bucket "$media_bucket"
ensure_bucket "$backup_bucket"

media_versioning=$(storj_s3 s3api get-bucket-versioning \
    --bucket "$media_bucket" --output json \
    | storj_jq -r '.Status // "Disabled"')
[ "$media_versioning" = Disabled ] \
    || fail "media bucket must remain unversioned (found ${media_versioning})"
storj_s3 s3api put-bucket-versioning \
    --bucket "$backup_bucket" \
    --versioning-configuration Status=Enabled >/dev/null
backup_versioning=$(storj_s3 s3api get-bucket-versioning \
    --bucket "$backup_bucket" --output json \
    | storj_jq -r '.Status // "Disabled"')
[ "$backup_versioning" = Enabled ] || fail "backup bucket versioning is not enabled"

contract_file=$bootstrap_dir/storage-v1.json
storj_jq -S -n \
    --arg environment "$DEPLOYMENT_ENVIRONMENT" \
    --arg projectId "$STORJ_PROJECT_ID" \
    --arg encryptionModel "$STORJ_ENCRYPTION_MODEL" \
    --arg mediaBucket "$media_bucket" \
    --arg backupBucket "$backup_bucket" \
    --argjson mediaThresholdGiB "$media_threshold_gib" \
    --argjson backupThresholdGiB "$backup_threshold_gib" \
    --argjson backupMaxObjectTtlHours "$backup_ttl_hours" \
    '{
        schemaVersion: 1,
        provider: "storj",
        environment: $environment,
        projectId: $projectId,
        encryptionModel: $encryptionModel,
        bucketLocation: "global-1",
        mediaBucket: $mediaBucket,
        backupBucket: $backupBucket,
        mediaThresholdGiB: $mediaThresholdGiB,
        backupThresholdGiB: $backupThresholdGiB,
        h2seReadVersions: [1],
        temporaryObjectTtlHours: 24,
        backupMaxObjectTtlHours: $backupMaxObjectTtlHours,
        retentionMode: "storj-object-ttl-v1"
    }' > "$contract_file"
contract_sha256=$("$STORJ_SHA256SUM_BIN" "$contract_file")
contract_sha256=${contract_sha256%% *}
storj_s3 s3api put-object \
    --bucket "$media_bucket" \
    --key "$STORAGE_CONTRACT_KEY" \
    --body "$contract_file" \
    --content-type application/json \
    --metadata "sha256=${contract_sha256}" \
    --output json >/dev/null

storj_require_private_anonymous_get \
    "${STORJ_S3_ENDPOINT}/${media_bucket}/${STORAGE_CONTRACT_KEY}" \
    || fail "private storage marker did not return the exact anonymous 403/404 contract"

printf '%s\n' \
    "Storj bootstrap: ${DEPLOYMENT_ENVIRONMENT} buckets and private storage contract are ready" \
    "STORAGE_CONTRACT_KEY=${STORAGE_CONTRACT_KEY}" \
    "STORAGE_CONTRACT_SHA256=${contract_sha256}"

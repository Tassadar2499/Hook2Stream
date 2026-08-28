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

fail() { printf '%s\n' "Storj acceptance: $*" >&2; exit 1; }
storj_initialize_operator_runtime || fail "trusted operator runtime validation failed"

: "${DEPLOYMENT_ENVIRONMENT:?DEPLOYMENT_ENVIRONMENT is required}"
: "${MEDIA_S3_ACCESS_KEY_FILE:?MEDIA_S3_ACCESS_KEY_FILE is required}"
: "${MEDIA_S3_SECRET_KEY_FILE:?MEDIA_S3_SECRET_KEY_FILE is required}"
: "${BACKUP_S3_ACCESS_KEY_FILE:?BACKUP_S3_ACCESS_KEY_FILE is required}"
: "${BACKUP_S3_SECRET_KEY_FILE:?BACKUP_S3_SECRET_KEY_FILE is required}"
: "${STORAGE_CONTRACT_SHA256:?STORAGE_CONTRACT_SHA256 is required}"
: "${STORJ_S3_ENDPOINT:=https://gateway.storjshare.io}"
: "${STORJ_S3_REGION:=global}"

case "$DEPLOYMENT_ENVIRONMENT" in
    staging)
        media_bucket=hook2stream-com-staging-media
        backup_bucket=hook2stream-com-staging-pg-backups
        other_media_bucket=hook2stream-com-production-media
        other_backup_bucket=hook2stream-com-production-pg-backups
        ;;
    production)
        media_bucket=hook2stream-com-production-media
        backup_bucket=hook2stream-com-production-pg-backups
        other_media_bucket=hook2stream-com-staging-media
        other_backup_bucket=hook2stream-com-staging-pg-backups
        ;;
    *) fail "DEPLOYMENT_ENVIRONMENT must be staging or production" ;;
esac
[ "$STORJ_S3_ENDPOINT" = https://gateway.storjshare.io ] || fail "unexpected Storj endpoint"
[ "$STORJ_S3_REGION" = global ] || fail "unexpected Storj signing region"
printf '%s\n' "$STORAGE_CONTRACT_SHA256" \
    | "$STORJ_ENV_BIN" -i PATH="$STORJ_TRUSTED_PATH" LC_ALL=C LANG=C \
        "$STORJ_GREP_BIN" -Eq '^[0-9a-f]{64}$' \
    || fail "STORAGE_CONTRACT_SHA256 must be a lowercase SHA-256 digest"
storj_validate_credential_file "$MEDIA_S3_ACCESS_KEY_FILE" 'media access key' \
    || fail "media access-key file validation failed"
storj_validate_credential_file "$MEDIA_S3_SECRET_KEY_FILE" 'media secret key' \
    || fail "media secret-key file validation failed"
storj_validate_credential_file "$BACKUP_S3_ACCESS_KEY_FILE" 'backup access key' \
    || fail "backup access-key file validation failed"
storj_validate_credential_file "$BACKUP_S3_SECRET_KEY_FILE" 'backup secret key' \
    || fail "backup secret-key file validation failed"
[ "$MEDIA_S3_ACCESS_KEY_FILE" != "$MEDIA_S3_SECRET_KEY_FILE" ] \
    && [ "$MEDIA_S3_ACCESS_KEY_FILE" != "$BACKUP_S3_ACCESS_KEY_FILE" ] \
    && [ "$MEDIA_S3_ACCESS_KEY_FILE" != "$BACKUP_S3_SECRET_KEY_FILE" ] \
    && [ "$MEDIA_S3_SECRET_KEY_FILE" != "$BACKUP_S3_ACCESS_KEY_FILE" ] \
    && [ "$MEDIA_S3_SECRET_KEY_FILE" != "$BACKUP_S3_SECRET_KEY_FILE" ] \
    && [ "$BACKUP_S3_ACCESS_KEY_FILE" != "$BACKUP_S3_SECRET_KEY_FILE" ] \
    || fail "media and backup credentials must use four distinct files"
operator_secret_dir=${MEDIA_S3_ACCESS_KEY_FILE%/*}
for credential_file in \
    "$MEDIA_S3_SECRET_KEY_FILE" \
    "$BACKUP_S3_ACCESS_KEY_FILE" \
    "$BACKUP_S3_SECRET_KEY_FILE"; do
    [ "${credential_file%/*}" = "$operator_secret_dir" ] \
        || fail "all acceptance credentials must share one private operator directory"
done

umask 077
acceptance_dir=$("$STORJ_ENV_BIN" -i \
    PATH="$STORJ_TRUSTED_PATH" LC_ALL=C LANG=C \
    "$STORJ_MKTEMP_BIN" -d -- \
        "$operator_secret_dir/.hook2stream-storj-acceptance.XXXXXXXXXX") \
    || fail "cannot create private acceptance workspace"
STORJ_OPERATOR_HOME=$acceptance_dir/home
"$STORJ_MKDIR_BIN" -m 700 -- "$STORJ_OPERATOR_HOME"
media_key=.hook2stream-acceptance/${DEPLOYMENT_ENVIRONMENT}/media-$$
multipart_key=.hook2stream-acceptance/${DEPLOYMENT_ENVIRONMENT}/multipart-$$
backup_key=hook2stream/acceptance/${DEPLOYMENT_ENVIRONMENT}/backup-$$.age
multipart_upload_id=
media_object_created=false

cleanup() {
    cleanup_status=$?
    trap - EXIT HUP INT TERM
    if [ "$media_object_created" = true ]; then
        media_aws s3api delete-object --bucket "$media_bucket" --key "$media_key" >/dev/null 2>&1 || true
    fi
    if [ -n "$multipart_upload_id" ]; then
        media_aws s3api abort-multipart-upload \
            --bucket "$media_bucket" --key "$multipart_key" --upload-id "$multipart_upload_id" \
            >/dev/null 2>&1 || true
    fi
    "$STORJ_RM_BIN" -rf -- "$acceptance_dir" || cleanup_status=1
    exit "$cleanup_status"
}
trap cleanup EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM

STORJ_AWS_CONFIG_FILE=$acceptance_dir/aws-config
media_credentials=$acceptance_dir/media-credentials
backup_credentials=$acceptance_dir/backup-credentials
printf '%s\n' \
    '[default]' \
    "region = $STORJ_S3_REGION" \
    'request_checksum_calculation = when_required' \
    'response_checksum_validation = when_required' \
    's3 =' \
    '    addressing_style = path' > "$STORJ_AWS_CONFIG_FILE"

storj_write_aws_credentials_file \
    "$MEDIA_S3_ACCESS_KEY_FILE" "$MEDIA_S3_SECRET_KEY_FILE" \
    "$media_credentials" media || fail "media credential validation failed"
storj_write_aws_credentials_file \
    "$BACKUP_S3_ACCESS_KEY_FILE" "$BACKUP_S3_SECRET_KEY_FILE" \
    "$backup_credentials" backup || fail "backup credential validation failed"

media_aws() { storj_run_aws "$media_credentials" "$@"; }
backup_aws() { storj_run_aws "$backup_credentials" "$@"; }
storj_jq() {
    "$STORJ_ENV_BIN" -i \
        PATH="$STORJ_TRUSTED_PATH" HOME="$STORJ_OPERATOR_HOME" \
        LC_ALL=C LANG=C \
        "$STORJ_JQ_BIN" "$@"
}

require_exact_permission_denial() {
    denial_error_file=$1
    denial_operation=$2
    denial_context=$3
    if ! storj_require_permission_denied_error \
        "$denial_error_file" "$denial_operation"; then
        denial_code=$(storj_aws_error_code_from_file \
            "$denial_error_file" "$denial_operation" 2>/dev/null || printf '%s' ambiguous)
        fail "${denial_context} returned ${denial_code}, not an exact permission denial"
    fi
}

expect_forbidden_put() {
    forbidden_role=$1
    forbidden_bucket=$2
    forbidden_key=$3
    case "$forbidden_role" in
        media) forbidden_aws=media_aws ;;
        backup) forbidden_aws=backup_aws ;;
        *) fail "unknown credential role ${forbidden_role}" ;;
    esac
    forbidden_error=$acceptance_dir/forbidden-put.stderr
    if "$forbidden_aws" s3api put-object \
        --bucket "$forbidden_bucket" \
        --key "$forbidden_key" \
        --body "$payload" \
        --metadata Object-Expires=+1h >/dev/null 2>"$forbidden_error"; then
        "$forbidden_aws" s3api delete-object \
            --bucket "$forbidden_bucket" --key "$forbidden_key" \
            >/dev/null 2>&1 || true
        fail "${forbidden_role} credential can write forbidden bucket ${forbidden_bucket}"
    fi
    require_exact_permission_denial \
        "$forbidden_error" PutObject \
        "${forbidden_role} forbidden PUT to ${forbidden_bucket}"
}

expect_forbidden_head() {
    forbidden_role=$1
    forbidden_bucket=$2
    forbidden_key=$3
    case "$forbidden_role" in
        media) forbidden_aws=media_aws ;;
        backup) forbidden_aws=backup_aws ;;
        *) fail "unknown credential role ${forbidden_role}" ;;
    esac
    forbidden_error=$acceptance_dir/forbidden-head.stderr
    if "$forbidden_aws" s3api head-object \
        --bucket "$forbidden_bucket" --key "$forbidden_key" \
        >/dev/null 2>"$forbidden_error"; then
        fail "${forbidden_role} credential can read forbidden bucket ${forbidden_bucket}"
    fi
    require_exact_permission_denial \
        "$forbidden_error" HeadObject \
        "${forbidden_role} forbidden HEAD from ${forbidden_bucket}"
}

expect_forbidden_delete() {
    forbidden_role=$1
    forbidden_bucket=$2
    forbidden_key=$3
    case "$forbidden_role" in
        media) forbidden_aws=media_aws ;;
        backup) forbidden_aws=backup_aws ;;
        *) fail "unknown credential role ${forbidden_role}" ;;
    esac
    forbidden_error=$acceptance_dir/forbidden-delete.stderr
    if "$forbidden_aws" s3api delete-object \
        --bucket "$forbidden_bucket" --key "$forbidden_key" \
        >/dev/null 2>"$forbidden_error"; then
        fail "${forbidden_role} credential can delete from forbidden bucket ${forbidden_bucket}"
    fi
    require_exact_permission_denial \
        "$forbidden_error" DeleteObject \
        "${forbidden_role} forbidden DELETE from ${forbidden_bucket}"
}

expect_forbidden_list() {
    forbidden_role=$1
    forbidden_bucket=$2
    case "$forbidden_role" in
        media) forbidden_aws=media_aws ;;
        backup) forbidden_aws=backup_aws ;;
        *) fail "unknown credential role ${forbidden_role}" ;;
    esac
    forbidden_error=$acceptance_dir/forbidden-list.stderr
    if "$forbidden_aws" s3api list-objects-v2 \
        --bucket "$forbidden_bucket" --max-keys 1 \
        >/dev/null 2>"$forbidden_error"; then
        fail "${forbidden_role} credential can list forbidden bucket ${forbidden_bucket}"
    fi
    require_exact_permission_denial \
        "$forbidden_error" ListObjectsV2 \
        "${forbidden_role} forbidden LIST on ${forbidden_bucket}"
}

contract=$acceptance_dir/storage-v1.json
media_aws s3api get-object \
    --bucket "$media_bucket" --key .hook2stream/contracts/storage-v1.json "$contract" >/dev/null
contract_sha256=$("$STORJ_SHA256SUM_BIN" "$contract")
contract_sha256=${contract_sha256%% *}
[ "$contract_sha256" = "$STORAGE_CONTRACT_SHA256" ] \
    || fail "storage contract digest mismatch"

payload=$acceptance_dir/payload
range_body=$acceptance_dir/range
printf '%s' hook2stream-storj-acceptance-v1 > "$payload"
media_aws s3api put-object --bucket "$media_bucket" --key "$media_key" --body "$payload" >/dev/null
media_object_created=true
[ "$(media_aws s3api head-object --bucket "$media_bucket" --key "$media_key" --query ContentLength --output text)" = 31 ] \
    || fail "media HEAD returned an unexpected length"
media_aws s3api get-object \
    --bucket "$media_bucket" --key "$media_key" --range bytes=12-16 "$range_body" >/dev/null
printf '%s' storj | "$STORJ_CMP_BIN" -s - "$range_body" \
    || fail "media Range returned unexpected bytes"

multipart_upload_id=$(media_aws s3api create-multipart-upload \
    --bucket "$media_bucket" --key "$multipart_key" --query UploadId --output text)
[ -n "$multipart_upload_id" ] && [ "$multipart_upload_id" != None ] \
    || fail "multipart create returned no upload ID"
printf '%s' multipart-part > "$acceptance_dir/part"
media_aws s3api upload-part \
    --bucket "$media_bucket" --key "$multipart_key" --upload-id "$multipart_upload_id" \
    --part-number 1 --body "$acceptance_dir/part" >/dev/null
media_aws s3api list-multipart-uploads --bucket "$media_bucket" --output json \
    | storj_jq -e --arg key "$multipart_key" --arg uploadId "$multipart_upload_id" \
        'any((.Uploads // [])[]; .Key == $key and .UploadId == $uploadId)' >/dev/null \
    || fail "incomplete multipart upload was not listed"
media_aws s3api abort-multipart-upload \
    --bucket "$media_bucket" --key "$multipart_key" --upload-id "$multipart_upload_id" >/dev/null
multipart_upload_id=

[ "$(backup_aws s3api get-bucket-versioning --bucket "$backup_bucket" --query Status --output text)" = Enabled ] \
    || fail "backup bucket versioning is not enabled"
backup_version_id=$(backup_aws s3api put-object \
    --bucket "$backup_bucket" --key "$backup_key" --body "$payload" \
    --query VersionId --output text)
[ -n "$backup_version_id" ] && [ "$backup_version_id" != None ] \
    || fail "versioned backup PUT returned no VersionId"
backup_aws s3api head-object \
    --bucket "$backup_bucket" --key "$backup_key" --version-id "$backup_version_id" \
    >/dev/null || fail "backup writer cannot read its own versioned object"
media_aws s3api list-objects-v2 \
    --bucket "$media_bucket" --prefix .hook2stream-acceptance/ --max-keys 1 \
    >/dev/null || fail "media credential lacks required List permission"
backup_aws s3api list-objects-v2 \
    --bucket "$backup_bucket" --prefix hook2stream/acceptance/ --max-keys 1 \
    >/dev/null || fail "backup credential lacks required List permission"
expect_forbidden_delete backup "$backup_bucket" "$backup_key"

expect_forbidden_head backup "$media_bucket" "$media_key"
expect_forbidden_head media "$backup_bucket" "$backup_key"
expect_forbidden_head media "$other_media_bucket" .hook2stream/contracts/storage-v1.json
expect_forbidden_head backup "$other_media_bucket" .hook2stream/contracts/storage-v1.json
expect_forbidden_delete backup "$media_bucket" "$media_key"
expect_forbidden_delete media "$backup_bucket" "$backup_key"

for forbidden_bucket in "$backup_bucket" "$other_media_bucket" "$other_backup_bucket"; do
    expect_forbidden_list media "$forbidden_bucket"
done
for forbidden_bucket in "$media_bucket" "$other_media_bucket" "$other_backup_bucket"; do
    expect_forbidden_list backup "$forbidden_bucket"
done

for forbidden_bucket in "$backup_bucket" "$other_media_bucket" "$other_backup_bucket"; do
    expect_forbidden_put media "$forbidden_bucket" \
        ".hook2stream-acceptance/${DEPLOYMENT_ENVIRONMENT}/forbidden-media-write-$$"
done
for forbidden_bucket in "$media_bucket" "$other_media_bucket" "$other_backup_bucket"; do
    expect_forbidden_put backup "$forbidden_bucket" \
        ".hook2stream-acceptance/${DEPLOYMENT_ENVIRONMENT}/forbidden-backup-write-$$"
done

media_aws s3api delete-object --bucket "$media_bucket" --key "$media_key" >/dev/null
media_object_created=false

storj_require_private_anonymous_get \
    "${STORJ_S3_ENDPOINT}/${media_bucket}/.hook2stream/contracts/storage-v1.json" \
    || fail "media marker did not return the exact anonymous 403/404 contract"

printf '%s\n' \
    "Storj acceptance: ${DEPLOYMENT_ENVIRONMENT} marker, privacy, read/write/list/delete role isolation, SigV4, Range, multipart abort, versioning, and no-Delete passed" \
    "Storj acceptance: backup probe ${backup_key} version ${backup_version_id} remains for credential-enforced TTL expiry verification"

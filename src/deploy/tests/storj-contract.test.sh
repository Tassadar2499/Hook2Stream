#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
temporary_dir=$(mktemp -d)
trap 'rm -rf "$temporary_dir"' EXIT HUP INT TERM

fail() { printf '%s\n' "Storj contract test: $*" >&2; exit 1; }

bootstrap=$deployment_dir/storj/bootstrap-buckets.sh
acceptance=$deployment_dir/storj/live-acceptance.sh
janitor=$deployment_dir/scripts/storj-media-janitor.sh
janitor_health=$deployment_dir/scripts/storj-media-janitor-healthcheck.sh

for required_file in "$bootstrap" "$acceptance" "$janitor" "$janitor_health"; do
    [ -f "$required_file" ] || fail "missing ${required_file}"
    sh -n "$required_file"
done

for fixed_bucket in \
    hook2stream-com-staging-media \
    hook2stream-com-staging-pg-backups \
    hook2stream-com-production-media \
    hook2stream-com-production-pg-backups; do
    grep -Fq "$fixed_bucket" "$bootstrap" || fail "bootstrap omits ${fixed_bucket}"
done
grep -Fq 'LocationConstraint=global-1' "$bootstrap" \
    || fail "bootstrap does not create the global-1 bucket location"
grep -Fq 'versioning-configuration Status=Enabled' "$bootstrap" \
    || fail "bootstrap does not enable backup versioning"
grep -Fq 'retentionMode: "storj-object-ttl-v1"' "$bootstrap" \
    || fail "bootstrap marker omits Storj TTL retention mode"
if grep -R -E 'put-bucket-lifecycle|PutBucketLifecycle' "$deployment_dir/storj"; then
    fail "Storj tooling attempts unsupported bucket lifecycle mutations"
fi
grep -Fq 'disallow-deletes' "$deployment_dir/storj/README.md" \
    || fail "operator instructions omit backup no-Delete grant"
grep -Fq 'max-object-ttl 168h' "$deployment_dir/storj/README.md" \
    && grep -Fq '840h' "$deployment_dir/storj/README.md" \
    || fail "operator instructions omit environment-specific backup TTL grants"
grep -Fq 'media credential lacks required List permission' "$acceptance" \
    && grep -Fq 'backup credential lacks required List permission' "$acceptance" \
    && grep -Fq 'expect_forbidden_head backup "$media_bucket" "$media_key"' "$acceptance" \
    && grep -Fq 'expect_forbidden_head media "$backup_bucket" "$backup_key"' "$acceptance" \
    && grep -Fq 'expect_forbidden_put media' "$acceptance" \
    && grep -Fq 'expect_forbidden_put backup' "$acceptance" \
    || fail "live acceptance does not prove read/write/list cross-role denial"

mock_bin=$temporary_dir/bin
secret_dir=$temporary_dir/secrets
state_dir=$temporary_dir/state
mkdir -p "$mock_bin" "$secret_dir" "$state_dir"

cat > "$mock_bin/hook2stream-storage-tool" <<'EOF'
#!/bin/sh
set -eu
operation=$1
shift
endpoint=
region=
bucket=
access_key_file=
secret_key_file=
older_than_seconds=
while [ "$#" -gt 0 ]; do
    case "$1" in
        --endpoint) endpoint=$2; shift 2 ;;
        --region) region=$2; shift 2 ;;
        --bucket) bucket=$2; shift 2 ;;
        --access-key-file) access_key_file=$2; shift 2 ;;
        --secret-key-file) secret_key_file=$2; shift 2 ;;
        --older-than-seconds) older_than_seconds=$2; shift 2 ;;
        *) shift ;;
    esac
done
[ "$operation" = abort-multipart-older-than ] || exit 41
[ "$endpoint" = https://gateway.storjshare.io ] || exit 42
[ "$region" = global ] && [ "$bucket" = hook2stream-com-staging-media ] || exit 43
[ "$(cat "$access_key_file")" = media-access ] || exit 44
[ "$(cat "$secret_key_file")" = media-secret ] || exit 45
[ "$older_than_seconds" = 86400 ] || exit 46
printf '%s\n' "$operation" >> "$JANITOR_OPERATION_LOG"
printf '%s\n' '{"aborted":1}'
EOF

cat > "$mock_bin/jq" <<'EOF'
#!/usr/bin/env node
const fs = require("node:fs");
const args = process.argv.slice(2);
if (args[0] !== "-er") process.exit(47);
const result = JSON.parse(fs.readFileSync(0, "utf8"));
if (!Number.isInteger(result.aborted) || result.aborted < 0) process.exit(48);
process.stdout.write(`${result.aborted}\n`);
EOF
chmod 0755 "$mock_bin/hook2stream-storage-tool" "$mock_bin/jq"

printf '%s\n' media-access > "$secret_dir/access"
printf '%s\n' media-secret > "$secret_dir/secret"
: > "$state_dir/operations"

PATH="$mock_bin:$PATH" \
JANITOR_OPERATION_LOG=$state_dir/operations \
S3_ENDPOINT=https://gateway.storjshare.io \
S3_REGION=global \
S3_BUCKET=hook2stream-com-staging-media \
S3_ACCESS_KEY_FILE=$secret_dir/access \
S3_SECRET_KEY_FILE=$secret_dir/secret \
MEDIA_MULTIPART_MAX_AGE_SECONDS=86400 \
MEDIA_JANITOR_INTERVAL_SECONDS=86400 \
MEDIA_JANITOR_SUCCESS_MARKER=$state_dir/last-success \
    sh "$janitor" run-once >/dev/null

[ "$(cat "$state_dir/operations")" = abort-multipart-older-than ] \
    || fail "janitor performed unexpected S3 operations"
MEDIA_JANITOR_SUCCESS_MARKER=$state_dir/last-success \
MEDIA_JANITOR_MAX_AGE_SECONDS=93600 \
    sh "$janitor_health"

if PATH="$mock_bin:$PATH" \
    JANITOR_OPERATION_LOG=$state_dir/operations \
    S3_ENDPOINT=https://gateway.storjshare.io \
    S3_REGION=global \
    S3_BUCKET=hook2stream-com-staging-media \
    S3_ACCESS_KEY_FILE=$secret_dir/access \
    S3_SECRET_KEY_FILE=$secret_dir/secret \
    MEDIA_MULTIPART_MAX_AGE_SECONDS=7200 \
    MEDIA_JANITOR_INTERVAL_SECONDS=86400 \
        sh "$janitor" run-once >/dev/null 2>&1; then
    fail "janitor accepted a multipart age other than 24 hours"
fi

printf '%s\n' "Storj contract test: bootstrap, grants, marker, versioning, and 24-hour multipart janitor passed"

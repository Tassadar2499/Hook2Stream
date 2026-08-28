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

cat > "$mock_bin/aws" <<'EOF'
#!/bin/sh
set -eu
[ -f "$AWS_CONFIG_FILE" ] \
    && grep -Fq 'addressing_style = path' "$AWS_CONFIG_FILE" \
    && grep -Fq 'request_checksum_calculation = when_required' "$AWS_CONFIG_FILE" \
    && grep -Fq 'response_checksum_validation = when_required' "$AWS_CONFIG_FILE" \
    || exit 41
operation=
previous=
key=
upload_id=
for argument in "$@"; do
    [ "$previous" != s3api ] || operation=$argument
    [ "$previous" != --key ] || key=$argument
    [ "$previous" != --upload-id ] || upload_id=$argument
    previous=$argument
done
printf '%s\n' "$operation" >> "$JANITOR_OPERATION_LOG"
case "$operation" in
    list-multipart-uploads)
        printf '%s\n' '{"IsTruncated":false,"Uploads":[{"Key":"staging/old.h2se/data","UploadId":"old-upload","Initiated":"2020-01-01T00:00:00Z"},{"Key":"staging/new.h2se/data","UploadId":"new-upload","Initiated":"2999-01-01T00:00:00Z"}]}'
        ;;
    abort-multipart-upload)
        printf '%s\t%s\n' "$key" "$upload_id" >> "$JANITOR_ABORT_LOG"
        ;;
    *) exit 42 ;;
esac
EOF

cat > "$mock_bin/jq" <<'EOF'
#!/usr/bin/env node
const fs = require("node:fs");
const args = process.argv.slice(2);
const page = JSON.parse(fs.readFileSync(args.at(-1), "utf8"));
const expression = args.at(-2);
if (args.includes("--argjson")) {
  const cutoff = Number(args[args.indexOf("--argjson") + 2]);
  for (const upload of page.Uploads ?? []) {
    if (Date.parse(upload.Initiated) / 1000 <= cutoff) {
      process.stdout.write(`${upload.Key}\t${upload.UploadId}\n`);
    }
  }
} else if (expression.includes("IsTruncated")) {
  process.stdout.write(`${page.IsTruncated ?? false}\n`);
} else {
  process.exit(43);
}
EOF
chmod 0755 "$mock_bin/aws" "$mock_bin/jq"

printf '%s\n' media-access > "$secret_dir/access"
printf '%s\n' media-secret > "$secret_dir/secret"
: > "$state_dir/operations"
: > "$state_dir/aborts"

PATH="$mock_bin:$PATH" \
JANITOR_OPERATION_LOG=$state_dir/operations \
JANITOR_ABORT_LOG=$state_dir/aborts \
S3_ENDPOINT=https://gateway.storjshare.io \
S3_REGION=global \
S3_BUCKET=hook2stream-com-staging-media \
S3_ACCESS_KEY_FILE=$secret_dir/access \
S3_SECRET_KEY_FILE=$secret_dir/secret \
MEDIA_MULTIPART_MAX_AGE_SECONDS=86400 \
MEDIA_JANITOR_INTERVAL_SECONDS=86400 \
MEDIA_JANITOR_SUCCESS_MARKER=$state_dir/last-success \
    sh "$janitor" run-once >/dev/null

expected_abort=$(printf 'staging/old.h2se/data\told-upload')
[ "$(cat "$state_dir/aborts")" = "$expected_abort" ] \
    || fail "janitor did not abort only the incomplete multipart older than 24 hours"
[ "$(cat "$state_dir/operations")" = "list-multipart-uploads
abort-multipart-upload" ] \
    || fail "janitor performed unexpected S3 operations"
MEDIA_JANITOR_SUCCESS_MARKER=$state_dir/last-success \
MEDIA_JANITOR_MAX_AGE_SECONDS=93600 \
    sh "$janitor_health"

if PATH="$mock_bin:$PATH" \
    JANITOR_OPERATION_LOG=$state_dir/operations \
    JANITOR_ABORT_LOG=$state_dir/aborts \
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

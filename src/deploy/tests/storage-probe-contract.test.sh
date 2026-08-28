#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
temporary_dir=$(mktemp -d)
trap 'rm -rf "$temporary_dir"' EXIT HUP INT TERM

fail_test() {
    printf '%s\n' "storage probe test: $*" >&2
    exit 1
}

mock_bin=$temporary_dir/bin
mkdir -p "$mock_bin"

cat > "$mock_bin/hook2stream-storage-tool" <<'EOF'
#!/bin/sh
set -eu
operation=$1
shift
output_file=
object_key=
range_header=
endpoint=
region=
bucket=
access_key_file=
secret_key_file=
while [ "$#" -gt 0 ]; do
    case "$1" in
        --output) output_file=$2; shift 2 ;;
        --key) object_key=$2; shift 2 ;;
        --range) range_header=$2; shift 2 ;;
        --endpoint) endpoint=$2; shift 2 ;;
        --region) region=$2; shift 2 ;;
        --bucket) bucket=$2; shift 2 ;;
        --access-key-file) access_key_file=$2; shift 2 ;;
        --secret-key-file) secret_key_file=$2; shift 2 ;;
        *) shift ;;
    esac
done
[ "$endpoint" = https://gateway.storjshare.io ] || exit 92
[ "$region" = global ] && [ "$bucket" = hook2stream-com-staging-media ] || exit 93
[ "$(cat "$access_key_file")" = probe-access-key ] || exit 94
[ "$(cat "$secret_key_file")" = probe-secret-key ] || exit 95
printf '%s\n' "$operation" >> "$AWS_OPERATION_LOG"
case "$operation" in
    put-object) printf '%s\n' '{"versionId":""}' ;;
    delete-object) printf '%s\n' '{"deleted":true}' ;;
    head-object) printf '%s\n' '{"contentLength":28}' ;;
    get-object)
        if [ "$object_key" = .hook2stream/contracts/storage-v1.json ]; then
            cp "$STORAGE_MARKER_SOURCE" "$output_file"
        else
            [ "$range_header" = bytes=12-18 ] || exit 96
            printf '%s' storage > "$output_file"
        fi
        ;;
    *) exit 91 ;;
esac
EOF
cat > "$mock_bin/jq" <<'EOF'
#!/usr/bin/env node
const fs = require("node:fs");
const args = process.argv.slice(2);
if (args[0] === "-er") {
  const response = JSON.parse(fs.readFileSync(0, "utf8"));
  if (!Number.isInteger(response.contentLength)) process.exit(90);
  process.stdout.write(`${response.contentLength}\n`);
  process.exit(0);
}
if (args[0] !== "-e") process.exit(90);
const values = {};
for (let i = 1; i < args.length - 2;) {
  const kind = args[i];
  if (kind !== "--arg" && kind !== "--argjson") break;
  values[args[i + 1]] = kind === "--argjson" ? JSON.parse(args[i + 2]) : args[i + 2];
  i += 3;
}
const marker = JSON.parse(fs.readFileSync(args.at(-1), "utf8"));
const valid = marker.schemaVersion === 1 &&
  marker.provider === "storj" &&
  marker.environment === values.environment &&
  typeof marker.projectId === "string" && marker.projectId.length > 0 &&
  marker.mediaBucket === values.mediaBucket &&
  marker.backupBucket === values.backupBucket &&
  marker.bucketLocation === "global-1" &&
  marker.retentionMode === "storj-object-ttl-v1" &&
  Array.isArray(marker.h2seReadVersions) && marker.h2seReadVersions.includes(values.protocolVersion);
process.exit(valid ? 0 : 1);
EOF
chmod 0755 "$mock_bin/hook2stream-storage-tool" "$mock_bin/jq"

access_key_file=$temporary_dir/access
secret_key_file=$temporary_dir/secret
printf '%s\n' probe-access-key > "$access_key_file"
printf '%s\n' probe-secret-key > "$secret_key_file"
operation_log=$temporary_dir/operations
: > "$operation_log"
marker_file=$temporary_dir/storage-v1.json
printf '%s\n' '{"schemaVersion":1,"provider":"storj","environment":"staging","projectId":"project-staging","bucketLocation":"global-1","mediaBucket":"hook2stream-com-staging-media","backupBucket":"hook2stream-com-staging-pg-backups","h2seReadVersions":[1],"retentionMode":"storj-object-ttl-v1"}' > "$marker_file"
marker_sha256=$(sha256sum "$marker_file" | cut -d' ' -f1)

PATH="$mock_bin:$PATH" \
AWS_OPERATION_LOG=$operation_log \
STORAGE_MARKER_SOURCE=$marker_file \
DEPLOYMENT_ENVIRONMENT=staging \
S3_ENDPOINT=https://gateway.storjshare.io \
S3_REGION=global \
S3_BUCKET=hook2stream-com-staging-media \
BACKUP_S3_BUCKET=hook2stream-com-staging-pg-backups \
STORAGE_CONTRACT_KEY=.hook2stream/contracts/storage-v1.json \
STORAGE_CONTRACT_SHA256=$marker_sha256 \
STORAGE_PROTOCOL_VERSION=1 \
S3_ACCESS_KEY_FILE=$access_key_file \
S3_SECRET_KEY_FILE=$secret_key_file \
    sh "$deployment_dir/scripts/storage-probe.sh" >/dev/null

expected_operations='get-object
put-object
head-object
get-object
delete-object'
[ "$(cat "$operation_log")" = "$expected_operations" ] \
    || fail_test "S3 operations were not PUT, HEAD, single Range GET, DELETE in order"

: > "$operation_log"
if PATH="$mock_bin:$PATH" \
    AWS_OPERATION_LOG=$operation_log \
    STORAGE_MARKER_SOURCE=$marker_file \
    DEPLOYMENT_ENVIRONMENT=staging \
    S3_ENDPOINT=https://gateway.storjshare.io \
    S3_REGION=global \
    S3_BUCKET=hook2stream-com-staging-media \
    BACKUP_S3_BUCKET=hook2stream-com-staging-pg-backups \
    STORAGE_CONTRACT_KEY=.hook2stream/contracts/storage-v1.json \
    STORAGE_CONTRACT_SHA256=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa \
    STORAGE_PROTOCOL_VERSION=1 \
    S3_ACCESS_KEY_FILE=$access_key_file \
    S3_SECRET_KEY_FILE=$secret_key_file \
        sh "$deployment_dir/scripts/storage-probe.sh" >/dev/null 2>&1; then
    fail_test "wrong storage contract digest was accepted"
fi
[ "$(cat "$operation_log")" = get-object ] \
    || fail_test "storage mutations ran before contract validation"

printf '%s\n%s\n' first second > "$access_key_file"
: > "$operation_log"
if PATH="$mock_bin:$PATH" \
    AWS_OPERATION_LOG=$operation_log \
    STORAGE_MARKER_SOURCE=$marker_file \
    DEPLOYMENT_ENVIRONMENT=staging \
    S3_ENDPOINT=https://gateway.storjshare.io \
    S3_REGION=global \
    S3_BUCKET=hook2stream-com-staging-media \
    BACKUP_S3_BUCKET=hook2stream-com-staging-pg-backups \
    STORAGE_CONTRACT_KEY=.hook2stream/contracts/storage-v1.json \
    STORAGE_CONTRACT_SHA256=$marker_sha256 \
    STORAGE_PROTOCOL_VERSION=1 \
    S3_ACCESS_KEY_FILE=$access_key_file \
    S3_SECRET_KEY_FILE=$secret_key_file \
        sh "$deployment_dir/scripts/storage-probe.sh" >/dev/null 2>&1; then
    fail_test "multi-line access key was accepted"
fi
[ ! -s "$operation_log" ] \
    || fail_test "S3 operations ran with a malformed credential file"
printf '%s\n' probe-access-key > "$access_key_file"

probe_line=$(grep -n '^current_stage=remote-storage-probe$' "$deployment_dir/scripts/deploy-release.sh" | cut -d: -f1)
pre_replacement_backup_line=$(grep -n '^    current_stage=pre-replacement-backup$' "$deployment_dir/scripts/deploy-release.sh" | cut -d: -f1)
database_start_line=$(grep -n '^current_stage=database-start$' "$deployment_dir/scripts/deploy-release.sh" | cut -d: -f1)
backup_line=$(grep -n '^current_stage=pre-migration-backup$' "$deployment_dir/scripts/deploy-release.sh" | cut -d: -f1)
bootstrap_line=$(grep -n '^current_stage=bootstrap$' "$deployment_dir/scripts/deploy-release.sh" | cut -d: -f1)
[ "$probe_line" -lt "$backup_line" ] && [ "$probe_line" -lt "$bootstrap_line" ] \
    || fail_test "storage probe does not run before backup and migrations"
[ "$probe_line" -lt "$pre_replacement_backup_line" ] \
    && [ "$pre_replacement_backup_line" -lt "$database_start_line" ] \
    || fail_test "an existing PostgreSQL is replaced before its fresh encrypted backup"
grep -Fq 'compose_tools run --rm --no-deps postgres-backup backup-once' \
    "$deployment_dir/scripts/deploy-release.sh" \
    || fail_test "pre-replacement backup can start candidate database dependencies"

grep -A35 '^  storage-probe:' "$deployment_dir/compose.yaml" \
    | grep -Fq 'HTTPS_PROXY: http://egress-s3:3128' \
    || fail_test "storage probe does not use the role-specific egress proxy"
grep -Fq 'options.UsePathStyle = true' "$deployment_dir/backup/storage-tool/main.go" \
    || fail_test "storage probe does not force path-style S3 addressing"
grep -Fq 'RequestChecksumCalculationWhenRequired' "$deployment_dir/backup/storage-tool/main.go" \
    && grep -Fq 'ResponseChecksumValidationWhenRequired' "$deployment_dir/backup/storage-tool/main.go" \
    || fail_test "storage probe does not use the Storj-compatible checksum mode"

printf '%s\n' \
    "storage probe test: pinned Storj contract v1, path-style S3, and PUT/HEAD/single-Range/DELETE ordering passed"

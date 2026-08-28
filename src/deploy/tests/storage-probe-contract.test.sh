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

cat > "$mock_bin/aws" <<'EOF'
#!/bin/sh
set -eu
[ -f "$AWS_CONFIG_FILE" ] \
    && grep -Fx '    addressing_style = path' "$AWS_CONFIG_FILE" >/dev/null \
    && grep -Fx 'request_checksum_calculation = when_required' "$AWS_CONFIG_FILE" >/dev/null \
    && grep -Fx 'response_checksum_validation = when_required' "$AWS_CONFIG_FILE" >/dev/null \
    || exit 92
[ "$AWS_SHARED_CREDENTIALS_FILE" = /dev/null ] || exit 93
operation=
output_file=
object_key=
previous=
for argument in "$@"; do
    if [ "$previous" = s3api ]; then operation=$argument; fi
    if [ "$previous" = --key ]; then object_key=$argument; fi
    previous=$argument
    output_file=$argument
done
printf '%s\n' "$operation" >> "$AWS_OPERATION_LOG"
case "$operation" in
    put-object|delete-object) ;;
    head-object) printf '%s\n' 28 ;;
    get-object)
        if [ "$object_key" = .hook2stream/contracts/storage-v1.json ]; then
            cp "$STORAGE_MARKER_SOURCE" "$output_file"
        else
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
chmod 0755 "$mock_bin/aws" "$mock_bin/jq"

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
backup_line=$(grep -n '^current_stage=pre-migration-backup$' "$deployment_dir/scripts/deploy-release.sh" | cut -d: -f1)
bootstrap_line=$(grep -n '^current_stage=bootstrap$' "$deployment_dir/scripts/deploy-release.sh" | cut -d: -f1)
[ "$probe_line" -lt "$backup_line" ] && [ "$probe_line" -lt "$bootstrap_line" ] \
    || fail_test "storage probe does not run before backup and migrations"

grep -A35 '^  storage-probe:' "$deployment_dir/compose.yaml" \
    | grep -Fq 'HTTPS_PROXY: http://egress-s3:3128' \
    || fail_test "storage probe does not use the role-specific egress proxy"
grep -Fq 'addressing_style = path' "$deployment_dir/scripts/storage-probe.sh" \
    || fail_test "storage probe does not force path-style S3 addressing"
grep -Fq 'request_checksum_calculation = when_required' "$deployment_dir/scripts/storage-probe.sh" \
    && grep -Fq 'response_checksum_validation = when_required' "$deployment_dir/scripts/storage-probe.sh" \
    || fail_test "storage probe does not use the Storj-compatible checksum mode"

printf '%s\n' \
    "storage probe test: pinned Storj contract v1, path-style S3, and PUT/HEAD/single-Range/DELETE ordering passed"

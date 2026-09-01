#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
backup_script=${deployment_dir}/scripts/postgres-backup.sh
healthcheck_script=${deployment_dir}/scripts/postgres-backup-healthcheck.sh
temporary_dir=$(mktemp -d)

cleanup() {
    rm -rf "$temporary_dir"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

fail() {
    printf '%s\n' "postgres backup key-versioning test: $*" >&2
    exit 1
}

stub_bin=${temporary_dir}/bin
secret_dir=${temporary_dir}/secrets
state_dir=${temporary_dir}/state
upload_dir=${temporary_dir}/uploads
mkdir -p "$stub_bin" "$secret_dir" "$state_dir" "$upload_dir"

cat > "${stub_bin}/pg_isready" <<'EOF'
#!/bin/sh
exit 0
EOF

cat > "${stub_bin}/pg_dump" <<'EOF'
#!/bin/sh
printf '%s\n' 'deterministic-postgres-dump'
EOF

cat > "${stub_bin}/flock" <<'EOF'
#!/bin/sh
set -eu
printf '%s\n' "$*" >> "${TEST_STATE_DIR}/flock-invocations"
case "${1:-}" in
    -n)
        [ "${2:-}" = 9 ] || exit 64
        attempt_file=${TEST_STATE_DIR}/flock-attempt
        attempt=0
        [ ! -f "$attempt_file" ] || attempt=$(cat "$attempt_file")
        attempt=$((attempt + 1))
        printf '%s\n' "$attempt" > "$attempt_file"
        [ "${TEST_FLOCK_ALWAYS_BUSY:-false}" != true ] || exit 1
        [ "$attempt" -gt "${TEST_FLOCK_FAIL_ATTEMPTS:-0}" ] || exit 1
        ;;
    -u)
        [ "${2:-}" = 9 ] || exit 64
        ;;
    *)
        printf '%s\n' "unsupported BusyBox flock arguments: $*" >&2
        exit 64
        ;;
esac
EOF

cat > "${stub_bin}/sleep" <<'EOF'
#!/bin/sh
set -eu
[ "$#" -eq 1 ] && [ "$1" = 1 ] || exit 64
printf '%s\n' "$1" >> "${TEST_STATE_DIR}/sleep-invocations"
EOF

cat > "${stub_bin}/hook2stream-storage-tool" <<'EOF'
#!/bin/sh
set -eu
operation=$1
shift
output_file=
recipient=
source_file=
destination=
endpoint=
region=
bucket=
access_key_file=
secret_key_file=
while [ "$#" -gt 0 ]; do
    case "$1" in
        --output) output_file=$2; shift 2 ;;
        --recipient) recipient=$2; shift 2 ;;
        --body) source_file=$2; shift 2 ;;
        --key) destination=$2; shift 2 ;;
        --endpoint) endpoint=$2; shift 2 ;;
        --region) region=$2; shift 2 ;;
        --bucket) bucket=$2; shift 2 ;;
        --access-key-file) access_key_file=$2; shift 2 ;;
        --secret-key-file) secret_key_file=$2; shift 2 ;;
        *) shift ;;
    esac
done
case "$operation" in
    encrypt-age-x25519)
        [ -n "$output_file" ] && [ -n "$recipient" ] || exit 44
        printf '%s' "$recipient" > "${TEST_STATE_DIR}/used-recipient"
        cat > "$output_file"
        ;;
    put-object)
        [ "$endpoint" = https://gateway.storjshare.io ] || exit 45
        [ "$region" = test-region-1 ] && [ "$bucket" = test-backups ] || exit 46
        [ -z "${AWS_ACCESS_KEY_ID+x}" ] && [ -z "${AWS_SECRET_ACCESS_KEY+x}" ] || exit 47
        [ -n "$access_key_file" ] && [ -n "$secret_key_file" ] || exit 48
        cat "$access_key_file" > "${TEST_STATE_DIR}/used-access-key-id"
        cat "$secret_key_file" > "${TEST_STATE_DIR}/used-secret-access-key"
        [ -n "$source_file" ] && [ -n "$destination" ] || exit 48
        case "$destination" in
            *.manifest.json)
                [ "${TEST_FAIL_MANIFEST:-false}" != true ] || exit 42
                ;;
        esac
        cp "$source_file" "${TEST_UPLOAD_DIR}/$(basename "$destination")"
        printf '%s\n' "$destination" >> "${TEST_STATE_DIR}/upload-order"
        version_counter_file=${TEST_STATE_DIR}/version-counter
        version_counter=0
        [ ! -f "$version_counter_file" ] || version_counter=$(cat "$version_counter_file")
        version_counter=$((version_counter + 1))
        printf '%s\n' "$version_counter" > "$version_counter_file"
        printf '{"versionId":"version-%s"}\n' "$version_counter"
        ;;
    *)
        printf '%s\n' "unexpected storage helper command: $operation" >&2
        exit 1
        ;;
esac
EOF

cat > "${stub_bin}/curl" <<'EOF'
#!/bin/sh
set -eu
printf '%s\n' "$*" >> "${TEST_STATE_DIR}/unexpected-http-invocations"
exit 47
EOF

cat > "${stub_bin}/jq" <<'EOF'
#!/usr/bin/env node
const args = process.argv.slice(2);
if (args[0] === "-er") {
  const response = JSON.parse(require("node:fs").readFileSync(args.at(-1), "utf8"));
  if (typeof response.versionId !== "string" || response.versionId.length === 0) process.exit(1);
  process.stdout.write(`${response.versionId}\n`);
  process.exit(0);
}
if (args[0] !== "-n") throw new Error(`unexpected jq arguments: ${args.join(" ")}`);

const values = {};
for (let index = 1; index < args.length - 1;) {
  const option = args[index];
  if (option !== "--arg" && option !== "--argjson") {
    throw new Error(`unexpected jq option: ${option}`);
  }
  const name = args[index + 1];
  const rawValue = args[index + 2];
  values[name] = option === "--argjson" ? JSON.parse(rawValue) : rawValue;
  index += 3;
}

process.stdout.write(JSON.stringify({
  schemaVersion: 3,
  kind: values.kind,
  createdAt: values.createdAt,
  database: values.database,
  encryption: {
    format: "age",
    recipientType: "X25519",
    recipientFingerprint: values.recipientFingerprint,
  },
  encryptedDump: {
    objectKey: values.dumpObjectKey,
    versionId: values.dumpVersionId,
    sha256: values.ciphertextSha256,
  },
  checksum: {
    objectKey: values.checksumObjectKey,
    versionId: values.checksumVersionId,
  },
  retention: {
    mode: "storj-access-grant-max-object-ttl",
    maxObjectTtlHours: values.maxObjectTtlHours,
  },
}, null, 2));
EOF

chmod 0700 "${stub_bin}"/*

printf '%s\n' 'database-password' > "${secret_dir}/postgres_password"
printf '%s\n' 'file-access-key-id' > "${secret_dir}/backup_s3_access_key"
printf '%s\n' 'file-secret-access-key' > "${secret_dir}/backup_s3_secret_key"
printf '%s\n' 'age1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq' > "${secret_dir}/backup_age_recipient"

run_backup() {
    env -u AWS_ACCESS_KEY_ID -u AWS_SECRET_ACCESS_KEY \
        PATH="${stub_bin}:${PATH}" \
        TEST_STATE_DIR="$state_dir" \
        TEST_UPLOAD_DIR="$upload_dir" \
        TEST_FAIL_MANIFEST="${TEST_FAIL_MANIFEST:-false}" \
        TEST_FLOCK_ALWAYS_BUSY="${TEST_FLOCK_ALWAYS_BUSY:-false}" \
        TEST_FLOCK_FAIL_ATTEMPTS="${TEST_FLOCK_FAIL_ATTEMPTS:-2}" \
        POSTGRES_HOST=postgres \
        POSTGRES_PORT=5432 \
        POSTGRES_DB=testdb \
        POSTGRES_USER=testuser \
        POSTGRES_PASSWORD_FILE="${secret_dir}/postgres_password" \
        BACKUP_S3_ENDPOINT=https://gateway.storjshare.io \
        BACKUP_S3_REGION=test-region-1 \
        BACKUP_S3_BUCKET=test-backups \
        BACKUP_S3_PREFIX=rotation-test/postgres \
        BACKUP_S3_FORCE_PATH_STYLE=true \
        BACKUP_S3_ACCESS_KEY_FILE="${secret_dir}/backup_s3_access_key" \
        BACKUP_S3_SECRET_KEY_FILE="${secret_dir}/backup_s3_secret_key" \
        BACKUP_AGE_RECIPIENT_FILE="${secret_dir}/backup_age_recipient" \
        BACKUP_INTERVAL_SECONDS=300 \
        BACKUP_MAX_AGE_SECONDS=7200 \
        BACKUP_RETENTION_DAYS=35 \
        BACKUP_MAX_OBJECT_TTL_HOURS=840 \
        BACKUP_SUCCESS_MARKER="${state_dir}/last-successful-backup" \
        BACKUP_LOCK_TIMEOUT_SECONDS="${BACKUP_LOCK_TIMEOUT_SECONDS:-60}" \
        "$backup_script" backup-once
}

run_backup >"${state_dir}/first-backup-output" 2>&1
[ ! -e "${state_dir}/unexpected-http-invocations" ] \
    || fail "backup attempted an external HTTP notification"

[ "$(cat "${state_dir}/used-access-key-id")" = 'file-access-key-id' ] \
    || fail "backup did not load the S3 access-key ID from its file"
[ "$(cat "${state_dir}/used-secret-access-key")" = 'file-secret-access-key' ] \
    || fail "backup did not load the S3 secret access key from its file"
grep -Fq 'options.UsePathStyle = true' "$deployment_dir/backup/storage-tool/main.go" \
    || fail "backup does not force path-style S3 addressing"
grep -Fq 'RequestChecksumCalculationWhenRequired' "$deployment_dir/backup/storage-tool/main.go" \
    && grep -Fq 'ResponseChecksumValidationWhenRequired' "$deployment_dir/backup/storage-tool/main.go" \
    || fail "backup does not use the Storj-compatible checksum mode"
grep -Fq 'while ! flock -n 9; do' "$backup_script" \
    && grep -Fq 'flock -u 9' "$backup_script" \
    || fail "backup-once and daemon runs are not serialized with portable flock operations"
if grep -Eq 'flock[[:space:]]+(-[^[:space:]]*[wW]|--wait|--timeout)' "$backup_script"; then
    fail "backup lock uses a wait option unsupported by BusyBox flock"
fi
[ "$(cat "${state_dir}/flock-attempt")" -eq 3 ] \
    || fail "backup did not retry the non-blocking lock after contention"
[ "$(wc -l < "${state_dir}/sleep-invocations" | tr -d ' ')" -eq 2 ] \
    || fail "backup did not wait once between each contended lock attempt"
[ "$(sed -n '4p' "${state_dir}/flock-invocations")" = '-u 9' ] \
    || fail "successful backup did not explicitly release the shared lock"
[ "$(cat "${state_dir}/used-recipient")" = 'age1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq' ] \
    || fail "backup did not use the configured age recipient"

set -- "${upload_dir}"/*.manifest.json
[ "$#" -eq 1 ] && [ -f "$1" ] || fail "expected exactly one uploaded manifest"
manifest_file=$1
first_manifest_name=$(basename "$manifest_file")
dump_file=${manifest_file%.manifest.json}
checksum_file=${dump_file}.sha256
[ -f "$dump_file" ] || fail "encrypted dump was not uploaded"
[ -f "$checksum_file" ] || fail "checksum was not uploaded"

case "$(basename "$dump_file")" in
    testdb-*-age-*.dump.age) ;;
    *) fail "encrypted dump name does not contain the age recipient fingerprint" ;;
esac

node - "$manifest_file" "$dump_file" <<'EOF'
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");

const [manifestPath, dumpPath] = process.argv.slice(2);
const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
const ciphertext = fs.readFileSync(dumpPath);

assert.equal(manifest.schemaVersion, 3);
assert.equal(manifest.kind, "hook2stream-postgresql-logical-backup");
assert.equal(manifest.database, "testdb");
assert.match(manifest.createdAt, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/);
assert.deepEqual(manifest.encryption, {
  format: "age",
  recipientType: "X25519",
  recipientFingerprint: crypto.createHash("sha256").update("age1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq").digest("hex").slice(0, 16),
});
assert.match(manifest.encryptedDump.objectKey, /-age-[0-9a-f]{16}\.dump\.age$/);
assert.match(manifest.encryptedDump.versionId, /^version-\d+$/);
assert.equal(
  manifest.encryptedDump.sha256,
  crypto.createHash("sha256").update(ciphertext).digest("hex"),
);
assert.equal(
  manifest.checksum.objectKey,
  `${manifest.encryptedDump.objectKey}.sha256`,
);
assert.match(manifest.checksum.versionId, /^version-\d+$/);
assert.deepEqual(manifest.retention, {
  mode: "storj-access-grant-max-object-ttl",
  maxObjectTtlHours: 840,
});
EOF

[ "$(wc -l < "${state_dir}/upload-order" | tr -d ' ')" -eq 3 ] \
    || fail "successful backup did not upload exactly three objects"
case "$(sed -n '1p' "${state_dir}/upload-order")" in
    *.dump.age) ;;
    *) fail "encrypted dump was not uploaded first" ;;
esac
case "$(sed -n '2p' "${state_dir}/upload-order")" in
    *.dump.age.sha256) ;;
    *) fail "checksum was not uploaded second" ;;
esac
case "$(sed -n '3p' "${state_dir}/upload-order")" in
    *.dump.age.manifest.json) ;;
    *) fail "manifest was not uploaded last" ;;
esac

expected_recipient_fingerprint=$(printf '%s' 'age1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq' | sha256sum | cut -c1-16)
[ "$(sed -n '2p' "${state_dir}/last-successful-backup")" = "$expected_recipient_fingerprint" ] \
    || fail "freshness marker does not record the age recipient fingerprint"
[ -n "$(sed -n '3p' "${state_dir}/last-successful-backup")" ] \
    && [ -n "$(sed -n '4p' "${state_dir}/last-successful-backup")" ] \
    || fail "freshness marker does not record the completion manifest key and VersionId"

env \
    BACKUP_SUCCESS_MARKER="${state_dir}/last-successful-backup" \
    BACKUP_MAX_AGE_SECONDS=7200 \
    BACKUP_AGE_RECIPIENT_FILE="${secret_dir}/backup_age_recipient" \
    "$healthcheck_script"

run_backup >/dev/null
set -- "${upload_dir}"/*.manifest.json
[ "$#" -eq 2 ] || fail "same-second backups reused an object key"
[ -f "${upload_dir}/${first_manifest_name}" ] \
    || fail "the first backup manifest disappeared after a retry"
[ "$(wc -l < "${state_dir}/upload-order" | tr -d ' ')" -eq 6 ] \
    || fail "the second successful backup did not upload its own three objects"

printf '%s\n' 'age1pppppppppppppppppppppppppppppppppppppppppppppppppppppppp' > "${secret_dir}/backup_age_recipient"
if env \
    BACKUP_SUCCESS_MARKER="${state_dir}/last-successful-backup" \
    BACKUP_MAX_AGE_SECONDS=7200 \
    BACKUP_AGE_RECIPIENT_FILE="${secret_dir}/backup_age_recipient" \
    "$healthcheck_script"; then
    fail "healthcheck accepted a marker written with a different age recipient"
fi

marker_before_failed_upload=$(cat "${state_dir}/last-successful-backup")
if TEST_FAIL_MANIFEST=true run_backup >"${state_dir}/manifest-failure-output" 2>&1; then
    fail "backup succeeded when the completion manifest upload failed"
fi
[ "$(cat "${state_dir}/last-successful-backup")" = "$marker_before_failed_upload" ] \
    || fail "failed manifest upload replaced the last successful freshness marker"

if grep -Eq 'delete-object|list-object-versions|s3 cp' "${state_dir}/upload-order"; then
    fail "backup used a delete/list retention operation or multipart-capable high-level upload"
fi

printf '%s\n' '../invalid-recipient' > "${secret_dir}/backup_age_recipient"
if run_backup >"${state_dir}/invalid-key-output" 2>&1; then
    fail "backup accepted an unsafe age recipient"
fi
grep -q 'age recipient' "${state_dir}/invalid-key-output" \
    || fail "unsafe age recipient failure did not explain the problem"

rm -f \
    "${state_dir}/flock-attempt" \
    "${state_dir}/flock-invocations" \
    "${state_dir}/sleep-invocations"
if TEST_FLOCK_ALWAYS_BUSY=true \
    TEST_FLOCK_FAIL_ATTEMPTS=0 \
    BACKUP_LOCK_TIMEOUT_SECONDS=60 \
    run_backup >"${state_dir}/lock-timeout-output" 2>&1; then
    fail "backup succeeded while the shared lock remained contended"
fi
grep -Fq 'shared lock within 60 seconds' "${state_dir}/lock-timeout-output" \
    || fail "bounded lock retry did not report the configured timeout"
[ "$(cat "${state_dir}/flock-attempt")" -eq 61 ] \
    || fail "bounded lock retry did not stop at the configured timeout"
[ "$(wc -l < "${state_dir}/sleep-invocations" | tr -d ' ')" -eq 60 ] \
    || fail "bounded lock retry did not preserve one-second timeout semantics"
if grep -q '^-u 9$' "${state_dir}/flock-invocations"; then
    fail "timed-out backup attempted to unlock a lock it never acquired"
fi

printf '%s\n' "postgres backup key-versioning test: passed"

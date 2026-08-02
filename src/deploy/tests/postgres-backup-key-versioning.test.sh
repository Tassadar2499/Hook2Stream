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

cat > "${stub_bin}/openssl" <<'EOF'
#!/bin/sh
set -eu
output_file=
pass_argument=
while [ "$#" -gt 0 ]; do
    case "$1" in
        -out)
            output_file=$2
            shift 2
            ;;
        -pass)
            pass_argument=$2
            shift 2
            ;;
        *) shift ;;
    esac
done
[ -n "$output_file" ] && [ "${pass_argument#file:}" != "$pass_argument" ]
passphrase_file=${pass_argument#file:}
cp "$passphrase_file" "${TEST_STATE_DIR}/used-passphrase"
cat > "$output_file"
EOF

cat > "${stub_bin}/aws" <<'EOF'
#!/bin/sh
set -eu
printf '%s' "$AWS_ACCESS_KEY_ID" > "${TEST_STATE_DIR}/used-access-key-id"
printf '%s' "$AWS_SECRET_ACCESS_KEY" > "${TEST_STATE_DIR}/used-secret-access-key"

case "${1:-}:${2:-}" in
    s3:cp)
        source_file=$4
        destination=$5
        case "$destination" in
            *.manifest.json)
                [ "${TEST_FAIL_MANIFEST:-false}" != true ] || exit 42
                ;;
        esac
        cp "$source_file" "${TEST_UPLOAD_DIR}/$(basename "$destination")"
        printf '%s\n' "$destination" >> "${TEST_STATE_DIR}/upload-order"
        ;;
    s3api:list-object-versions)
        [ "${TEST_FAIL_RETENTION:-false}" != true ] || exit 43
        printf '%s\n' '{"Versions":[],"DeleteMarkers":[]}'
        ;;
    *)
        printf '%s\n' "unexpected aws command: $*" >&2
        exit 1
        ;;
esac
EOF

cat > "${stub_bin}/curl" <<'EOF'
#!/bin/sh
set -eu
printf '%s\n' "$*" >> "${TEST_STATE_DIR}/heartbeat-invocations"
[ "${TEST_FAIL_HEARTBEAT:-false}" != true ] || exit 44
printf '%s' "${TEST_HEARTBEAT_STATUS:-204}"
EOF

cat > "${stub_bin}/jq" <<'EOF'
#!/usr/bin/env node
const args = process.argv.slice(2);
if (args[0] === "-r") process.exit(0);
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
  schemaVersion: 1,
  kind: values.kind,
  createdAt: values.createdAt,
  database: values.database,
  encryption: {
    keyId: values.keyId,
    cipher: "aes-256-cbc",
    kdf: "pbkdf2-hmac-sha256",
    kdfIterations: values.kdfIterations,
  },
  encryptedDump: {
    objectKey: values.dumpObjectKey,
    sha256: values.ciphertextSha256,
  },
  checksum: {
    objectKey: values.checksumObjectKey,
  },
}, null, 2));
EOF

chmod 0700 "${stub_bin}"/*

printf '%s\n' 'database-password' > "${secret_dir}/postgres_password"
printf '%s\n' 'file-access-key-id' > "${secret_dir}/backup_s3_access_key"
printf '%s\n' 'file-secret-access-key' > "${secret_dir}/backup_s3_secret_key"
printf '%s\n' 'high-entropy-backup-passphrase' > "${secret_dir}/backup_encryption_passphrase"
printf '%s\n' '2026-07' > "${secret_dir}/backup_encryption_key_id"
printf '%s\n' 'https://heartbeat.example.test/super-secret-token' \
    > "${secret_dir}/backup_heartbeat_url"

run_backup() {
    env \
        PATH="${stub_bin}:${PATH}" \
        TEST_STATE_DIR="$state_dir" \
        TEST_UPLOAD_DIR="$upload_dir" \
        TEST_FAIL_MANIFEST="${TEST_FAIL_MANIFEST:-false}" \
        TEST_FAIL_RETENTION="${TEST_FAIL_RETENTION:-false}" \
        TEST_FAIL_HEARTBEAT="${TEST_FAIL_HEARTBEAT:-false}" \
        TEST_HEARTBEAT_STATUS="${TEST_HEARTBEAT_STATUS:-204}" \
        POSTGRES_HOST=postgres \
        POSTGRES_PORT=5432 \
        POSTGRES_DB=testdb \
        POSTGRES_USER=testuser \
        POSTGRES_PASSWORD_FILE="${secret_dir}/postgres_password" \
        BACKUP_S3_ENDPOINT= \
        BACKUP_S3_REGION=test-region-1 \
        BACKUP_S3_BUCKET=test-backups \
        BACKUP_S3_PREFIX=rotation-test/postgres \
        BACKUP_S3_ACCESS_KEY_FILE="${secret_dir}/backup_s3_access_key" \
        BACKUP_S3_SECRET_KEY_FILE="${secret_dir}/backup_s3_secret_key" \
        BACKUP_ENCRYPTION_PASSPHRASE_FILE="${secret_dir}/backup_encryption_passphrase" \
        BACKUP_ENCRYPTION_KEY_ID_FILE="${secret_dir}/backup_encryption_key_id" \
        BACKUP_INTERVAL_SECONDS=300 \
        BACKUP_MAX_AGE_SECONDS=7200 \
        BACKUP_RETENTION_DAYS=35 \
        BACKUP_RETENTION_SAFETY_SECONDS=300 \
        BACKUP_KDF_ITERATIONS=100000 \
        BACKUP_SUCCESS_MARKER="${state_dir}/last-successful-backup" \
        BACKUP_HEARTBEAT_URL_FILE="${TEST_HEARTBEAT_URL_FILE-${secret_dir}/backup_heartbeat_url}" \
        "$backup_script" backup-once
}

run_backup >"${state_dir}/first-backup-output" 2>&1

[ "$(wc -l < "${state_dir}/heartbeat-invocations" | tr -d ' ')" -eq 1 ] \
    || fail "successful backup did not send exactly one heartbeat"
grep -q -- "--proto =https" "${state_dir}/heartbeat-invocations" \
    || fail "heartbeat client did not restrict requests to HTTPS"
grep -q -- "--connect-timeout 3" "${state_dir}/heartbeat-invocations" \
    || fail "heartbeat client did not use the expected short connection timeout"
grep -q -- "--max-time 10" "${state_dir}/heartbeat-invocations" \
    || fail "heartbeat client did not use the expected total timeout"
grep -q -- "--retry 2" "${state_dir}/heartbeat-invocations" \
    || fail "heartbeat client did not use the expected retry limit"
grep -q -- "--retry-max-time 20" "${state_dir}/heartbeat-invocations" \
    || fail "heartbeat client did not bound the retry window"
grep -q -- "--write-out %{http_code}" "${state_dir}/heartbeat-invocations" \
    || fail "heartbeat client did not inspect the HTTP response status"
if grep -q 'super-secret-token' "${state_dir}/first-backup-output"; then
    fail "backup logs exposed the heartbeat URL"
fi

[ "$(cat "${state_dir}/used-access-key-id")" = 'file-access-key-id' ] \
    || fail "backup did not load the S3 access-key ID from its file"
[ "$(cat "${state_dir}/used-secret-access-key")" = 'file-secret-access-key' ] \
    || fail "backup did not load the S3 secret access key from its file"
[ "$(cat "${state_dir}/used-passphrase")" = 'high-entropy-backup-passphrase' ] \
    || fail "backup did not snapshot the configured encryption passphrase"

set -- "${upload_dir}"/*.manifest.json
[ "$#" -eq 1 ] && [ -f "$1" ] || fail "expected exactly one uploaded manifest"
manifest_file=$1
first_manifest_name=$(basename "$manifest_file")
dump_file=${manifest_file%.manifest.json}
checksum_file=${dump_file}.sha256
[ -f "$dump_file" ] || fail "encrypted dump was not uploaded"
[ -f "$checksum_file" ] || fail "checksum was not uploaded"

case "$(basename "$dump_file")" in
    testdb-*-key-2026-07.dump.enc) ;;
    *) fail "encrypted dump name does not contain the key ID" ;;
esac

node - "$manifest_file" "$dump_file" <<'EOF'
const assert = require("node:assert/strict");
const crypto = require("node:crypto");
const fs = require("node:fs");

const [manifestPath, dumpPath] = process.argv.slice(2);
const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
const ciphertext = fs.readFileSync(dumpPath);

assert.equal(manifest.schemaVersion, 1);
assert.equal(manifest.kind, "hook2stream-postgresql-logical-backup");
assert.equal(manifest.database, "testdb");
assert.match(manifest.createdAt, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/);
assert.deepEqual(manifest.encryption, {
  keyId: "2026-07",
  cipher: "aes-256-cbc",
  kdf: "pbkdf2-hmac-sha256",
  kdfIterations: 100000,
});
assert.match(manifest.encryptedDump.objectKey, /-key-2026-07\.dump\.enc$/);
assert.equal(
  manifest.encryptedDump.sha256,
  crypto.createHash("sha256").update(ciphertext).digest("hex"),
);
assert.equal(
  manifest.checksum.objectKey,
  `${manifest.encryptedDump.objectKey}.sha256`,
);
EOF

[ "$(wc -l < "${state_dir}/upload-order" | tr -d ' ')" -eq 3 ] \
    || fail "successful backup did not upload exactly three objects"
case "$(sed -n '1p' "${state_dir}/upload-order")" in
    *.dump.enc) ;;
    *) fail "encrypted dump was not uploaded first" ;;
esac
case "$(sed -n '2p' "${state_dir}/upload-order")" in
    *.dump.enc.sha256) ;;
    *) fail "checksum was not uploaded second" ;;
esac
case "$(sed -n '3p' "${state_dir}/upload-order")" in
    *.dump.enc.manifest.json) ;;
    *) fail "manifest was not uploaded last" ;;
esac

[ "$(sed -n '2p' "${state_dir}/last-successful-backup")" = '2026-07' ] \
    || fail "freshness marker does not record the successful key ID"

env \
    BACKUP_SUCCESS_MARKER="${state_dir}/last-successful-backup" \
    BACKUP_MAX_AGE_SECONDS=7200 \
    BACKUP_ENCRYPTION_KEY_ID_FILE="${secret_dir}/backup_encryption_key_id" \
    "$healthcheck_script"

run_backup >/dev/null
set -- "${upload_dir}"/*.manifest.json
[ "$#" -eq 2 ] || fail "same-second backups reused an object key"
[ -f "${upload_dir}/${first_manifest_name}" ] \
    || fail "the first backup manifest disappeared after a retry"
[ "$(wc -l < "${state_dir}/upload-order" | tr -d ' ')" -eq 6 ] \
    || fail "the second successful backup did not upload its own three objects"
[ "$(wc -l < "${state_dir}/heartbeat-invocations" | tr -d ' ')" -eq 2 ] \
    || fail "the second successful backup did not send its own heartbeat"

printf '%s\n' '2026-08' > "${secret_dir}/backup_encryption_key_id"
if env \
    BACKUP_SUCCESS_MARKER="${state_dir}/last-successful-backup" \
    BACKUP_MAX_AGE_SECONDS=7200 \
    BACKUP_ENCRYPTION_KEY_ID_FILE="${secret_dir}/backup_encryption_key_id" \
    "$healthcheck_script"; then
    fail "healthcheck accepted a marker written with a different encryption key ID"
fi

marker_before_failed_upload=$(cat "${state_dir}/last-successful-backup")
printf '%s\n' 'rotated-high-entropy-backup-passphrase' \
    > "${secret_dir}/backup_encryption_passphrase"
if TEST_FAIL_MANIFEST=true run_backup >"${state_dir}/manifest-failure-output" 2>&1; then
    fail "backup succeeded when the completion manifest upload failed"
fi
[ "$(cat "${state_dir}/last-successful-backup")" = "$marker_before_failed_upload" ] \
    || fail "failed manifest upload replaced the last successful freshness marker"
[ "$(wc -l < "${state_dir}/heartbeat-invocations" | tr -d ' ')" -eq 2 ] \
    || fail "failed backup sent a success heartbeat"

if TEST_FAIL_RETENTION=true run_backup >"${state_dir}/retention-failure-output" 2>&1; then
    fail "backup succeeded when the retention cycle failed"
fi
[ "$(cat "${state_dir}/last-successful-backup")" = "$marker_before_failed_upload" ] \
    || fail "failed retention cycle replaced the last successful freshness marker"
[ "$(wc -l < "${state_dir}/heartbeat-invocations" | tr -d ' ')" -eq 2 ] \
    || fail "failed retention cycle sent a success heartbeat"

printf '%s\n' '2026-08' > "${secret_dir}/backup_encryption_key_id"
if ! TEST_FAIL_HEARTBEAT=true run_backup \
    >"${state_dir}/heartbeat-failure-output" 2>&1; then
    fail "heartbeat delivery failure turned a successful backup into a failure"
fi
grep -q 'warning: success heartbeat delivery failed' \
    "${state_dir}/heartbeat-failure-output" \
    || fail "heartbeat delivery failure did not produce a warning"
if grep -q 'super-secret-token' "${state_dir}/heartbeat-failure-output"; then
    fail "heartbeat delivery warning exposed the heartbeat URL"
fi
[ "$(wc -l < "${state_dir}/heartbeat-invocations" | tr -d ' ')" -eq 3 ] \
    || fail "failing heartbeat client was not invoked exactly once"

if ! TEST_HEARTBEAT_STATUS=302 run_backup \
    >"${state_dir}/heartbeat-redirect-output" 2>&1; then
    fail "heartbeat redirect turned a successful backup into a failure"
fi
grep -q 'warning: success heartbeat delivery failed' \
    "${state_dir}/heartbeat-redirect-output" \
    || fail "heartbeat redirect was incorrectly accepted as delivery"
if grep -q 'super-secret-token' "${state_dir}/heartbeat-redirect-output"; then
    fail "heartbeat redirect warning exposed the heartbeat URL"
fi
[ "$(wc -l < "${state_dir}/heartbeat-invocations" | tr -d ' ')" -eq 4 ] \
    || fail "redirecting heartbeat client was not invoked exactly once"

printf '%s\n' 'http://heartbeat.example.test/super-secret-token' \
    > "${secret_dir}/backup_heartbeat_url"
if ! run_backup >"${state_dir}/insecure-heartbeat-output" 2>&1; then
    fail "insecure heartbeat URL turned a successful backup into a failure"
fi
grep -q 'warning: heartbeat URL must use HTTPS' \
    "${state_dir}/insecure-heartbeat-output" \
    || fail "insecure heartbeat URL did not produce a warning"
if grep -q 'super-secret-token' "${state_dir}/insecure-heartbeat-output"; then
    fail "insecure heartbeat warning exposed the heartbeat URL"
fi
[ "$(wc -l < "${state_dir}/heartbeat-invocations" | tr -d ' ')" -eq 4 ] \
    || fail "backup contacted an insecure heartbeat URL"

if ! TEST_HEARTBEAT_URL_FILE= run_backup \
    >"${state_dir}/disabled-heartbeat-output" 2>&1; then
    fail "backup failed when heartbeat monitoring was disabled"
fi
[ "$(wc -l < "${state_dir}/heartbeat-invocations" | tr -d ' ')" -eq 4 ] \
    || fail "disabled heartbeat monitoring still invoked the HTTP client"

: > "${secret_dir}/backup_heartbeat_url"
if ! run_backup >"${state_dir}/empty-heartbeat-output" 2>&1; then
    fail "empty optional heartbeat secret turned a successful backup into a failure"
fi
[ "$(wc -l < "${state_dir}/heartbeat-invocations" | tr -d ' ')" -eq 4 ] \
    || fail "empty optional heartbeat secret invoked the HTTP client"

printf '%s\n' '../invalid-key-id' > "${secret_dir}/backup_encryption_key_id"
if run_backup >"${state_dir}/invalid-key-output" 2>&1; then
    fail "backup accepted an unsafe encryption key ID"
fi
grep -q 'key ID' "${state_dir}/invalid-key-output" \
    || fail "unsafe key ID failure did not explain the problem"

printf '%s\n' "postgres backup key-versioning test: passed"

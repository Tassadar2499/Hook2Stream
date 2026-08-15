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

cat > "${stub_bin}/age" <<'EOF'
#!/bin/sh
set -eu
output_file=
recipient=
while [ "$#" -gt 0 ]; do
    case "$1" in
        --output)
            output_file=$2
            shift 2
            ;;
        --recipient)
            recipient=$2
            shift 2
            ;;
        *) shift ;;
    esac
done
[ -n "$output_file" ] && [ -n "$recipient" ]
printf '%s' "$recipient" > "${TEST_STATE_DIR}/used-recipient"
cat > "$output_file"
EOF

cat > "${stub_bin}/aws" <<'EOF'
#!/bin/sh
set -eu
[ -f "$AWS_CONFIG_FILE" ] \
    && grep -Fx '    addressing_style = path' "$AWS_CONFIG_FILE" >/dev/null \
    || exit 45
[ "$AWS_SHARED_CREDENTIALS_FILE" = /dev/null ] || exit 46
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
printf '%s\n' "$*" >> "${TEST_STATE_DIR}/unexpected-http-invocations"
exit 47
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
  schemaVersion: 2,
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
printf '%s\n' 'age1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq' > "${secret_dir}/backup_age_recipient"

run_backup() {
    env \
        PATH="${stub_bin}:${PATH}" \
        TEST_STATE_DIR="$state_dir" \
        TEST_UPLOAD_DIR="$upload_dir" \
        TEST_FAIL_MANIFEST="${TEST_FAIL_MANIFEST:-false}" \
        TEST_FAIL_RETENTION="${TEST_FAIL_RETENTION:-false}" \
        POSTGRES_HOST=postgres \
        POSTGRES_PORT=5432 \
        POSTGRES_DB=testdb \
        POSTGRES_USER=testuser \
        POSTGRES_PASSWORD_FILE="${secret_dir}/postgres_password" \
        BACKUP_S3_ENDPOINT= \
        BACKUP_S3_REGION=test-region-1 \
        BACKUP_S3_BUCKET=test-backups \
        BACKUP_S3_PREFIX=rotation-test/postgres \
        S3_FORCE_PATH_STYLE=true \
        BACKUP_S3_ACCESS_KEY_FILE="${secret_dir}/backup_s3_access_key" \
        BACKUP_S3_SECRET_KEY_FILE="${secret_dir}/backup_s3_secret_key" \
        BACKUP_AGE_RECIPIENT_FILE="${secret_dir}/backup_age_recipient" \
        BACKUP_INTERVAL_SECONDS=300 \
        BACKUP_MAX_AGE_SECONDS=7200 \
        BACKUP_RETENTION_DAYS=35 \
        BACKUP_RETENTION_SAFETY_SECONDS=300 \
        BACKUP_SUCCESS_MARKER="${state_dir}/last-successful-backup" \
        "$backup_script" backup-once
}

run_backup >"${state_dir}/first-backup-output" 2>&1
[ ! -e "${state_dir}/unexpected-http-invocations" ] \
    || fail "backup attempted an external HTTP notification"

[ "$(cat "${state_dir}/used-access-key-id")" = 'file-access-key-id' ] \
    || fail "backup did not load the S3 access-key ID from its file"
[ "$(cat "${state_dir}/used-secret-access-key")" = 'file-secret-access-key' ] \
    || fail "backup did not load the S3 secret access key from its file"
grep -Fq 'addressing_style = path' "$backup_script" \
    || fail "backup does not force path-style S3 addressing"
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

assert.equal(manifest.schemaVersion, 2);
assert.equal(manifest.kind, "hook2stream-postgresql-logical-backup");
assert.equal(manifest.database, "testdb");
assert.match(manifest.createdAt, /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/);
assert.deepEqual(manifest.encryption, {
  format: "age",
  recipientType: "X25519",
  recipientFingerprint: crypto.createHash("sha256").update("age1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq").digest("hex").slice(0, 16),
});
assert.match(manifest.encryptedDump.objectKey, /-age-[0-9a-f]{16}\.dump\.age$/);
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

if TEST_FAIL_RETENTION=true run_backup >"${state_dir}/retention-failure-output" 2>&1; then
    fail "backup succeeded when the retention cycle failed"
fi
[ "$(cat "${state_dir}/last-successful-backup")" = "$marker_before_failed_upload" ] \
    || fail "failed retention cycle replaced the last successful freshness marker"

printf '%s\n' '../invalid-recipient' > "${secret_dir}/backup_age_recipient"
if run_backup >"${state_dir}/invalid-key-output" 2>&1; then
    fail "backup accepted an unsafe age recipient"
fi
grep -q 'age recipient' "${state_dir}/invalid-key-output" \
    || fail "unsafe age recipient failure did not explain the problem"

printf '%s\n' "postgres backup key-versioning test: passed"

#!/bin/sh
set -eu

storage_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
fail() { printf '%s\n' "storage topology contract: $*" >&2; exit 1; }

assert_env() {
    environment=$1 media=$2 backup=$3 media_quota=$4 backup_quota=$5 retention=$6
    file=$storage_dir/environments/$environment.env.example
    for assignment in \
        "DEPLOYMENT_ENVIRONMENT=$environment" \
        'STORAGE_PROTOCOL_VERSION=1' \
        'STORAGE_OBJECT_FORMAT=H2SEv1' \
        'MINIO_REGION=us-east-1' \
        "MINIO_MEDIA_BUCKET=$media" \
        "MINIO_BACKUP_BUCKET=$backup" \
        "MINIO_MEDIA_QUOTA_GIB=$media_quota" \
        "MINIO_BACKUP_QUOTA_GIB=$backup_quota" \
        "BACKUP_RETENTION_DAYS=$retention" \
        'STORAGE_DATA_DIR=/srv/hook2stream-storage/minio-data' \
        'SECRETS_DIR=/srv/hook2stream-storage/secrets/current' \
        'MANAGED_IDENTITY_INVENTORY_FILE=/srv/hook2stream-storage/release-state/managed-identities.v1'; do
        grep -Fx "$assignment" "$file" >/dev/null || fail "$environment omitted exact $assignment"
    done
    grep -E "^STORAGE_TLS_SERVER_NAME=h2s-storage-$environment\.[^.]+\.ts\.net$" "$file" >/dev/null \
        || fail "$environment TLS name is not canonical"
}
assert_env staging hook2stream-staging-media hook2stream-staging-pg-backups 35 10 7
assert_env production hook2stream-production-media hook2stream-production-pg-backups 160 30 35

node - "$storage_dir" <<'JS' || fail "JSON topology contract differs"
const fs = require("fs");
const path = require("path");
const root = process.argv[2];
const read = (...parts) => JSON.parse(fs.readFileSync(path.join(root, ...parts), "utf8"));
const exactKeys = (value, keys) => JSON.stringify(Object.keys(value).sort()) === JSON.stringify([...keys].sort());
const release = read("storage-release.json");
if (!exactKeys(release, ["schemaVersion","kind","protocolVersion","storageFormatVersion","objectFormat","minioRelease","minioSourceCommit"]) ||
    release.schemaVersion !== 1 || release.kind !== "hook2stream-storage-runtime" || release.protocolVersion !== 1 ||
    release.storageFormatVersion !== 1 || release.objectFormat !== "H2SEv1" ||
    release.minioRelease !== "RELEASE.2025-10-15T17-29-55Z" ||
    release.minioSourceCommit !== "9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a") process.exit(1);
for (const environment of ["staging", "production"]) {
  const media = read("lifecycle", `${environment}-media.json`);
  if (media.Rules.length !== 2 || !media.Rules.some(r => r.ID === `hook2stream-${environment}-media-abort-multipart-1d` && r.AbortIncompleteMultipartUpload?.DaysAfterInitiation === 1) ||
      !media.Rules.some(r => r.ID === `hook2stream-${environment}-staging-object-expiry-1d` && r.Filter?.Prefix === "staging/" && r.Expiration?.Days === 1)) process.exit(1);
  for (const policy of ["runtime-media", "bootstrap-media", "postgres-backup"]) {
    const document = read("policies", environment, `${policy}.json`);
    if (document.Version !== "2012-10-17" || !Array.isArray(document.Statement) || document.Statement.length < 2) process.exit(1);
  }
}
const staging = read("lifecycle", "staging-backup.json").Rules;
const production = read("lifecycle", "production-backup.json").Rules;
if (!staging.some(r => r.ID === "hook2stream-staging-backup-retention-7d" && r.Expiration?.Days === 7 && r.NoncurrentVersionExpiration?.NoncurrentDays === 7) ||
    !production.some(r => r.ID === "hook2stream-production-backup-retention-35d" && r.Expiration?.Days === 35 && r.NoncurrentVersionExpiration?.NoncurrentDays === 35)) process.exit(1);
JS
grep -F 'root/bootstrap/runtime/backup credential values must all be distinct' "$storage_dir/init/minio-init.sh" >/dev/null \
    || fail "credential separation is not enforced"
grep -F 'mc_command version suspend "$alias_name/$MINIO_MEDIA_BUCKET"' "$storage_dir/init/minio-init.sh" >/dev/null \
    || fail "media versioning state is not enforced"
grep -F 'mc_command version enable "$alias_name/$MINIO_BACKUP_BUCKET"' "$storage_dir/init/minio-init.sh" >/dev/null \
    || fail "backup versioning state is not enforced"
grep -F '| mc_command admin user add "$alias_name" >/dev/null' "$storage_dir/init/minio-init.sh" >/dev/null \
    || fail "managed user secrets are not delivered over stdin"
! grep -E 'admin[[:space:]]+user[[:space:]]+add.*\$(access_key|secret_key)' "$storage_dir/init/minio-init.sh" >/dev/null \
    || fail "managed user credentials can enter process argv"
grep -F 'must not inherit a group policy' "$storage_dir/init/minio-init.sh" >/dev/null \
    || fail "managed identities do not reject inherited group policy"
grep -F 'must have exactly policy' "$storage_dir/init/minio-init.sh" >/dev/null \
    || fail "managed identity policy set is not verified exactly"
grep -F 'retired access key remains active' "$storage_dir/init/minio-init.sh" >/dev/null \
    || fail "managed identity rotation does not fail closed on old-user revocation"
grep -F 'HOOK2STREAM_STORAGE_MANAGED_IDENTITIES_V1' "$storage_dir/init/minio-init.sh" >/dev/null \
    || fail "managed identity inventory version is not validated"
if grep -R -n -E 'mc([[:space:]]+--config-dir[^[:space:]]+)?[[:space:]]+alias[[:space:]]+set' \
    "$storage_dir/init" "$storage_dir/scripts" "$storage_dir/tests" >/dev/null 2>&1; then
    fail "an mc alias command can expose credentials through process argv"
fi
. "$storage_dir/scripts/lib/storage-common.sh"
storage_validate_mc_host_credential test 'Hex_0123.safe+-value'
for invalid_credential in 'slash/value' 'padded='; do
    if (storage_validate_mc_host_credential test "$invalid_credential") >/dev/null 2>&1; then
        fail "MC_HOST-unsafe credential was accepted: $invalid_credential"
    fi
done
printf '%s\n' "storage topology contract: PASS"

#!/bin/sh
set -eu

storage_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
forced=$storage_dir/scripts/storage-forced-command.sh
deploy=$storage_dir/scripts/deploy-storage.sh
fail() { printf '%s\n' "storage host contract: $*" >&2; exit 1; }

grep -F "only 'deploy-storage storage-candidate-SHA-RUN-ATTEMPT' is allowed" "$forced" >/dev/null \
    || fail "forced command wire protocol changed"
grep -F 'dd iflag=fullblock bs=1048576 count=129' "$forced" >/dev/null \
    || fail "bounded SSH envelope read can truncate partial pipe blocks"
grep -F 'STORAGE_STAGING_SIGNERS' "$forced" >/dev/null || fail "production staging receipt gate is absent"
grep -F 'hook2stream-storage-staging-receipt' "$storage_dir/scripts/validate-production-approval.sh" >/dev/null \
    || fail "production signature namespace changed"
grep -F 'hook2stream-storage-staging' "$storage_dir/scripts/validate-production-approval.sh" >/dev/null \
    || fail "production signer identity changed"
grep -F 'minimumStorageFormatVersion' "$forced" >/dev/null || fail "MinIO on-disk downgrade floor is absent"
grep -F 'minimumMinioSecuritySequence' "$forced" >/dev/null \
    || fail "monotonic MinIO security floor is absent"
grep -F 'minioSourceCommit' "$forced" >/dev/null || fail "MinIO source identity is not audited"
security_gate_line=$(grep -nF 'storage_validate_minio_security_policy' "$forced" | cut -d: -f1)
floor_gate_line=$(grep -nF 'storage_validate_minio_security_transition' "$forced" | cut -d: -f1)
format_marker_line=$(grep -nF 'format_marker=$STORAGE_STATE_DIR/storage-format-floor.json' "$forced" | cut -d: -f1)
floor_persist_line=$(grep -nF 'storage_write_format_floor "$format_marker"' "$forced" | head -n 1 | cut -d: -f1)
deploy_line=$(grep -nF '"$release_dir/storage/scripts/deploy-storage.sh"' "$forced" | cut -d: -f1)
floor_success_line=$(grep -nF 'storage_write_format_floor "$format_marker"' "$forced" | tail -n 1 | cut -d: -f1)
test "$security_gate_line" -lt "$format_marker_line" && test "$security_gate_line" -lt "$deploy_line" \
    || fail "host MinIO approval gate does not precede format-floor and Docker mutation"
test "$floor_gate_line" -lt "$floor_persist_line" && test "$floor_persist_line" -lt "$deploy_line" \
    || fail "candidate format floor is not validated and persisted before Docker mutation"
test "$deploy_line" -lt "$floor_success_line" \
    || fail "pending format attempt is marked successful before deployment verification"
grep -F 'pendingReleaseSha' "$forced" >/dev/null \
    || fail "failed format-changing attempts are not persisted for forward-fix recovery"
grep -F '< "$MANAGED_IDENTITY_INVENTORY_FILE" >&2' "$deploy" >/dev/null \
    || fail "root-only managed identity inventory is not streamed to init over stdin"
grep -F 'chmod 0600 "$inventory_tmp"' "$deploy" >/dev/null \
    || fail "atomically updated managed identity inventory is not root-only"
if command -v jq >/dev/null 2>&1; then
    floor_scratch=$(mktemp -d)
    trap 'rm -rf "$floor_scratch"' EXIT HUP INT TERM
    mkdir -p "$floor_scratch/small-chunk-envelope/candidate"
    truncate -s 2097152 "$floor_scratch/small-chunk-envelope/candidate/payload"
    tar -cf "$floor_scratch/envelope.source.tar" \
        -C "$floor_scratch/small-chunk-envelope" candidate
    node - "$floor_scratch/envelope.source.tar" <<'JS' \
        | dd bs=1048576 count=129 iflag=fullblock \
            of="$floor_scratch/envelope.received.tar" 2>/dev/null
const fs = require("fs");
const descriptor = fs.openSync(process.argv[2], "r");
const chunk = Buffer.alloc(4096);
for (;;) {
  const size = fs.readSync(descriptor, chunk, 0, chunk.length, null);
  if (size === 0) break;
  let offset = 0;
  while (offset < size) offset += fs.writeSync(1, chunk, offset, size - offset);
}
fs.closeSync(descriptor);
JS
    cmp -s "$floor_scratch/envelope.source.tar" "$floor_scratch/envelope.received.tar" \
        || fail "full-block envelope read truncated a valid small-chunk SSH stream"
    . "$storage_dir/scripts/lib/storage-common.sh"
    inventory_fixture=$storage_dir/host/managed-identities.v1.empty
    storage_validate_managed_identity_inventory "$inventory_fixture"
    storage_managed_identity_inventory_is_empty "$inventory_fixture" \
        || fail "first-run managed identity inventory is not recognized as empty"
    printf '%s\n' HOOK2STREAM_STORAGE_MANAGED_IDENTITIES_V1 \
        bootstrap=duplicate runtime=duplicate backup=- \
        > "$floor_scratch/malformed-inventory.v1"
    if (storage_validate_managed_identity_inventory \
        "$floor_scratch/malformed-inventory.v1") >/dev/null 2>&1; then
        fail "managed identity inventory accepted duplicate role access keys"
    fi
    if (storage_validate_minio_security_policy \
        "$storage_dir/minio-security-policy.json" \
        RELEASE.2025-10-15T17-29-55Z \
        9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a) >/dev/null 2>&1; then
        fail "host gate accepted a MinIO source pin absent from the approved set"
    fi
    jq '.approvedSourceReleases = [{
            release:"RELEASE.2025-10-15T17-29-55Z",
            commit:"9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a",
            source:"https://github.com/minio/minio",
            reviewedAt:"2026-08-15",
            securitySequence:1
        }]' "$storage_dir/minio-security-policy.json" > "$floor_scratch/approved-policy.json"
    approved_sequence=$(storage_validate_minio_security_policy \
        "$floor_scratch/approved-policy.json" \
        RELEASE.2025-10-15T17-29-55Z \
        9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a)
    [ "$approved_sequence" = 1 ] || fail "host policy returned the wrong security sequence"
    jq '.approvedSourceReleases += [{
            release:"RELEASE.2026-12-31T00-00-00Z",
            commit:"3333333333333333333333333333333333333333",
            source:"https://github.com/minio/minio",
            reviewedAt:"2026-12-31",
            securitySequence:2
        }]' "$floor_scratch/approved-policy.json" > "$floor_scratch/forward-policy.json"
    forward_sequence=$(storage_validate_minio_security_policy \
        "$floor_scratch/forward-policy.json" \
        RELEASE.2026-12-31T00-00-00Z \
        3333333333333333333333333333333333333333)
    [ "$forward_sequence" = 2 ] || fail "approved forward source did not return its higher sequence"
    if (storage_validate_minio_security_policy \
        "$floor_scratch/forward-policy.json" \
        RELEASE.2027-01-01T00-00-00Z \
        4444444444444444444444444444444444444444) >/dev/null 2>&1; then
        fail "host gate accepted an unapproved future MinIO source pin"
    fi
    jq '.unexpected = true' "$floor_scratch/approved-policy.json" \
        > "$floor_scratch/malformed-policy.json"
    if (storage_validate_minio_security_policy \
        "$floor_scratch/malformed-policy.json" \
        RELEASE.2025-10-15T17-29-55Z \
        9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a) >/dev/null 2>&1; then
        fail "host gate accepted a MinIO security policy with an unknown field"
    fi
    old_release=RELEASE.2025-10-15T17-29-55Z
    old_commit=9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a
    new_release=RELEASE.2026-12-31T00-00-00Z
    new_commit=3333333333333333333333333333333333333333
    storage_validate_minio_security_transition \
        1 "$old_release" "$old_commit" 1 "$old_release" "$old_commit"
    storage_validate_minio_security_transition \
        2 "$new_release" "$new_commit" 1 "$old_release" "$old_commit"
    if (storage_validate_minio_security_transition \
        1 "$new_release" "$new_commit" 1 "$old_release" "$old_commit") >/dev/null 2>&1; then
        fail "equal MinIO security sequence changed the source pin"
    fi
    if (storage_validate_minio_security_transition \
        1 "$old_release" "$old_commit" 2 "$new_release" "$new_commit") >/dev/null 2>&1; then
        fail "old MinIO source became eligible after raising the security sequence"
    fi
    old_sha=1111111111111111111111111111111111111111
    attempted_sha=2222222222222222222222222222222222222222
    source_commit=$new_commit
    storage_write_format_floor "$floor_scratch/floor.json" production \
        2 2 2 H2SEv1 RELEASE.2026-12-31T00-00-00Z "$source_commit" \
        "$attempted_sha" "$old_sha"
    jq -e --arg attempted "$attempted_sha" --arg old "$old_sha" '
        .minimumProtocolVersion == 2 and .minimumStorageFormatVersion == 2 and
        .minimumMinioSecuritySequence == 2 and
        .pendingReleaseSha == $attempted and .lastSuccessfulReleaseSha == $old
    ' "$floor_scratch/floor.json" >/dev/null \
        || fail "a failed format-changing attempt did not retain its raised pending floor"
    storage_write_format_floor "$floor_scratch/floor.json" production \
        2 2 2 H2SEv1 RELEASE.2026-12-31T00-00-00Z "$source_commit" \
        "" "$attempted_sha"
    jq -e --arg successful "$attempted_sha" '
        .pendingReleaseSha == null and .lastSuccessfulReleaseSha == $successful
    ' "$floor_scratch/floor.json" >/dev/null \
        || fail "a verified format-changing attempt did not become successful"
fi
grep -F 'STORAGE_MINIO_SECURITY_POLICY=/etc/hook2stream-storage/minio-security-policy.json' \
    "$storage_dir/host/deploy.conf.example" >/dev/null \
    || fail "release-independent host security policy path changed"
grep -F '[ "$(stat -c '\''%u:%g:%a'\'' "$STORAGE_MINIO_SECURITY_POLICY")" = 0:0:600 ]' \
    "$forced" >/dev/null || fail "host security policy ownership is not fail-closed"
! grep -F '.minioRelease == "RELEASE.2025-10-15T17-29-55Z"' "$forced" >/dev/null \
    || fail "release-independent forced command hardcodes a MinIO source release"
! grep -F '.minioRelease == "RELEASE.2025-10-15T17-29-55Z"' \
    "$storage_dir/scripts/validate-candidate.sh" >/dev/null \
    || fail "release-independent candidate validator hardcodes a MinIO source release"
grep -F 'empty managed identity inventory cannot bootstrap non-empty MinIO data' \
    "$storage_dir/scripts/validate-config.sh" >/dev/null \
    || fail "empty identity inventory can silently bless existing MinIO data"
grep -F 'managed identity inventory must be root:root mode 0600' \
    "$storage_dir/scripts/validate-config.sh" >/dev/null \
    || fail "managed identity inventory is not root-only"
grep -F 'non-empty storage without a format floor requires operator recovery' "$forced" >/dev/null \
    || fail "legacy/unmarked storage does not fail closed"
grep -F 'HOOK2STREAM_STORAGE_REMOTE_RECEIPT=' "$forced" >/dev/null || fail "remote receipt prefix changed"
mount_dropin=$storage_dir/host/docker.service.d/hook2stream-storage-mount.conf
grep -Fx 'RequiresMountsFor=/srv/hook2stream-storage' "$mount_dropin" >/dev/null \
    || fail "Docker is not ordered after the encrypted storage mount"
grep -Fx 'After=srv-hook2stream\x2dstorage.mount' "$mount_dropin" >/dev/null \
    || fail "Docker lacks an explicit encrypted mount ordering dependency"
grep -Fx 'ConditionPathIsMountPoint=/srv/hook2stream-storage' "$mount_dropin" >/dev/null \
    || fail "Docker can start while the encrypted storage mount is absent"
for identity_contract in \
    'hook2stream-minio:10001' \
    'hook2stream-storage-caddy:10002' \
    'hook2stream-storage-init:10003'; do
    grep -F "$identity_contract" "$storage_dir/scripts/validate-config.sh" >/dev/null \
        || fail "host validator omitted dedicated identity $identity_contract"
done
grep -F '"/usr/sbin/nologin"' "$storage_dir/scripts/validate-config.sh" >/dev/null \
    || fail "service identities are not required to be non-login"
grep -F 'storage_validate_proc_visibility "$proc_options"' "$storage_dir/scripts/validate-config.sh" >/dev/null \
    || fail "host validator does not enforce process credential isolation"
. "$storage_dir/scripts/lib/storage-common.sh"
storage_validate_proc_visibility 'rw,nosuid,nodev,hidepid=2'
if (storage_validate_proc_visibility 'rw,hidepid=2,gid=2000') >/dev/null 2>&1; then
    fail "storage config accepted a /proc visibility bypass through gid="
fi
if (storage_validate_proc_visibility 'rw,nosuid,nodev') >/dev/null 2>&1; then
    fail "storage config accepted /proc without hidepid=2"
fi
for check in policy-verification quota-verification versioning-verification lifecycle-verification digest-verification; do
    grep -F "\"$check\"" "$forced" >/dev/null || fail "remote receipt omitted $check"
done
auth_line=$(grep -nF '/opt/hook2stream/minio-auth-healthcheck.sh' "$deploy" | cut -d: -f1)
isolation_line=$(grep -nF '/opt/hook2stream/minio-policy-isolation-probe.sh' "$deploy" | cut -d: -f1)
inventory_line=$(grep -nF 'persist_managed_identity_inventory' "$deploy" | tail -n 1 | cut -d: -f1)
caddy_line=$(grep -nF 'up -d --no-deps --force-recreate caddy' "$deploy" | cut -d: -f1)
source_label_line=$(grep -nF 'verify_minio_source_identity' "$deploy" | tail -n 1 | cut -d: -f1)
minio_start_line=$(grep -nF 'up -d --no-deps minio' "$deploy" | cut -d: -f1)
grep -F 'com.hook2stream.minio.source-release' "$deploy" >/dev/null \
    && grep -F 'com.hook2stream.minio.source-commit' "$deploy" >/dev/null \
    || fail "host does not verify the immutable Hook2Stream MinIO source labels"
if grep -F 'org.opencontainers.image.version' "$deploy" >/dev/null \
    || grep -F 'org.opencontainers.image.revision' "$deploy" >/dev/null; then
    fail "host trusts OCI labels that GitHub build metadata can override"
fi
test "$source_label_line" -lt "$minio_start_line" \
    || fail "MinIO source labels are not bound to the approved manifest before server start"
test "$inventory_line" -lt "$auth_line" && test "$auth_line" -lt "$isolation_line" \
    && test "$isolation_line" -lt "$caddy_line" \
    || fail "identity inventory/health/policy isolation do not safely gate Caddy startup"
printf '%s\n' "storage host contract: PASS"

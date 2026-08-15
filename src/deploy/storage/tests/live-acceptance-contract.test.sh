#!/bin/sh
set -eu

storage_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
runner=$storage_dir/tests/run-minio-acceptance.sh
multipart=$storage_dir/tests/minio-live-multipart-abort.sh
fail() { printf '%s\n' "storage live acceptance contract: $*" >&2; exit 1; }

grep -F '${MINIO_IMAGE:?MINIO_IMAGE must be the published image@sha256 reference}' "$runner" >/dev/null \
    || fail "published MinIO digest is not required"
grep -F '${MINIO_MC_IMAGE:?MINIO_MC_IMAGE must be the reviewed image@sha256 reference}' "$runner" >/dev/null \
    || fail "reviewed mc digest is not required"
grep -F '${CADDY_IMAGE:?CADDY_IMAGE must be the reviewed image@sha256 reference}' "$runner" >/dev/null \
    || fail "reviewed Caddy digest is not required"
grep -F 'acceptance_projects="staging:' "$runner" | grep -F ' production:' >/dev/null \
    || fail "both staging and production are not exercised"
[ "$(grep -c -- '--profile tools run --rm --no-deps -T minio-init' "$runner")" -eq 3 ] \
    || fail "live init does not cover idempotency plus failed-attempt rotation"
grep -F '< "$acceptance_root/$acceptance_run_environment/managed-identities.v1"' "$runner" >/dev/null \
    || fail "live init does not receive the root-only inventory through stdin"
grep -F 'minio-live-inject-stale-policy.sh' "$runner" >/dev/null \
    || fail "live init does not receive a stale broad-policy fixture"
grep -F 'admin policy attach fixture_root readwrite' "$storage_dir/tests/minio-live-inject-stale-policy.sh" >/dev/null \
    || fail "stale-policy fixture is not broad"
grep -F 'minio-live-retired-identity-deny.sh' "$runner" >/dev/null \
    || fail "live credential-ID rotation does not prove the retired identity is denied"
grep -F 'runtime0000000003' "$runner" >/dev/null \
    || fail "live acceptance does not rotate the runtime access-key ID twice"
grep -F 'simulated post-init probe failure' "$runner" >/dev/null \
    || fail "live acceptance omits the failed-after-init inventory transaction"
grep -F '.policyName == $policy' "$runner" >/dev/null \
    || fail "post-reconciliation policy set is not parsed exactly"
grep -F 'restart minio' "$runner" >/dev/null || fail "restart persistence is not tested"
grep -F '[ -z "$(docker port "$acceptance_run_container"' "$runner" >/dev/null \
    || fail "host-port absence is not tested"
grep -F 'minio-live-multipart-abort.sh' "$runner" >/dev/null \
    || fail "multipart abort acceptance is not invoked"
grep -F 'ls --incomplete "$target"' "$multipart" >/dev/null \
    || fail "a real incomplete multipart upload is not listed"
grep -F 'rm --incomplete --force "$target"' "$multipart" >/dev/null \
    || fail "the runtime identity does not abort the incomplete upload"
grep -F 'S3ObjectStorageMinioTests.H2se_round_trips_ranges_and_never_persists_plaintext_in_real_minio' "$runner" >/dev/null \
    || fail "the exact-digest gate omits H2SE upload/range/download acceptance"
grep -F 'HOOK2STREAM_TEST_MINIO="http://$acceptance_h2se_minio_ip:9000"' "$runner" >/dev/null \
    || fail "H2SE acceptance is not bound to the Compose MinIO under test"
grep -F '[ -z "$(docker port "$acceptance_postgres_container"' "$runner" >/dev/null \
    || fail "H2SE PostgreSQL dependency exposes a host port"
grep -F 'openssl req -x509 -newkey rsa:2048' "$runner" >/dev/null \
    || fail "live Caddy does not receive a real test certificate"
grep -F 'keys == ["443/tcp"]' "$runner" >/dev/null \
    || fail "live Caddy binding is not restricted to TCP 443"
grep -F 'docker port "$acceptance_caddy_container" 443/udp' "$runner" >/dev/null \
    || fail "live Caddy acceptance does not reject UDP 443"
grep -F 'https://$acceptance_caddy_name/.well-known/hook2stream-storage-protocol' "$runner" >/dev/null \
    || fail "storage protocol is not tested through live HTTPS Caddy"
for private_path in /minio/admin/v3/info /minio/storage/acceptance-internal-route /minio/health/ready; do
    grep -F "$private_path" "$runner" >/dev/null \
        || fail "live Caddy acceptance omits 404 probe for $private_path"
done
grep -F '"$(docker inspect --format '\''{{.Config.Image}}'\'' "$acceptance_caddy_container")" = "$CADDY_IMAGE"' "$runner" >/dev/null \
    || fail "live Caddy digest is not verified"
grep -F 'com.hook2stream.minio.source-release' "$runner" >/dev/null \
    && grep -F 'com.hook2stream.minio.source-commit' "$runner" >/dev/null \
    || fail "live MinIO digest is not bound to its manifest source labels"
grep -F 'down --volumes --remove-orphans' "$runner" >/dev/null \
    || fail "acceptance does not own cleanup"
printf '%s\n' "storage live acceptance contract: PASS"

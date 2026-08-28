#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
compose=$deployment_dir/compose.yaml
wrapper=$deployment_dir/scripts/deploy-forced-command.sh
release=$deployment_dir/scripts/deploy-release.sh
rollback=$deployment_dir/scripts/rollback-application.sh
validator=$deployment_dir/scripts/validate-candidate.sh
e2e_gate=$deployment_dir/scripts/post-deploy-e2e.sh
fail() { printf '%s\n' "MVP deployment contract test: $*" >&2; exit 1; }

for value in \
  'StorageEncryption__Mode: H2se' \
  'StorageEncryption__AllowLegacyPlaintextReads: "false"' \
  'StorageEncryption__MaxConcurrentEncryptions: ${H2SE_MAX_CONCURRENT_ENCRYPTIONS:-8}' \
  'StorageEncryption__MaxConcurrentDownloads: ${H2SE_MAX_CONCURRENT_DOWNLOADS:-4}' \
  'Auth__InviteOnly: "true"' \
  'Auth__InvitedEmailsFile: /run/secrets/invited_emails' \
  'BACKUP_AGE_RECIPIENT_FILE: /run/secrets/backup_age_recipient' \
  'TMPDIR: /tmp'; do
  grep -Fq "$value" "$compose" || fail "Compose is missing $value"
done

for environment_file in \
  "$deployment_dir/environments/staging.env.example" \
  "$deployment_dir/environments/production.env.example"; do
  grep -Fxq 'H2SE_MAX_CONCURRENT_ENCRYPTIONS=4' "$environment_file" \
    || fail "$(basename "$environment_file") does not cap H2SE encryptions at four"
  grep -Fxq 'H2SE_MAX_CONCURRENT_DOWNLOADS=2' "$environment_file" \
    || fail "$(basename "$environment_file") does not cap H2SE downloads at two"
done

[ "$(grep -Fc 'image: ${EGRESS_PROXY_IMAGE:?Set EGRESS_PROXY_IMAGE to an immutable image reference}' "$compose")" -eq 4 ] \
  || fail "four role-specific digest-pinned egress proxies are required"
[ "$(grep -Fc 'internal: true' "$compose")" -ge 4 ] || fail "application networks are not internal"
grep -Fq 'HOOK2STREAM_DEFER_SUCCESS_MARKER' "$release" || fail "deploy-release cannot defer success"
grep -Fq 'deployed releases require STORAGE_MODE=external; MinIO is local/CI only' "$release" \
  || fail "deploy-release does not reject the local-only MinIO mode"
for storj_contract in \
  'S3_ENDPOINT_HOST must be exactly gateway.storjshare.io' \
  'BACKUP_S3_ENDPOINT_HOST must be exactly gateway.storjshare.io' \
  'Storj media requires S3_REGION=global' \
  'Storj backups require BACKUP_S3_REGION=global' \
  'deployed Storj storage requires STORAGE_PROVISIONING_MODE=VerifyOnly' \
  'deployed media storage requires STORAGE_OBJECT_EXPIRATION_MODE=Storj' \
  'STORAGE_CONTRACT_KEY must be the canonical storage-v1 marker key' \
  'STORAGE_CONTRACT_SHA256 must be a lowercase SHA-256 digest'; do
  grep -Fq "$storj_contract" "$release" || fail "deploy-release is missing Storj contract: $storj_contract"
done
if grep -Eq 'h2s-storage-|\.ts\.net|remote MinIO|S3_PUBLIC_SERVICE_URL' "$release"; then
  fail "deploy-release still contains the removed remote-MinIO/Tailscale storage contract"
fi
if grep -Eq 'compose(_tools)? (pull|up|run).*minio|minio-init|public MinIO readiness' "$release"; then
  fail "deploy-release still contains a deployable MinIO startup path"
fi
grep -Fq 'HOOK2STREAM_DEFER_SUCCESS_MARKER=true' "$wrapper" || fail "forced deploy does not defer success"
grep -Fq '.deploy-bundle.sha256' "$wrapper" || fail "idempotent release bundle marker is missing"
grep -Fq 'candidate must contain exactly four files' "$validator" || fail "candidate extras are not rejected"
grep -Fq 'worker-media' "$wrapper" && grep -Fq 'worker-export' "$wrapper" || fail "all worker digests are not checked"
grep -Fq 'egress-s3' "$wrapper" && grep -Fq 'egress-control' "$wrapper" \
  && grep -Fq 'egress-backup' "$wrapper" || fail "all egress digests are not checked"
grep -Fq "'POSTGRES_BACKUP_IMAGE:storage-janitor'" "$wrapper" \
  || fail "the Storj media janitor digest is not checked"
grep -Fq 'compose up -d --no-deps caddy postgres-backup storage-janitor' "$release" \
  || fail "the Storj media janitor is not started by deployment"
grep -Fq 'storageFormats:["H2SEv1"]' "$wrapper" || fail "H2SE rollback capability is not recorded"
grep -Fq 'HOOK2STREAM_ROLLBACK_RECEIPT=' "$wrapper" || fail "rollback receipt marker is missing"
grep -Fq 'MIN_ROLLBACK_RELEASE_SHA must identify the first approved H2SE release' "$wrapper" \
  || fail "mandatory pre-mutation rollback floor validation is missing"
grep -Fq 'minimumRollbackReleaseSha:$minimum' "$wrapper" || fail "rollback receipt does not bind the host floor"
grep -Fq 'rollback-application.sh' "$wrapper" || fail "forced rollback is not routed through the app-only implementation"
grep -Fq 'release lacks the forward deploy or application-only rollback implementation' "$wrapper" \
  || fail "successful releases are not required to carry the app-only rollback implementation"
grep -Fq 'application-images-only' "$wrapper" && grep -Fq 'infrastructure-unchanged' "$wrapper" \
  && grep -Fq 'no-migrations' "$wrapper" || fail "rollback receipt does not attest app-only semantics"
grep -Fq 'docker image pull' "$rollback" && grep -Fq 'compose up -d --no-deps' "$rollback" \
  || fail "app-only rollback does not explicitly constrain service mutation"
if grep -Eq 'compose(_tools)? (run|up).*bootstrapper|deploy-release\.sh' "$rollback"; then
  fail "app-only rollback invokes the bootstrapper, migration path, or full deployment"
fi
if grep -Fq 'HOOK2STREAM_ENV_FILE=${release_snapshot} ${deployment_dir}/scripts/deploy-release.sh' "$release"; then
  fail "failed deploy still recommends the full migration path as rollback"
fi
grep -Fq 'forced-command.lock' "$wrapper" && grep -Fq 'flock -n 8' "$wrapper" \
  || fail "outer forced-command concurrency lock is missing"
grep -Fq 'exactly one DEPLOYMENT_ENVIRONMENT' "$wrapper" \
  || fail "production approval environment selection is not duplicate-safe"
for soak_contract in \
  'soak accepts exactly one candidate ID' \
  'soak is allowed only on staging' \
  'current-candidate.json' \
  '"$HOOK2STREAM_E2E_HOOK" staging "$current_env" "$commit" soak-60m' \
  'elapsed time is outside 3600-3900 seconds' \
  'renderActiveSeconds' \
  'exactly one worker-render container must exist after soak' \
  'HOOK2STREAM_REMOTE_SOAK_RECEIPT='; do
  grep -Fq "$soak_contract" "$wrapper" \
    || fail "forced staging soak omits contract: $soak_contract"
done
grep -Fq '.checks == ["pre-migration-backup","migration","smoke","e2e","digest-verification","render-network-soak"]' "$validator" \
  || fail "production host does not require the signed render/network soak check"
grep -Fq '.soakResult.hookResult.networkFailures == 0' "$validator" \
  && grep -Fq '.soakResult.workerRenderOomKilled == false' "$validator" \
  || fail "production host does not independently validate soak failure and OOM evidence"
grep -Fq '"$environment_file" "$commit" soak-60m' "$e2e_gate" \
  && grep -Fq 'hook2stream-soak-hook-result-v1' "$e2e_gate" \
  && grep -Fq '2>"$temporary_dir/soak.stderr"' "$e2e_gate" \
  || fail "post-deploy hook does not strictly isolate and validate sustained soak output"
if grep -Eq '^DEPLOYMENT_ENVIRONMENT=' "$deployment_dir/host/deploy.conf.example"; then
  fail "launcher config exports a Compose input override"
fi

printf '%s\n' "MVP deployment contract test: encryption, egress, deferred success, digest verification, and rollback floor passed"

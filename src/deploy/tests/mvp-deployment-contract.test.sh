#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
compose=$deployment_dir/compose.yaml
billing_overlay=$deployment_dir/compose.billing-stripe.yaml
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
  'Legal__TermsVersion: "2026-09-04"' \
  'Legal__PrivacyVersion: "2026-09-04"' \
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
config_mode_gate='hook2stream_prepare_container_config_modes "$release_dir/deploy" 0:0'
[ "$(grep -Fc "$config_mode_gate" "$wrapper")" -eq 1 ] \
  || fail "published release must prepare exact non-root container config modes once"
config_mode_line=$(grep -nF "$config_mode_gate" "$wrapper" | cut -d: -f1)
[ "$(sed -n "$((config_mode_line - 1))p" "$wrapper")" = '    fi' ] \
  || fail "container config modes are not repaired after both new and reused release paths"
grep -Fq 'candidate must contain exactly four files' "$validator" || fail "candidate extras are not rejected"
grep -Fq 'worker-media' "$wrapper" && grep -Fq 'worker-export' "$wrapper" || fail "all worker digests are not checked"
grep -Fq 'egress-s3' "$wrapper" && grep -Fq 'egress-control' "$wrapper" \
  && grep -Fq 'egress-backup' "$wrapper" || fail "all egress digests are not checked"
grep -Fq "'POSTGRES_BACKUP_IMAGE:storage-janitor'" "$wrapper" \
  || fail "the Storj media janitor digest is not checked"
grep -Fq 'compose up -d --no-deps caddy postgres-backup storage-janitor' "$release" \
  || fail "the Storj media janitor is not started by deployment"
grep -Fq 'storageFormats:["H2SEv1"]' "$wrapper" || fail "H2SE rollback capability is not recorded"
grep -Fq 'hook2stream-application-rollback-v2' "$wrapper" \
  && grep -Fq 'active-infrastructure-release.json' "$wrapper" \
  || fail "rollback protocol v2 or active infrastructure marker is missing"
[ "$(grep -Fc '> "$active_infrastructure.tmp"' "$wrapper")" -eq 1 ] \
  && [ "$(grep -Fc 'mv -f "$active_infrastructure.tmp" "$active_infrastructure"' "$wrapper")" -eq 1 ] \
  || fail "active infrastructure marker is not forward-deploy-only state"
grep -Fq 'HOOK2STREAM_ROLLBACK_RECEIPT=' "$wrapper" || fail "rollback receipt marker is missing"
grep -Fq 'MIN_ROLLBACK_RELEASE_SHA must identify the first approved H2SE release' "$wrapper" \
  || fail "mandatory pre-mutation rollback floor validation is missing"
grep -Fq 'minimumRollbackReleaseSha:$minimum' "$wrapper" || fail "rollback receipt does not bind the host floor"
grep -Fq 'rollback-application.sh' "$wrapper" || fail "forced rollback is not routed through the app-only implementation"
grep -Fq 'rollback_program=/usr/local/libexec/hook2stream/rollback-application.sh' "$wrapper" \
  || fail "rollback is not pinned to the installed root-owned orchestrator"
if grep -Fq 'rollback_dir/deploy/scripts/rollback-application.sh' "$wrapper"; then
  fail "rollback executes target bundle control-plane code"
fi
grep -Fq 'infrastructure_release_dir/deploy/scripts/lib/deployment-common.sh' "$wrapper" \
  && grep -Fq 'infrastructure_release_dir/deploy/compose.yaml' "$wrapper" \
  && grep -Fq 'infrastructure_release_dir/deploy/compose.billing-stripe.yaml' "$wrapper" \
  && grep -Fq 'differs from the active infrastructure release marker' "$wrapper" \
  || fail "active infrastructure compose/helper source is not validated"
grep -Fq 'application-images-only' "$wrapper" && grep -Fq 'infrastructure-unchanged' "$wrapper" \
  && grep -Fq 'no-migrations' "$wrapper" || fail "rollback receipt does not attest app-only semantics"
grep -Fq 'docker --config "$DOCKER_CONFIG" image pull' "$rollback" \
  && grep -Fq 'compose up -d --no-deps' "$rollback" \
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
for transactional_contract in \
  'first deployment requires explicit prepare followed by operator OAuth bootstrap and finalize' \
  'transactionMode:$transactionMode' \
  'finalize-transaction:immediate-deploy' \
  'publish_compensated_state' \
  'recovery-required.json' \
  'public Caddy could not be proven stopped' \
  'rollback-verify' \
  'recover_failed_rollback' \
  'rollout_recovery_interrupted' \
  'runtime-ready-publication-failed' \
  'compensation_interrupted' \
  'rollback_recovery_interrupted' \
  'bounded-e2e-reverification' \
  'rollback is forbidden while a forward deployment is pending'; do
  grep -Fq "$transactional_contract" "$wrapper" \
    || fail "forced command omits transactional boundary: $transactional_contract"
done
grep -Fq 'docker stop --time 10 "$recovery_container"' "$wrapper" \
  && grep -Fq 'docker kill "$recovery_container"' "$wrapper" \
  || fail "recovery-required path does not fail closed by stopping exact owned Caddy"
grep -Fq '"$environment" "$active_rollback_env" "$identifier" rollback-verify' "$wrapper" \
  || fail "rollback does not invoke the mandatory fourth-argument bounded gate"
if grep -Fq '"$environment" "$active_rollback_env" "$identifier"' "$wrapper" \
   && ! grep -Fq '"$environment" "$active_rollback_env" "$identifier" rollback-verify' "$wrapper"; then
  fail "rollback retained the obsolete three-argument authenticated hook"
fi
grep -Fq 'infrastructure_ledger=$HOOK2STREAM_RELEASE_STATE_DIR/infrastructure' "$wrapper" \
  && grep -Fq 'active compensated infrastructure ledger is incomplete or unsafe' "$wrapper" \
  && grep -Fq 'same-SHA compensated infrastructure ledger is stale or unsafe' "$wrapper" \
  && grep -Fq '"$HOOK2STREAM_RELEASE_STATE_DIR/infrastructure/$commit.env"' "$wrapper" \
  && grep -Fq 'install -m 0600 "$compensated_active_env"' "$wrapper" \
  || fail "rollback cannot resolve the exact compensated infrastructure ledger"
for rollback_transaction in \
  'rollback_exit()' \
  'restore_application()' \
  'mutation_started=true' \
  'mv -f "$active_environment_tmp" "$active_environment_file"'; do
  grep -Fq "$rollback_transaction" "$rollback" \
    || fail "application rollback omits reversible transaction boundary: $rollback_transaction"
done
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

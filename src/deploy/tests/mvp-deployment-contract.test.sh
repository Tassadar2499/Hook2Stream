#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
compose=$deployment_dir/compose.yaml
wrapper=$deployment_dir/scripts/deploy-forced-command.sh
release=$deployment_dir/scripts/deploy-release.sh
rollback=$deployment_dir/scripts/rollback-application.sh
validator=$deployment_dir/scripts/validate-candidate.sh
fail() { printf '%s\n' "MVP deployment contract test: $*" >&2; exit 1; }

for value in \
  'StorageEncryption__Mode: H2se' \
  'StorageEncryption__AllowLegacyPlaintextReads: "false"' \
  'StorageEncryption__MaxConcurrentEncryptions: "8"' \
  'StorageEncryption__MaxConcurrentDownloads: "4"' \
  'Auth__InviteOnly: "true"' \
  'Auth__InvitedEmailsFile: /run/secrets/invited_emails' \
  'BACKUP_AGE_RECIPIENT_FILE: /run/secrets/backup_age_recipient' \
  'TMPDIR: /tmp'; do
  grep -Fq "$value" "$compose" || fail "Compose is missing $value"
done

[ "$(grep -Fc 'image: ${EGRESS_PROXY_IMAGE:?Set EGRESS_PROXY_IMAGE to an immutable image reference}' "$compose")" -eq 3 ] \
  || fail "three role-specific digest-pinned egress proxies are required"
[ "$(grep -Fc 'internal: true' "$compose")" -ge 4 ] || fail "application networks are not internal"
grep -Fq 'HOOK2STREAM_DEFER_SUCCESS_MARKER' "$release" || fail "deploy-release cannot defer success"
grep -Fq 'HOOK2STREAM_DEFER_SUCCESS_MARKER=true' "$wrapper" || fail "forced deploy does not defer success"
grep -Fq '.deploy-bundle.sha256' "$wrapper" || fail "idempotent release bundle marker is missing"
grep -Fq 'candidate must contain exactly four files' "$validator" || fail "candidate extras are not rejected"
grep -Fq 'worker-media' "$wrapper" && grep -Fq 'worker-export' "$wrapper" || fail "all worker digests are not checked"
grep -Fq 'egress-s3' "$wrapper" && grep -Fq 'egress-control' "$wrapper" || fail "all egress digests are not checked"
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
if grep -Eq '^DEPLOYMENT_ENVIRONMENT=' "$deployment_dir/host/deploy.conf.example"; then
  fail "launcher config exports a Compose input override"
fi

printf '%s\n' "MVP deployment contract test: encryption, egress, deferred success, digest verification, and rollback floor passed"

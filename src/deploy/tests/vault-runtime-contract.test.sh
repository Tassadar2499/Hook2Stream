#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
temporary_dir=$(mktemp -d)

cleanup() {
    rm -rf "$temporary_dir"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

test_fail() {
    printf '%s\n' "Vault runtime contract test: $*" >&2
    exit 1
}

if ! command -v jq >/dev/null 2>&1; then
    printf '%s\n' "Vault runtime contract test: skipped (jq is unavailable)"
    exit 0
fi

deployment_program=vault-runtime-contract-test
environment_file=$temporary_dir/staging.env
printf '%s\n' 'DEPLOYMENT_ENVIRONMENT=staging' 'BILLING_MODE=stripe' > "$environment_file"
HOOK2STREAM_ENV_FILE=$environment_file
export HOOK2STREAM_ENV_FILE
. "$deployment_dir/scripts/lib/deployment-common.sh"

# Splitting is normally root-only. These stubs let the contract test exercise
# the exact data transformation without weakening production ownership checks.
chown() { :; }
stat() {
    [ "${1:-}" = -c ] || return 1
    case "${3:-}" in
        */manifest.json) printf '%s\n' '0:0:600' ;;
        *) printf '%s\n' '0:2000:640' ;;
    esac
}

vault_secrets_gid=2000
candidate=$temporary_dir/candidate
mkdir "$candidate"
printf '%s\n' '{"kv_version":11,"secrets":{"postgres_password":"postgres"}}' \
    > "$candidate/foundation.json"
printf '%s\n' '{"kv_version":12,"secrets":{"access_key_id":"media-id","secret_access_key":"media-secret"}}' \
    > "$candidate/runtime-s3.json"
printf '%s\n' '{"kv_version":13,"secrets":{"google_client_secret":"google","stripe_secret_key":"stripe","stripe_webhook_secret":"webhook"}}' \
    > "$candidate/api.json"
printf '%s\n' '{"kv_version":14,"secrets":{"openrouter_api_key":"openrouter"}}' \
    > "$candidate/control.json"
printf '%s\n' '{"kv_version":15,"secrets":{"access_key_id":"backup-id","secret_access_key":"backup-secret"}}' \
    > "$candidate/backup-s3.json"
printf '%s\n' '{"kv_version":16,"secrets":{"invited_emails":"first@example.com\nsecond@example.com","media_keyring":"{\"activeKeyId\":\"k1\",\"keys\":{\"k1\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\"}}"}}' \
    > "$candidate/media-security.json"
printf '%s\n' '{"kv_version":17,"secrets":{"age_recipient":"age1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq"}}' \
    > "$candidate/backup-encryption.json"

vault_validate_candidate_bundles "$candidate" \
    || test_fail "seven strict candidate bundles were rejected"
vault_split_candidate "$candidate" \
    || test_fail "candidate splitting failed"
vault_validate_generation "$candidate" \
    || test_fail "split generation validation failed"

actual_scalars=$(find "$candidate" -mindepth 1 -maxdepth 1 -type f \
    ! -name manifest.json -print | sed 's#^.*/##' | sort)
expected_scalars=$(deployment_required_secret_files | sort)
[ "$actual_scalars" = "$expected_scalars" ] \
    || test_fail "split generation does not contain exactly the 12 runtime scalars"

jq -e '
    .schema_version == 1
    and .bundle_kv_versions == {
        "api": 13,
        "backup-encryption": 17,
        "backup-s3": 15,
        "control": 14,
        "foundation": 11,
        "media-security": 16,
        "runtime-s3": 12
    }
' "$candidate/manifest.json" >/dev/null \
    || test_fail "generation manifest does not bind all seven KV versions"

[ "$(cat "$candidate/backup_age_recipient")" = \
    'age1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq' ] \
    || test_fail "age recipient was not materialized"
[ "$(cat "$candidate/invited_emails")" = "$(printf 'first@example.com\nsecond@example.com')" ] \
    || test_fail "invited email LFs were not preserved"
jq -e '.activeKeyId == "k1" and (.keys | keys) == ["k1"]' \
    "$candidate/media_keyring" >/dev/null \
    || test_fail "one-line media keyring JSON was not materialized"

disabled_candidate=$temporary_dir/disabled-candidate
mkdir "$disabled_candidate"
printf '%s\n' '{"kv_version":21,"secrets":{"postgres_password":"postgres"}}' \
    > "$disabled_candidate/foundation.json"
printf '%s\n' '{"kv_version":22,"secrets":{"access_key_id":"media-id","secret_access_key":"media-secret"}}' \
    > "$disabled_candidate/runtime-s3.json"
printf '%s\n' '{"kv_version":23,"secrets":{"google_client_secret":"google"}}' \
    > "$disabled_candidate/api.json"
printf '%s\n' '{"kv_version":24,"secrets":{"openrouter_api_key":"openrouter"}}' \
    > "$disabled_candidate/control.json"
printf '%s\n' '{"kv_version":25,"secrets":{"access_key_id":"backup-id","secret_access_key":"backup-secret"}}' \
    > "$disabled_candidate/backup-s3.json"
printf '%s\n' '{"kv_version":26,"secrets":{"invited_emails":"first@example.com","media_keyring":"{\"activeKeyId\":\"k1\",\"keys\":{\"k1\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\"}}"}}' \
    > "$disabled_candidate/media-security.json"
printf '%s\n' '{"kv_version":27,"secrets":{"age_recipient":"age1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq"}}' \
    > "$disabled_candidate/backup-encryption.json"
environment_file=$temporary_dir/production.env
printf '%s\n' 'DEPLOYMENT_ENVIRONMENT=production' 'BILLING_MODE=disabled' > "$environment_file"
vault_validate_candidate_bundles "$disabled_candidate" \
    || test_fail "billing-disabled Vault candidate without Stripe was rejected"
vault_split_candidate "$disabled_candidate" \
    || test_fail "billing-disabled Vault candidate splitting failed"
vault_validate_generation "$disabled_candidate" \
    || test_fail "billing-disabled Vault generation validation failed"
if find "$disabled_candidate" -maxdepth 1 -type f -name 'stripe_*' | grep -q .; then
    test_fail "billing-disabled Vault generation materialized Stripe secrets"
fi
environment_file=$temporary_dir/staging.env

active=$temporary_dir/active
cp -R "$candidate" "$active"
printf '%s' 'replacement@example.com' > "$candidate/invited_emails"
[ "$(vault_changed_bundles "$candidate" "$active")" = media-security ] \
    || test_fail "media-security drift was not isolated to its bundle"
cp "$active/invited_emails" "$candidate/invited_emails"

reconcile_log=$temporary_dir/reconcile.log
vault_recreate_and_wait() {
    printf 'recreate:%s\n' "$*" >> "$reconcile_log"
}
compose() {
    printf 'compose:%s\n' "$*" >> "$reconcile_log"
}

vault_reconcile_regular_consumers \
    'runtime-s3 api control media-security backup-s3' apply \
    || test_fail "regular consumer reconciliation failed"
first_recreate=$(sed -n '1p' "$reconcile_log")
for service in \
    worker-media worker-analysis worker-render worker-export worker-control api \
    storage-janitor; do
    occurrences=$(printf '%s\n' "$first_recreate" \
        | sed 's/^recreate://' | tr ' ' '\n' \
        | grep -cx "$service" || true)
    [ "$occurrences" -eq 1 ] \
        || test_fail "$service was not reconciled exactly once"
done
grep -qx 'compose:run --rm postgres-backup backup-once' "$reconcile_log" \
    || test_fail "backup credential rotation did not create a proving backup"
grep -qx 'recreate:postgres-backup' "$reconcile_log" \
    || test_fail "backup daemon was not reconciled"

: > "$reconcile_log"
vault_reconcile_backup_age_recipient_consumer apply \
    || test_fail "age recipient consumer reconciliation failed"
[ "$(sed -n '1p' "$reconcile_log")" = \
    'compose:run --rm postgres-backup backup-once' ] \
    || test_fail "age recipient was not proven before daemon recreation"
[ "$(sed -n '2p' "$reconcile_log")" = 'recreate:postgres-backup' ] \
    || test_fail "age recipient rotation did not recreate the daemon last"

rotation=$deployment_dir/scripts/rotate-backup-age-recipient.sh
[ -x "$rotation" ] || test_fail "age recipient rotation script is not executable"
grep -Fq 'vault_restore_links "$old_current_target" "$old_previous_target"' "$rotation" \
    || test_fail "age recipient rotation does not roll back managed links"
grep -Fq 'vault_reconcile_backup_age_recipient_consumer rollback' "$rotation" \
    || test_fail "age recipient rotation does not roll back the backup daemon"
if rg -n 'passphrase|backup-key-registry|private.*identity' \
    "$deployment_dir/vault/agent.hcl" \
    "$deployment_dir/vault/templates" \
    "$deployment_dir/vault/policies" \
    "$rotation" >/dev/null; then
    test_fail "Vault runtime retains backup passphrases, key history, or private identities"
fi

printf '%s\n' \
    "Vault runtime contract test: 7 bundles, 12 scalars, manifest, and reconciliation passed"

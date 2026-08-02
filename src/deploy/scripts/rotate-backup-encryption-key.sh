#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
deployment_dir=$(CDPATH= cd -- "$script_dir/.." && pwd)
deployment_program=rotate-backup-encryption-key
. "$script_dir/lib/deployment-common.sh"

usage() {
    printf '%s\n' \
        "Usage: rotate-backup-encryption-key.sh" \
        "" \
        "Activates a new backup key ID/passphrase pair, creates a proving backup," \
        "then recreates the backup daemon. Existing key IDs are immutable."
}

case "${1:-}" in
    "") ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
esac
[ "$#" -eq 0 ] || { usage >&2; exit 2; }

deployment_require_base_tools
deployment_require_command sha256sum
deployment_acquire_lock
vault_require_configuration

active_generation=$(vault_current_generation) \
    || fail "no valid active Vault generation; run render-vault-secrets.sh for initial activation"
old_current_target=$(vault_link_target current)
old_previous_target=$(vault_link_target previous 2>/dev/null || true)
candidate_generation=$(vault_render_generation)
changed_bundles=$(vault_changed_bundles "$candidate_generation" "$active_generation")

if [ -z "$changed_bundles" ]; then
    vault_safe_remove_generation "$candidate_generation" \
        || fail "no values changed, but candidate cleanup failed"
    deployment_log "backup encryption key is already current"
    exit 0
fi
for changed_bundle in $changed_bundles; do
    [ "$changed_bundle" = backup-encryption ] || {
        vault_safe_remove_generation "$candidate_generation" || true
        fail "another bundle also changed (${changed_bundle}); rotate it separately before backup encryption"
    }
done

backup_key_registry=$secret_state_dir/backup-key-registry
touch "$backup_key_registry"
chmod 0600 "$backup_key_registry"

validate_key_id() {
    backup_key_id_to_validate=$1
    case "$backup_key_id_to_validate" in
        [A-Za-z0-9]*) ;;
        *) return 1 ;;
    esac
    case "$backup_key_id_to_validate" in
        *[!A-Za-z0-9._-]*) return 1 ;;
    esac
    [ "${#backup_key_id_to_validate}" -le 64 ]
}

registry_digest_for_id() {
    backup_registry_id=$1
    awk -v requested_id="$backup_registry_id" '
        $1 == requested_id {
            if (found && digest != $2) exit 2
            digest = $2
            found = 1
        }
        END { if (found) print digest }
    ' "$backup_key_registry"
}

register_key_pair() {
    backup_register_id=$1
    backup_register_digest=$2
    backup_existing_digest=$(registry_digest_for_id "$backup_register_id") || return 1
    if [ -n "$backup_existing_digest" ]; then
        [ "$backup_existing_digest" = "$backup_register_digest" ]
        return
    fi
    printf '%s %s\n' "$backup_register_id" "$backup_register_digest" \
        >> "$backup_key_registry"
}

active_key_id=$(sed -e 's/[[:space:]]*$//' "$active_generation/backup_encryption_key_id")
candidate_key_id=$(sed -e 's/[[:space:]]*$//' "$candidate_generation/backup_encryption_key_id")
validate_key_id "$active_key_id" && validate_key_id "$candidate_key_id" || {
    vault_safe_remove_generation "$candidate_generation" || true
    fail "backup encryption key ID is invalid"
}
active_key_digest=$(sha256sum "$active_generation/backup_encryption_passphrase" | awk '{print $1}')
candidate_key_digest=$(sha256sum "$candidate_generation/backup_encryption_passphrase" | awk '{print $1}')

# Seed the registry with the already-active successful key when upgrading an
# installation that predates the registry.
register_key_pair "$active_key_id" "$active_key_digest" \
    || { vault_safe_remove_generation "$candidate_generation" || true; fail "active key ID conflicts with the backup-key registry"; }
registered_candidate_digest=$(registry_digest_for_id "$candidate_key_id") \
    || { vault_safe_remove_generation "$candidate_generation" || true; fail "backup-key registry contains conflicting records"; }
if [ -n "$registered_candidate_digest" ] \
    && [ "$registered_candidate_digest" != "$candidate_key_digest" ]; then
    vault_safe_remove_generation "$candidate_generation" || true
    fail "backup key ID reuse with a different passphrase was refused before activation"
fi

if ! vault_activate_generation "$candidate_generation"; then
    vault_restore_links "$old_current_target" "$old_previous_target" 2>/dev/null || true
    vault_safe_remove_generation "$candidate_generation" || true
    fail "could not atomically activate the backup encryption candidate"
fi

if ! vault_reconcile_backup_encryption_consumer apply; then
    printf '%s\n' \
        "${deployment_program}: proving backup failed; restoring the prior key" >&2
    backup_rollback_ok=true
    vault_restore_links "$old_current_target" "$old_previous_target" \
        || backup_rollback_ok=false
    vault_reconcile_backup_encryption_consumer rollback \
        || backup_rollback_ok=false
    if [ "$backup_rollback_ok" = true ]; then
        vault_safe_remove_generation "$candidate_generation" || true
        fail "backup key rotation failed; active key and daemon were rolled back"
    fi
    fail "automatic backup-key rollback was incomplete; failed generation retained for recovery"
fi

register_key_pair "$candidate_key_id" "$candidate_key_digest" \
    || fail "new backup succeeded, but its immutable key registry record could not be written"
vault_prune_generations \
    || fail "backup key rotation succeeded, but stale generation cleanup failed"
unset active_key_digest candidate_key_digest registered_candidate_digest
deployment_log "backup encryption key rotation completed"

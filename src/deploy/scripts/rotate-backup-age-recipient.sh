#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
deployment_dir=$(CDPATH= cd -- "$script_dir/.." && pwd)
deployment_program=rotate-backup-age-recipient
. "$script_dir/lib/deployment-common.sh"

usage() {
    printf '%s\n' \
        "Usage: rotate-backup-age-recipient.sh" \
        "" \
        "Activates the public age recipient rendered from Vault, proves it by" \
        "creating a fresh encrypted PostgreSQL backup, then recreates the daemon."
}

case "${1:-}" in
    "") ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
esac
[ "$#" -eq 0 ] || { usage >&2; exit 2; }

deployment_require_base_tools
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
    deployment_log "backup age recipient is already current"
    exit 0
fi
for changed_bundle in $changed_bundles; do
    [ "$changed_bundle" = backup-encryption ] || {
        vault_safe_remove_generation "$candidate_generation" || true
        fail "another bundle also changed (${changed_bundle}); rotate it separately before the backup age recipient"
    }
done

if ! vault_activate_generation "$candidate_generation"; then
    vault_restore_links "$old_current_target" "$old_previous_target" 2>/dev/null || true
    vault_safe_remove_generation "$candidate_generation" || true
    fail "could not atomically activate the backup age-recipient candidate"
fi

if ! vault_reconcile_backup_age_recipient_consumer apply; then
    printf '%s\n' \
        "${deployment_program}: proving backup failed; restoring the prior recipient" >&2
    backup_rollback_ok=true
    vault_restore_links "$old_current_target" "$old_previous_target" \
        || backup_rollback_ok=false
    vault_reconcile_backup_age_recipient_consumer rollback \
        || backup_rollback_ok=false
    if [ "$backup_rollback_ok" = true ]; then
        vault_safe_remove_generation "$candidate_generation" || true
        fail "backup recipient rotation failed; active links and daemon were rolled back"
    fi
    fail "automatic backup-recipient rollback was incomplete; failed generation retained for recovery"
fi

vault_prune_generations \
    || fail "backup recipient rotation succeeded, but stale generation cleanup failed"
deployment_log "backup age recipient rotation completed"

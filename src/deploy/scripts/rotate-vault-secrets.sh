#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
deployment_dir=$(CDPATH= cd -- "$script_dir/.." && pwd)
deployment_program=rotate-vault-secrets
. "$script_dir/lib/deployment-common.sh"

usage() {
    printf '%s\n' \
        "Usage: rotate-vault-secrets.sh" \
        "" \
        "Rotates routine Vault bundles and health-gates their exact consumers." \
        "PostgreSQL and backup age-recipient changes require specialized scripts."
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
    deployment_log "no scalar secret changes detected"
    exit 0
fi

special_change=false
if vault_list_has "$changed_bundles" foundation; then
    printf '%s\n' \
        "${deployment_program}: foundation changed; use rotate-postgres-password.sh" >&2
    special_change=true
fi
if vault_list_has "$changed_bundles" backup-encryption; then
    printf '%s\n' \
        "${deployment_program}: backup-encryption changed; use rotate-backup-age-recipient.sh" >&2
    special_change=true
fi
if [ "$special_change" = true ]; then
    vault_safe_remove_generation "$candidate_generation" || true
    fail "specialized secret changes were refused before activation"
fi

if ! vault_activate_generation "$candidate_generation"; then
    vault_restore_links "$old_current_target" "$old_previous_target" 2>/dev/null || true
    vault_safe_remove_generation "$candidate_generation" || true
    fail "could not atomically activate the candidate generation"
fi

if ! vault_reconcile_regular_consumers "$changed_bundles" apply; then
    printf '%s\n' \
        "${deployment_program}: consumer reconciliation failed; restoring the prior generation" >&2
    if ! vault_restore_links "$old_current_target" "$old_previous_target"; then
        fail "automatic rollback could not restore current/previous symlinks; failed generation retained"
    fi
    if ! vault_reconcile_regular_consumers "$changed_bundles" rollback; then
        fail "prior symlink was restored, but one or more consumers failed to roll back; failed generation retained"
    fi
    vault_safe_remove_generation "$candidate_generation" || true
    fail "rotation failed; consumers and active secrets were rolled back"
fi

vault_prune_generations \
    || fail "rotation succeeded, but stale generation cleanup failed"
deployment_log "rotated bundles: $changed_bundles"

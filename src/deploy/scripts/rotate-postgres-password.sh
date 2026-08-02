#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
deployment_dir=$(CDPATH= cd -- "$script_dir/.." && pwd)
deployment_program=rotate-postgres-password
. "$script_dir/lib/deployment-common.sh"

usage() {
    printf '%s\n' \
        "Usage: rotate-postgres-password.sh" \
        "" \
        "Changes the PostgreSQL role password, activates the matching generation," \
        "then recreates PgBouncer and every database consumer with health rollback."
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
    deployment_log "PostgreSQL password is already current"
    exit 0
fi
if ! vault_list_has "$changed_bundles" foundation; then
    vault_safe_remove_generation "$candidate_generation" || true
    fail "foundation did not change; use rotate-vault-secrets.sh for routine bundles"
fi
for changed_bundle in $changed_bundles; do
    [ "$changed_bundle" = foundation ] || {
        vault_safe_remove_generation "$candidate_generation" || true
        fail "another bundle also changed (${changed_bundle}); rotate it separately before PostgreSQL"
    }
done

wait_for_service postgres \
    || { vault_safe_remove_generation "$candidate_generation" || true; fail "PostgreSQL is not healthy"; }
if ! vault_set_postgres_password "$candidate_generation/postgres_password"; then
    vault_safe_remove_generation "$candidate_generation" || true
    fail "PostgreSQL rejected the new role password; active secrets were not changed"
fi

if ! vault_activate_generation "$candidate_generation"; then
    vault_set_postgres_password "$active_generation/postgres_password" 2>/dev/null || true
    vault_restore_links "$old_current_target" "$old_previous_target" 2>/dev/null || true
    vault_safe_remove_generation "$candidate_generation" || true
    fail "new database password was reverted after generation activation failed"
fi

if ! vault_reconcile_postgres_consumers apply; then
    printf '%s\n' \
        "${deployment_program}: database consumer reconciliation failed; rolling back" >&2
    database_rollback_ok=true
    vault_set_postgres_password "$active_generation/postgres_password" \
        || database_rollback_ok=false
    vault_restore_links "$old_current_target" "$old_previous_target" \
        || database_rollback_ok=false
    vault_reconcile_postgres_consumers rollback \
        || database_rollback_ok=false
    if [ "$database_rollback_ok" = true ]; then
        vault_safe_remove_generation "$candidate_generation" || true
        fail "rotation failed; PostgreSQL and all consumers were rolled back"
    fi
    fail "automatic PostgreSQL rollback was incomplete; failed generation retained for recovery"
fi

vault_prune_generations \
    || fail "PostgreSQL rotation succeeded, but stale generation cleanup failed"
deployment_log "PostgreSQL password rotation completed"

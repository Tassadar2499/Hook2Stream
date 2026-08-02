#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
deployment_dir=$(CDPATH= cd -- "$script_dir/.." && pwd)
deployment_program=render-vault-secrets
. "$script_dir/lib/deployment-common.sh"

usage() {
    printf '%s\n' \
        "Usage: render-vault-secrets.sh" \
        "" \
        "Renders and validates Vault once. The first valid generation is activated;" \
        "later value drift is reported without changing the active generation."
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
vault_preflight_release

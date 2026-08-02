#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
temporary_dir=$(mktemp -d)

cleanup() {
    rm -rf "$temporary_dir"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

fail_test() {
    printf '%s\n' "Vault endpoint preflight test: $*" >&2
    exit 1
}

environment_file=${temporary_dir}/deployment.env
cat > "$environment_file" <<EOF
SECRET_PROVIDER=vault
VAULT_ADDR=https://vault.example.invalid:8200
SECRETS_DIR=${temporary_dir}/secrets/current
EOF

deployment_program=vault-endpoint-preflight-test
HOOK2STREAM_ENV_FILE=$environment_file
export HOOK2STREAM_ENV_FILE
. "$deployment_dir/scripts/lib/deployment-common.sh"

require_https_origin VAULT_ADDR

cat > "$environment_file" <<EOF
SECRET_PROVIDER=vault
VAULT_ADDR=http://vault.example.invalid:8200
SECRETS_DIR=${temporary_dir}/secrets/current
EOF
if (vault_require_configuration) >"${temporary_dir}/http-output" 2>&1; then
    fail_test "Vault HTTP endpoint was accepted"
fi
grep -F 'VAULT_ADDR must be an unquoted HTTPS origin' \
    "${temporary_dir}/http-output" >/dev/null \
    || fail_test "Vault HTTP rejection did not explain the HTTPS requirement"

cat > "$environment_file" <<EOF
SECRET_PROVIDER=vault
VAULT_ADDR=https://vault.example.invalid:8200/unexpected-path
SECRETS_DIR=${temporary_dir}/secrets/current
EOF
if (require_https_origin VAULT_ADDR) >"${temporary_dir}/path-output" 2>&1; then
    fail_test "Vault endpoint with a path was accepted as an origin"
fi

printf '%s\n' "Vault endpoint preflight test: HTTPS origin is enforced"

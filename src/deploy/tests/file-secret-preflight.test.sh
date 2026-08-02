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
    printf '%s\n' "file secret preflight test: $*" >&2
    exit 1
}

secret_dir=${temporary_dir}/secrets
stub_bin=${temporary_dir}/bin
environment_file=${temporary_dir}/deployment.env
mkdir -p "$secret_dir" "$stub_bin"

cat > "${stub_bin}/stat" <<'EOF'
#!/bin/sh
set -eu
target=${3:-}
case "$target" in
    */secrets) printf '0:%s:750\n' "${TEST_SECRETS_GID:-2000}" ;;
    *) printf '0:%s:%s\n' \
        "${TEST_SECRETS_GID:-2000}" "${TEST_SECRET_MODE:-640}" ;;
esac
EOF
chmod 0700 "${stub_bin}/stat"

cat > "$environment_file" <<EOF
SECRETS_DIR=$secret_dir
SECRETS_GID=2000
EOF

deployment_program=file-secret-preflight-test
HOOK2STREAM_ENV_FILE=$environment_file
export HOOK2STREAM_ENV_FILE
. "$deployment_dir/scripts/lib/deployment-common.sh"

for secret_name in $(deployment_required_secret_files); do
    printf '%s\n' test-secret > "$secret_dir/$secret_name"
done
: > "$secret_dir/backup_heartbeat_url"

PATH="${stub_bin}:${PATH}" deployment_validate_file_secrets

if (PATH="${stub_bin}:${PATH}" TEST_SECRET_MODE=644 \
    deployment_validate_file_secrets) >/dev/null 2>&1; then
    test_fail "preflight accepted a group-writable/world-readable secret mode"
fi

: > "$secret_dir/postgres_password"
if (PATH="${stub_bin}:${PATH}" deployment_validate_file_secrets) >/dev/null 2>&1; then
    test_fail "preflight accepted an empty required secret"
fi
printf '%s\n' test-secret > "$secret_dir/postgres_password"

mv "$secret_dir/google_client_secret" "$secret_dir/google_client_secret.value"
ln -s "$secret_dir/google_client_secret.value" "$secret_dir/google_client_secret"
if (PATH="${stub_bin}:${PATH}" deployment_validate_file_secrets) >/dev/null 2>&1; then
    test_fail "preflight accepted a symlinked secret"
fi
rm "$secret_dir/google_client_secret"
mv "$secret_dir/google_client_secret.value" "$secret_dir/google_client_secret"

SECRETS_GID=2468
export SECRETS_GID
PATH="${stub_bin}:${PATH}" TEST_SECRETS_GID=2468 deployment_validate_file_secrets

SECRETS_GID=invalid
export SECRETS_GID
if (PATH="${stub_bin}:${PATH}" deployment_validate_file_secrets) >/dev/null 2>&1; then
    test_fail "preflight accepted a non-numeric SECRETS_GID"
fi

if command -v jq >/dev/null 2>&1; then
    vault_backup_bundle=${temporary_dir}/backup-s3.json
    cat > "$vault_backup_bundle" <<'EOF'
{"kv_version":1,"secrets":{"access_key_id":"id","heartbeat_url":"","secret_access_key":"secret"}}
EOF
    vault_validate_bundle "$vault_backup_bundle" \
        '["access_key_id","heartbeat_url","secret_access_key"]' \
        '["heartbeat_url"]' \
        || test_fail "Vault contract rejected an empty optional heartbeat URL"

    cat > "$vault_backup_bundle" <<'EOF'
{"kv_version":1,"secrets":{"access_key_id":"","heartbeat_url":"https://monitor.invalid/token","secret_access_key":"secret"}}
EOF
    if vault_validate_bundle "$vault_backup_bundle" \
        '["access_key_id","heartbeat_url","secret_access_key"]' \
        '["heartbeat_url"]'; then
        test_fail "Vault contract accepted an empty required credential"
    fi
fi

printf '%s\n' "file secret preflight test: passed"

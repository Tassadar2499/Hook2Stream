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
DEPLOYMENT_ENVIRONMENT=staging
BILLING_MODE=stripe
SECRETS_DIR=$secret_dir
SECRETS_GID=2000
EOF

deployment_program=file-secret-preflight-test
HOOK2STREAM_ENV_FILE=$environment_file
export HOOK2STREAM_ENV_FILE
. "$deployment_dir/scripts/lib/deployment-common.sh"

for secret_name in $(deployment_required_secret_files); do
    printf '%s' test-secret > "$secret_dir/$secret_name"
done
printf '%s' \
    '{"activeKeyId":"k1","keys":{"k1":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="}}' \
    > "$secret_dir/media_keyring"
printf '%s' 'first@example.com
second@example.com' > "$secret_dir/invited_emails"
printf '%s' \
    'age1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq' \
    > "$secret_dir/backup_age_recipient"

PATH="${stub_bin}:${PATH}" deployment_validate_file_secrets

production_environment_file=${temporary_dir}/production.env
cat > "$production_environment_file" <<EOF
DEPLOYMENT_ENVIRONMENT=production
BILLING_MODE=disabled
SECRETS_DIR=$secret_dir
SECRETS_GID=2000
EOF
environment_file=$production_environment_file
deployment_validate_environment_billing_mode production disabled
if deployment_required_secret_files | grep -Eq '^stripe_(secret_key|webhook_secret)$'; then
    test_fail "billing-disabled production still requires Stripe secrets"
fi
if (PATH="${stub_bin}:${PATH}" deployment_validate_file_secrets) >/dev/null 2>&1; then
    test_fail "billing-disabled production accepted residual Stripe secret files"
fi
rm "$secret_dir/stripe_secret_key" "$secret_dir/stripe_webhook_secret"
PATH="${stub_bin}:${PATH}" deployment_validate_file_secrets
printf '%s' test-secret > "$secret_dir/stripe_secret_key"
printf '%s' test-secret > "$secret_dir/stripe_webhook_secret"
environment_file=${temporary_dir}/deployment.env
deployment_validate_environment_billing_mode staging stripe

if (deployment_validate_environment_billing_mode staging disabled) >/dev/null 2>&1; then
    test_fail "staging accepted BILLING_MODE=disabled"
fi
if (deployment_validate_environment_billing_mode production stripe) >/dev/null 2>&1; then
    test_fail "production accepted BILLING_MODE=stripe"
fi

if (PATH="${stub_bin}:${PATH}" TEST_SECRET_MODE=644 \
    deployment_validate_file_secrets) >/dev/null 2>&1; then
    test_fail "preflight accepted a group-writable/world-readable secret mode"
fi

: > "$secret_dir/postgres_password"
if (PATH="${stub_bin}:${PATH}" deployment_validate_file_secrets) >/dev/null 2>&1; then
    test_fail "preflight accepted an empty required secret"
fi
printf '%s' test-secret > "$secret_dir/postgres_password"

mv "$secret_dir/google_client_secret" "$secret_dir/google_client_secret.value"
ln -s "$secret_dir/google_client_secret.value" "$secret_dir/google_client_secret"
if (PATH="${stub_bin}:${PATH}" deployment_validate_file_secrets) >/dev/null 2>&1; then
    test_fail "preflight accepted a symlinked secret"
fi
rm "$secret_dir/google_client_secret"
mv "$secret_dir/google_client_secret.value" "$secret_dir/google_client_secret"

# Deployed app hosts use managed external storage and never require MinIO root
# credentials. The local/CI overlay supplies its own disposable credentials.
printf '%s' test-root-user > "$secret_dir/minio_root_user"
printf '%s' test-root-password > "$secret_dir/minio_root_password"
if deployment_required_secret_files | grep -q '^minio_root_'; then
    test_fail "deployed app secret contract still requires MinIO root credentials"
fi
PATH="${stub_bin}:${PATH}" deployment_validate_file_secrets
rm "$secret_dir/minio_root_user" "$secret_dir/minio_root_password"
PATH="${stub_bin}:${PATH}" deployment_validate_file_secrets

minio_environment_file=${temporary_dir}/minio-deployment.env
cp "$environment_file" "$minio_environment_file"
printf '%s\n' 'STORAGE_MODE=minio' >> "$minio_environment_file"
if (environment_file=$minio_environment_file; compose config) >/dev/null 2>&1; then
    test_fail "deployed Compose helper accepted local-only STORAGE_MODE=minio"
fi

SECRETS_GID=2468
export SECRETS_GID
PATH="${stub_bin}:${PATH}" TEST_SECRETS_GID=2468 deployment_validate_file_secrets

SECRETS_GID=invalid
export SECRETS_GID
if (PATH="${stub_bin}:${PATH}" deployment_validate_file_secrets) >/dev/null 2>&1; then
    test_fail "preflight accepted a non-numeric SECRETS_GID"
fi

if command -v jq >/dev/null 2>&1; then
    vault_candidate=${temporary_dir}/vault-candidate
    mkdir "$vault_candidate"
    printf '%s\n' '{"kv_version":1,"secrets":{"postgres_password":"secret"}}' \
        > "$vault_candidate/foundation.json"
    printf '%s\n' '{"kv_version":1,"secrets":{"access_key_id":"id","secret_access_key":"secret"}}' \
        > "$vault_candidate/runtime-s3.json"
    printf '%s\n' '{"kv_version":1,"secrets":{"google_client_secret":"google","stripe_secret_key":"stripe","stripe_webhook_secret":"webhook"}}' \
        > "$vault_candidate/api.json"
    printf '%s\n' '{"kv_version":1,"secrets":{"openrouter_api_key":"openrouter"}}' \
        > "$vault_candidate/control.json"
    printf '%s\n' '{"kv_version":1,"secrets":{"access_key_id":"id","secret_access_key":"secret"}}' \
        > "$vault_candidate/backup-s3.json"
    printf '%s\n' '{"kv_version":1,"secrets":{"invited_emails":"first@example.com\nsecond@example.com","media_keyring":"{\"activeKeyId\":\"k1\",\"keys\":{\"k1\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=\"}}"}}' \
        > "$vault_candidate/media-security.json"
    printf '%s\n' '{"kv_version":1,"secrets":{"age_recipient":"age1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq"}}' \
        > "$vault_candidate/backup-encryption.json"
    vault_validate_candidate_bundles "$vault_candidate" \
        || test_fail "Vault contract rejected the exact seven-bundle app schema"

    printf '%s\n' '{"kv_version":1,"secrets":{"access_key_id":"id","secret_access_key":"secret"}}' \
        > "$vault_candidate/bootstrap-s3.json"
    if vault_validate_candidate_bundles "$vault_candidate"; then
        test_fail "Vault contract accepted the removed bootstrap-s3 bundle"
    fi
    rm "$vault_candidate/bootstrap-s3.json"

    vault_backup_bundle=${temporary_dir}/backup-s3.json
    cat > "$vault_backup_bundle" <<'EOF'
{"kv_version":1,"secrets":{"access_key_id":"id","secret_access_key":"secret"}}
EOF
    vault_validate_bundle "$vault_backup_bundle" \
        '["access_key_id","secret_access_key"]' \
        || test_fail "Vault contract rejected the exact backup S3 schema"

    cat > "$vault_backup_bundle" <<'EOF'
{"kv_version":1,"secrets":{"access_key_id":"id","heartbeat_url":"https://monitor.invalid/token","secret_access_key":"secret"}}
EOF
    if vault_validate_bundle "$vault_backup_bundle" \
        '["access_key_id","secret_access_key"]'; then
        test_fail "Vault contract accepted the removed legacy heartbeat_url field"
    fi

    cat > "$vault_backup_bundle" <<'EOF'
{"kv_version":1,"secrets":{"access_key_id":"","secret_access_key":"secret"}}
EOF
    if vault_validate_bundle "$vault_backup_bundle" \
        '["access_key_id","secret_access_key"]'; then
        test_fail "Vault contract accepted an empty required credential"
    fi

    vault_media_bundle=${temporary_dir}/media-security.json
    printf '%s\n' '{"kv_version":1,"secrets":{"invited_emails":"first@example.com\nsecond@example.com","media_keyring":"{\"activeKeyId\":\"k1\",\"keys\":{\"k1\":\"value\"}}"}}' \
        > "$vault_media_bundle"
    vault_validate_bundle "$vault_media_bundle" \
        '["invited_emails","media_keyring"]' \
        '["invited_emails"]' '["media_keyring"]' \
        || test_fail "Vault contract rejected internal LF in invited_emails"

    printf '%s\n' '{"kv_version":1,"secrets":{"invited_emails":"first@example.com","media_keyring":"{\n\"activeKeyId\":\"k1\"}"}}' \
        > "$vault_media_bundle"
    if vault_validate_bundle "$vault_media_bundle" \
        '["invited_emails","media_keyring"]' \
        '["invited_emails"]' '["media_keyring"]'; then
        test_fail "Vault contract accepted a multiline media keyring"
    fi

    printf '%s\n' '{"kv_version":1,"secrets":{"invited_emails":" first@example.com","media_keyring":"{}"}}' \
        > "$vault_media_bundle"
    if vault_validate_bundle "$vault_media_bundle" \
        '["invited_emails","media_keyring"]' \
        '["invited_emails"]' '["media_keyring"]'; then
        test_fail "Vault contract accepted leading whitespace"
    fi

    printf '%s\n' '{"kv_version":1,"secrets":{"invited_emails":"first@example.com","media_keyring":"[]"}}' \
        > "$vault_media_bundle"
    if vault_validate_bundle "$vault_media_bundle" \
        '["invited_emails","media_keyring"]' \
        '["invited_emails"]' '["media_keyring"]'; then
        test_fail "Vault contract accepted a non-object media keyring JSON string"
    fi

    vault_control_bundle=${temporary_dir}/control.json
    for invalid_control_json in \
        '{"kv_version":1,"secrets":{"openrouter_api_key":"line-one\nline-two"}}' \
        '{"kv_version":1,"secrets":{"openrouter_api_key":"line-one\rline-two"}}' \
        '{"kv_version":1,"secrets":{"openrouter_api_key":"line-one\u0000line-two"}}' \
        '{"kv_version":1,"secrets":{"openrouter_api_key":"trailing "}}'; do
        printf '%s\n' "$invalid_control_json" > "$vault_control_bundle"
        if vault_validate_bundle "$vault_control_bundle" \
            '["openrouter_api_key"]'; then
            test_fail "Vault contract accepted prohibited scalar whitespace/control bytes"
        fi
    done

    environment_file=$production_environment_file
    if vault_validate_candidate_bundles "$vault_candidate"; then
        test_fail "billing-disabled Vault contract accepted Stripe fields"
    fi
    printf '%s\n' '{"kv_version":1,"secrets":{"google_client_secret":"google"}}' \
        > "$vault_candidate/api.json"
    vault_validate_candidate_bundles "$vault_candidate" \
        || test_fail "billing-disabled Vault contract rejected the Google-only API schema"
    environment_file=${temporary_dir}/deployment.env
    printf '%s\n' '{"kv_version":1,"secrets":{"google_client_secret":"google","stripe_secret_key":"stripe","stripe_webhook_secret":"webhook"}}' \
        > "$vault_candidate/api.json"

    printf '%s\n' '{"kv_version":1,"secrets":{"age_recipient":"not-an-age-recipient"}}' \
        > "$vault_candidate/backup-encryption.json"
    if vault_validate_candidate_bundles "$vault_candidate"; then
        test_fail "Vault contract accepted an invalid backup age recipient"
    fi
fi

printf '%s\n' "file secret preflight test: passed"

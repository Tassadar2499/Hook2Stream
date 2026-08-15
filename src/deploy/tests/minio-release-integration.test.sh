#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
release_script=${deployment_dir}/scripts/deploy-release.sh
temporary_dir=$(mktemp -d)

cleanup() {
    rm -rf "$temporary_dir"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

fail_test() {
    printf '%s\n' "MinIO release integration test: $*" >&2
    exit 1
}

digest=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
secret_dir=${temporary_dir}/secrets
state_dir=${temporary_dir}/release-state
current_uid=$(id -u)
stub_bin=${temporary_dir}/bin
command_log=${temporary_dir}/commands.log
curl_log=${temporary_dir}/curl.log
environment_file=${temporary_dir}/minio.env
mkdir -p "$secret_dir" "$state_dir" "$stub_bin"
chmod 0750 "$secret_dir"

for secret_name in \
    postgres_password \
    s3_runtime_access_key \
    s3_runtime_secret_key \
    s3_bootstrap_access_key \
    s3_bootstrap_secret_key \
    google_client_secret \
    stripe_secret_key \
    stripe_webhook_secret \
    openrouter_api_key \
    media_keyring \
    invited_emails \
    backup_s3_access_key \
    backup_s3_secret_key \
    backup_age_recipient \
    minio_root_user \
    minio_root_password; do
    printf '%s\n' "test-${secret_name}" > "${secret_dir}/${secret_name}"
done

cat > "$environment_file" <<EOF
COMPOSE_PROJECT_NAME=hook2stream-test
RELEASE_VERSION=test-release
SECRET_PROVIDER=file
STORAGE_MODE=minio
DEPLOYMENT_ENVIRONMENT=staging
ROBOTS_HEADER=noindex, nofollow, noarchive
API_IMAGE=registry.invalid/api@sha256:${digest}
WORKER_IMAGE=registry.invalid/worker@sha256:${digest}
BOOTSTRAPPER_IMAGE=registry.invalid/bootstrapper@sha256:${digest}
WEB_IMAGE=registry.invalid/web@sha256:${digest}
POSTGRES_BACKUP_IMAGE=registry.invalid/postgres-backup@sha256:${digest}
CADDY_IMAGE=registry.invalid/caddy@sha256:${digest}
POSTGRES_IMAGE=registry.invalid/postgres@sha256:${digest}
PGBOUNCER_IMAGE=registry.invalid/pgbouncer@sha256:${digest}
EGRESS_PROXY_IMAGE=registry.invalid/squid@sha256:${digest}
MINIO_IMAGE=registry.invalid/minio@sha256:${digest}
MINIO_MC_IMAGE=registry.invalid/minio-mc@sha256:${digest}
APP_DOMAIN=staging.test.invalid
PUBLIC_ORIGIN=https://staging.test.invalid
S3_PUBLIC_DOMAIN=s3-staging.test.invalid
ACME_EMAIL=ops@test.invalid
S3_SERVICE_URL=http://minio:9000
S3_PUBLIC_SERVICE_URL=https://s3-staging.test.invalid
S3_REGION=us-east-1
S3_MEDIA_BUCKET=hook2stream-staging-media
S3_FORCE_PATH_STYLE=true
S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE=false
MINIO_MEDIA_QUOTA_GIB=180
MINIO_BACKUP_QUOTA_GIB=20
BACKUP_S3_ENDPOINT=http://minio:9000
BACKUP_S3_REGION=us-east-1
BACKUP_S3_BUCKET=hook2stream-staging-pg-backups
BACKUP_S3_PREFIX=hook2stream/staging/postgres
BACKUP_INTERVAL_SECONDS=3600
BACKUP_MAX_AGE_SECONDS=7200
BACKUP_RETENTION_DAYS=7
GOOGLE_CLIENT_ID=ci-test.apps.googleusercontent.com
STRIPE_PRICE_ART_CREDITS_5=price_art_credits_5
STRIPE_PRICE_MINI_RELEASE=price_mini_release
STRIPE_PRICE_RELEASE_PACK=price_release_pack
STRIPE_PRICE_CLEAN_COVER=price_clean_cover
STRIPE_PRICE_ACTIVE_ARTIST=price_active_artist
SECRETS_DIR=${secret_dir}
SECRETS_GID=2000
EOF

cat > "${stub_bin}/docker" <<'EOF'
#!/bin/sh
set -eu
printf '%s\n' "$*" >> "$TEST_COMMAND_LOG"
case "${1:-}" in
    compose)
        case " $* " in
            *" ps -q "*)
                service_name=
                for docker_argument in "$@"; do
                    service_name=$docker_argument
                done
                printf 'container-%s\n' "$service_name"
                ;;
        esac
        ;;
    inspect)
        printf '%s\n' healthy
        ;;
esac
EOF
cat > "${stub_bin}/curl" <<'EOF'
#!/bin/sh
set -eu
requested_url=
for curl_argument in "$@"; do
    requested_url=$curl_argument
done
printf '%s\n' "$requested_url" >> "$TEST_CURL_LOG"
printf '%s' 200
EOF
cat > "${stub_bin}/flock" <<'EOF'
#!/bin/sh
exit 0
EOF
cat > "${stub_bin}/stat" <<'EOF'
#!/bin/sh
set -eu
stat_target=
for stat_argument in "$@"; do
    stat_target=$stat_argument
done
case "$stat_target" in
    "$TEST_SECRETS_DIR") printf '%s\n' '0:2000:750' ;;
    "$TEST_RELEASE_STATE_DIR") printf '%s\n' "${TEST_CURRENT_UID}:700" ;;
    *) printf '%s\n' '0:2000:640' ;;
esac
EOF
chmod 0700 \
    "${stub_bin}/docker" \
    "${stub_bin}/curl" \
    "${stub_bin}/flock" \
    "${stub_bin}/stat"

PATH=${stub_bin}:$PATH
HOOK2STREAM_ENV_FILE=$environment_file
HOOK2STREAM_RELEASE_STATE_DIR=$state_dir
TEST_COMMAND_LOG=$command_log
TEST_CURL_LOG=$curl_log
TEST_SECRETS_DIR=$secret_dir
TEST_RELEASE_STATE_DIR=$state_dir
TEST_CURRENT_UID=$current_uid
export \
    PATH \
    HOOK2STREAM_ENV_FILE \
    HOOK2STREAM_RELEASE_STATE_DIR \
    TEST_COMMAND_LOG \
    TEST_CURL_LOG \
    TEST_SECRETS_DIR \
    TEST_RELEASE_STATE_DIR \
    TEST_CURRENT_UID

sh "$release_script" > "${temporary_dir}/release.out" 2>&1 \
    || fail_test "a valid MinIO release did not complete"

line_number() {
    grep -nF -- "$1" "$command_log" | head -n 1 | cut -d: -f1
}

minio_pull_line=$(line_number ' pull minio minio-init')
minio_start_line=$(line_number ' up -d minio')
minio_init_line=$(line_number ' run --rm --no-deps minio-init')
postgres_start_line=$(line_number ' up -d postgres pgbouncer')
backup_line=$(line_number ' run --rm postgres-backup backup-once')
bootstrap_line=$(line_number ' run --rm bootstrapper')
edge_start_line=$(line_number ' up -d --no-deps caddy postgres-backup')
backup_health_line=$(line_number ' ps -q postgres-backup')
api_smoke_line=$(line_number ' exec -T api ')

for required_line in \
    "$minio_pull_line" \
    "$minio_start_line" \
    "$minio_init_line" \
    "$postgres_start_line" \
    "$backup_line" \
    "$bootstrap_line" \
    "$edge_start_line" \
    "$backup_health_line" \
    "$api_smoke_line"; do
    case "$required_line" in
        ''|*[!0-9]*) fail_test "a required release command was not recorded" ;;
    esac
done
[ "$minio_pull_line" -lt "$minio_start_line" ] \
    || fail_test "MinIO started before its images were pulled"
[ "$minio_start_line" -lt "$minio_init_line" ] \
    || fail_test "MinIO init ran before the daemon was started and checked"
[ "$minio_init_line" -lt "$postgres_start_line" ] \
    || fail_test "PostgreSQL started before MinIO initialization"
[ "$postgres_start_line" -lt "$backup_line" ] \
    || fail_test "the pre-migration backup ran before PostgreSQL startup"
[ "$backup_line" -lt "$bootstrap_line" ] \
    || fail_test "bootstrap ran before the mandatory pre-migration backup"
[ "$edge_start_line" -lt "$backup_health_line" ] \
    || fail_test "the persistent backup daemon was checked before it started"
[ "$backup_health_line" -lt "$api_smoke_line" ] \
    || fail_test "release smoke began before the backup daemon became healthy"

grep -Fx 'https://s3-staging.test.invalid/minio/health/ready' "$curl_log" >/dev/null \
    || fail_test "the public MinIO readiness endpoint was not checked"
grep -F -- "-f ${deployment_dir}/compose.minio.yaml" "$command_log" >/dev/null \
    || fail_test "MinIO release commands did not include the MinIO overlay"

for validation_assignment in \
    'S3_SERVICE_URL=http://minio:9000' \
    'S3_PUBLIC_SERVICE_URL=https://s3-staging.example.invalid' \
    'S3_PUBLIC_DOMAIN=s3-staging.example.invalid' \
    'S3_REGION=us-east-1' \
    'S3_MEDIA_BUCKET=hook2stream-staging-media' \
    'S3_FORCE_PATH_STYLE=true' \
    'S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE=false' \
    'BACKUP_S3_ENDPOINT=http://minio:9000' \
    'BACKUP_S3_REGION=us-east-1' \
    'BACKUP_S3_BUCKET=hook2stream-staging-pg-backups' \
    'BACKUP_S3_PREFIX=hook2stream/staging/postgres' \
    'BACKUP_RETENTION_DAYS=7' \
    'MINIO_MEDIA_QUOTA_GIB=180' \
    'MINIO_BACKUP_QUOTA_GIB=20'; do
    grep -F "$validation_assignment" \
        "$deployment_dir/scripts/validate-deployment.sh" >/dev/null \
        || fail_test "deployment validation omitted exact staging value: $validation_assignment"
done

assert_rejected_environment() {
    rejected_environment=$1
    expected_diagnostic=$2
    rejected_output=${temporary_dir}/rejected.out
    if HOOK2STREAM_ENV_FILE=$rejected_environment \
        sh "$release_script" --no-pull > "$rejected_output" 2>&1; then
        fail_test "an invalid MinIO environment passed preflight"
    fi
    grep -F "$expected_diagnostic" "$rejected_output" >/dev/null \
        || fail_test "invalid MinIO environment did not report: $expected_diagnostic"
}

bad_service_environment=${temporary_dir}/bad-service.env
sed 's#^S3_SERVICE_URL=.*#S3_SERVICE_URL=https://minio.test.invalid#' \
    "$environment_file" > "$bad_service_environment"
assert_rejected_environment \
    "$bad_service_environment" \
    'S3_SERVICE_URL must be exactly http://minio:9000'

bad_backup_environment=${temporary_dir}/bad-backup.env
sed 's#^BACKUP_S3_ENDPOINT=.*#BACKUP_S3_ENDPOINT=#' \
    "$environment_file" > "$bad_backup_environment"
assert_rejected_environment \
    "$bad_backup_environment" \
    'BACKUP_S3_ENDPOINT must be exactly http://minio:9000'

bad_public_environment=${temporary_dir}/bad-public.env
sed \
    -e 's/^S3_PUBLIC_DOMAIN=.*/S3_PUBLIC_DOMAIN=staging.test.invalid/' \
    -e 's#^S3_PUBLIC_SERVICE_URL=.*#S3_PUBLIC_SERVICE_URL=https://staging.test.invalid#' \
    "$environment_file" > "$bad_public_environment"
assert_rejected_environment \
    "$bad_public_environment" \
    'S3_PUBLIC_DOMAIN must be distinct from APP_DOMAIN'

bad_image_environment=${temporary_dir}/bad-image.env
sed 's#^MINIO_IMAGE=.*#MINIO_IMAGE=registry.invalid/minio:latest#' \
    "$environment_file" > "$bad_image_environment"
assert_rejected_environment \
    "$bad_image_environment" \
    'MINIO_IMAGE must be a full image@sha256 reference'

bad_quota_environment=${temporary_dir}/bad-quota.env
sed 's/^MINIO_MEDIA_QUOTA_GIB=.*/MINIO_MEDIA_QUOTA_GIB=181/' \
    "$environment_file" > "$bad_quota_environment"
assert_rejected_environment \
    "$bad_quota_environment" \
    'MINIO_MEDIA_QUOTA_GIB must be 180 for the staging profile'

bad_multipart_lifecycle_environment=${temporary_dir}/bad-multipart-lifecycle.env
sed 's/^S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE=.*/S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE=true/' \
    "$environment_file" > "$bad_multipart_lifecycle_environment"
assert_rejected_environment \
    "$bad_multipart_lifecycle_environment" \
    'S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE must be false when STORAGE_MODE=minio'

vault_environment=${temporary_dir}/vault-minio.env
sed 's/^SECRET_PROVIDER=.*/SECRET_PROVIDER=vault/' \
    "$environment_file" > "$vault_environment"
assert_rejected_environment \
    "$vault_environment" \
    'MVP staging/production requires environment-local root-owned file secrets'

external_environment=${temporary_dir}/external.env
sed \
    -e '/^STORAGE_MODE=/d' \
    -e '/^MINIO_/d' \
    -e '/^S3_PUBLIC_DOMAIN=/d' \
    -e '/^S3_ENDPOINT_HOST=/d' \
    -e '/^S3_CONFIGURE_BUCKET_LIFECYCLE=/d' \
    -e '/^STORAGE_PROTOCOL_VERSION=/d' \
    -e '/^EGRESS_CONFIG_DIR=/d' \
    -e 's/^COMPOSE_PROJECT_NAME=.*/COMPOSE_PROJECT_NAME=hook2stream-staging/' \
    -e 's/^APP_DOMAIN=.*/APP_DOMAIN=staging.hook2stream.com/' \
    -e 's#^PUBLIC_ORIGIN=.*#PUBLIC_ORIGIN=https://staging.hook2stream.com#' \
    -e 's#^S3_SERVICE_URL=.*#S3_SERVICE_URL=https://h2s-storage-staging.tail1234.ts.net#' \
    -e 's#^S3_PUBLIC_SERVICE_URL=.*#S3_PUBLIC_SERVICE_URL=https://h2s-storage-staging.tail1234.ts.net#' \
    -e 's#^BACKUP_S3_ENDPOINT=.*#BACKUP_S3_ENDPOINT=https://h2s-storage-staging.tail1234.ts.net#' \
    "$environment_file" > "$external_environment"
cat >> "$external_environment" <<'EOF'
S3_ENDPOINT_HOST=h2s-storage-staging.tail1234.ts.net
S3_CONFIGURE_BUCKET_LIFECYCLE=false
STORAGE_PROTOCOL_VERSION=1
EGRESS_CONFIG_DIR=./egress/rendered/staging
BACKUP_RETENTION_SAFETY_SECONDS=7200
EOF
external_command_log=${temporary_dir}/external-commands.log
external_curl_log=${temporary_dir}/external-curl.log
TEST_COMMAND_LOG=$external_command_log
TEST_CURL_LOG=$external_curl_log
export TEST_COMMAND_LOG TEST_CURL_LOG
HOOK2STREAM_ENV_FILE=$external_environment \
    sh "$release_script" --no-pull > "${temporary_dir}/external-release.out" 2>&1 \
    || fail_test "the default external-storage release behavior regressed"
if grep -F 'compose.minio.yaml' "$external_command_log" >/dev/null; then
    fail_test "the default external-storage release loaded the MinIO overlay"
fi
if grep -F 'minio-init' "$external_command_log" >/dev/null; then
    fail_test "the default external-storage release ran MinIO initialization"
fi
if grep -F '/minio/health/ready' "$external_curl_log" >/dev/null; then
    fail_test "the default external-storage release ran the MinIO public smoke"
fi

duplicate_external_environment=${temporary_dir}/duplicate-external.env
cp "$external_environment" "$duplicate_external_environment"
printf '%s\n' 'S3_ENDPOINT_HOST=h2s-storage-staging.tail9999.ts.net' \
    >> "$duplicate_external_environment"
assert_rejected_environment \
    "$duplicate_external_environment" \
    'environment file contains duplicate assignments: S3_ENDPOINT_HOST'

bad_external_prefix_environment=${temporary_dir}/bad-external-prefix.env
sed 's#^BACKUP_S3_PREFIX=.*#BACKUP_S3_PREFIX=hook2stream/production/postgres#' \
    "$external_environment" > "$bad_external_prefix_environment"
assert_rejected_environment \
    "$bad_external_prefix_environment" \
    'BACKUP_S3_PREFIX must match the selected environment'

bad_external_retention_environment=${temporary_dir}/bad-external-retention.env
sed 's/^BACKUP_RETENTION_DAYS=.*/BACKUP_RETENTION_DAYS=2/' \
    "$external_environment" > "$bad_external_retention_environment"
assert_rejected_environment \
    "$bad_external_retention_environment" \
    'BACKUP_RETENTION_DAYS must be 7 for staging'

bad_external_interval_environment=${temporary_dir}/bad-external-interval.env
sed 's/^BACKUP_INTERVAL_SECONDS=.*/BACKUP_INTERVAL_SECONDS=86400/' \
    "$external_environment" > "$bad_external_interval_environment"
assert_rejected_environment \
    "$bad_external_interval_environment" \
    'BACKUP_INTERVAL_SECONDS must be 3600 for remote MinIO'

bad_external_max_age_environment=${temporary_dir}/bad-external-max-age.env
sed 's/^BACKUP_MAX_AGE_SECONDS=.*/BACKUP_MAX_AGE_SECONDS=172800/' \
    "$external_environment" > "$bad_external_max_age_environment"
assert_rejected_environment \
    "$bad_external_max_age_environment" \
    'BACKUP_MAX_AGE_SECONDS must be 7200 for remote MinIO'

bad_external_safety_environment=${temporary_dir}/bad-external-safety.env
sed 's/^BACKUP_RETENTION_SAFETY_SECONDS=.*/BACKUP_RETENTION_SAFETY_SECONDS=0/' \
    "$external_environment" > "$bad_external_safety_environment"
assert_rejected_environment \
    "$bad_external_safety_environment" \
    'BACKUP_RETENTION_SAFETY_SECONDS must be 7200 for remote MinIO'

TEST_COMMAND_LOG=${temporary_dir}/compose-selection.log
export TEST_COMMAND_LOG
deployment_program=minio-release-integration-test
. "$deployment_dir/scripts/lib/deployment-common.sh"
environment_file=$external_environment
compose config
external_compose_call=$(tail -n 1 "$TEST_COMMAND_LOG")
case "$external_compose_call" in
    *compose.minio.yaml*|*compose.vault.yaml*)
        fail_test "external file-secret Compose selection gained an overlay"
        ;;
esac

compose_vault_minio_environment=${temporary_dir}/compose-vault-minio.env
printf '%s\n' \
    'SECRET_PROVIDER=vault' \
    'STORAGE_MODE=minio' > "$compose_vault_minio_environment"
environment_file=$compose_vault_minio_environment
compose config
vault_minio_compose_call=$(tail -n 1 "$TEST_COMMAND_LOG")
case "$vault_minio_compose_call" in
    *compose.yaml*compose.vault.yaml*compose.minio.yaml*) ;;
    *) fail_test "Compose did not merge the Vault and MinIO overlays in order" ;;
esac

environment_file=${temporary_dir}/minio.env
minio_secret_names=$(deployment_required_secret_files)
printf '%s\n' "$minio_secret_names" | grep -Fx minio_root_user >/dev/null \
    || fail_test "MinIO file-secret preflight omitted minio_root_user"
printf '%s\n' "$minio_secret_names" | grep -Fx minio_root_password >/dev/null \
    || fail_test "MinIO file-secret preflight omitted minio_root_password"

environment_file=$external_environment
if deployment_required_secret_files | grep -q '^minio_root_'; then
    fail_test "external-storage file-secret preflight requires MinIO root credentials"
fi
override_output=${temporary_dir}/override.out
if (MINIO_MC_IMAGE=registry.invalid/minio-mc:latest; export MINIO_MC_IMAGE; \
    deployment_reject_compose_environment_overrides) \
    > "$override_output" 2>&1; then
    fail_test "an exported MinIO overlay input bypassed override preflight"
fi
grep -F 'MINIO_MC_IMAGE' "$override_output" >/dev/null \
    || fail_test "the rejected MinIO override was not identified"

printf '%s\n' \
    "MinIO release integration test: overlay selection, preflight, ordering, backup health, and smoke passed"

#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
deployment_dir=$(CDPATH= cd -- "$script_dir/.." && pwd)
deployment_program=deploy-release
. "$script_dir/lib/deployment-common.sh"

current_stage=preflight
release_snapshot=

usage() {
    cat <<'EOF'
Usage: deploy-release.sh [--no-pull]

Deploys one Hook2Stream release in dependency order. Set HOOK2STREAM_ENV_FILE to
use an environment file other than deploy/.env. Vault candidates are rendered
before any workload mutation; value drift must be applied by a rotation script.
EOF
}

pull_images=true
case "${1:-}" in
    "") ;;
    --no-pull) pull_images=false ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; exit 2 ;;
esac
[ "$#" -le 1 ] || { usage >&2; exit 2; }

on_exit() {
    release_exit_code=$1
    trap - EXIT HUP INT TERM
    if [ "$release_exit_code" -ne 0 ]; then
        printf '%s\n' "deploy-release: failed during stage '${current_stage}'" >&2
        if [ -n "$release_snapshot" ]; then
            printf '%s\n' \
                "deploy-release: previous environment snapshot: ${release_snapshot}" \
                "deploy-release: do not down-migrate; after checking migration compatibility, restore those image references with:" \
                "  HOOK2STREAM_ENV_FILE=${release_snapshot} ${deployment_dir}/scripts/deploy-release.sh" >&2
        fi
    fi
    exit "$release_exit_code"
}
trap 'on_exit $?' EXIT
trap 'exit 130' HUP INT TERM

deployment_require_base_tools
deployment_require_command curl
deployment_acquire_lock

secret_provider=$(deployment_secret_provider)
case "$secret_provider" in
    file) deployment_validate_file_secrets ;;
    vault)
        current_stage=vault-secrets-preflight
        vault_require_configuration
        vault_preflight_release \
            || fail "Vault drift must be reconciled before this release"
        ;;
    *) fail "SECRET_PROVIDER must be file or vault" ;;
esac

current_stage=configuration-validation
compose_tools config --quiet
public_origin=$(read_env_value PUBLIC_ORIGIN)
case "$public_origin" in
    https://?*) ;;
    *) fail "PUBLIC_ORIGIN must be an unquoted HTTPS origin in $environment_file" ;;
esac
public_origin=${public_origin%/}
app_domain=$(read_env_value APP_DOMAIN)
case "$app_domain" in
    ""|*/*|*:*|.*|*.|*[!A-Za-z0-9.-]*)
        fail "APP_DOMAIN must be an unquoted DNS hostname in $environment_file"
        ;;
esac
[ "$public_origin" = "https://${app_domain}" ] \
    || fail "PUBLIC_ORIGIN must be exactly https://APP_DOMAIN with no path or port"
case "$app_domain" in
    app.example.com|*.example.com) fail "replace the example APP_DOMAIN before deployment" ;;
esac
acme_email=$(read_env_value ACME_EMAIL)
case "$acme_email" in
    *@*.*) ;;
    *) fail "ACME_EMAIL must be replaced with an operational email address" ;;
esac
case "$acme_email" in
    *@example.com) fail "replace the example ACME_EMAIL before deployment" ;;
esac
require_https_endpoint S3_SERVICE_URL
require_https_endpoint S3_PUBLIC_SERVICE_URL
require_https_endpoint_or_empty BACKUP_S3_ENDPOINT
for required_identifier in \
    S3_MEDIA_BUCKET \
    BACKUP_S3_BUCKET \
    GOOGLE_CLIENT_ID \
    STRIPE_PRICE_ART_CREDITS_5 \
    STRIPE_PRICE_MINI_RELEASE \
    STRIPE_PRICE_RELEASE_PACK \
    STRIPE_PRICE_CLEAN_COVER \
    STRIPE_PRICE_ACTIVE_ARTIST; do
    required_identifier_value=$(read_env_value "$required_identifier")
    case "$required_identifier_value" in
        ""|*replace*|*example.com*)
            fail "$required_identifier must be replaced with a production identifier in $environment_file"
            ;;
    esac
done
for image_variable in \
    API_IMAGE \
    WORKER_IMAGE \
    BOOTSTRAPPER_IMAGE \
    WEB_IMAGE \
    POSTGRES_BACKUP_IMAGE \
    CADDY_IMAGE \
    POSTGRES_IMAGE \
    PGBOUNCER_IMAGE; do
    require_digest_image "$image_variable"
done
if [ "$secret_provider" = vault ]; then
    require_digest_image VAULT_AGENT_IMAGE
fi

last_successful_environment="${release_state_dir}/last-successful.env"
if [ -r "$last_successful_environment" ]; then
    snapshot_timestamp=$(date -u +%Y%m%dT%H%M%SZ)
    release_snapshot="${release_state_dir}/${snapshot_timestamp}.env"
    install -m 0600 "$last_successful_environment" "$release_snapshot"
fi

if [ "$pull_images" = true ]; then
    current_stage=image-pull
    compose_tools pull \
        caddy web api worker-media worker-analysis worker-control \
        worker-render worker-export bootstrapper pgbouncer postgres postgres-backup
fi

current_stage=database-start
compose up -d postgres pgbouncer
wait_for_service postgres || fail "PostgreSQL did not become healthy"
wait_for_service pgbouncer || fail "PgBouncer did not become healthy"

current_stage=pre-migration-backup
compose run --rm postgres-backup backup-once

current_stage=bootstrap
compose_tools run --rm bootstrapper

current_stage=leaf-workers
compose up -d --no-deps worker-media worker-analysis worker-render worker-export
for service_name in worker-media worker-analysis worker-render worker-export; do
    wait_for_service "$service_name" \
        || fail "$service_name did not become healthy"
done

current_stage=control-worker
compose up -d --no-deps worker-control
wait_for_service worker-control || fail "worker-control did not become healthy"

current_stage=api
compose up -d --no-deps api
wait_for_service api || fail "api did not become healthy"

current_stage=web
compose up -d --no-deps web
wait_for_service web || fail "web did not become healthy"

current_stage=edge-and-backup
compose up -d --no-deps caddy postgres-backup
wait_for_service caddy || fail "caddy did not become healthy"

current_stage=smoke
compose exec -T api /bin/sh /opt/hook2stream/http-healthcheck.sh
wait_for_url "${public_origin}/health/ready" \
    || fail "public readiness smoke failed"
wait_for_url "${public_origin}/health/api-ready" \
    || fail "public API readiness smoke failed"
wait_for_url "${public_origin}/api/v1/auth/session" \
    || fail "public session smoke failed"

current_stage=complete
install -m 0600 "$environment_file" "${last_successful_environment}.tmp"
mv -f "${last_successful_environment}.tmp" "$last_successful_environment"
printf '%s\n' \
    "deploy-release: release completed successfully" \
    "deploy-release: last-successful environment recorded at ${last_successful_environment}"
if [ -n "$release_snapshot" ]; then
    printf '%s\n' "deploy-release: rollback environment snapshot: ${release_snapshot}"
fi

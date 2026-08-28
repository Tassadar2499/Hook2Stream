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
                "deploy-release: do not run this snapshot through deploy-release.sh and do not down-migrate" \
                "deploy-release: use the forced application-only rollback for a recorded compatible SHA, or forward-fix" >&2
        fi
    fi
    exit "$release_exit_code"
}
trap 'on_exit $?' EXIT
trap 'exit 130' HUP INT TERM

deployment_require_base_tools
deployment_require_command curl
deployment_validate_ghcr_pull_auth
deployment_acquire_lock

storage_mode=$(deployment_storage_mode)
[ "$storage_mode" = external ] \
    || fail "deployed releases require STORAGE_MODE=external; MinIO is local/CI only"

secret_provider=$(deployment_secret_provider)
case "$secret_provider" in
    file) deployment_validate_file_secrets ;;
    vault) fail "MVP staging/production requires environment-local root-owned file secrets" ;;
    *) fail "SECRET_PROVIDER must be file or vault" ;;
esac

current_stage=configuration-validation
deployment_environment=$(read_env_value DEPLOYMENT_ENVIRONMENT)
case "$deployment_environment" in staging|production) ;; *) fail "DEPLOYMENT_ENVIRONMENT must be staging or production" ;; esac
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
case "$deployment_environment:$app_domain" in
    staging:staging.hook2stream.com|production:hook2stream.com) ;;
    *) fail "APP_DOMAIN does not match the selected Hook2Stream environment" ;;
esac
[ "$(read_env_value ROBOTS_HEADER)" = "noindex, nofollow, noarchive" ] || [ "$deployment_environment" = production ] \
    || fail "staging must emit a noindex X-Robots-Tag"
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
case "$storage_mode" in
    external)
        [ "$(read_env_value COMPOSE_PROJECT_NAME)" = "hook2stream-${deployment_environment}" ] \
            || fail "COMPOSE_PROJECT_NAME must match the selected deployed environment"
        require_https_origin S3_SERVICE_URL
        require_https_origin BACKUP_S3_ENDPOINT
        s3_endpoint_host=$(read_env_value S3_ENDPOINT_HOST)
        [ "$s3_endpoint_host" = gateway.storjshare.io ] \
            || fail "S3_ENDPOINT_HOST must be exactly gateway.storjshare.io"
        s3_endpoint_url=https://$s3_endpoint_host
        [ "$(read_env_value S3_SERVICE_URL)" = "$s3_endpoint_url" ] \
            || fail "S3_SERVICE_URL must be exactly https://S3_ENDPOINT_HOST"
        backup_s3_endpoint_host=$(read_env_value BACKUP_S3_ENDPOINT_HOST)
        [ "$backup_s3_endpoint_host" = gateway.storjshare.io ] \
            || fail "BACKUP_S3_ENDPOINT_HOST must be exactly gateway.storjshare.io"
        backup_s3_endpoint_url=https://$backup_s3_endpoint_host
        [ "$(read_env_value BACKUP_S3_ENDPOINT)" = "$backup_s3_endpoint_url" ] \
            || fail "BACKUP_S3_ENDPOINT must be exactly https://BACKUP_S3_ENDPOINT_HOST"
        [ "$(read_env_value S3_REGION)" = global ] \
            || fail "Storj media requires S3_REGION=global"
        [ "$(read_env_value BACKUP_S3_REGION)" = global ] \
            || fail "Storj backups require BACKUP_S3_REGION=global"
        [ "$(read_env_value S3_FORCE_PATH_STYLE)" = true ] \
            || fail "Storj media requires S3_FORCE_PATH_STYLE=true"
        [ "$(read_env_value BACKUP_S3_FORCE_PATH_STYLE)" = true ] \
            || fail "Storj backups require BACKUP_S3_FORCE_PATH_STYLE=true"
        [ "$(read_env_value STORAGE_PROVISIONING_MODE)" = VerifyOnly ] \
            || fail "deployed Storj storage requires STORAGE_PROVISIONING_MODE=VerifyOnly"
        [ "$(read_env_value STORAGE_OBJECT_EXPIRATION_MODE)" = Storj ] \
            || fail "deployed media storage requires STORAGE_OBJECT_EXPIRATION_MODE=Storj"
        [ "$(read_env_value S3_CONFIGURE_BUCKET_LIFECYCLE)" = false ] \
            || fail "VerifyOnly mode must disable bucket lifecycle mutations"
        [ "$(read_env_value S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE)" = false ] \
            || fail "VerifyOnly mode must disable multipart lifecycle mutations"
        [ "$(read_env_value STORAGE_PROTOCOL_VERSION)" = 1 ] \
            || fail "STORAGE_PROTOCOL_VERSION must be exactly 1"
        [ "$(read_env_value STORAGE_CONTRACT_KEY)" = .hook2stream/contracts/storage-v1.json ] \
            || fail "STORAGE_CONTRACT_KEY must be the canonical storage-v1 marker key"
        storage_contract_sha256=$(read_env_value STORAGE_CONTRACT_SHA256)
        printf '%s\n' "$storage_contract_sha256" | grep -Eq '^[0-9a-f]{64}$' \
            || fail "STORAGE_CONTRACT_SHA256 must be a lowercase SHA-256 digest"
        [ "$(read_env_value EGRESS_CONFIG_DIR)" = "./egress/rendered/$deployment_environment" ] \
            || fail "EGRESS_CONFIG_DIR must select the rendered $deployment_environment allowlist"
        [ "$(read_env_value S3_MEDIA_BUCKET)" = "hook2stream-com-${deployment_environment}-media" ] \
            || fail "S3_MEDIA_BUCKET must match the selected environment"
        [ "$(read_env_value BACKUP_S3_BUCKET)" = "hook2stream-com-${deployment_environment}-pg-backups" ] \
            || fail "BACKUP_S3_BUCKET must match the selected environment"
        [ "$(read_env_value BACKUP_S3_PREFIX)" = "hook2stream/${deployment_environment}/postgres" ] \
            || fail "BACKUP_S3_PREFIX must match the selected environment"
        [ "$(read_env_value BACKUP_INTERVAL_SECONDS)" = 3600 ] \
            || fail "BACKUP_INTERVAL_SECONDS must be 3600"
        [ "$(read_env_value BACKUP_MAX_AGE_SECONDS)" = 7200 ] \
            || fail "BACKUP_MAX_AGE_SECONDS must be 7200"
        case "$deployment_environment" in
            staging)
                expected_media_usage_gib=35
                expected_backup_usage_gib=10
                expected_backup_retention_days=7
                expected_backup_ttl_hours=168
                ;;
            production)
                expected_media_usage_gib=160
                expected_backup_usage_gib=30
                expected_backup_retention_days=35
                expected_backup_ttl_hours=840
                ;;
        esac
        [ "$(read_env_value MEDIA_USAGE_THRESHOLD_GIB)" = "$expected_media_usage_gib" ] \
            || fail "MEDIA_USAGE_THRESHOLD_GIB must be $expected_media_usage_gib for $deployment_environment"
        [ "$(read_env_value BACKUP_USAGE_THRESHOLD_GIB)" = "$expected_backup_usage_gib" ] \
            || fail "BACKUP_USAGE_THRESHOLD_GIB must be $expected_backup_usage_gib for $deployment_environment"
        [ "$(read_env_value MEDIA_JANITOR_INTERVAL_SECONDS)" = 86400 ] \
            || fail "MEDIA_JANITOR_INTERVAL_SECONDS must be 86400"
        [ "$(read_env_value MEDIA_JANITOR_MAX_AGE_SECONDS)" = 93600 ] \
            || fail "MEDIA_JANITOR_MAX_AGE_SECONDS must be 93600"
        [ "$(read_env_value BACKUP_RETENTION_DAYS)" = "$expected_backup_retention_days" ] \
            || fail "BACKUP_RETENTION_DAYS must be $expected_backup_retention_days for $deployment_environment"
        [ "$(read_env_value BACKUP_MAX_OBJECT_TTL_HOURS)" = "$expected_backup_ttl_hours" ] \
            || fail "BACKUP_MAX_OBJECT_TTL_HOURS must be $expected_backup_ttl_hours for $deployment_environment"
        ;;
esac
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
require_digest_image EGRESS_PROXY_IMAGE
if [ "$secret_provider" = vault ]; then
    require_digest_image VAULT_AGENT_IMAGE
fi
HOOK2STREAM_ENV_FILE=$environment_file \
    "$script_dir/render-egress-configs.sh" "$environment_file"
compose_tools config --quiet

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
        worker-render worker-export bootstrapper pgbouncer postgres postgres-backup \
        storage-janitor egress-api egress-s3 egress-control egress-backup
fi

current_stage=egress-proxies
compose up -d egress-api egress-s3 egress-control egress-backup
for service_name in egress-api egress-s3 egress-control egress-backup; do
    wait_for_service "$service_name" || fail "$service_name did not become healthy"
done

current_stage=remote-storage-probe
compose_tools run --rm --no-deps storage-probe

if [ -r "$last_successful_environment" ]; then
    current_stage=pre-replacement-backup
    compose_tools run --rm --no-deps postgres-backup backup-once
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

current_stage=caddy-volume-ownership
compose stop caddy
compose_tools run --rm --no-deps caddy-volume-init

current_stage=edge-backup-and-media-janitor
compose up -d --no-deps caddy postgres-backup storage-janitor
wait_for_service caddy || fail "caddy did not become healthy"
wait_for_service postgres-backup || fail "postgres-backup did not become healthy"
wait_for_service storage-janitor || fail "storage-janitor did not become healthy"

current_stage=smoke
compose exec -T api /bin/sh /opt/hook2stream/http-healthcheck.sh
wait_for_url "${public_origin}/health/ready" \
    || fail "public readiness smoke failed"
wait_for_url "${public_origin}/health/api-ready" \
    || fail "public API readiness smoke failed"
wait_for_url "${public_origin}/api/v1/auth/session" \
    || fail "public session smoke failed"
current_stage=complete
if [ "${HOOK2STREAM_DEFER_SUCCESS_MARKER:-false}" != true ]; then
install -m 0600 "$environment_file" "${last_successful_environment}.tmp"
mv -f "${last_successful_environment}.tmp" "$last_successful_environment"
successful_dir=${release_state_dir}/successful
install -d -m 0700 "$successful_dir"
release_version=$(read_env_value RELEASE_VERSION)
case "$release_version" in
    [0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f])
        install -m 0600 "$environment_file" "$successful_dir/$release_version.env"
        ;;
esac
else
    deployment_log "success marker deferred to the forced-command verification gate"
fi
printf '%s\n' \
    "deploy-release: release completed successfully" \
    "deploy-release: rollout stages completed"
if [ -n "$release_snapshot" ]; then
    printf '%s\n' "deploy-release: rollback environment snapshot: ${release_snapshot}"
fi

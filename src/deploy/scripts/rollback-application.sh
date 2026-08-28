#!/bin/sh
set -eu
set -f

fail_rollback() {
    printf '%s\n' "rollback-application: $*" >&2
    exit 1
}

usage() {
    printf '%s\n' \
        "Usage: rollback-application.sh CURRENT_ENV TARGET_ENV ACTIVE_ENV TARGET_SHA ACTIVE_INFRA_DEPLOY_DIR" \
        "" \
        "Replaces only API_IMAGE, WORKER_IMAGE, WEB_IMAGE and RELEASE_VERSION." \
        "It never starts the bootstrapper, runs a migration, or mutates infrastructure services." >&2
    exit 2
}

[ "$#" -eq 5 ] || usage
current_environment_file=$1
target_environment_file=$2
active_environment_file=$3
target_release_sha=$4
deployment_dir=$5

case "$target_release_sha" in
    *[!0-9a-f]*|'') fail_rollback "TARGET_SHA must be a full lowercase commit SHA" ;;
esac
[ "${#target_release_sha}" -eq 40 ] \
    || fail_rollback "TARGET_SHA must be a full lowercase commit SHA"
case "$deployment_dir" in /*/deploy) ;; *) fail_rollback "ACTIVE_INFRA_DEPLOY_DIR must be an absolute deploy directory" ;; esac
[ -d "$deployment_dir" ] && [ ! -L "$deployment_dir" ] \
    || fail_rollback "ACTIVE_INFRA_DEPLOY_DIR must be a real directory"
deployment_owner=$(id -u):$(id -g)
for deployment_helper in \
    "$deployment_dir/compose.yaml" \
    "$deployment_dir/scripts/lib/deployment-common.sh" \
    "$deployment_dir/scripts/lib/forced-command-trust.sh"; do
    [ -f "$deployment_helper" ] && [ ! -L "$deployment_helper" ] \
        && [ "$(stat -c '%u:%g' "$deployment_helper")" = "$deployment_owner" ] \
        || fail_rollback "active infrastructure compose/helper source is unsafe"
done

validate_recorded_environment() {
    rollback_environment_path=$1
    rollback_environment_label=$2
    [ -f "$rollback_environment_path" ] && [ ! -L "$rollback_environment_path" ] \
        || fail_rollback "$rollback_environment_label must be a regular non-symlink file"
    if [ "$(id -u)" -eq 0 ]; then
        [ "$(stat -c '%u:%a' "$rollback_environment_path")" = "0:600" ] \
            || fail_rollback "$rollback_environment_label must be root-owned mode 0600"
    fi
}

read_unique_environment_value() {
    rollback_environment_path=$1
    rollback_variable_name=$2
    rollback_variable_count=$(awk -F= -v requested="$rollback_variable_name" \
        '$1 == requested {count++} END {print count + 0}' "$rollback_environment_path")
    [ "$rollback_variable_count" -eq 1 ] \
        || fail_rollback "$rollback_environment_path must contain exactly one $rollback_variable_name"
    awk -F= -v requested="$rollback_variable_name" \
        '$1 == requested {print substr($0,index($0,"=")+1)}' "$rollback_environment_path"
}

validate_recorded_environment "$current_environment_file" "current successful environment"
validate_recorded_environment "$target_environment_file" "rollback target environment"
case "$active_environment_file" in
    /*) ;;
    *) fail_rollback "ACTIVE_ENV must be an absolute path" ;;
esac
[ "$active_environment_file" != / ] || fail_rollback "ACTIVE_ENV must not be /"
[ ! -L "$active_environment_file" ] || fail_rollback "ACTIVE_ENV must not be a symlink"
[ -d "${active_environment_file%/*}" ] && [ ! -L "${active_environment_file%/*}" ] \
    || fail_rollback "ACTIVE_ENV parent must be a real directory"

current_deployment_environment=$(read_unique_environment_value \
    "$current_environment_file" DEPLOYMENT_ENVIRONMENT)
target_deployment_environment=$(read_unique_environment_value \
    "$target_environment_file" DEPLOYMENT_ENVIRONMENT)
[ "$current_deployment_environment" = "$target_deployment_environment" ] \
    || fail_rollback "rollback target belongs to a different deployment environment"
case "$current_deployment_environment" in
    staging|production) ;;
    *) fail_rollback "DEPLOYMENT_ENVIRONMENT must be staging or production" ;;
esac

target_recorded_release=$(read_unique_environment_value \
    "$target_environment_file" RELEASE_VERSION)
[ "$target_recorded_release" = "$target_release_sha" ] \
    || fail_rollback "rollback target RELEASE_VERSION differs from TARGET_SHA"

application_variables='API_IMAGE WORKER_IMAGE WEB_IMAGE'
infrastructure_variables='BOOTSTRAPPER_IMAGE POSTGRES_BACKUP_IMAGE CADDY_IMAGE POSTGRES_IMAGE PGBOUNCER_IMAGE EGRESS_PROXY_IMAGE'
for rollback_variable in $application_variables $infrastructure_variables RELEASE_VERSION; do
    read_unique_environment_value "$current_environment_file" "$rollback_variable" >/dev/null
    read_unique_environment_value "$target_environment_file" "$rollback_variable" >/dev/null
done

target_api_image=$(read_unique_environment_value "$target_environment_file" API_IMAGE)
target_worker_image=$(read_unique_environment_value "$target_environment_file" WORKER_IMAGE)
target_web_image=$(read_unique_environment_value "$target_environment_file" WEB_IMAGE)
active_environment_tmp=${active_environment_file}.tmp.$$
trap 'rm -f "$active_environment_tmp"' EXIT HUP INT TERM
awk -F= \
    -v api_image="$target_api_image" \
    -v worker_image="$target_worker_image" \
    -v web_image="$target_web_image" \
    -v release_version="$target_release_sha" '
    $1 == "API_IMAGE" { print "API_IMAGE=" api_image; next }
    $1 == "WORKER_IMAGE" { print "WORKER_IMAGE=" worker_image; next }
    $1 == "WEB_IMAGE" { print "WEB_IMAGE=" web_image; next }
    $1 == "RELEASE_VERSION" { print "RELEASE_VERSION=" release_version; next }
    { print }
' "$current_environment_file" > "$active_environment_tmp"
chmod 0600 "$active_environment_tmp"
validate_recorded_environment "$active_environment_tmp" "pending active rollback environment"
for rollback_variable in $application_variables; do
    active_value=$(read_unique_environment_value "$active_environment_tmp" "$rollback_variable")
    target_value=$(read_unique_environment_value "$target_environment_file" "$rollback_variable")
    [ "$active_value" = "$target_value" ] \
        || fail_rollback "$rollback_variable was not selected from the rollback target"
done
for rollback_variable in $infrastructure_variables; do
    active_value=$(read_unique_environment_value "$active_environment_tmp" "$rollback_variable")
    current_value=$(read_unique_environment_value "$current_environment_file" "$rollback_variable")
    [ "$active_value" = "$current_value" ] \
        || fail_rollback "$rollback_variable must remain at the current infrastructure digest"
done
[ "$(read_unique_environment_value "$active_environment_tmp" RELEASE_VERSION)" = "$target_release_sha" ] \
    || fail_rollback "active rollback RELEASE_VERSION is invalid"

deployment_program=rollback-application
HOOK2STREAM_ENV_FILE=$active_environment_tmp
export HOOK2STREAM_ENV_FILE
. "$deployment_dir/scripts/lib/deployment-common.sh"

deployment_require_base_tools
deployment_require_command curl
deployment_validate_ghcr_pull_auth
case "$(deployment_secret_provider)" in
    file) ;;
    *) fail "application rollback requires environment-local file secrets" ;;
esac
compose_tools config --quiet

for rollback_variable in $application_variables $infrastructure_variables; do
    require_digest_image "$rollback_variable"
done

public_origin=$(read_env_value PUBLIC_ORIGIN)
case "$public_origin" in
    https://?*) ;;
    *) fail "PUBLIC_ORIGIN must be an unquoted HTTPS origin" ;;
esac
public_origin=${public_origin%/}

verify_running_image() {
    rollback_image_variable=$1
    rollback_service=$2
    rollback_expected=$(read_env_value "$rollback_image_variable")
    rollback_container=$(compose ps -q "$rollback_service")
    [ -n "$rollback_container" ] \
        || fail "$rollback_service has no running container"
    rollback_actual=$(docker inspect --format '{{.Config.Image}}' "$rollback_container")
    [ "$rollback_actual" = "$rollback_expected" ] \
        || fail "$rollback_service is not running $rollback_image_variable=$rollback_expected"
}

# Prove the currently running infrastructure matches the current environment
# before mutating any application container. These are read-only inspections.
for rollback_mapping in \
    'POSTGRES_BACKUP_IMAGE:postgres-backup' \
    'POSTGRES_BACKUP_IMAGE:storage-janitor' \
    'CADDY_IMAGE:caddy' \
    'POSTGRES_IMAGE:postgres' \
    'PGBOUNCER_IMAGE:pgbouncer' \
    'EGRESS_PROXY_IMAGE:egress-api' \
    'EGRESS_PROXY_IMAGE:egress-s3' \
    'EGRESS_PROXY_IMAGE:egress-control' \
    'EGRESS_PROXY_IMAGE:egress-backup'; do
    verify_running_image "${rollback_mapping%%:*}" "${rollback_mapping#*:}"
done

current_stage=application-image-pull
for rollback_variable in $application_variables; do
    docker --config "$DOCKER_CONFIG" image pull "$(read_env_value "$rollback_variable")"
done

# Publish the selected environment only after every target application digest
# has been authenticated and pulled. An auth/registry failure leaves any
# existing active rollback environment untouched.
mv -f "$active_environment_tmp" "$active_environment_file"
trap - EXIT HUP INT TERM
HOOK2STREAM_ENV_FILE=$active_environment_file
environment_file=$active_environment_file
export HOOK2STREAM_ENV_FILE

current_stage=leaf-workers
compose up -d --no-deps worker-media worker-analysis worker-render worker-export
for rollback_service in worker-media worker-analysis worker-render worker-export; do
    wait_for_service "$rollback_service" || fail "$rollback_service did not become healthy"
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

current_stage=smoke
compose exec -T api /bin/sh /opt/hook2stream/http-healthcheck.sh
wait_for_url "${public_origin}/health/ready" \
    || fail "public readiness smoke failed"
wait_for_url "${public_origin}/health/api-ready" \
    || fail "public API readiness smoke failed"
wait_for_url "${public_origin}/api/v1/auth/session" \
    || fail "public session smoke failed"

current_stage=digest-verification
for rollback_mapping in \
    'API_IMAGE:api' \
    'WORKER_IMAGE:worker-media' \
    'WORKER_IMAGE:worker-analysis' \
    'WORKER_IMAGE:worker-control' \
    'WORKER_IMAGE:worker-render' \
    'WORKER_IMAGE:worker-export' \
    'WEB_IMAGE:web' \
    'POSTGRES_BACKUP_IMAGE:postgres-backup' \
    'POSTGRES_BACKUP_IMAGE:storage-janitor' \
    'CADDY_IMAGE:caddy' \
    'POSTGRES_IMAGE:postgres' \
    'PGBOUNCER_IMAGE:pgbouncer' \
    'EGRESS_PROXY_IMAGE:egress-api' \
    'EGRESS_PROXY_IMAGE:egress-s3' \
    'EGRESS_PROXY_IMAGE:egress-control' \
    'EGRESS_PROXY_IMAGE:egress-backup'; do
    verify_running_image "${rollback_mapping%%:*}" "${rollback_mapping#*:}"
done

printf '%s\n' \
    "rollback-application: application images are healthy at ${target_release_sha}" \
    "rollback-application: infrastructure services were inspection-only; migrations and bootstrapper were not invoked"

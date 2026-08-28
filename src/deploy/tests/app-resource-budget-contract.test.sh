#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
staging_env=$deployment_dir/environments/staging.env.example
production_env=$deployment_dir/environments/production.env.example
compose_file=$deployment_dir/compose.yaml

fail_test() {
    printf '%s\n' "app resource budget test: $*" >&2
    exit 1
}

read_value() {
    resource_name=$1
    resource_file=$2
    awk -F= -v name="$resource_name" '$1 == name { print substr($0, index($0, "=") + 1) }' "$resource_file"
}

to_mib() {
    resource_value=$1
    case "$resource_value" in
        *M) printf '%s\n' "${resource_value%M}" ;;
        *G) printf '%s\n' "$(( ${resource_value%G} * 1024 ))" ;;
        *) fail_test "unsupported memory unit: $resource_value" ;;
    esac
}

limit_names='CADDY_MEMORY_LIMIT WEB_MEMORY_LIMIT API_MEMORY_LIMIT WORKER_MEDIA_MEMORY_LIMIT WORKER_ANALYSIS_MEMORY_LIMIT WORKER_CONTROL_MEMORY_LIMIT WORKER_RENDER_MEMORY_LIMIT WORKER_EXPORT_MEMORY_LIMIT PGBOUNCER_MEMORY_LIMIT POSTGRES_MEMORY_LIMIT POSTGRES_BACKUP_MEMORY_LIMIT STORAGE_JANITOR_MEMORY_LIMIT'
reservation_names='CADDY_MEMORY_RESERVATION WEB_MEMORY_RESERVATION API_MEMORY_RESERVATION WORKER_MEDIA_MEMORY_RESERVATION WORKER_ANALYSIS_MEMORY_RESERVATION WORKER_CONTROL_MEMORY_RESERVATION WORKER_RENDER_MEMORY_RESERVATION WORKER_EXPORT_MEMORY_RESERVATION PGBOUNCER_MEMORY_RESERVATION POSTGRES_MEMORY_RESERVATION POSTGRES_BACKUP_MEMORY_RESERVATION STORAGE_JANITOR_MEMORY_RESERVATION'

staging_limit_mib=0
for resource_name in $limit_names; do
    resource_value=$(read_value "$resource_name" "$staging_env")
    [ -n "$resource_value" ] || fail_test "$resource_name is missing from staging"
    staging_limit_mib=$((staging_limit_mib + $(to_mib "$resource_value")))
    compose_token=$(printf '${%s:-' "$resource_name")
    grep -Fq "$compose_token" "$compose_file" \
        || fail_test "compose does not parameterize $resource_name"
done
egress_limit=$(to_mib "$(read_value EGRESS_PROXY_MEMORY_LIMIT "$staging_env")")
staging_limit_mib=$((staging_limit_mib + egress_limit * 4))
[ "$staging_limit_mib" -eq 6144 ] \
    || fail_test "staging long-running limit sum changed: ${staging_limit_mib} MiB"
[ "$staging_limit_mib" -le 6144 ] \
    || fail_test "staging containers consume more than 6 GiB of the 8 GiB host"

production_limit_mib=0
for resource_name in $limit_names; do
    resource_value=$(read_value "$resource_name" "$production_env")
    [ -n "$resource_value" ] || fail_test "$resource_name is missing from production"
    [ "$resource_value" = "$(read_value "$resource_name" "$staging_env")" ] \
        || fail_test "$resource_name differs between the two 8 GiB profiles"
    production_limit_mib=$((production_limit_mib + $(to_mib "$resource_value")))
done
production_egress_limit=$(to_mib "$(read_value EGRESS_PROXY_MEMORY_LIMIT "$production_env")")
[ "$production_egress_limit" -eq "$egress_limit" ] \
    || fail_test "egress proxy limits differ between the two 8 GiB profiles"
production_limit_mib=$((production_limit_mib + production_egress_limit * 4))
[ "$production_limit_mib" -eq 6144 ] \
    || fail_test "production long-running limit sum changed: ${production_limit_mib} MiB"
[ "$production_limit_mib" -le 6144 ] \
    || fail_test "production containers consume more than 6 GiB of the 8 GiB host"

for resource_name in $reservation_names EGRESS_PROXY_MEMORY_RESERVATION; do
    staging_value=$(read_value "$resource_name" "$staging_env")
    production_value=$(read_value "$resource_name" "$production_env")
    [ -n "$staging_value" ] \
        || fail_test "$resource_name is missing from staging"
    [ -n "$production_value" ] \
        || fail_test "$resource_name is missing from production"
    [ "$production_value" = "$staging_value" ] \
        || fail_test "$resource_name differs between the two 8 GiB profiles"
done
[ "$(read_value BOOTSTRAPPER_MEMORY_LIMIT "$production_env")" = \
    "$(read_value BOOTSTRAPPER_MEMORY_LIMIT "$staging_env")" ] \
    || fail_test "bootstrapper memory differs between the two 8 GiB profiles"

[ "$(read_value WORKER_RENDER_MEMORY_LIMIT "$staging_env")" = 1536M ] \
    || fail_test "staging render worker lost its 1536 MiB render budget"
[ "$(read_value POSTGRES_SHARED_BUFFERS "$staging_env")" = 384MB ] \
    || fail_test "staging PostgreSQL tuning is not aligned with its 1 GiB limit"
[ "$(read_value WORKER_RENDER_MEMORY_LIMIT "$production_env")" = 1536M ] \
    || fail_test "production render worker lost its 1536 MiB render budget"
[ "$(read_value POSTGRES_MEMORY_LIMIT "$production_env")" = 1024M ] \
    || fail_test "production PostgreSQL lost its 1 GiB memory limit"
[ "$(read_value POSTGRES_SHARED_BUFFERS "$production_env")" = 384MB ] \
    || fail_test "production PostgreSQL tuning is not aligned with its 1 GiB limit"
[ "$(read_value POSTGRES_MAX_CONNECTIONS "$production_env")" = 50 ] \
    || fail_test "production PostgreSQL max_connections is not capped at 50"

render_service=$(awk '
    $0 == "  worker-render:" { capture = 1 }
    capture && $0 ~ /^  [a-z0-9-]+:$/ && $0 != "  worker-render:" { exit }
    capture { print }
' "$compose_file")
[ "$(grep -Ec '^  worker-render:$' "$compose_file")" -eq 1 ] \
    || fail_test "compose must define exactly one render worker service"
[ "$(grep -Fc 'Worker__Capabilities__0: render' "$compose_file")" -eq 1 ] \
    || fail_test "compose must assign the render capability to exactly one service"
printf '%s\n' "$render_service" | grep -Fq 'cpus: "3.00"' \
    || fail_test "render worker must be capped at three CPUs"
printf '%s\n' "$render_service" | grep -Fq 'replicas: 1' \
    || fail_test "render worker must be fixed to one replica"

printf '%s\n' \
    "app resource budget test: staging/production long-running limits are 6144/6144 MiB with one 3-CPU render worker"

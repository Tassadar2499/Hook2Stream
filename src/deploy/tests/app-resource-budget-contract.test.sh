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

limit_names='CADDY_MEMORY_LIMIT WEB_MEMORY_LIMIT API_MEMORY_LIMIT WORKER_MEDIA_MEMORY_LIMIT WORKER_ANALYSIS_MEMORY_LIMIT WORKER_CONTROL_MEMORY_LIMIT WORKER_RENDER_MEMORY_LIMIT WORKER_EXPORT_MEMORY_LIMIT PGBOUNCER_MEMORY_LIMIT POSTGRES_MEMORY_LIMIT POSTGRES_BACKUP_MEMORY_LIMIT'
reservation_names='CADDY_MEMORY_RESERVATION WEB_MEMORY_RESERVATION API_MEMORY_RESERVATION WORKER_MEDIA_MEMORY_RESERVATION WORKER_ANALYSIS_MEMORY_RESERVATION WORKER_CONTROL_MEMORY_RESERVATION WORKER_RENDER_MEMORY_RESERVATION WORKER_EXPORT_MEMORY_RESERVATION PGBOUNCER_MEMORY_RESERVATION POSTGRES_MEMORY_RESERVATION POSTGRES_BACKUP_MEMORY_RESERVATION'

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
staging_limit_mib=$((staging_limit_mib + egress_limit * 3))
[ "$staging_limit_mib" -eq 5952 ] \
    || fail_test "staging long-running limit sum changed: ${staging_limit_mib} MiB"
[ "$staging_limit_mib" -le 6144 ] \
    || fail_test "staging containers consume more than 6 GiB of the 8 GiB host"

for resource_name in $reservation_names EGRESS_PROXY_MEMORY_RESERVATION; do
    [ -n "$(read_value "$resource_name" "$staging_env")" ] \
        || fail_test "$resource_name is missing from staging"
    [ -n "$(read_value "$resource_name" "$production_env")" ] \
        || fail_test "$resource_name is missing from production"
done

[ "$(read_value WORKER_RENDER_MEMORY_LIMIT "$staging_env")" = 1536M ] \
    || fail_test "staging render worker lost its 1536 MiB render budget"
[ "$(read_value POSTGRES_SHARED_BUFFERS "$staging_env")" = 384MB ] \
    || fail_test "staging PostgreSQL tuning is not aligned with its 1 GiB limit"

printf '%s\n' \
    "app resource budget test: staging long-running limits are 5952 MiB with a 1536 MiB render worker"

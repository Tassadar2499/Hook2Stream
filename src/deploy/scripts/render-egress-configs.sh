#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
deployment_dir=$(CDPATH= cd -- "$script_dir/.." && pwd)
environment_file=${1:-${HOOK2STREAM_ENV_FILE:-$deployment_dir/.env}}

fail() { printf '%s\n' "egress render: $*" >&2; exit 1; }
read_value() {
    render_name=$1
    awk -v name="$render_name" '
        index($0, "=") > 0 {
            candidate = substr($0, 1, index($0, "=") - 1)
            if (candidate == name) value = substr($0, index($0, "=") + 1)
        }
        END { sub(/\r$/, "", value); print value }
    ' "$environment_file"
}

[ -r "$environment_file" ] || fail "environment file is not readable"
duplicate_names=$(awk -F= '
    /^[A-Za-z_][A-Za-z0-9_]*=/ { count[$1]++ }
    END { for (name in count) if (count[name] > 1) print name }
' "$environment_file" | sort)
[ -z "$duplicate_names" ] \
    || fail "environment file contains duplicate assignments: $(printf '%s' "$duplicate_names" | tr '\n' ' ')"
environment=$(read_value DEPLOYMENT_ENVIRONMENT)
case "$environment" in staging|production) ;; *) fail "DEPLOYMENT_ENVIRONMENT must be staging or production" ;; esac

endpoint_host=$(read_value S3_ENDPOINT_HOST)
printf '%s\n' "$endpoint_host" \
    | grep -Eq "^h2s-storage-${environment}\.[a-z0-9]([a-z0-9-]*[a-z0-9])?\.ts\.net$" \
    || fail "S3_ENDPOINT_HOST must be the environment-specific h2s-storage hostname in the tailnet"
case "$endpoint_host" in *'<tailnet>'*|*'*'*|.*|*.) fail "replace the example or wildcard S3_ENDPOINT_HOST" ;; esac

endpoint_url=https://$endpoint_host
[ "$(read_value S3_SERVICE_URL)" = "$endpoint_url" ] \
    || fail "S3_SERVICE_URL must be exactly https://S3_ENDPOINT_HOST"
[ "$(read_value S3_PUBLIC_SERVICE_URL)" = "$endpoint_url" ] \
    || fail "S3_PUBLIC_SERVICE_URL must be exactly https://S3_ENDPOINT_HOST"
[ "$(read_value BACKUP_S3_ENDPOINT)" = "$endpoint_url" ] \
    || fail "BACKUP_S3_ENDPOINT must be exactly https://S3_ENDPOINT_HOST"
[ "$(read_value S3_FORCE_PATH_STYLE)" = true ] \
    || fail "remote MinIO requires S3_FORCE_PATH_STYLE=true"
[ "$(read_value S3_CONFIGURE_BUCKET_LIFECYCLE)" = false ] \
    || fail "remote MinIO storage must exclusively own bucket lifecycle"
[ "$(read_value S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE)" = false ] \
    || fail "remote MinIO bootstrap must not configure unsupported multipart lifecycle"
[ "$(read_value STORAGE_PROTOCOL_VERSION)" = 1 ] \
    || fail "STORAGE_PROTOCOL_VERSION must be exactly 1"

expected_relative_dir=./egress/rendered/$environment
[ "$(read_value EGRESS_CONFIG_DIR)" = "$expected_relative_dir" ] \
    || fail "EGRESS_CONFIG_DIR must be exactly $expected_relative_dir"
output_dir=${2:-$deployment_dir/egress/rendered/$environment}
case "$output_dir" in /*) ;; *) fail "output directory must be absolute" ;; esac
[ ! -L "$deployment_dir/egress" ] || fail "egress template directory must not be a symlink"
[ ! -e "$output_dir" ] || [ ! -L "$output_dir" ] \
    || fail "output directory must not be a symlink"
mkdir -p "$output_dir"
chmod 0755 "$output_dir"

temporary_files=
cleanup() {
    for temporary_file in $temporary_files; do
        rm -f -- "$temporary_file"
    done
}
trap cleanup EXIT HUP INT TERM

for config_name in api s3 control; do
    template=$deployment_dir/egress/$config_name.conf.in
    [ -f "$template" ] && [ ! -L "$template" ] \
        || fail "$template must be a regular non-symlink template"
    temporary_file=$(mktemp "$output_dir/.${config_name}.conf.XXXXXX")
    temporary_files="$temporary_files $temporary_file"
    sed "s/__HOOK2STREAM_S3_ENDPOINT_HOST__/$endpoint_host/g" \
        "$template" > "$temporary_file"
    grep -Fq "dstdomain $endpoint_host" "$temporary_file" \
        || fail "$config_name did not receive the exact storage hostname"
    awk -v expected="$endpoint_host" '
        {
            for (i = 1; i <= NF; i++) {
                if ($i ~ /\.ts\.net$/ && $i != expected) exit 1
            }
        }
    ' "$temporary_file" \
        || fail "$config_name contains a wildcard or unexpected ts.net hostname"
    if grep -Fq '*' "$temporary_file"; then
        fail "$config_name contains a wildcard token"
    fi
    chmod 0644 "$temporary_file"
    mv -f -- "$temporary_file" "$output_dir/$config_name.conf"
done

temporary_files=
trap - EXIT HUP INT TERM
printf '%s\n' "egress render: exact $environment storage hostname rendered"

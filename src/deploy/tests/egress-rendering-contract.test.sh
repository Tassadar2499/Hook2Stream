#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
temporary_dir=$(mktemp -d)
trap 'rm -rf "$temporary_dir"' EXIT HUP INT TERM

fail_test() {
    printf '%s\n' "egress rendering test: $*" >&2
    exit 1
}

write_environment() {
    render_environment=$1
    render_host=$2
    render_file=$3
    cat > "$render_file" <<EOF
DEPLOYMENT_ENVIRONMENT=$render_environment
S3_ENDPOINT_HOST=$render_host
S3_SERVICE_URL=https://$render_host
S3_PUBLIC_SERVICE_URL=https://$render_host
S3_FORCE_PATH_STYLE=true
S3_CONFIGURE_BUCKET_LIFECYCLE=false
S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE=false
STORAGE_PROTOCOL_VERSION=1
BACKUP_S3_ENDPOINT=https://$render_host
EGRESS_CONFIG_DIR=./egress/rendered/$render_environment
EOF
}

for render_environment in staging production; do
    render_host=h2s-storage-${render_environment}.tail1234.ts.net
    render_env_file=$temporary_dir/$render_environment.env
    render_output=$temporary_dir/$render_environment
    write_environment "$render_environment" "$render_host" "$render_env_file"
    sh "$deployment_dir/scripts/render-egress-configs.sh" \
        "$render_env_file" "$render_output" >/dev/null

    for config_name in api s3 control; do
        rendered=$render_output/$config_name.conf
        [ "$(stat -c '%a' "$rendered")" = 644 ] \
            || fail_test "$config_name mode is not 0644"
        [ "$(grep -Foc "$render_host" "$rendered")" -eq 1 ] \
            || fail_test "$config_name does not contain exactly one exact storage hostname"
        if ! awk -v expected="$render_host" '
            { for (i=1; i<=NF; i++) if ($i ~ /\.ts\.net$/ && $i != expected) exit 1 }
        ' "$rendered"; then
            fail_test "$config_name contains a wildcard or foreign ts.net hostname"
        fi
        grep -Fq "acl allowed_domains dstdomain $render_host" "$rendered" \
            || fail_test "$config_name did not put the exact host in dstdomain"
        if grep -Eiq 'better(stack|uptime)' "$rendered"; then
            fail_test "$config_name retained a Better Stack egress destination"
        fi
    done
done

bad_env=$temporary_dir/bad.env
write_environment production h2s-storage-staging.tail1234.ts.net "$bad_env"
if sh "$deployment_dir/scripts/render-egress-configs.sh" "$bad_env" "$temporary_dir/bad" >/dev/null 2>&1; then
    fail_test "cross-environment storage hostname was accepted"
fi

write_environment staging 'h2s-storage-staging.*.ts.net' "$bad_env"
if sh "$deployment_dir/scripts/render-egress-configs.sh" "$bad_env" "$temporary_dir/bad" >/dev/null 2>&1; then
    fail_test "wildcard storage hostname was accepted"
fi

write_environment staging h2s-storage-staging.tail1234.ts.net "$bad_env"
sed -i 's/S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE=false/S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE=true/' "$bad_env"
if sh "$deployment_dir/scripts/render-egress-configs.sh" "$bad_env" "$temporary_dir/bad" >/dev/null 2>&1; then
    fail_test "unsupported remote MinIO lifecycle bootstrap was accepted"
fi

if grep -R -E 'your-objectstorage|\.ts\.net|__HOOK2STREAM' \
    "$deployment_dir/egress/api.conf" \
    "$deployment_dir/egress/s3.conf" \
    "$deployment_dir/egress/control.conf" >/dev/null; then
    fail_test "default local/CI allowlists contain an external storage hostname"
fi
if grep -Eiq 'better(stack|uptime)|http_access allow allowed_domains' \
    "$deployment_dir/egress/s3.conf"; then
    fail_test "default local/CI S3 proxy is not deny-all"
fi
if grep -R -Eiq 'better[[:space:]-]*stack|betterstack|betteruptime' \
    "$deployment_dir/../../docs/operations" \
    "$deployment_dir/egress" \
    "$deployment_dir/storage/README.md" \
    "$deployment_dir/secrets/README.md" \
    "$deployment_dir/vault/README.md"; then
    fail_test "tracked MVP runtime configuration or operator documentation retained Better Stack"
fi

printf '%s\n' \
    "egress rendering test: staging/production exact-host allowlists and fail-closed defaults passed"

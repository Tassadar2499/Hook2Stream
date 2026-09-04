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
BILLING_MODE=$(if [ "$render_environment" = staging ]; then printf stripe; else printf disabled; fi)
STORAGE_MODE=external
STORAGE_PROVISIONING_MODE=VerifyOnly
STORAGE_OBJECT_EXPIRATION_MODE=Storj
S3_ENDPOINT_HOST=$render_host
S3_SERVICE_URL=https://$render_host
S3_REGION=global
S3_MEDIA_BUCKET=hook2stream-com-$render_environment-media
S3_FORCE_PATH_STYLE=true
S3_CONFIGURE_BUCKET_LIFECYCLE=false
S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE=false
STORAGE_PROTOCOL_VERSION=1
STORAGE_CONTRACT_KEY=.hook2stream/contracts/storage-v1.json
STORAGE_CONTRACT_SHA256=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
BACKUP_S3_ENDPOINT_HOST=$render_host
BACKUP_S3_ENDPOINT=https://$render_host
BACKUP_S3_REGION=global
BACKUP_S3_BUCKET=hook2stream-com-$render_environment-pg-backups
BACKUP_S3_FORCE_PATH_STYLE=true
EGRESS_CONFIG_DIR=./egress/rendered/$render_environment
EOF
}

for render_environment in staging production; do
    render_host=gateway.storjshare.io
    render_env_file=$temporary_dir/$render_environment.env
    render_output=$temporary_dir/$render_environment
    write_environment "$render_environment" "$render_host" "$render_env_file"
    sh "$deployment_dir/scripts/render-egress-configs.sh" \
        "$render_env_file" "$render_output" >/dev/null

    for config_name in api s3 control backup; do
        rendered=$render_output/$config_name.conf
        [ "$(stat -c '%a' "$rendered")" = 644 ] \
            || fail_test "$config_name mode is not 0644"
        [ "$(grep -Foc "$render_host" "$rendered")" -eq 1 ] \
            || fail_test "$config_name does not contain exactly one exact storage hostname"
        grep -Fq "acl allowed_domains dstdomain $render_host" "$rendered" \
            || fail_test "$config_name did not put the exact host in dstdomain"
        if grep -Eiq 'better(stack|uptime)' "$rendered"; then
            fail_test "$config_name retained a Better Stack egress destination"
        fi
        if grep -Eq 'dstdomain([^[:space:]]*[[:space:]])+[.]' "$rendered"; then
            fail_test "$config_name contains a broad suffix-domain allowlist"
        fi
    done

    rendered_api=$render_output/api.conf
    case "$render_environment" in
        staging)
            grep -Fxq \
                "acl allowed_domains dstdomain $render_host accounts.google.com oauth2.googleapis.com openidconnect.googleapis.com api.stripe.com" \
                "$rendered_api" \
                || fail_test "staging API proxy does not contain the exact Stripe-enabled role allowlist"
            ;;
        production)
            grep -Fxq \
                "acl allowed_domains dstdomain $render_host accounts.google.com oauth2.googleapis.com openidconnect.googleapis.com" \
                "$rendered_api" \
                || fail_test "production API proxy does not contain the exact billing-disabled role allowlist"
            if grep -Fq 'api.stripe.com' "$rendered_api"; then
                fail_test "production BILLING_MODE=disabled retained Stripe egress"
            fi
            ;;
    esac
    if grep -Eq '(^|[[:space:]])\.(google|googleapis|gstatic|stripe)\.com([[:space:]]|$)' \
        "$rendered_api"; then
        fail_test "API proxy retained a broad Google or Stripe domain suffix"
    fi
done

bad_env=$temporary_dir/bad.env
write_environment production gateway.storjshare.io "$bad_env"
sed -i 's/BILLING_MODE=disabled/BILLING_MODE=stripe/' "$bad_env"
if sh "$deployment_dir/scripts/render-egress-configs.sh" "$bad_env" "$temporary_dir/bad" >/dev/null 2>&1; then
    fail_test "production accepted BILLING_MODE=stripe"
fi

write_environment staging gateway.storjshare.io "$bad_env"
sed -i 's/BILLING_MODE=stripe/BILLING_MODE=disabled/' "$bad_env"
if sh "$deployment_dir/scripts/render-egress-configs.sh" "$bad_env" "$temporary_dir/bad" >/dev/null 2>&1; then
    fail_test "staging accepted BILLING_MODE=disabled"
fi

write_environment production gateway.storjshare.io "$bad_env"
sed -i 's/hook2stream-com-production-media/hook2stream-com-staging-media/' "$bad_env"
if sh "$deployment_dir/scripts/render-egress-configs.sh" "$bad_env" "$temporary_dir/bad" >/dev/null 2>&1; then
    fail_test "cross-environment media bucket was accepted"
fi

write_environment staging '*.storjshare.io' "$bad_env"
if sh "$deployment_dir/scripts/render-egress-configs.sh" "$bad_env" "$temporary_dir/bad" >/dev/null 2>&1; then
    fail_test "wildcard storage hostname was accepted"
fi

write_environment staging gateway.storjshare.io "$bad_env"
sed -i 's/S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE=false/S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE=true/' "$bad_env"
if sh "$deployment_dir/scripts/render-egress-configs.sh" "$bad_env" "$temporary_dir/bad" >/dev/null 2>&1; then
    fail_test "unsupported Storj lifecycle bootstrap was accepted"
fi

write_environment staging gateway.storjshare.io "$bad_env"
sed -i 's#BACKUP_S3_ENDPOINT=https://gateway.storjshare.io#BACKUP_S3_ENDPOINT=https://arbitrary.example#' "$bad_env"
if sh "$deployment_dir/scripts/render-egress-configs.sh" "$bad_env" "$temporary_dir/bad" >/dev/null 2>&1; then
    fail_test "backup endpoint diverging from its exact host was accepted"
fi

if grep -R -E 'your-objectstorage|\.ts\.net|__HOOK2STREAM' \
    "$deployment_dir/egress/api.conf" \
    "$deployment_dir/egress/s3.conf" \
    "$deployment_dir/egress/control.conf" \
    "$deployment_dir/egress/backup.conf" >/dev/null; then
    fail_test "default local/CI allowlists contain an external storage hostname"
fi
for deny_all_config in s3 backup; do
    if grep -Eiq 'better(stack|uptime)|http_access allow allowed_domains' \
        "$deployment_dir/egress/$deny_all_config.conf"; then
        fail_test "default local/CI $deny_all_config proxy is not deny-all"
    fi
done
if grep -R -Eiq 'better[[:space:]-]*stack|betterstack|betteruptime' \
    "$deployment_dir/../../docs/operations" \
    "$deployment_dir/egress" \
    "$deployment_dir/secrets/README.md" \
    "$deployment_dir/vault/README.md"; then
    fail_test "tracked MVP runtime configuration or operator documentation retained Better Stack"
fi

printf '%s\n' \
    "egress rendering test: staging/production exact-host allowlists and fail-closed defaults passed"

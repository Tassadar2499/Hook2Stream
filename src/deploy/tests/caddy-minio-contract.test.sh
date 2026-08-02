#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
base_caddyfile=${deployment_dir}/Caddyfile
minio_caddyfile=${deployment_dir}/Caddyfile.minio

fail() {
    printf '%s\n' "MinIO Caddy contract test: $*" >&2
    exit 1
}

[ -r "$minio_caddyfile" ] || fail "Caddyfile.minio is missing"

grep -Fq '{$APP_DOMAIN}' "$minio_caddyfile" \
    || fail "the application hostname is missing"
grep -Fq '{$S3_PUBLIC_DOMAIN}' "$minio_caddyfile" \
    || fail "the public S3 hostname is missing"
grep -Fq 'reverse_proxy minio:9000' "$minio_caddyfile" \
    || fail "the S3 API is not proxied to the internal MinIO port"
grep -Fq 'header_up Host {http.request.hostport}' "$minio_caddyfile" \
    || fail "the signed S3 Host header is not preserved"
grep -Fq '/minio/admin/*' "$minio_caddyfile" \
    || fail "the public route does not block the MinIO admin API"
grep -Fq '/minio/v2/metrics/*' "$minio_caddyfile" \
    || fail "the public route does not block legacy MinIO metrics"
grep -Fq '/minio/metrics/*' "$minio_caddyfile" \
    || fail "the public route does not block current MinIO metrics"

if grep -Fq 'minio:9001' "$minio_caddyfile"; then
    fail "the MinIO console must not be proxied"
fi
if grep -Eq 'S3_PUBLIC_DOMAIN|reverse_proxy[[:space:]]+minio:' "$base_caddyfile"; then
    fail "the external-storage Caddyfile unexpectedly enables MinIO"
fi

printf '%s\n' "MinIO Caddy contract test: passed"

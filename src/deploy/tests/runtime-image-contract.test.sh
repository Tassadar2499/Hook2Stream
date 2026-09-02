#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
repository_root=$(CDPATH= cd -- "$deployment_dir/../.." && pwd)
caddy=$deployment_dir/caddy/Dockerfile
pgbouncer=$deployment_dir/pgbouncer/Dockerfile
egress=$deployment_dir/egress-proxy/Dockerfile
backup=$deployment_dir/backup/Dockerfile
postgres=$deployment_dir/postgres/Dockerfile
api=$repository_root/src/Hook2Stream.Api/Dockerfile
worker=$repository_root/src/Hook2Stream.Worker/Dockerfile
bootstrapper=$repository_root/src/Hook2Stream.Bootstrapper/Dockerfile
web=$repository_root/src/web/Dockerfile
compose=$deployment_dir/compose.yaml
build_compose=$deployment_dir/compose.build.yaml
environment=$deployment_dir/.env.example

fail() {
    printf '%s\n' "runtime image contract test: $*" >&2
    exit 1
}

for dockerfile in \
    "$caddy" "$pgbouncer" "$egress" "$backup" "$postgres" \
    "$api" "$worker" "$bootstrapper" "$web"; do
    [ -r "$dockerfile" ] || fail "missing Dockerfile: $dockerfile"
done

alpine_digest='sha256:28bd5fe8b56d1bd048e5babf5b10710ebe0bae67db86916198a6eec434943f8b'
go_digest='sha256:4c9fe60190a2a3350ddc51de80d0224b8a6698d12bdfc999fee45ea9d6c46dbc'
dotnet_sdk_digest='sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0'
dotnet_runtime_digest='sha256:1dcd9841b075d1d1013caa170b86ae58b8a8a563de9a3e319fd46a45e7ecc130'
node_digest='sha256:e67514e5d0f6c46656005e1b693b2ec9d52e80b641307de684d4a015ba7a4eaf'
postgres_digest='sha256:18cfe3ef5e6815560c98237d6216d1e5119702fb0f3894c8785dd58b8bbe5d73'

for dotnet_dockerfile in "$api" "$worker" "$bootstrapper"; do
    grep -Fq "ARG DOTNET_SDK_DIGEST=$dotnet_sdk_digest" "$dotnet_dockerfile" \
        && grep -Fq 'FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_SDK_VERSION}@${DOTNET_SDK_DIGEST} AS build' "$dotnet_dockerfile" \
        && grep -Fq "mcr.microsoft.com/dotnet/aspnet:10.0.11@$dotnet_runtime_digest" "$dotnet_dockerfile" \
        || fail ".NET build/runtime bases are not digest-pinned: $dotnet_dockerfile"
done
grep -Fq "docker.io/library/node:24-alpine@$node_digest" "$web" \
    && [ "$(grep -Fc 'FROM ${NODE_IMAGE}' "$web")" -eq 2 ] \
    || fail "web build/runtime bases are not pinned to one reviewed Node digest"
grep -Fq "docker.io/library/golang:1.27.0-alpine3.24@$go_digest" "$backup" \
    && grep -Fq "docker.io/library/postgres:17.11-alpine3.24@$postgres_digest" "$backup" \
    && grep -Fq "docker.io/library/postgres:17.11-alpine3.24@$postgres_digest" "$postgres" \
    && [ "$(grep -Fc "docker.io/library/postgres:17.11-alpine3.24@$postgres_digest" "$build_compose")" -eq 2 ] \
    || fail "backup/PostgreSQL base images are not digest-pinned in Docker and Compose builds"
grep -Fq 'jq=1.8.2-r0' "$backup" \
    || fail "backup jq package is not pinned to the reviewed patched version"

grep -Fq "golang:1.27.0-alpine3.24@$go_digest" "$caddy" \
    || fail "Caddy builder is not pinned to reviewed Go 1.27.0 digest"
grep -Fq 'ARG CADDY_RELEASE=v2.11.4' "$caddy" \
    && grep -Fq 'ARG CADDY_COMMIT=e2eee6a7fce366321294c9c2a79f3146891dcbdf' "$caddy" \
    || fail "Caddy source release/commit is not pinned"
for module in \
    'github.com/go-chi/chi/v5@v5.3.0' \
    'github.com/klauspost/compress@v1.18.7' \
    'go.opentelemetry.io/otel@v1.44.0' \
    'golang.org/x/crypto@v0.55.0' \
    'golang.org/x/net@v0.57.0' \
    'golang.org/x/text@v0.41.0' \
    'google.golang.org/grpc@v1.83.1'; do
    grep -Fq "$module" "$caddy" || fail "Caddy security dependency is not pinned: $module"
done
grep -Fq 'go list -m -f '\''{{.Version}}'\'' golang.org/x/crypto' "$caddy" \
    && grep -Fq "golang.org/x/crypto[[:space:]]+v0\\.55\\.0" "$caddy" \
    || fail "Caddy build does not assert the patched x/crypto module in its graph and binary"
grep -Fq 'go list -m -f '\''{{.Version}}'\'' google.golang.org/grpc' "$caddy" \
    && grep -Fq "google.golang.org/grpc[[:space:]]+v1\\.83\\.1" "$caddy" \
    || fail "Caddy build does not assert the patched gRPC module in its graph and binary"
grep -Fq 'FROM scratch AS runtime' "$caddy" \
    && grep -Fq 'CustomVersion=${CADDY_RELEASE}' "$caddy" \
    && grep -Fq 'USER 10001:10001' "$caddy" \
    && grep -Fq 'CMD ["caddy", "run"' "$caddy" \
    || fail "Caddy must retain the versioned non-root scratch runtime contract"

grep -Fq "alpine:3.24@$alpine_digest" "$pgbouncer" \
    || fail "PgBouncer Alpine base is not digest-pinned"
grep -Fq 'ARG PGBOUNCER_VERSION=1.25.2' "$pgbouncer" \
    && grep -Fq 'ARG PGBOUNCER_SOURCE_SHA256=924ad35113fd0a71c8e2dbe85b5d03445532e2b7b37a9f8a48983beea238b332' "$pgbouncer" \
    || fail "PgBouncer source release/checksum is not pinned"
for package in \
    'libcrypto3=3.5.8-r0' \
    'libevent=2.1.13-r0' \
    'libssl3=3.5.8-r0' \
    'postgresql17-client=17.11-r0'; do
    grep -Fq "$package" "$pgbouncer" || fail "PgBouncer runtime package is not pinned: $package"
done
grep -Fq 'USER 10001:10001' "$pgbouncer" \
    || fail "PgBouncer runtime must be non-root"

grep -Fq "alpine:3.24@$alpine_digest" "$egress" \
    || fail "egress proxy Alpine base is not digest-pinned"
grep -Fq 'ARG SQUID_VERSION=7.6-r0' "$egress" \
    && grep -Fq 'libcrypto3=3.5.8-r0' "$egress" \
    && grep -Fq 'libssl3=3.5.8-r0' "$egress" \
    || fail "Squid/OpenSSL packages are not pinned to the reviewed patched versions"
grep -Fq 'USER 31:31' "$egress" \
    || fail "Squid runtime must use its non-root package UID"

[ "$(grep -Fc 'user: "31:31"' "$compose")" -eq 4 ] \
    || fail "all four egress proxies must enforce the Squid non-root UID"
[ "$(grep -Fc 'uid=31,gid=31,mode=0750' "$compose")" -eq 12 ] \
    || fail "all Squid tmpfs mounts must be owned by the non-root runtime UID"
grep -Fq 'image: ${CADDY_IMAGE:?Set CADDY_IMAGE to the immutable Hook2Stream Caddy release image}' "$compose" \
    && grep -Fq 'image: ${PGBOUNCER_IMAGE:?Set PGBOUNCER_IMAGE to the immutable Hook2Stream PgBouncer release image}' "$compose" \
    || fail "Caddy and PgBouncer must fail closed without candidate image references"
grep -Fq 'user: "10001:10001"' "$compose" \
    && grep -Fq 'caddy-volume-init:' "$compose" \
    && grep -Fq 'chown -R 10001:10001 /data /config' "$compose" \
    && grep -Fq 'memory: 64M' "$compose" \
    && grep -Fq 'compose stop caddy' "$deployment_dir/scripts/deploy-release.sh" \
    && grep -Fq 'compose_tools run --rm --no-deps caddy-volume-init' "$deployment_dir/scripts/deploy-release.sh" \
    || fail "Caddy named volumes are not initialized before the non-root runtime"

if grep -Eq '(^|[=/])(caddy:2\.11\.4-alpine|edoburu/pgbouncer|ubuntu/squid)' "$environment" "$compose"; then
    fail "deployment configuration retains a vulnerable external runtime image"
fi

printf '%s\n' "runtime image contracts passed"

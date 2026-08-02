#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
temporary_dir=$(mktemp -d)

cleanup() {
    rm -rf "$temporary_dir"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

fail() {
    printf '%s\n' "deployment validation: $*" >&2
    exit 1
}

command -v docker >/dev/null 2>&1 || fail "docker is required"
docker compose version >/dev/null 2>&1 \
    || fail "the Docker Compose plugin v2 or newer is required"
command -v node >/dev/null 2>&1 || fail "node is required"
node_major=$(node -p 'process.versions.node.split(".")[0]')
case "$node_major" in
    *[!0-9]*|'') fail "could not determine the Node.js major version" ;;
esac
[ "$node_major" -ge 24 ] || fail "Node.js 24 or newer is required"

secret_dir=${temporary_dir}/secrets
mkdir -p "$secret_dir"
chmod 0750 "$secret_dir"
umask 027
for secret_name in \
    postgres_password \
    s3_runtime_access_key \
    s3_runtime_secret_key \
    s3_bootstrap_access_key \
    s3_bootstrap_secret_key \
    google_client_secret \
    stripe_secret_key \
    stripe_webhook_secret \
    openrouter_api_key \
    backup_s3_access_key \
    backup_s3_secret_key \
    backup_encryption_passphrase \
    backup_encryption_key_id \
    backup_heartbeat_url \
    minio_root_user \
    minio_root_password; do
    printf '%s\n' "ci-placeholder-not-a-production-secret" \
        > "${secret_dir}/${secret_name}"
done
printf '%s\n' "ci-backup-key-001" > "${secret_dir}/backup_encryption_key_id"
: > "${secret_dir}/backup_heartbeat_url"

for script_path in "$deployment_dir"/scripts/*.sh; do
    sh -n "$script_path"
done
for test_path in "$deployment_dir"/tests/*.test.sh; do
    sh "$test_path"
done
node -e \
    'JSON.parse(require("node:fs").readFileSync(process.argv[1], "utf8"))' \
    "$deployment_dir/backup/lifecycle-policy.json"

validation_caddy_image=$(awk -F= '
    $1 == "CADDY_IMAGE" { print substr($0, index($0, "=") + 1) }
' "$deployment_dir/.env.example")
[ -n "$validation_caddy_image" ] \
    || fail "CADDY_IMAGE is missing from .env.example"

validate_caddyfile() {
    validation_caddyfile=$1
    shift
    [ -r "${deployment_dir}/${validation_caddyfile}" ] \
        || fail "Caddy configuration is missing: ${validation_caddyfile}"

    docker run --rm \
        --network none \
        --read-only \
        --cap-drop ALL \
        --cap-add NET_BIND_SERVICE \
        --security-opt no-new-privileges \
        --tmpfs /data:rw,nosuid,nodev,noexec,size=1m \
        --tmpfs /config:rw,nosuid,nodev,noexec,size=1m \
        --tmpfs /tmp:rw,nosuid,nodev,noexec,size=1m \
        --env APP_DOMAIN=app.example.invalid \
        --env ACME_EMAIL=deploy@example.invalid \
        "$@" \
        --mount \
            "type=bind,source=${deployment_dir}/${validation_caddyfile},target=/etc/caddy/Caddyfile,readonly" \
        "$validation_caddy_image" \
        caddy validate --config /etc/caddy/Caddyfile --adapter caddyfile
}

validate_caddyfile Caddyfile
validate_caddyfile Caddyfile.minio \
    --env S3_PUBLIC_DOMAIN=s3-staging.example.invalid \
    --env S3_MEDIA_BUCKET=hook2stream-staging-media

validation_digest=cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc
API_IMAGE=registry.example/hook2stream-api@sha256:$validation_digest
WORKER_IMAGE=registry.example/hook2stream-worker@sha256:$validation_digest
BOOTSTRAPPER_IMAGE=registry.example/hook2stream-bootstrapper@sha256:$validation_digest
WEB_IMAGE=registry.example/hook2stream-web@sha256:$validation_digest
POSTGRES_BACKUP_IMAGE=registry.example/hook2stream-postgres-backup@sha256:$validation_digest
CADDY_IMAGE=registry.example/caddy@sha256:$validation_digest
POSTGRES_IMAGE=registry.example/postgres@sha256:$validation_digest
PGBOUNCER_IMAGE=registry.example/pgbouncer@sha256:$validation_digest
VAULT_AGENT_IMAGE=registry.example/vault@sha256:$validation_digest
export \
    API_IMAGE \
    WORKER_IMAGE \
    BOOTSTRAPPER_IMAGE \
    WEB_IMAGE \
    POSTGRES_BACKUP_IMAGE \
    CADDY_IMAGE \
    POSTGRES_IMAGE \
    PGBOUNCER_IMAGE \
    VAULT_AGENT_IMAGE

SECRET_PROVIDER=file SECRETS_DIR=$secret_dir docker compose \
    --env-file "$deployment_dir/.env.example" \
    --profile tools \
    -f "$deployment_dir/compose.yaml" \
    config --format json > "$temporary_dir/external-compose.json"
node "$deployment_dir/../ci/validate-compose-images.mjs" \
    "$temporary_dir/external-compose.json"

SECRET_PROVIDER=file SECRETS_DIR=$secret_dir docker compose \
    --env-file "$deployment_dir/.env.example" \
    --profile tools \
    -f "$deployment_dir/compose.yaml" \
    -f "$deployment_dir/compose.build.yaml" \
    config --format json > "$temporary_dir/build-compose.json"
node "$deployment_dir/../ci/validate-compose-images.mjs" \
    "$temporary_dir/build-compose.json"

[ -r "$deployment_dir/compose.minio.yaml" ] \
    || fail "MinIO Compose overlay is missing: $deployment_dir/compose.minio.yaml"
validation_minio_image=registry.example/hook2stream-minio@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
validation_minio_mc_image=registry.example/minio-mc@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
STORAGE_MODE=minio \
MINIO_IMAGE=$validation_minio_image \
MINIO_MC_IMAGE=$validation_minio_mc_image \
S3_SERVICE_URL=http://minio:9000 \
S3_PUBLIC_SERVICE_URL=https://s3-staging.example.invalid \
S3_PUBLIC_DOMAIN=s3-staging.example.invalid \
S3_REGION=us-east-1 \
S3_MEDIA_BUCKET=hook2stream-staging-media \
S3_FORCE_PATH_STYLE=true \
BACKUP_S3_BUCKET=hook2stream-staging-pg-backups \
BACKUP_S3_ENDPOINT=http://minio:9000 \
BACKUP_S3_REGION=us-east-1 \
BACKUP_S3_PREFIX=hook2stream/staging/postgres \
BACKUP_RETENTION_DAYS=7 \
S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE=false \
MINIO_MEDIA_QUOTA_GIB=180 \
MINIO_BACKUP_QUOTA_GIB=20 \
SECRET_PROVIDER=file \
SECRETS_DIR=$secret_dir \
    docker compose \
    --env-file "$deployment_dir/.env.example" \
    --profile tools \
    -f "$deployment_dir/compose.yaml" \
    -f "$deployment_dir/compose.minio.yaml" \
    config --format json > "$temporary_dir/minio-compose.json"
node "$deployment_dir/../ci/validate-minio-compose.mjs" \
    "$temporary_dir/minio-compose.json"
node "$deployment_dir/../ci/validate-compose-images.mjs" \
    "$temporary_dir/minio-compose.json"

mkdir -p \
    "$temporary_dir/vault-auth" \
    "$temporary_dir/vault-candidate"
: > "$temporary_dir/vault-ca.pem"
SECRET_PROVIDER=vault \
SECRETS_DIR=$secret_dir \
VAULT_AUTH_DIR=$temporary_dir/vault-auth \
VAULT_CACERT=$temporary_dir/vault-ca.pem \
VAULT_CANDIDATE_DIR=$temporary_dir/vault-candidate \
    docker compose \
        --env-file "$deployment_dir/.env.example" \
        --profile tools \
        -f "$deployment_dir/compose.yaml" \
        -f "$deployment_dir/compose.vault.yaml" \
        config --format json > "$temporary_dir/vault-compose.json"
node "$deployment_dir/../ci/validate-compose-images.mjs" \
    "$temporary_dir/vault-compose.json"

printf '%s\n' \
    "deployment validation: shell tests, lifecycle JSON, Caddyfiles, digest images, and base/build/MinIO/Vault Compose models are valid"

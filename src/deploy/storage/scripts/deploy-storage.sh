#!/bin/sh
set -eu
set -f

[ "$#" -eq 2 ] || { printf '%s\n' "usage: deploy-storage.sh RELEASE_DIR ACTIVE_ENV" >&2; exit 2; }
STORAGE_RELEASE_DIR=$1
STORAGE_ACTIVE_ENV_FILE=$2
export STORAGE_RELEASE_DIR STORAGE_ACTIVE_ENV_FILE
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_dir/lib/storage-common.sh"

[ "$(id -u)" -eq 0 ] || storage_fail "release deployment must run as root"
[ -d "$STORAGE_RELEASE_DIR/storage" ] && [ ! -L "$STORAGE_RELEASE_DIR/storage" ] \
    || storage_fail "release storage directory is invalid"
"$script_dir/validate-config.sh" "$STORAGE_ACTIVE_ENV_FILE"
set -a
. "$STORAGE_ACTIVE_ENV_FILE"
set +a
export DEPLOYMENT_ENVIRONMENT

wait_healthy() {
    service=$1
    attempts=${2:-60}
    while [ "$attempts" -gt 0 ]; do
        container=$(storage_compose ps -q "$service")
        if [ -n "$container" ]; then
            health=$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' "$container" 2>/dev/null || true)
            [ "$health" = healthy ] && return 0
            [ "$health" = exited ] || [ "$health" = dead ] && storage_fail "$service exited before becoming healthy"
        fi
        attempts=$((attempts - 1))
        sleep 2
    done
    storage_fail "$service did not become healthy"
}

persist_managed_identity_inventory() {
    [ "$MANAGED_IDENTITY_INVENTORY_FILE" = "$STORAGE_STATE_DIR/managed-identities.v1" ] \
        || storage_fail "managed identity inventory path changed after validation"
    inventory_bootstrap=$(storage_read_secret_file "$SECRETS_DIR/s3_bootstrap_access_key")
    inventory_runtime=$(storage_read_secret_file "$SECRETS_DIR/s3_runtime_access_key")
    inventory_backup=$(storage_read_secret_file "$SECRETS_DIR/backup_s3_access_key")
    storage_validate_mc_host_credential s3_bootstrap_access_key "$inventory_bootstrap"
    storage_validate_mc_host_credential s3_runtime_access_key "$inventory_runtime"
    storage_validate_mc_host_credential backup_s3_access_key "$inventory_backup"
    inventory_tmp=$(mktemp "$STORAGE_STATE_DIR/.managed-identities.XXXXXX")
    {
        printf '%s\n' HOOK2STREAM_STORAGE_MANAGED_IDENTITIES_V1
        printf 'bootstrap=%s\n' "$inventory_bootstrap"
        printf 'runtime=%s\n' "$inventory_runtime"
        printf 'backup=%s\n' "$inventory_backup"
    } > "$inventory_tmp"
    chown 0:0 "$inventory_tmp"
    chmod 0600 "$inventory_tmp"
    mv -f "$inventory_tmp" "$MANAGED_IDENTITY_INVENTORY_FILE"
    unset inventory_bootstrap inventory_runtime inventory_backup inventory_tmp
}

verify_minio_source_identity() {
    source_manifest=$STORAGE_RELEASE_DIR/storage/storage-release.json
    expected_source_release=$(jq -r .minioRelease "$source_manifest")
    expected_source_commit=$(jq -r .minioSourceCommit "$source_manifest")
    actual_source_release=$(docker image inspect --format \
        '{{index .Config.Labels "com.hook2stream.minio.source-release"}}' "$MINIO_IMAGE")
    actual_source_commit=$(docker image inspect --format \
        '{{index .Config.Labels "com.hook2stream.minio.source-commit"}}' "$MINIO_IMAGE")
    [ "$actual_source_release" = "$expected_source_release" ] \
        && [ "$actual_source_commit" = "$expected_source_commit" ] \
        || storage_fail "MinIO image labels do not bind the policy-approved source release and commit"
    unset source_manifest expected_source_release expected_source_commit \
        actual_source_release actual_source_commit
}

printf '%s\n' "storage deploy: pulling immutable images" >&2
storage_compose --profile tools pull minio minio-init caddy >&2
verify_minio_source_identity
storage_compose up -d --no-deps minio >&2
wait_healthy minio 60
storage_compose --profile tools run --rm --no-deps -T minio-init \
    < "$MANAGED_IDENTITY_INVENTORY_FILE" >&2
# MinIO has already committed the user mutations. Persist their access-key IDs
# before any later health/isolation/Caddy failure so a retry can always retire
# identities created by a failed deployment attempt.
persist_managed_identity_inventory
storage_compose --profile tools run --rm --no-deps \
    --entrypoint /bin/sh minio-init /opt/hook2stream/minio-auth-healthcheck.sh >&2
storage_compose --profile tools run --rm --no-deps \
    --entrypoint /bin/sh minio-init /opt/hook2stream/minio-policy-isolation-probe.sh >&2
storage_compose up -d --no-deps --force-recreate caddy >&2
wait_healthy caddy 60

protocol_body=$(curl --fail --silent --show-error --max-time 10 \
    --resolve "$STORAGE_TLS_SERVER_NAME:443:$TAILSCALE_IPV4" \
    "https://$STORAGE_TLS_SERVER_NAME/.well-known/hook2stream-storage-protocol")
[ "$protocol_body" = 1 ] || storage_fail "private storage protocol endpoint did not return exact body 1"

for mapping in MINIO_IMAGE:minio CADDY_IMAGE:caddy; do
    variable=${mapping%%:*}
    service=${mapping#*:}
    expected=$(storage_env_value "$STORAGE_ACTIVE_ENV_FILE" "$variable")
    container=$(storage_compose ps -q "$service")
    [ -n "$container" ] || storage_fail "$service container is absent"
    actual=$(docker inspect --format '{{.Config.Image}}' "$container")
    [ "$actual" = "$expected" ] || storage_fail "$service is not running the candidate digest"
done
docker image inspect "$MINIO_MC_IMAGE" >/dev/null 2>&1 \
    || storage_fail "the candidate MinIO client digest is unavailable after init"
printf '%s\n' "storage deploy: topology and runtime verified" >&2

#!/bin/sh
set -eu

image=${1:?pass the postgres-backup image tag}
temporary_root=${RUNNER_TEMP:-/tmp}
secret_dir=${MINIO_ACCEPTANCE_SECRET_DIR:-${temporary_root}/hook2stream-ci-minio-secrets}
network_name=${MINIO_ACCEPTANCE_NETWORK:-hook2stream-ci-storage}
secret_gid=${MINIO_ACCEPTANCE_SECRET_GID:-2000}

fail() {
    printf '%s\n' "storage tool MinIO smoke: $*" >&2
    exit 1
}

case "$image" in
    ''|*[!a-zA-Z0-9_./:@-]*) fail "image reference contains unsafe characters" ;;
esac
case "$secret_gid" in
    ''|*[!0-9]*) fail "secret GID must be numeric" ;;
esac
[ "$secret_gid" -ge 1 ] || fail "secret GID must be positive"
docker image inspect "$image" >/dev/null 2>&1 || fail "acceptance image is missing"
docker network inspect "$network_name" >/dev/null 2>&1 || fail "acceptance network is missing"

docker run --rm \
    --network "$network_name" \
    --user postgres \
    --group-add "$secret_gid" \
    --read-only \
    --cap-drop ALL \
    --security-opt no-new-privileges=true \
    --pids-limit 64 \
    --tmpfs /tmp:rw,noexec,nosuid,nodev,size=8m,mode=1777 \
    --mount "type=bind,source=${secret_dir}/s3_runtime_access_key,target=/run/secrets/access,readonly" \
    --mount "type=bind,source=${secret_dir}/s3_runtime_secret_key,target=/run/secrets/secret,readonly" \
    --entrypoint /bin/sh \
    "$image" -ec '
        storage_command() {
            operation=$1
            shift
            hook2stream-storage-tool "$operation" \
                --endpoint http://minio:9000 \
                --region us-east-1 \
                --bucket hook2stream-staging-media \
                --access-key-file /run/secrets/access \
                --secret-key-file /run/secrets/secret \
                "$@"
        }

        probe_key="acceptance/storage-tool-$$"
        object_created=false
        cleanup() {
            cleanup_status=$?
            trap - EXIT
            if [ "$object_created" = true ]; then
                storage_command delete-object --key "$probe_key" >/dev/null 2>&1 || true
            fi
            exit "$cleanup_status"
        }
        trap cleanup EXIT
        trap "exit 130" HUP INT TERM

        printf %s hook2stream-storage-probe-v1 > /tmp/payload
        storage_command put-object --key "$probe_key" --body /tmp/payload \
            | jq -e ".versionId | type == \"string\"" >/dev/null
        object_created=true
        storage_command head-object --key "$probe_key" \
            | jq -e ".contentLength == 28" >/dev/null
        storage_command get-object --key "$probe_key" --range bytes=12-18 --output /tmp/range
        test "$(cat /tmp/range)" = storage
        storage_command abort-multipart-older-than --older-than-seconds 86400 \
            | jq -e ".aborted as \$value | (\$value | type) == \"number\" and \$value >= 0" >/dev/null
        storage_command delete-object --key "$probe_key" \
            | jq -e ".deleted == true" >/dev/null
        object_created=false

        if hook2stream-storage-tool head-object \
            --endpoint http://minio:9001 \
            --region us-east-1 \
            --bucket hook2stream-staging-media \
            --access-key-file /run/secrets/access \
            --secret-key-file /run/secrets/secret \
            --key "$probe_key" >/dev/null 2>&1; then
            echo "unsafe non-canonical HTTP endpoint was accepted" >&2
            exit 1
        fi
    '

printf '%s\n' \
    "storage tool MinIO smoke: PUT/HEAD/Range/DELETE, multipart listing, and exact HTTP origin passed"

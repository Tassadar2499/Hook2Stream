#!/bin/sh
set -eu

repository_root=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
environment_output=${1:?pass the GitHub environment output file as the first argument}
minio_image=${MINIO_ACCEPTANCE_IMAGE:-hook2stream-minio:ci}
mc_image=${MINIO_ACCEPTANCE_MC_IMAGE:-minio/mc:RELEASE.2025-07-21T05-28-08Z}
caddy_image=${MINIO_ACCEPTANCE_CADDY_IMAGE:-caddy:2.11.4-alpine}
minio_host_port=${MINIO_ACCEPTANCE_MINIO_PORT:-9000}
caddy_host_port=${MINIO_ACCEPTANCE_CADDY_PORT:-9443}
install_ca=${MINIO_ACCEPTANCE_INSTALL_CA:-true}
use_sudo=${MINIO_ACCEPTANCE_USE_SUDO:-true}
secret_gid=${MINIO_ACCEPTANCE_SECRET_GID:-2000}
temporary_root=${RUNNER_TEMP:-/tmp}
secret_dir=${temporary_root}/hook2stream-ci-minio-secrets
ca_certificate=${temporary_root}/hook2stream-ci-caddy-root.crt
network_name=hook2stream-ci-storage
minio_name=hook2stream-ci-minio
caddy_name=hook2stream-ci-caddy

fail() {
    printf '%s\n' "MinIO acceptance setup: $*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || fail "$1 is required"
}

write_secret() {
    secret_name=$1
    secret_value=$2
    if [ "$use_sudo" = true ]; then
        printf '%s\n' "$secret_value" \
            | sudo tee "${secret_dir}/${secret_name}" >/dev/null
        sudo chown "root:${secret_gid}" "${secret_dir}/${secret_name}"
        sudo chmod 0640 "${secret_dir}/${secret_name}"
    else
        printf '%s\n' "$secret_value" \
            | tee "${secret_dir}/${secret_name}" >/dev/null
        chgrp "$secret_gid" "${secret_dir}/${secret_name}"
        chmod 0640 "${secret_dir}/${secret_name}"
    fi
}

wait_for_http() {
    readiness_url=$1
    readiness_attempt=0
    while [ "$readiness_attempt" -lt 30 ]; do
        if curl --fail --silent --show-error "$readiness_url" >/dev/null 2>&1; then
            return 0
        fi
        readiness_attempt=$((readiness_attempt + 1))
        sleep 1
    done
    return 1
}

run_init() {
    docker run --rm \
        --network "$network_name" \
        --user 1000:1000 \
        --group-add "$secret_gid" \
        --read-only \
        --cap-drop ALL \
        --security-opt no-new-privileges=true \
        --tmpfs /tmp:rw,noexec,nosuid,nodev,size=32m,uid=1000,gid=1000,mode=0700 \
        --env STORAGE_MODE=minio \
        --env MINIO_ENDPOINT=http://minio:9000 \
        --env MINIO_REGION=us-east-1 \
        --env MINIO_MEDIA_BUCKET=hook2stream-staging-media \
        --env MINIO_BACKUP_BUCKET=hook2stream-staging-pg-backups \
        --env MINIO_BACKUP_PREFIX=hook2stream/staging/postgres \
        --env MINIO_MEDIA_QUOTA_GIB=180 \
        --env MINIO_BACKUP_QUOTA_GIB=20 \
        --env MINIO_ROOT_USER_FILE=/run/secrets/minio_root_user \
        --env MINIO_ROOT_PASSWORD_FILE=/run/secrets/minio_root_password \
        --env S3_RUNTIME_ACCESS_KEY_FILE=/run/secrets/s3_runtime_access_key \
        --env S3_RUNTIME_SECRET_KEY_FILE=/run/secrets/s3_runtime_secret_key \
        --env S3_BOOTSTRAP_ACCESS_KEY_FILE=/run/secrets/s3_bootstrap_access_key \
        --env S3_BOOTSTRAP_SECRET_KEY_FILE=/run/secrets/s3_bootstrap_secret_key \
        --env BACKUP_S3_ACCESS_KEY_FILE=/run/secrets/backup_s3_access_key \
        --env BACKUP_S3_SECRET_KEY_FILE=/run/secrets/backup_s3_secret_key \
        --env MC_CONFIG_DIR=/tmp/mc \
        --mount "type=bind,source=${secret_dir},target=/run/secrets,readonly" \
        --mount "type=bind,source=${repository_root}/src/deploy/minio/minio-init.sh,target=/opt/hook2stream/minio-init.sh,readonly" \
        --mount "type=bind,source=${repository_root}/src/deploy/minio/policies/runtime-media.json,target=/etc/hook2stream/minio/runtime-media.json,readonly" \
        --mount "type=bind,source=${repository_root}/src/deploy/minio/policies/bootstrap-media.json,target=/etc/hook2stream/minio/bootstrap-media.json,readonly" \
        --mount "type=bind,source=${repository_root}/src/deploy/minio/policies/postgres-backup.json,target=/etc/hook2stream/minio/postgres-backup.json,readonly" \
        --mount "type=bind,source=${repository_root}/src/deploy/minio/backup-lifecycle.json,target=/etc/hook2stream/minio/backup-lifecycle.json,readonly" \
        --entrypoint /bin/sh \
        "$mc_image" \
        /opt/hook2stream/minio-init.sh
}

require_command curl
require_command docker
require_command grep
[ "$use_sudo" != true ] || require_command sudo
[ -f "$environment_output" ] || fail "environment output file is missing: $environment_output"
[ ! -e "$secret_dir" ] || fail "temporary secret directory already exists: $secret_dir"
for acceptance_port in "$minio_host_port" "$caddy_host_port"; do
    case "$acceptance_port" in
        ''|*[!0-9]*) fail "acceptance ports must be numeric" ;;
    esac
    [ "$acceptance_port" -ge 1 ] && [ "$acceptance_port" -le 65535 ] \
        || fail "acceptance ports must be between 1 and 65535"
done
case "$install_ca" in
    true|false) ;;
    *) fail "MINIO_ACCEPTANCE_INSTALL_CA must be true or false" ;;
esac
case "$use_sudo" in
    true|false) ;;
    *) fail "MINIO_ACCEPTANCE_USE_SUDO must be true or false" ;;
esac
case "$secret_gid" in
    ''|*[!0-9]*) fail "MINIO_ACCEPTANCE_SECRET_GID must be numeric" ;;
esac
[ "$secret_gid" -ge 1 ] || fail "MINIO_ACCEPTANCE_SECRET_GID must be positive"
[ "$install_ca:$use_sudo" != true:false ] \
    || fail "installing the local CA requires MINIO_ACCEPTANCE_USE_SUDO=true"

if [ "$use_sudo" = true ]; then
    sudo install -d -o root -g "$secret_gid" -m 0750 "$secret_dir"
else
    install -d -m 0750 "$secret_dir"
    chgrp "$secret_gid" "$secret_dir"
fi
write_secret minio_root_user hook2stream-ci-root
write_secret minio_root_password hook2stream-ci-root-secret-value
write_secret s3_runtime_access_key hook2stream-ci-runtime
write_secret s3_runtime_secret_key hook2stream-ci-runtime-secret
write_secret s3_bootstrap_access_key hook2stream-ci-bootstrap
write_secret s3_bootstrap_secret_key hook2stream-ci-bootstrap-secret
write_secret backup_s3_access_key hook2stream-ci-backup
write_secret backup_s3_secret_key hook2stream-ci-backup-secret

docker network create "$network_name" >/dev/null
docker run --detach --rm \
    --name "$minio_name" \
    --network "$network_name" \
    --network-alias minio \
    --publish "127.0.0.1:${minio_host_port}:9000" \
    --user 10001:10001 \
    --group-add "$secret_gid" \
    --read-only \
    --cap-drop ALL \
    --security-opt no-new-privileges=true \
    --pids-limit 256 \
    --tmpfs /tmp:rw,noexec,nosuid,nodev,size=64m,uid=10001,gid=10001,mode=0700 \
    --env STORAGE_MODE=minio \
    --env MINIO_BROWSER=off \
    --env MINIO_API_CORS_ALLOW_ORIGIN=off \
    --env MINIO_REGION_NAME=us-east-1 \
    --env MINIO_ROOT_USER_FILE=/run/secrets/minio_root_user \
    --env MINIO_ROOT_PASSWORD_FILE=/run/secrets/minio_root_password \
    --mount "type=bind,source=${secret_dir}/minio_root_user,target=/run/secrets/minio_root_user,readonly" \
    --mount "type=bind,source=${secret_dir}/minio_root_password,target=/run/secrets/minio_root_password,readonly" \
    "$minio_image" \
    server --address :9000 /data >/dev/null

if ! wait_for_http "http://127.0.0.1:${minio_host_port}/minio/health/ready"; then
    docker logs "$minio_name" >&2 || true
    fail "MinIO did not become ready"
fi

# The second real run proves that the selected server and mc releases can
# re-apply the exact buckets, quotas, lifecycle, policies, and users.
run_init
run_init

state_output=$(docker run --rm \
    --network "$network_name" \
    --user 1000:1000 \
    --group-add "$secret_gid" \
    --read-only \
    --cap-drop ALL \
    --security-opt no-new-privileges=true \
    --tmpfs /tmp:rw,noexec,nosuid,nodev,size=32m,uid=1000,gid=1000,mode=0700 \
    --mount "type=bind,source=${secret_dir},target=/run/secrets,readonly" \
    --entrypoint /bin/bash \
    "$mc_image" -ceu '
        IFS= read -r root_user < /run/secrets/minio_root_user
        IFS= read -r root_password < /run/secrets/minio_root_password
        mc --config-dir /tmp/mc alias set h http://minio:9000 "$root_user" "$root_password" --api S3v4 --path on >/dev/null
        mc --config-dir /tmp/mc version info h/hook2stream-staging-media
        mc --config-dir /tmp/mc version info h/hook2stream-staging-pg-backups
        mc --config-dir /tmp/mc quota info h/hook2stream-staging-media
        mc --config-dir /tmp/mc quota info h/hook2stream-staging-pg-backups
        mc --config-dir /tmp/mc ilm rule ls --json h/hook2stream-staging-pg-backups
        mc --config-dir /tmp/mc anonymous get h/hook2stream-staging-media
        mc --config-dir /tmp/mc anonymous get h/hook2stream-staging-pg-backups
    ')
printf '%s\n' "$state_output" | grep -F 'media versioning is suspended' >/dev/null \
    || fail "media versioning is not suspended"
printf '%s\n' "$state_output" | grep -F 'pg-backups versioning is enabled' >/dev/null \
    || fail "backup versioning is not enabled"
printf '%s\n' "$state_output" | grep -F 'hard quota of 180 GiB' >/dev/null \
    || fail "media quota is not 180 GiB"
printf '%s\n' "$state_output" | grep -F 'hard quota of 20 GiB' >/dev/null \
    || fail "backup quota is not 20 GiB"
printf '%s\n' "$state_output" | grep -F 'hook2stream-staging-backup-retention-7d' >/dev/null \
    || fail "backup lifecycle is missing"
[ "$(printf '%s\n' "$state_output" | grep -Fc 'is `private`')" -eq 2 ] \
    || fail "both buckets must deny anonymous access"

docker run --rm \
    --network "$network_name" \
    --user 1000:1000 \
    --group-add "$secret_gid" \
    --read-only \
    --cap-drop ALL \
    --security-opt no-new-privileges=true \
    --tmpfs /tmp:rw,noexec,nosuid,nodev,size=32m,uid=1000,gid=1000,mode=0700 \
    --mount "type=bind,source=${secret_dir},target=/run/secrets,readonly" \
    --entrypoint /bin/bash \
    "$mc_image" -ceu '
        IFS= read -r runtime_user < /run/secrets/s3_runtime_access_key
        IFS= read -r runtime_secret < /run/secrets/s3_runtime_secret_key
        IFS= read -r bootstrap_user < /run/secrets/s3_bootstrap_access_key
        IFS= read -r bootstrap_secret < /run/secrets/s3_bootstrap_secret_key
        IFS= read -r backup_user < /run/secrets/backup_s3_access_key
        IFS= read -r backup_secret < /run/secrets/backup_s3_secret_key

        mc --config-dir /tmp/runtime alias set runtime http://minio:9000 "$runtime_user" "$runtime_secret" --api S3v4 --path on >/dev/null
        printf runtime-policy-check | mc --config-dir /tmp/runtime pipe runtime/hook2stream-staging-media/acceptance/runtime-policy.txt >/dev/null
        test "$(mc --config-dir /tmp/runtime cat runtime/hook2stream-staging-media/acceptance/runtime-policy.txt)" = runtime-policy-check
        if mc --config-dir /tmp/runtime ls runtime/hook2stream-staging-pg-backups >/dev/null 2>&1; then exit 20; fi
        mc --config-dir /tmp/runtime rm --force runtime/hook2stream-staging-media/acceptance/runtime-policy.txt >/dev/null

        mc --config-dir /tmp/bootstrap alias set bootstrap http://minio:9000 "$bootstrap_user" "$bootstrap_secret" --api S3v4 --path on >/dev/null
        mc --config-dir /tmp/bootstrap ls bootstrap/hook2stream-staging-media >/dev/null
        if printf forbidden | mc --config-dir /tmp/bootstrap pipe bootstrap/hook2stream-staging-media/acceptance/bootstrap-forbidden.txt >/dev/null 2>&1; then exit 21; fi

        mc --config-dir /tmp/backup alias set backup http://minio:9000 "$backup_user" "$backup_secret" --api S3v4 --path on >/dev/null
        printf backup-policy-check | mc --config-dir /tmp/backup pipe backup/hook2stream-staging-pg-backups/hook2stream/staging/postgres/acceptance/policy.txt >/dev/null
        mc --config-dir /tmp/backup ls --versions backup/hook2stream-staging-pg-backups/hook2stream/staging/postgres/acceptance/policy.txt >/dev/null
        if mc --config-dir /tmp/backup ls backup/hook2stream-staging-media >/dev/null 2>&1; then exit 22; fi
        mc --config-dir /tmp/backup rm --versions --force backup/hook2stream-staging-pg-backups/hook2stream/staging/postgres/acceptance/policy.txt >/dev/null
    '

docker run --detach --rm \
    --name "$caddy_name" \
    --network "$network_name" \
    --publish "127.0.0.1:${caddy_host_port}:443" \
    --env APP_DOMAIN=app.localhost \
    --env S3_PUBLIC_DOMAIN=s3.localhost \
    --env S3_MEDIA_BUCKET=hook2stream-staging-media \
    --env ACME_EMAIL=ci@example.invalid \
    --mount "type=bind,source=${repository_root}/src/deploy/Caddyfile.minio,target=/etc/caddy/Caddyfile,readonly" \
    "$caddy_image" >/dev/null

certificate_attempt=0
while [ "$certificate_attempt" -lt 30 ]; do
    if docker cp \
        "${caddy_name}:/data/caddy/pki/authorities/local/root.crt" \
        "$ca_certificate" >/dev/null 2>&1; then
        break
    fi
    certificate_attempt=$((certificate_attempt + 1))
    sleep 1
done
[ -s "$ca_certificate" ] || fail "Caddy local CA certificate was not created"

if ! getent ahostsv4 s3.localhost >/dev/null 2>&1; then
    [ "$use_sudo" = true ] \
        || fail "s3.localhost does not resolve and /etc/hosts requires sudo"
    printf '%s\n' '127.0.0.1 s3.localhost app.localhost' \
        | sudo tee -a /etc/hosts >/dev/null
fi
case "$install_ca" in
    true)
        installed_ca_certificate=/usr/local/share/ca-certificates/hook2stream-ci-caddy.crt
        sudo install -o root -g root -m 0644 \
            "$ca_certificate" \
            "$installed_ca_certificate"
        sudo update-ca-certificates >/dev/null
        # Preserve the public trust store for subsequent NuGet/npm downloads;
        # SSL_CERT_FILE replaces rather than augments the process trust file.
        trusted_ca_certificate=/etc/ssl/certs/ca-certificates.crt
        ;;
    false)
        trusted_ca_certificate=$ca_certificate
        ;;
esac

https_attempt=0
while [ "$https_attempt" -lt 30 ]; do
    if curl --fail --silent --show-error \
        --cacert "$ca_certificate" \
        "https://s3.localhost:${caddy_host_port}/minio/health/ready" >/dev/null 2>&1; then
        break
    fi
    https_attempt=$((https_attempt + 1))
    sleep 1
done
[ "$https_attempt" -lt 30 ] || {
    docker logs "$caddy_name" >&2 || true
    fail "Caddy HTTPS endpoint did not become ready"
}

allowed_headers=${temporary_root}/hook2stream-ci-cors-allowed.headers
wrong_headers=${temporary_root}/hook2stream-ci-cors-wrong.headers
backup_headers=${temporary_root}/hook2stream-ci-cors-backup.headers
curl --fail --silent --show-error \
    --dump-header "$allowed_headers" \
    --output /dev/null \
    --request OPTIONS \
    --cacert "$ca_certificate" \
    --header 'Origin: https://app.localhost' \
    --header 'Access-Control-Request-Method: PUT' \
    --header 'Access-Control-Request-Headers: content-type,x-amz-date' \
    "https://s3.localhost:${caddy_host_port}/hook2stream-staging-media/acceptance/cors"
grep -Eiq '^access-control-allow-origin:[[:space:]]*https://app\.localhost[[:space:]]*$' \
    "$allowed_headers" || fail "exact media CORS origin was not returned"
grep -Eiq '^access-control-expose-headers:[[:space:]]*ETag[[:space:]]*$' \
    "$allowed_headers" || fail "media CORS does not expose ETag"

curl --silent --show-error \
    --dump-header "$wrong_headers" \
    --output /dev/null \
    --request OPTIONS \
    --cacert "$ca_certificate" \
    --header 'Origin: https://untrusted.invalid' \
    --header 'Access-Control-Request-Method: PUT' \
    "https://s3.localhost:${caddy_host_port}/hook2stream-staging-media/acceptance/cors"
if grep -Eiq '^access-control-allow-origin:' "$wrong_headers"; then
    fail "untrusted origin received media CORS access"
fi

curl --silent --show-error \
    --dump-header "$backup_headers" \
    --output /dev/null \
    --request OPTIONS \
    --cacert "$ca_certificate" \
    --header 'Origin: https://app.localhost' \
    --header 'Access-Control-Request-Method: PUT' \
    "https://s3.localhost:${caddy_host_port}/hook2stream-staging-pg-backups/hook2stream/staging/postgres/cors"
if grep -Eiq '^access-control-allow-origin:' "$backup_headers"; then
    fail "backup bucket received browser CORS access"
fi

for private_path in /minio/admin/v3/info /minio/v2/metrics/cluster /minio/metrics/v3; do
    private_status=$(curl --silent --show-error \
        --output /dev/null \
        --write-out '%{http_code}' \
        --cacert "$ca_certificate" \
        "https://s3.localhost:${caddy_host_port}${private_path}")
    [ "$private_status" = 404 ] \
        || fail "public private-route guard returned ${private_status} for ${private_path}"
done
if docker exec "$minio_name" \
    wget -q -O /dev/null http://127.0.0.1:9001/ >/dev/null 2>&1; then
    fail "MinIO console is listening inside the container"
fi

cat >> "$environment_output" <<EOF
HOOK2STREAM_TEST_MINIO=http://127.0.0.1:${minio_host_port}
HOOK2STREAM_TEST_MINIO_PUBLIC=https://s3.localhost:${caddy_host_port}
HOOK2STREAM_TEST_MINIO_ACCESS_KEY=hook2stream-ci-runtime
HOOK2STREAM_TEST_MINIO_SECRET_KEY=hook2stream-ci-runtime-secret
HOOK2STREAM_TEST_MINIO_BOOTSTRAP_ACCESS_KEY=hook2stream-ci-bootstrap
HOOK2STREAM_TEST_MINIO_BOOTSTRAP_SECRET_KEY=hook2stream-ci-bootstrap-secret
HOOK2STREAM_TEST_MINIO_BUCKET=hook2stream-staging-media
HOOK2STREAM_TEST_MINIO_BACKUP_BUCKET=hook2stream-staging-pg-backups
HOOK2STREAM_TEST_MINIO_BROWSER_ORIGIN=https://app.localhost
SSL_CERT_FILE=${trusted_ca_certificate}
NO_PROXY=localhost,127.0.0.1,.localhost
EOF

printf '%s\n' \
    "MinIO acceptance setup: real init x2, state, IAM, HTTPS, CORS, and private routes passed"

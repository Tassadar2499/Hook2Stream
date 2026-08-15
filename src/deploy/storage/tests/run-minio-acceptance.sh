#!/bin/sh
set -eu
set -f

storage_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
repository_root=$(CDPATH= cd -- "$storage_dir/../../.." && pwd)
. "$storage_dir/scripts/lib/storage-common.sh"

fail() { printf '%s\n' "storage live acceptance: $*" >&2; exit 1; }
require_command() { command -v "$1" >/dev/null 2>&1 || fail "$1 is required"; }
: "${MINIO_IMAGE:?MINIO_IMAGE must be the published image@sha256 reference}"
: "${MINIO_MC_IMAGE:?MINIO_MC_IMAGE must be the reviewed image@sha256 reference}"
: "${CADDY_IMAGE:?CADDY_IMAGE must be the reviewed image@sha256 reference}"
storage_validate_digest_image MINIO_IMAGE "$MINIO_IMAGE"
storage_validate_digest_image MINIO_MC_IMAGE "$MINIO_MC_IMAGE"
storage_validate_digest_image CADDY_IMAGE "$CADDY_IMAGE"
case "$MINIO_MC_IMAGE" in minio/mc@sha256:*|docker.io/minio/mc@sha256:*) ;; *) fail "MINIO_MC_IMAGE is not official minio/mc" ;; esac
case "$CADDY_IMAGE" in caddy@sha256:*|docker.io/library/caddy@sha256:*) ;; *) fail "CADDY_IMAGE is not official caddy" ;; esac
for command_name in cmp curl docker dotnet find grep jq mktemp node openssl sha256sum truncate; do require_command "$command_name"; done
docker compose version >/dev/null 2>&1 || fail "Docker Compose v2 is required"
[ "$(dotnet --version)" = 10.0.302 ] || fail "the pinned .NET SDK 10.0.302 is required"

acceptance_root=$(mktemp -d "${RUNNER_TEMP:-/tmp}/hook2stream-storage-live.XXXXXX")
acceptance_gid=$(id -g)
acceptance_projects="staging:hook2stream-storage-acceptance-staging-$$ production:hook2stream-storage-acceptance-production-$$"
acceptance_postgres_container=

compose_for() {
    acceptance_compose_environment=$1
    acceptance_compose_project=$2
    shift 2
    docker compose \
        --project-directory "$storage_dir" \
        --env-file "$acceptance_root/$acceptance_compose_environment/acceptance.env" \
        -p "$acceptance_compose_project" \
        -f "$storage_dir/compose.yaml" \
        "$@"
}

cleanup() {
    cleanup_status=$?
    if [ -n "$acceptance_postgres_container" ]; then
        docker rm -f "$acceptance_postgres_container" >/dev/null 2>&1 || true
    fi
    for cleanup_pair in $acceptance_projects; do
        cleanup_environment=${cleanup_pair%%:*}
        cleanup_project=${cleanup_pair#*:}
        if [ -f "$acceptance_root/$cleanup_environment/acceptance.env" ]; then
            compose_for "$cleanup_environment" "$cleanup_project" down --volumes --remove-orphans >/dev/null 2>&1 || true
            if [ -d "$acceptance_root/$cleanup_environment/data" ]; then
                docker run --rm --user 0:0 --read-only --cap-drop ALL \
                    --security-opt no-new-privileges=true \
                    --mount "type=bind,source=$acceptance_root/$cleanup_environment/data,target=/cleanup" \
                    --entrypoint /bin/sh "$MINIO_IMAGE" \
                    -c 'find /cleanup -mindepth 1 -delete' >/dev/null 2>&1 || true
            fi
        fi
    done
    rm -rf "$acceptance_root"
    trap - EXIT HUP INT TERM
    exit "$cleanup_status"
}
trap cleanup EXIT HUP INT TERM

write_secret() {
    acceptance_secret_path=$1
    acceptance_secret_value=$2
    (umask 027 && printf '%s\n' "$acceptance_secret_value" > "$acceptance_secret_path")
}

write_acceptance_inventory() {
    acceptance_inventory_environment=$1
    acceptance_inventory_runtime=$2
    case "$acceptance_inventory_environment" in
        staging) acceptance_inventory_prefix=stg ;;
        production) acceptance_inventory_prefix=prd ;;
        *) fail "unknown inventory environment" ;;
    esac
    if [ "$acceptance_inventory_runtime" = - ]; then
        acceptance_inventory_bootstrap=-
        acceptance_inventory_backup=-
    else
        acceptance_inventory_bootstrap=${acceptance_inventory_prefix}bootstrap00000001
        acceptance_inventory_backup=${acceptance_inventory_prefix}backup00000000001
    fi
    acceptance_inventory_path=$acceptance_root/$acceptance_inventory_environment/managed-identities.v1
    {
        printf '%s\n' HOOK2STREAM_STORAGE_MANAGED_IDENTITIES_V1
        printf 'bootstrap=%s\n' "$acceptance_inventory_bootstrap"
        printf 'runtime=%s\n' "$acceptance_inventory_runtime"
        printf 'backup=%s\n' "$acceptance_inventory_backup"
    } > "$acceptance_inventory_path"
    chmod 0600 "$acceptance_inventory_path"
}

wait_healthy() {
    acceptance_wait_environment=$1
    acceptance_wait_project=$2
    acceptance_wait_service=${3:-minio}
    acceptance_wait_attempts=60
    while [ "$acceptance_wait_attempts" -gt 0 ]; do
        acceptance_wait_container=$(compose_for "$acceptance_wait_environment" "$acceptance_wait_project" ps -q "$acceptance_wait_service")
        if [ -n "$acceptance_wait_container" ]; then
            acceptance_wait_health=$(docker inspect --format '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' \
                "$acceptance_wait_container" 2>/dev/null || true)
            [ "$acceptance_wait_health" = healthy ] && return 0
            [ "$acceptance_wait_health" = exited ] || [ "$acceptance_wait_health" = dead ] \
                && fail "$acceptance_wait_environment $acceptance_wait_service exited before becoming healthy"
        fi
        acceptance_wait_attempts=$((acceptance_wait_attempts - 1))
        sleep 2
    done
    fail "$acceptance_wait_environment $acceptance_wait_service did not become healthy"
}

create_environment() {
    acceptance_environment=$1
    case "$acceptance_environment" in
        staging)
            acceptance_media=hook2stream-staging-media
            acceptance_backup=hook2stream-staging-pg-backups
            acceptance_prefix=hook2stream/staging/postgres
            acceptance_media_quota=35
            acceptance_backup_quota=10
            acceptance_retention=7
            acceptance_tailscale_ip=127.0.0.2
            acceptance_credential_prefix=stg
            ;;
        production)
            acceptance_media=hook2stream-production-media
            acceptance_backup=hook2stream-production-pg-backups
            acceptance_prefix=hook2stream/production/postgres
            acceptance_media_quota=160
            acceptance_backup_quota=30
            acceptance_retention=35
            acceptance_tailscale_ip=127.0.0.3
            acceptance_credential_prefix=prd
            ;;
        *) fail "unknown acceptance environment" ;;
    esac
    acceptance_environment_dir=$acceptance_root/$acceptance_environment
    acceptance_secrets=$acceptance_environment_dir/secrets/current
    mkdir -p "$acceptance_environment_dir/data" "$acceptance_environment_dir/results" \
        "$acceptance_environment_dir/retired" "$acceptance_secrets"
    chmod 0777 "$acceptance_environment_dir/data" "$acceptance_environment_dir/results"
    chmod 0750 "$acceptance_environment_dir/secrets" "$acceptance_secrets"
    write_secret "$acceptance_secrets/minio_root_user" "${acceptance_credential_prefix}root0000000000001"
    write_secret "$acceptance_secrets/minio_root_password" "${acceptance_credential_prefix}-root-secret-0001-acceptance"
    write_secret "$acceptance_secrets/s3_bootstrap_access_key" "${acceptance_credential_prefix}bootstrap00000001"
    write_secret "$acceptance_secrets/s3_bootstrap_secret_key" "${acceptance_credential_prefix}-bootstrap-secret-0002-acceptance"
    write_secret "$acceptance_secrets/s3_runtime_access_key" "${acceptance_credential_prefix}runtime0000000001"
    write_secret "$acceptance_secrets/s3_runtime_secret_key" "${acceptance_credential_prefix}-runtime-secret-0003-acceptance"
    write_secret "$acceptance_secrets/backup_s3_access_key" "${acceptance_credential_prefix}backup00000000001"
    write_secret "$acceptance_secrets/backup_s3_secret_key" "${acceptance_credential_prefix}-backup-secret-0004-acceptance"
    write_secret "$acceptance_environment_dir/retired/runtime_access_key" \
        "${acceptance_credential_prefix}runtime0000000001"
    write_secret "$acceptance_environment_dir/retired/runtime_secret_key" \
        "${acceptance_credential_prefix}-runtime-secret-0003-acceptance"
    write_acceptance_inventory "$acceptance_environment" -
    acceptance_tls_name=h2s-storage-$acceptance_environment.acceptance.ts.net
    openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 1 \
        -subj "/CN=$acceptance_tls_name" \
        -addext "subjectAltName=DNS:$acceptance_tls_name" \
        -keyout "$acceptance_secrets/storage-tls.key" \
        -out "$acceptance_secrets/storage-tls.crt" >/dev/null 2>&1
    openssl x509 -in "$acceptance_secrets/storage-tls.crt" -noout -checkend 60 >/dev/null \
        || fail "$acceptance_environment acceptance certificate is invalid"
    chmod 0640 "$acceptance_secrets"/*
    cat > "$acceptance_environment_dir/acceptance.env" <<EOF
DEPLOYMENT_ENVIRONMENT=$acceptance_environment
STORAGE_PROTOCOL_VERSION=1
STORAGE_OBJECT_FORMAT=H2SEv1
STORAGE_TLS_SERVER_NAME=$acceptance_tls_name
TAILSCALE_IPV4=$acceptance_tailscale_ip
MINIO_REGION=us-east-1
MINIO_MEDIA_BUCKET=$acceptance_media
MINIO_BACKUP_BUCKET=$acceptance_backup
MINIO_BACKUP_PREFIX=$acceptance_prefix
MINIO_MEDIA_QUOTA_GIB=$acceptance_media_quota
MINIO_BACKUP_QUOTA_GIB=$acceptance_backup_quota
BACKUP_RETENTION_DAYS=$acceptance_retention
STORAGE_DATA_DIR=$acceptance_environment_dir/data
SECRETS_DIR=$acceptance_secrets
SECRETS_GID=$acceptance_gid
MANAGED_IDENTITY_INVENTORY_FILE=$acceptance_environment_dir/managed-identities.v1
TLS_CERT_FILE=$acceptance_secrets/storage-tls.crt
TLS_KEY_FILE=$acceptance_secrets/storage-tls.key
STORAGE_RELEASE_VERSION=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
MINIO_IMAGE=$MINIO_IMAGE
MINIO_MC_IMAGE=$MINIO_MC_IMAGE
CADDY_IMAGE=$CADDY_IMAGE
EOF
    chmod 0600 "$acceptance_environment_dir/acceptance.env"
}

verify_caddy() {
    acceptance_caddy_environment=$1
    acceptance_caddy_project=$2
    acceptance_caddy_container=$(compose_for "$acceptance_caddy_environment" "$acceptance_caddy_project" ps -q caddy)
    [ -n "$acceptance_caddy_container" ] \
        || fail "$acceptance_caddy_environment Caddy container is absent"
    [ "$(docker inspect --format '{{.Config.Image}}' "$acceptance_caddy_container")" = "$CADDY_IMAGE" ] \
        || fail "$acceptance_caddy_environment is not running the accepted Caddy digest"
    case "$acceptance_caddy_environment" in
        staging) acceptance_caddy_ip=127.0.0.2 ;;
        production) acceptance_caddy_ip=127.0.0.3 ;;
    esac
    acceptance_caddy_name=h2s-storage-$acceptance_caddy_environment.acceptance.ts.net
    acceptance_caddy_bindings=$(docker inspect --format '{{json .HostConfig.PortBindings}}' "$acceptance_caddy_container")
    printf '%s\n' "$acceptance_caddy_bindings" | jq -e --arg ip "$acceptance_caddy_ip" '
        keys == ["443/tcp"] and
        .["443/tcp"] == [{"HostIp":$ip,"HostPort":"443"}]
    ' >/dev/null || fail "$acceptance_caddy_environment Caddy is not bound only to test-Tailscale TCP 443"
    [ "$(docker port "$acceptance_caddy_container" 443/tcp)" = "$acceptance_caddy_ip:443" ] \
        || fail "$acceptance_caddy_environment Caddy TCP binding differs"
    if docker port "$acceptance_caddy_container" 443/udp >/dev/null 2>&1; then
        fail "$acceptance_caddy_environment Caddy unexpectedly publishes UDP 443"
    fi
    acceptance_caddy_cert=$acceptance_root/$acceptance_caddy_environment/secrets/current/storage-tls.crt
    acceptance_protocol_body=$(curl --fail --silent --show-error --max-time 10 --noproxy '*' \
        --cacert "$acceptance_caddy_cert" \
        --resolve "$acceptance_caddy_name:443:$acceptance_caddy_ip" \
        "https://$acceptance_caddy_name/.well-known/hook2stream-storage-protocol")
    [ "$acceptance_protocol_body" = 1 ] \
        || fail "$acceptance_caddy_environment HTTPS protocol endpoint did not return exact body 1"
    curl --fail --silent --show-error --max-time 10 --noproxy '*' \
        --cacert "$acceptance_caddy_cert" \
        --resolve "$acceptance_caddy_name:443:$acceptance_caddy_ip" \
        "https://$acceptance_caddy_name/healthz" >/dev/null \
        || fail "$acceptance_caddy_environment HTTPS reverse-proxy health failed"
    for acceptance_private_path in \
        /minio/admin/v3/info \
        /minio/storage/acceptance-internal-route \
        /minio/health/ready; do
        acceptance_private_status=$(curl --silent --show-error --max-time 10 --noproxy '*' \
            --cacert "$acceptance_caddy_cert" \
            --resolve "$acceptance_caddy_name:443:$acceptance_caddy_ip" \
            --output /dev/null --write-out '%{http_code}' \
            "https://$acceptance_caddy_name$acceptance_private_path")
        [ "$acceptance_private_status" = 404 ] \
            || fail "$acceptance_caddy_environment exposed internal route $acceptance_private_path"
    done
}

verify_state() {
    acceptance_state_environment=$1
    acceptance_state_project=$2
    acceptance_state_results=$acceptance_root/$acceptance_state_environment/results
    find "$acceptance_state_results" -mindepth 1 -delete
    compose_for "$acceptance_state_environment" "$acceptance_state_project" --profile tools run --rm --no-deps -T \
        --entrypoint /bin/sh \
        -v "$acceptance_state_results:/results" \
        minio-init -s < "$storage_dir/tests/minio-live-state.sh"
    case "$acceptance_state_environment" in
        staging) acceptance_state_media_quota=35; acceptance_state_backup_quota=10 ;;
        production) acceptance_state_media_quota=160; acceptance_state_backup_quota=30 ;;
    esac
    grep -i 'versioning is suspended' "$acceptance_state_results/media-version.txt" >/dev/null \
        || fail "$acceptance_state_environment media versioning is not suspended"
    grep -i 'versioning is enabled' "$acceptance_state_results/backup-version.txt" >/dev/null \
        || fail "$acceptance_state_environment backup versioning is not enabled"
    grep -Ei "(Quota:|hard quota of)[^0-9]*${acceptance_state_media_quota}[[:space:]]+GiB" \
        "$acceptance_state_results/media-quota.txt" >/dev/null \
        || fail "$acceptance_state_environment media quota differs"
    grep -Ei "(Quota:|hard quota of)[^0-9]*${acceptance_state_backup_quota}[[:space:]]+GiB" \
        "$acceptance_state_results/backup-quota.txt" >/dev/null \
        || fail "$acceptance_state_environment backup quota differs"
    jq -S '.Rules |= sort_by(.ID)' "$storage_dir/lifecycle/$acceptance_state_environment-media.json" \
        > "$acceptance_state_results/expected-media-ilm.json"
    jq -S '.Rules |= sort_by(.ID)' "$acceptance_state_results/media-ilm.json" \
        > "$acceptance_state_results/actual-media-ilm.json"
    cmp -s "$acceptance_state_results/expected-media-ilm.json" "$acceptance_state_results/actual-media-ilm.json" \
        || fail "$acceptance_state_environment media lifecycle differs from the exact source"
    jq -S '.Rules |= sort_by(.ID)' "$storage_dir/lifecycle/$acceptance_state_environment-backup.json" \
        > "$acceptance_state_results/expected-backup-ilm.json"
    jq -S '.Rules |= sort_by(.ID)' "$acceptance_state_results/backup-ilm.json" \
        > "$acceptance_state_results/actual-backup-ilm.json"
    cmp -s "$acceptance_state_results/expected-backup-ilm.json" "$acceptance_state_results/actual-backup-ilm.json" \
        || fail "$acceptance_state_environment backup lifecycle differs from the exact source"
    [ "$(grep -ci 'private' "$acceptance_state_results/anonymous.txt")" -eq 2 ] \
        || fail "$acceptance_state_environment buckets are not both private"
    for acceptance_identity in runtime bootstrap backup; do
        case "$acceptance_identity" in
            runtime) acceptance_policy=hook2stream-$acceptance_state_environment-runtime-media ;;
            bootstrap) acceptance_policy=hook2stream-$acceptance_state_environment-bootstrap-media ;;
            backup) acceptance_policy=hook2stream-$acceptance_state_environment-postgres-backup ;;
        esac
        jq -e --arg policy "$acceptance_policy" '
            .status == "success" and .policyName == $policy and
            .userStatus == "enabled" and (.memberOf // []) == []
        ' "$acceptance_state_results/$acceptance_identity-user.json" >/dev/null \
            || fail "$acceptance_state_environment $acceptance_identity identity has a stale policy or group"
    done
}

run_h2se_acceptance() {
    acceptance_h2se_environment=$1
    acceptance_h2se_project=$2
    [ "$acceptance_h2se_environment" = staging ] \
        || fail "H2SE acceptance must use the staging test topology"
    acceptance_postgres_source=postgres:17.10-alpine3.24
    docker pull --platform linux/amd64 "$acceptance_postgres_source" >/dev/null
    acceptance_postgres_image=$(docker image inspect --format '{{index .RepoDigests 0}}' "$acceptance_postgres_source")
    case "$acceptance_postgres_image" in
        postgres@sha256:????????????????????????????????????????????????????????????????|docker.io/library/postgres@sha256:????????????????????????????????????????????????????????????????) ;;
        *) fail "PostgreSQL acceptance dependency did not resolve to an official immutable digest" ;;
    esac
    acceptance_postgres_container=hook2stream-storage-h2se-postgres-$$
    docker run --detach --rm \
        --name "$acceptance_postgres_container" \
        --network "${acceptance_h2se_project}_storage" \
        --network-alias postgres \
        --shm-size 128m \
        --tmpfs /var/lib/postgresql/data:rw,nosuid,nodev,size=512m \
        --tmpfs /var/run/postgresql:rw,nosuid,nodev,size=16m \
        --env POSTGRES_PASSWORD=hook2stream-h2se-acceptance \
        --env POSTGRES_DB=postgres \
        "$acceptance_postgres_image" >/dev/null
    acceptance_postgres_attempts=60
    while [ "$acceptance_postgres_attempts" -gt 0 ]; do
        if docker exec "$acceptance_postgres_container" pg_isready -U postgres -d postgres >/dev/null 2>&1; then
            break
        fi
        acceptance_postgres_attempts=$((acceptance_postgres_attempts - 1))
        sleep 2
    done
    [ "$acceptance_postgres_attempts" -gt 0 ] \
        || fail "H2SE PostgreSQL lock dependency did not become ready"
    [ -z "$(docker port "$acceptance_postgres_container" 2>/dev/null)" ] \
        || fail "H2SE PostgreSQL dependency published a host port"

    acceptance_h2se_minio_container=$(compose_for "$acceptance_h2se_environment" "$acceptance_h2se_project" ps -q minio)
    acceptance_h2se_minio_ip=$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' \
        "$acceptance_h2se_minio_container")
    acceptance_h2se_postgres_ip=$(docker inspect --format '{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}' \
        "$acceptance_postgres_container")
    [ -n "$acceptance_h2se_minio_ip" ] && [ -n "$acceptance_h2se_postgres_ip" ] \
        || fail "H2SE dependency address is empty"
    case "$acceptance_h2se_minio_ip:$acceptance_h2se_postgres_ip" in
        *[!0-9.:]*) fail "H2SE dependency address is invalid" ;;
    esac
    curl --fail --silent --show-error --max-time 10 \
        "http://$acceptance_h2se_minio_ip:9000/minio/health/ready" >/dev/null \
        || fail "the host test process cannot reach exact-digest MinIO without a published port"

    CI=true \
    HOOK2STREAM_TEST_MINIO="http://$acceptance_h2se_minio_ip:9000" \
    HOOK2STREAM_TEST_MINIO_ACCESS_KEY=stgruntime0000000003 \
    HOOK2STREAM_TEST_MINIO_SECRET_KEY=stg-runtime-secret-0006-acceptance \
    HOOK2STREAM_TEST_MINIO_BUCKET=hook2stream-staging-media \
    HOOK2STREAM_TEST_POSTGRES="Host=$acceptance_h2se_postgres_ip;Port=5432;Database=postgres;Username=postgres;Password=hook2stream-h2se-acceptance" \
    dotnet test "$repository_root/src/tests/Hook2Stream.IntegrationTests/Hook2Stream.IntegrationTests.csproj" \
        --configuration Release \
        --filter 'FullyQualifiedName=Hook2Stream.IntegrationTests.S3ObjectStorageMinioTests.H2se_round_trips_ranges_and_never_persists_plaintext_in_real_minio' \
        --logger 'console;verbosity=normal'
    docker rm -f "$acceptance_postgres_container" >/dev/null
    acceptance_postgres_container=
    printf '%s\n' "storage live acceptance: H2SE upload/range/download/ciphertext PASS"
}

run_environment() {
    acceptance_run_environment=$1
    acceptance_run_project=$2
    create_environment "$acceptance_run_environment"
    compose_for "$acceptance_run_environment" "$acceptance_run_project" --profile tools pull minio minio-init caddy >/dev/null
    acceptance_source_release=$(jq -r .minioRelease "$storage_dir/storage-release.json")
    acceptance_source_commit=$(jq -r .minioSourceCommit "$storage_dir/storage-release.json")
    [ "$(docker image inspect --format '{{index .Config.Labels "com.hook2stream.minio.source-release"}}' "$MINIO_IMAGE")" \
        = "$acceptance_source_release" ] \
        || fail "$acceptance_run_environment MinIO image release label differs from the manifest"
    [ "$(docker image inspect --format '{{index .Config.Labels "com.hook2stream.minio.source-commit"}}' "$MINIO_IMAGE")" \
        = "$acceptance_source_commit" ] \
        || fail "$acceptance_run_environment MinIO image source-commit label differs from the manifest"
    compose_for "$acceptance_run_environment" "$acceptance_run_project" up -d --no-deps minio >/dev/null
    wait_healthy "$acceptance_run_environment" "$acceptance_run_project"
    acceptance_run_container=$(compose_for "$acceptance_run_environment" "$acceptance_run_project" ps -q minio)
    [ -n "$acceptance_run_container" ] || fail "$acceptance_run_environment MinIO container is absent"
    [ "$(docker inspect --format '{{.Config.Image}}' "$acceptance_run_container")" = "$MINIO_IMAGE" ] \
        || fail "$acceptance_run_environment is not running the accepted MinIO digest"
    [ -z "$(docker port "$acceptance_run_container" 2>/dev/null)" ] \
        || fail "$acceptance_run_environment published a MinIO host port"
    [ "$(docker network inspect --format '{{.Internal}}' "${acceptance_run_project}_storage")" = true ] \
        || fail "$acceptance_run_environment storage network is not internal"
    if docker exec "$acceptance_run_container" wget -q -O /dev/null http://127.0.0.1:9001/ >/dev/null 2>&1; then
        fail "$acceptance_run_environment MinIO console is listening"
    fi

    # The first run establishes the exact identities. Persist its host-side
    # inventory, grant runtime a real broad built-in policy, then rotate that
    # access-key ID. The second run must retire the old broad identity, create
    # only the new scoped identity, and remain idempotent.
    compose_for "$acceptance_run_environment" "$acceptance_run_project" --profile tools run --rm --no-deps -T minio-init \
        < "$acceptance_root/$acceptance_run_environment/managed-identities.v1" >/dev/null
    case "$acceptance_run_environment" in
        staging) acceptance_run_prefix=stg ;;
        production) acceptance_run_prefix=prd ;;
    esac
    write_acceptance_inventory "$acceptance_run_environment" \
        "${acceptance_run_prefix}runtime0000000001"
    compose_for "$acceptance_run_environment" "$acceptance_run_project" --profile tools run --rm --no-deps -T \
        --entrypoint /bin/sh minio-init -s < "$storage_dir/tests/minio-live-inject-stale-policy.sh"
    write_secret "$acceptance_root/$acceptance_run_environment/secrets/current/s3_runtime_access_key" \
        "${acceptance_run_prefix}runtime0000000002"
    write_secret "$acceptance_root/$acceptance_run_environment/secrets/current/s3_runtime_secret_key" \
        "${acceptance_run_prefix}-runtime-secret-0005-acceptance"
    compose_for "$acceptance_run_environment" "$acceptance_run_project" --profile tools run --rm --no-deps -T minio-init \
        < "$acceptance_root/$acceptance_run_environment/managed-identities.v1" >/dev/null
    # Production persists B immediately after init, before any probe. Mirror
    # that transaction, then force a downstream failure and rotate once more;
    # C init must use the persisted B inventory to revoke the failed-attempt ID.
    write_acceptance_inventory "$acceptance_run_environment" \
        "${acceptance_run_prefix}runtime0000000002"
    compose_for "$acceptance_run_environment" "$acceptance_run_project" --profile tools run --rm --no-deps -T \
        --entrypoint /bin/sh \
        -v "$acceptance_root/$acceptance_run_environment/retired:/retired:ro" \
        minio-init -s < "$storage_dir/tests/minio-live-retired-identity-deny.sh"
    if compose_for "$acceptance_run_environment" "$acceptance_run_project" --profile tools run --rm --no-deps -T \
        --entrypoint /bin/sh minio-init -c 'printf "%s\n" "simulated post-init probe failure" >&2; exit 42'; then
        fail "$acceptance_run_environment simulated post-init probe unexpectedly succeeded"
    fi
    write_secret "$acceptance_root/$acceptance_run_environment/retired/runtime_access_key" \
        "${acceptance_run_prefix}runtime0000000002"
    write_secret "$acceptance_root/$acceptance_run_environment/retired/runtime_secret_key" \
        "${acceptance_run_prefix}-runtime-secret-0005-acceptance"
    write_secret "$acceptance_root/$acceptance_run_environment/secrets/current/s3_runtime_access_key" \
        "${acceptance_run_prefix}runtime0000000003"
    write_secret "$acceptance_root/$acceptance_run_environment/secrets/current/s3_runtime_secret_key" \
        "${acceptance_run_prefix}-runtime-secret-0006-acceptance"
    compose_for "$acceptance_run_environment" "$acceptance_run_project" --profile tools run --rm --no-deps -T minio-init \
        < "$acceptance_root/$acceptance_run_environment/managed-identities.v1" >/dev/null
    write_acceptance_inventory "$acceptance_run_environment" \
        "${acceptance_run_prefix}runtime0000000003"
    compose_for "$acceptance_run_environment" "$acceptance_run_project" --profile tools run --rm --no-deps -T \
        --entrypoint /bin/sh \
        -v "$acceptance_root/$acceptance_run_environment/retired:/retired:ro" \
        minio-init -s < "$storage_dir/tests/minio-live-retired-identity-deny.sh"
    compose_for "$acceptance_run_environment" "$acceptance_run_project" --profile tools run --rm --no-deps \
        --entrypoint /bin/sh minio-init /opt/hook2stream/minio-auth-healthcheck.sh >/dev/null
    compose_for "$acceptance_run_environment" "$acceptance_run_project" --profile tools run --rm --no-deps \
        --entrypoint /bin/sh minio-init /opt/hook2stream/minio-policy-isolation-probe.sh >/dev/null
    verify_state "$acceptance_run_environment" "$acceptance_run_project"
    compose_for "$acceptance_run_environment" "$acceptance_run_project" up -d --no-deps caddy >/dev/null
    wait_healthy "$acceptance_run_environment" "$acceptance_run_project" caddy
    verify_caddy "$acceptance_run_environment" "$acceptance_run_project"

    acceptance_fixture=$acceptance_root/$acceptance_run_environment/multipart.bin
    truncate -s 128M "$acceptance_fixture"
    compose_for "$acceptance_run_environment" "$acceptance_run_project" --profile tools run --rm --no-deps -T \
        --entrypoint /bin/sh \
        -v "$acceptance_fixture:/fixtures/multipart.bin:ro" \
        minio-init -s < "$storage_dir/tests/minio-live-multipart-abort.sh"
    rm -f "$acceptance_fixture"

    compose_for "$acceptance_run_environment" "$acceptance_run_project" restart minio >/dev/null
    wait_healthy "$acceptance_run_environment" "$acceptance_run_project"
    compose_for "$acceptance_run_environment" "$acceptance_run_project" --profile tools run --rm --no-deps \
        --entrypoint /bin/sh minio-init /opt/hook2stream/minio-auth-healthcheck.sh >/dev/null
    compose_for "$acceptance_run_environment" "$acceptance_run_project" --profile tools run --rm --no-deps \
        --entrypoint /bin/sh minio-init /opt/hook2stream/minio-policy-isolation-probe.sh >/dev/null
    verify_state "$acceptance_run_environment" "$acceptance_run_project"
    verify_caddy "$acceptance_run_environment" "$acceptance_run_project"
    if [ "$acceptance_run_environment" = staging ]; then
        run_h2se_acceptance "$acceptance_run_environment" "$acceptance_run_project"
    fi
    printf '%s\n' "storage live acceptance: $acceptance_run_environment PASS"
}

for acceptance_pair in $acceptance_projects; do
    run_environment "${acceptance_pair%%:*}" "${acceptance_pair#*:}"
done
docker image inspect "$MINIO_MC_IMAGE" >/dev/null 2>&1 \
    || fail "accepted MinIO client digest is not present"
printf '%s\n' "storage live acceptance: exact digests and both topologies PASS"

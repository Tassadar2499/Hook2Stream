#!/bin/sh
set -eu
set -f

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_dir/lib/storage-common.sh"

env_file=${1:-${STORAGE_ACTIVE_ENV_FILE:-}}
[ -n "$env_file" ] || storage_fail "validate-config requires an environment file"
[ "$(id -u)" -eq 0 ] || storage_fail "configuration validation must run as root"
storage_validate_strict_env "$env_file"
[ "$(stat -c '%u:%a' "$env_file")" = 0:600 ] \
    || storage_fail "environment must be root-owned mode 0600"

required_keys='DEPLOYMENT_ENVIRONMENT STORAGE_PROTOCOL_VERSION STORAGE_OBJECT_FORMAT STORAGE_TLS_SERVER_NAME TAILSCALE_IPV4 MINIO_REGION MINIO_MEDIA_BUCKET MINIO_BACKUP_BUCKET MINIO_BACKUP_PREFIX MINIO_MEDIA_QUOTA_GIB MINIO_BACKUP_QUOTA_GIB BACKUP_RETENTION_DAYS STORAGE_DATA_DIR SECRETS_DIR SECRETS_GID MANAGED_IDENTITY_INVENTORY_FILE TLS_CERT_FILE TLS_KEY_FILE STORAGE_RELEASE_VERSION MINIO_IMAGE MINIO_MC_IMAGE CADDY_IMAGE'
for required_key in $required_keys; do storage_env_value "$env_file" "$required_key" >/dev/null; done

set -a
. "$env_file"
set +a

[ "$STORAGE_PROTOCOL_VERSION" = 1 ] || storage_fail "only storage protocol v1 is supported"
[ "$STORAGE_OBJECT_FORMAT" = H2SEv1 ] || storage_fail "only H2SEv1 is supported"
[ "$MINIO_REGION" = us-east-1 ] || storage_fail "MINIO_REGION must be us-east-1"
case "$STORAGE_RELEASE_VERSION" in *[!0-9a-f]*|'') storage_fail "STORAGE_RELEASE_VERSION is invalid" ;; esac
[ "${#STORAGE_RELEASE_VERSION}" -eq 40 ] || storage_fail "STORAGE_RELEASE_VERSION must be a full commit SHA"

case "$DEPLOYMENT_ENVIRONMENT" in
    staging)
        expected_name_label=h2s-storage-staging
        expected_media=hook2stream-staging-media
        expected_backup=hook2stream-staging-pg-backups
        expected_prefix=hook2stream/staging/postgres
        expected_media_quota=35
        expected_backup_quota=10
        expected_retention=7
        ;;
    production)
        expected_name_label=h2s-storage-production
        expected_media=hook2stream-production-media
        expected_backup=hook2stream-production-pg-backups
        expected_prefix=hook2stream/production/postgres
        expected_media_quota=160
        expected_backup_quota=30
        expected_retention=35
        ;;
    *) storage_fail "DEPLOYMENT_ENVIRONMENT must be staging or production" ;;
esac
printf '%s\n' "$STORAGE_TLS_SERVER_NAME" \
    | grep -E "^${expected_name_label}\.[a-z0-9-]+\.ts\.net$" >/dev/null \
    || storage_fail "STORAGE_TLS_SERVER_NAME must contain exactly one tailnet label"
[ "$MINIO_MEDIA_BUCKET" = "$expected_media" ] || storage_fail "media bucket differs from the exact topology"
[ "$MINIO_BACKUP_BUCKET" = "$expected_backup" ] || storage_fail "backup bucket differs from the exact topology"
[ "$MINIO_BACKUP_PREFIX" = "$expected_prefix" ] || storage_fail "backup prefix differs from the exact topology"
[ "$MINIO_MEDIA_QUOTA_GIB" = "$expected_media_quota" ] || storage_fail "media quota differs from the exact topology"
[ "$MINIO_BACKUP_QUOTA_GIB" = "$expected_backup_quota" ] || storage_fail "backup quota differs from the exact topology"
[ "$BACKUP_RETENTION_DAYS" = "$expected_retention" ] || storage_fail "backup retention differs from the exact topology"

case "$TAILSCALE_IPV4" in *[!0-9.]*|''|*.*.*.*.*) storage_fail "TAILSCALE_IPV4 is invalid" ;; esac
old_ifs=$IFS
IFS=.
set -- $TAILSCALE_IPV4
IFS=$old_ifs
[ "$#" -eq 4 ] || storage_fail "TAILSCALE_IPV4 is invalid"
for octet in "$@"; do
    case "$octet" in ''|*[!0-9]*) storage_fail "TAILSCALE_IPV4 is invalid" ;; esac
    [ "$octet" -le 255 ] || storage_fail "TAILSCALE_IPV4 is invalid"
done
ip -4 -o addr show dev tailscale0 | awk '{print $4}' | cut -d/ -f1 | grep -Fx "$TAILSCALE_IPV4" >/dev/null \
    || storage_fail "TAILSCALE_IPV4 is not assigned to tailscale0"

storage_validate_digest_image MINIO_IMAGE "$MINIO_IMAGE"
storage_validate_digest_image MINIO_MC_IMAGE "$MINIO_MC_IMAGE"
storage_validate_digest_image CADDY_IMAGE "$CADDY_IMAGE"
case "$MINIO_MC_IMAGE" in minio/mc@sha256:*|docker.io/minio/mc@sha256:*) ;; *) storage_fail "MINIO_MC_IMAGE must use the official repository" ;; esac
case "$CADDY_IMAGE" in caddy@sha256:*|docker.io/library/caddy@sha256:*) ;; *) storage_fail "CADDY_IMAGE must use the official repository" ;; esac

[ "$STORAGE_DATA_DIR" = /srv/hook2stream-storage/minio-data ] \
    || storage_fail "STORAGE_DATA_DIR must use the canonical encrypted mount"
[ -d "$STORAGE_DATA_DIR" ] && [ ! -L "$STORAGE_DATA_DIR" ] \
    && [ "$(stat -c '%u:%g:%a' "$STORAGE_DATA_DIR")" = 10001:10001:750 ] \
    || storage_fail "MinIO data directory must be 10001:10001 mode 0750"
[ "$SECRETS_DIR" = /srv/hook2stream-storage/secrets/current ] \
    || storage_fail "SECRETS_DIR must use the canonical current generation"
case "$SECRETS_GID" in ''|*[!0-9]*) storage_fail "SECRETS_GID is invalid" ;; esac
[ -d "$SECRETS_DIR" ] && [ ! -L "$SECRETS_DIR" ] \
    && [ "$(stat -c '%u:%g:%a' "$SECRETS_DIR")" = "0:$SECRETS_GID:750" ] \
    || storage_fail "secrets directory must be root:SECRETS_GID mode 0750"
[ "$MANAGED_IDENTITY_INVENTORY_FILE" = /srv/hook2stream-storage/release-state/managed-identities.v1 ] \
    || storage_fail "managed identity inventory path is not canonical"
[ -f "$MANAGED_IDENTITY_INVENTORY_FILE" ] && [ ! -L "$MANAGED_IDENTITY_INVENTORY_FILE" ] \
    && [ "$(stat -c '%u:%g:%a' "$MANAGED_IDENTITY_INVENTORY_FILE")" = 0:0:600 ] \
    || storage_fail "managed identity inventory must be root:root mode 0600"
storage_validate_managed_identity_inventory "$MANAGED_IDENTITY_INVENTORY_FILE"
if storage_managed_identity_inventory_is_empty "$MANAGED_IDENTITY_INVENTORY_FILE"; then
    [ -z "$(find "$STORAGE_DATA_DIR" -mindepth 1 -print -quit)" ] \
        || storage_fail "empty managed identity inventory cannot bootstrap non-empty MinIO data"
fi

secret_names='minio_root_user minio_root_password s3_bootstrap_access_key s3_bootstrap_secret_key s3_runtime_access_key s3_runtime_secret_key backup_s3_access_key backup_s3_secret_key'
secret_values=
for secret_name in $secret_names; do
    secret_path=$SECRETS_DIR/$secret_name
    [ -f "$secret_path" ] && [ ! -L "$secret_path" ] \
        && [ "$(stat -c '%u:%g:%a' "$secret_path")" = "0:$SECRETS_GID:640" ] \
        || storage_fail "$secret_name must be root:SECRETS_GID mode 0640"
    secret_value=$(storage_read_secret_file "$secret_path")
    storage_validate_mc_host_credential "$secret_name" "$secret_value"
    case " $secret_values " in *" $secret_value "*) storage_fail "all root/bootstrap/runtime/backup credential values must differ" ;; esac
    secret_values="$secret_values $secret_value"
done
unset secret_values secret_value

storage_require_command getent
for service_identity in \
    hook2stream-minio:10001 \
    hook2stream-storage-caddy:10002 \
    hook2stream-storage-init:10003; do
    service_name=${service_identity%%:*}
    service_id=${service_identity#*:}
    passwd_entry=$(getent passwd "$service_name" || true)
    group_entry=$(getent group "$service_name" || true)
    [ -n "$passwd_entry" ] && [ -n "$group_entry" ] \
        || storage_fail "dedicated service identity is missing: $service_name"
    printf '%s\n' "$passwd_entry" | awk -F: -v id="$service_id" '
        $3 == id && $4 == id && $6 == "/nonexistent" && $7 == "/usr/sbin/nologin" { found=1 }
        END { exit found ? 0 : 1 }
    ' || storage_fail "$service_name must be UID/GID $service_id, home /nonexistent, shell /usr/sbin/nologin"
    printf '%s\n' "$group_entry" | awk -F: -v id="$service_id" '$3 == id { found=1 } END { exit found ? 0 : 1 }' \
        || storage_fail "$service_name group must use GID $service_id"
done
deploy_uid=$(getent passwd hook2stream-storage-deploy | awk -F: '{print $3}' || true)
case "$deploy_uid" in 10001|10002|10003) storage_fail "deploy identity collides with a storage service identity" ;; esac

storage_require_command findmnt
proc_options=$(findmnt -n -o OPTIONS /proc)
storage_validate_proc_visibility "$proc_options"

for tls_path in "$TLS_CERT_FILE" "$TLS_KEY_FILE"; do
    case "$tls_path" in "$SECRETS_DIR"/*) ;; *) storage_fail "TLS material must be in the current secrets generation" ;; esac
    [ -f "$tls_path" ] && [ ! -L "$tls_path" ] \
        && [ "$(stat -c '%u:%g:%a' "$tls_path")" = "0:$SECRETS_GID:640" ] \
        || storage_fail "TLS material must be root:SECRETS_GID mode 0640"
done

storage_require_command docker
docker compose version >/dev/null 2>&1 || storage_fail "Docker Compose v2 is unavailable"
storage_require_command curl
storage_require_command jq
printf '%s\n' "storage configuration verified: $DEPLOYMENT_ENVIRONMENT" >&2

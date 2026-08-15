#!/bin/sh
# Shared deployment and Vault-secret reconciliation helpers.
# The calling script must set deployment_dir before sourcing this file.

: "${deployment_dir:?deployment_dir must be set before sourcing deployment-common.sh}"

environment_file=${HOOK2STREAM_ENV_FILE:-${deployment_dir}/.env}
health_timeout=${HOOK2STREAM_HEALTH_TIMEOUT_SECONDS:-600}
public_smoke_timeout=${HOOK2STREAM_PUBLIC_SMOKE_TIMEOUT_SECONDS:-180}
release_state_dir=${HOOK2STREAM_RELEASE_STATE_DIR:-}
secret_state_dir=${HOOK2STREAM_SECRET_STATE_DIR:-/var/lib/hook2stream/secrets}
generations_dir=${secret_state_dir}/generations
deployment_program=${deployment_program:-hook2stream-deploy}

deployment_log() {
    printf '%s\n' "${deployment_program}: $*"
}

fail() {
    printf '%s\n' "${deployment_program}: $*" >&2
    exit 1
}

read_env_value() {
    deployment_env_name=$1
    awk -v requested_name="$deployment_env_name" '
        index($0, "=") > 0 {
            name = substr($0, 1, index($0, "=") - 1)
            if (name == requested_name) {
                value = substr($0, index($0, "=") + 1)
            }
        }
        END {
            sub(/\r$/, "", value)
            print value
        }
    ' "$environment_file"
}

if [ -z "$release_state_dir" ]; then
    if [ -r "$environment_file" ]; then
        release_state_dir=$(read_env_value HOOK2STREAM_RELEASE_STATE_DIR)
    fi
    release_state_dir=${release_state_dir:-${deployment_dir}/.release-state}
fi

deployment_secret_provider() {
    if [ -n "${SECRET_PROVIDER:-}" ]; then
        printf '%s\n' "$SECRET_PROVIDER"
        return
    fi
    deployment_provider=$(read_env_value SECRET_PROVIDER)
    printf '%s\n' "${deployment_provider:-file}"
}

deployment_storage_mode() {
    deployment_mode=$(read_env_value STORAGE_MODE)
    printf '%s\n' "${deployment_mode:-external}"
}

compose() {
    deployment_compose_secret_provider=$(deployment_secret_provider)
    deployment_compose_storage_mode=$(deployment_storage_mode)
    case "${deployment_compose_secret_provider}:${deployment_compose_storage_mode}" in
        file:external)
            docker compose --env-file "$environment_file" \
                -f "$deployment_dir/compose.yaml" "$@"
            ;;
        vault:external)
            docker compose --env-file "$environment_file" \
                -f "$deployment_dir/compose.yaml" \
                -f "$deployment_dir/compose.vault.yaml" "$@"
            ;;
        file:minio)
            docker compose --env-file "$environment_file" \
                -f "$deployment_dir/compose.yaml" \
                -f "$deployment_dir/compose.minio.yaml" "$@"
            ;;
        vault:minio)
            docker compose --env-file "$environment_file" \
                -f "$deployment_dir/compose.yaml" \
                -f "$deployment_dir/compose.vault.yaml" \
                -f "$deployment_dir/compose.minio.yaml" "$@"
            ;;
        *)
            fail "SECRET_PROVIDER must be file or vault and STORAGE_MODE must be external or minio"
            ;;
    esac
}

compose_tools() {
    compose --profile tools "$@"
}

compose_vault_renderer() {
    vault_render_output=$1
    shift
    VAULT_CANDIDATE_DIR=$vault_render_output \
        docker compose --env-file "$environment_file" \
            -f "$deployment_dir/compose.yaml" \
            -f "$deployment_dir/compose.vault.yaml" \
            --profile tools "$@"
}

deployment_require_command() {
    command -v "$1" >/dev/null 2>&1 \
        || fail "$1 is required"
}

deployment_compose_input_names() {
    awk -F= '
        /^[A-Za-z_][A-Za-z0-9_]*=/ { print $1 }
    ' "$environment_file"

    for deployment_compose_source in \
        "$deployment_dir/compose.yaml" \
        "$deployment_dir/compose.vault.yaml" \
        "$deployment_dir/compose.minio.yaml"; do
        [ -r "$deployment_compose_source" ] || continue
        grep -oE '\$\{[A-Za-z_][A-Za-z0-9_]*' "$deployment_compose_source" \
            | sed 's/^${//'
    done

    env | sed -n 's/^\(COMPOSE_[A-Za-z0-9_]*\)=.*/\1/p'
}

deployment_reject_compose_environment_overrides() {
    deployment_override_names=
    for deployment_input_name in $(deployment_compose_input_names | sort -u); do
        if env | grep -q "^${deployment_input_name}="; then
            deployment_override_names="${deployment_override_names}${deployment_override_names:+ }${deployment_input_name}"
        fi
    done
    [ -z "$deployment_override_names" ] \
        || fail "unset exported Compose input overrides and configure only $environment_file: $deployment_override_names"
}

deployment_validate_timeouts() {
    case "$health_timeout:$public_smoke_timeout" in
        *[!0-9:]*|:*|*:) fail "health timeouts must be positive integers" ;;
    esac
    [ "$health_timeout" -gt 0 ] && [ "$public_smoke_timeout" -gt 0 ] \
        || fail "health timeouts must be positive integers"
}

deployment_require_base_tools() {
    [ -r "$environment_file" ] \
        || fail "environment file is not readable: $environment_file"
    deployment_reject_compose_environment_overrides
    deployment_require_command docker
    docker compose version >/dev/null 2>&1 \
        || fail "the Docker Compose plugin v2 or newer is required"
    deployment_require_command flock
    deployment_validate_timeouts
}

deployment_secret_gid() {
    if [ -n "${SECRETS_GID:-}" ]; then
        deployment_gid=$SECRETS_GID
    else
        deployment_gid=$(read_env_value SECRETS_GID)
    fi
    deployment_gid=${deployment_gid:-2000}
    case "$deployment_gid" in
        *[!0-9]*|'') fail "SECRETS_GID must be a positive numeric group ID" ;;
    esac
    [ "$deployment_gid" -gt 0 ] \
        || fail "SECRETS_GID must be a positive numeric group ID"
    printf '%s\n' "$deployment_gid"
}

deployment_required_secret_files() {
    printf '%s\n' \
        postgres_password \
        s3_runtime_access_key \
        s3_runtime_secret_key \
        s3_bootstrap_access_key \
        s3_bootstrap_secret_key \
        google_client_secret \
        stripe_secret_key \
        stripe_webhook_secret \
        openrouter_api_key \
        media_keyring \
        invited_emails \
        backup_s3_access_key \
        backup_s3_secret_key \
        backup_age_recipient
    if [ "$(deployment_storage_mode)" = minio ]; then
        printf '%s\n' \
            minio_root_user \
            minio_root_password
    fi
}

deployment_validate_file_secrets() {
    deployment_require_command stat
    if [ -n "${SECRETS_DIR:-}" ]; then
        deployment_file_secret_dir=$SECRETS_DIR
    else
        deployment_file_secret_dir=$(read_env_value SECRETS_DIR)
    fi
    deployment_file_secret_gid=$(deployment_secret_gid)
    case "$deployment_file_secret_dir" in
        /*) ;;
        *) fail "SECRETS_DIR must be an absolute path for SECRET_PROVIDER=file" ;;
    esac
    [ "$deployment_file_secret_dir" != / ] \
        || fail "SECRETS_DIR must not be /"
    [ -d "$deployment_file_secret_dir" ] && [ ! -L "$deployment_file_secret_dir" ] \
        || fail "SECRETS_DIR must be a real directory, not a symlink: $deployment_file_secret_dir"

    deployment_secret_dir_metadata=$(stat -c '%u:%g:%a' "$deployment_file_secret_dir")
    [ "$deployment_secret_dir_metadata" = "0:${deployment_file_secret_gid}:750" ] \
        || fail "SECRETS_DIR must be root:${deployment_file_secret_gid} with mode 0750"

    for deployment_secret_name in $(deployment_required_secret_files); do
        deployment_secret_path=${deployment_file_secret_dir}/${deployment_secret_name}
        [ -f "$deployment_secret_path" ] && [ ! -L "$deployment_secret_path" ] \
            || fail "required secret must be a regular non-symlink file: $deployment_secret_path"
        deployment_secret_metadata=$(stat -c '%u:%g:%a' "$deployment_secret_path")
        [ "$deployment_secret_metadata" = "0:${deployment_file_secret_gid}:640" ] \
            || fail "$deployment_secret_path must be root:${deployment_file_secret_gid} with mode 0640"
        [ -s "$deployment_secret_path" ] \
            || fail "required secret file is empty: $deployment_secret_path"
    done

    deployment_heartbeat_path=${deployment_file_secret_dir}/backup_heartbeat_url
    [ -f "$deployment_heartbeat_path" ] && [ ! -L "$deployment_heartbeat_path" ] \
        || fail "heartbeat secret must be a regular non-symlink file: $deployment_heartbeat_path"
    deployment_heartbeat_metadata=$(stat -c '%u:%g:%a' "$deployment_heartbeat_path")
    [ "$deployment_heartbeat_metadata" = "0:${deployment_file_secret_gid}:640" ] \
        || fail "$deployment_heartbeat_path must be root:${deployment_file_secret_gid} with mode 0640"
}

deployment_reject_symlink_path_components() {
    deployment_checked_path=$1
    while [ "$deployment_checked_path" != / ]; do
        [ ! -L "$deployment_checked_path" ] \
            || fail "release-state path must not contain symlinks: $deployment_checked_path"
        deployment_checked_parent=${deployment_checked_path%/*}
        [ -n "$deployment_checked_parent" ] || deployment_checked_parent=/
        [ "$deployment_checked_parent" != "$deployment_checked_path" ] \
            || fail "could not validate release-state path: $1"
        deployment_checked_path=$deployment_checked_parent
    done
}

deployment_validate_privileged_state_ancestors() {
    deployment_privileged_uid=$1
    [ "$deployment_privileged_uid" = 0 ] || return 0

    deployment_checked_path=$release_state_dir
    while :; do
        deployment_checked_metadata=$(stat -c '%u:%a' "$deployment_checked_path")
        deployment_checked_owner=${deployment_checked_metadata%%:*}
        deployment_checked_mode=${deployment_checked_metadata#*:}
        [ "$deployment_checked_owner" = 0 ] \
            || fail "privileged release-state path components must be root-owned: $deployment_checked_path"
        case "$deployment_checked_mode" in
            *[2367]|*[2367][0-7])
                fail "privileged release-state path components must not be group/world-writable: $deployment_checked_path"
                ;;
        esac
        [ "$deployment_checked_path" = / ] && break
        deployment_checked_parent=${deployment_checked_path%/*}
        [ -n "$deployment_checked_parent" ] || deployment_checked_parent=/
        deployment_checked_path=$deployment_checked_parent
    done
}

deployment_acquire_lock() {
    case "$release_state_dir" in
        /*) ;;
        *) fail "HOOK2STREAM_RELEASE_STATE_DIR must be an absolute path" ;;
    esac
    [ "$release_state_dir" != / ] \
        || fail "HOOK2STREAM_RELEASE_STATE_DIR must not be /"
    case "$release_state_dir" in
        */|*//*|*/./*|*/.|*/../*|*/..)
            fail "HOOK2STREAM_RELEASE_STATE_DIR must be a canonical absolute path"
            ;;
    esac

    deployment_require_command id
    deployment_require_command stat
    deployment_reject_symlink_path_components "$release_state_dir"
    if [ ! -e "$release_state_dir" ]; then
        (umask 077 && mkdir -p -- "$release_state_dir") \
            || fail "could not create release-state directory: $release_state_dir"
    fi
    deployment_reject_symlink_path_components "$release_state_dir"
    [ -d "$release_state_dir" ] && [ ! -L "$release_state_dir" ] \
        || fail "HOOK2STREAM_RELEASE_STATE_DIR must be a real directory: $release_state_dir"

    deployment_release_state_uid=$(id -u)
    deployment_release_state_metadata=$(stat -c '%u:%a' "$release_state_dir")
    [ "$deployment_release_state_metadata" = "${deployment_release_state_uid}:700" ] \
        || fail "$release_state_dir must be owned by uid ${deployment_release_state_uid} with mode 0700"
    deployment_validate_privileged_state_ancestors "$deployment_release_state_uid"

    deployment_lock_file=${release_state_dir}/deploy.lock
    if [ -e "$deployment_lock_file" ] || [ -L "$deployment_lock_file" ]; then
        [ -f "$deployment_lock_file" ] && [ ! -L "$deployment_lock_file" ] \
            || fail "deployment lock must be a regular non-symlink file: $deployment_lock_file"
    else
        (umask 077 && : > "$deployment_lock_file") \
            || fail "could not create deployment lock: $deployment_lock_file"
    fi
    [ -f "$deployment_lock_file" ] && [ ! -L "$deployment_lock_file" ] \
        || fail "deployment lock must be a regular non-symlink file: $deployment_lock_file"
    exec 9<>"$deployment_lock_file"
    flock -n 9 || fail "another deployment or secret rotation is already running"
}

require_https_endpoint() {
    deployment_variable_name=$1
    deployment_endpoint_value=$(read_env_value "$deployment_variable_name")
    case "$deployment_endpoint_value" in
        https://?*) ;;
        *) fail "${deployment_variable_name} must be an unquoted HTTPS endpoint in $environment_file" ;;
    esac
}

require_https_endpoint_or_empty() {
    deployment_variable_name=$1
    deployment_endpoint_value=$(read_env_value "$deployment_variable_name")
    case "$deployment_endpoint_value" in
        ""|https://?*) ;;
        *) fail "${deployment_variable_name} must be empty or an unquoted HTTPS endpoint in $environment_file" ;;
    esac
}

require_https_origin() {
    deployment_variable_name=$1
    deployment_origin_value=$(read_env_value "$deployment_variable_name")
    case "$deployment_origin_value" in
        https://?*) ;;
        *) fail "${deployment_variable_name} must be an unquoted HTTPS origin in $environment_file" ;;
    esac
    deployment_origin_authority=${deployment_origin_value#https://}
    case "$deployment_origin_authority" in
        ""|*/*|*'?'*|*'#'*|*@*|*[[:space:]]*)
            fail "${deployment_variable_name} must be an HTTPS origin without credentials, path, query, or fragment"
            ;;
    esac
}

require_digest_image() {
    deployment_variable_name=$1
    deployment_image_reference=$(read_env_value "$deployment_variable_name")
    if ! printf '%s\n' "$deployment_image_reference" \
        | grep -Eq '^[^[:space:]@]+@sha256:[0-9a-f]{64}$'; then
        fail "${deployment_variable_name} must be a full image@sha256 reference with a 64-character lowercase digest in $environment_file"
    fi
}

wait_for_service() {
    deployment_service_name=$1
    deployment_elapsed=0
    while [ "$deployment_elapsed" -lt "$health_timeout" ]; do
        deployment_container_id=$(compose ps -q "$deployment_service_name")
        if [ -n "$deployment_container_id" ]; then
            deployment_container_state=$(docker inspect --format \
                '{{if .State.Health}}{{.State.Health.Status}}{{else}}{{.State.Status}}{{end}}' \
                "$deployment_container_id" 2>/dev/null || true)
            case "$deployment_container_state" in
                healthy|running)
                    deployment_log "${deployment_service_name} is ${deployment_container_state}"
                    return 0
                    ;;
                unhealthy|exited|dead)
                    compose logs --tail 100 "$deployment_service_name" >&2 || true
                    printf '%s\n' \
                        "${deployment_program}: ${deployment_service_name} entered state ${deployment_container_state}" >&2
                    return 1
                    ;;
            esac
        fi
        sleep 2
        deployment_elapsed=$((deployment_elapsed + 2))
    done
    compose logs --tail 100 "$deployment_service_name" >&2 || true
    printf '%s\n' \
        "${deployment_program}: timed out waiting for ${deployment_service_name} health" >&2
    return 1
}

wait_for_url() {
    deployment_url=$1
    deployment_elapsed=0
    while [ "$deployment_elapsed" -lt "$public_smoke_timeout" ]; do
        deployment_http_status=$(
            curl \
                --silent \
                --show-error \
                --max-time 15 \
                --output /dev/null \
                --write-out '%{http_code}' \
                "$deployment_url" 2>/dev/null
        ) || deployment_http_status=
        if [ "$deployment_http_status" = 200 ]; then
            deployment_log "smoke passed for ${deployment_url}"
            return 0
        fi
        sleep 5
        deployment_elapsed=$((deployment_elapsed + 5))
    done
    printf '%s\n' "${deployment_program}: timed out waiting for public smoke ${deployment_url}" >&2
    return 1
}

vault_require_configuration() {
    [ "$(deployment_secret_provider)" = vault ] \
        || fail "SECRET_PROVIDER must be vault for this command"
    [ -r "$deployment_dir/compose.vault.yaml" ] \
        || fail "Vault Compose overlay is missing: $deployment_dir/compose.vault.yaml"
    require_https_origin VAULT_ADDR
    deployment_require_command jq
    deployment_require_command mktemp
    deployment_require_command stat

    case "$secret_state_dir" in
        /*) ;;
        *) fail "HOOK2STREAM_SECRET_STATE_DIR must be an absolute path" ;;
    esac
    [ "$secret_state_dir" != / ] \
        || fail "HOOK2STREAM_SECRET_STATE_DIR must not be /"

    vault_configured_secret_dir=$(read_env_value SECRETS_DIR)
    [ "$vault_configured_secret_dir" = "${secret_state_dir}/current" ] \
        || fail "SECRETS_DIR must be ${secret_state_dir}/current when SECRET_PROVIDER=vault"

    vault_secrets_gid=$(deployment_secret_gid)
    mkdir -p "$secret_state_dir" "$generations_dir"
    chown "0:${vault_secrets_gid}" "$secret_state_dir" "$generations_dir"
    chmod 0750 "$secret_state_dir" "$generations_dir"
}

vault_bundle_names() {
    printf '%s\n' \
        foundation \
        runtime-s3 \
        bootstrap-s3 \
        api \
        control \
        backup-s3 \
        backup-encryption
}

vault_bundle_scalar_files() {
    case "$1" in
        foundation) printf '%s\n' postgres_password ;;
        runtime-s3) printf '%s\n' s3_runtime_access_key s3_runtime_secret_key ;;
        bootstrap-s3) printf '%s\n' s3_bootstrap_access_key s3_bootstrap_secret_key ;;
        api) printf '%s\n' google_client_secret stripe_secret_key stripe_webhook_secret ;;
        control) printf '%s\n' openrouter_api_key ;;
        backup-s3) printf '%s\n' backup_s3_access_key backup_s3_secret_key backup_heartbeat_url ;;
        backup-encryption) printf '%s\n' backup_encryption_key_id backup_encryption_passphrase ;;
        *) return 1 ;;
    esac
}

vault_required_scalar_files() {
    for vault_required_bundle in $(vault_bundle_names); do
        vault_bundle_scalar_files "$vault_required_bundle"
    done
}

vault_validate_bundle() {
    vault_bundle_file=$1
    vault_expected_keys=$2
    vault_optional_empty_keys=${3:-'[]'}
    [ -f "$vault_bundle_file" ] && [ ! -L "$vault_bundle_file" ] \
        || return 1
    jq -e \
        --argjson expected "$vault_expected_keys" \
        --argjson optional_empty "$vault_optional_empty_keys" '
        type == "object"
        and ((keys | sort) == ["kv_version", "secrets"])
        and (.kv_version | (type == "number") and (. >= 1) and (floor == .))
        and (.secrets | type == "object")
        and ((.secrets | keys | sort) == ($expected | sort))
        and ([.secrets | to_entries[] |
            .key as $key |
            (.value | type == "string")
            and (
                (.value | length > 0)
                or (($optional_empty | index($key)) != null)
            )
            and (.value | contains("\u0000") | not)
            and (.value | contains("\n") | not)
            and (.value | contains("\r") | not)
            and (.value | test("^[[:space:]]|[[:space:]]$") | not)
        ] | all)
    ' "$vault_bundle_file" >/dev/null 2>&1
}

vault_validate_candidate_bundles() {
    vault_candidate_dir=$1
    [ -d "$vault_candidate_dir" ] && [ ! -L "$vault_candidate_dir" ] \
        || return 1
    [ -z "$(find "$vault_candidate_dir" -mindepth 1 -maxdepth 1 ! -type f -print -quit)" ] \
        || return 1
    vault_candidate_file_count=$(find "$vault_candidate_dir" -mindepth 1 -maxdepth 1 -type f | wc -l | tr -d '[:space:]')
    [ "$vault_candidate_file_count" = 7 ] || return 1

    vault_validate_bundle "$vault_candidate_dir/foundation.json" \
        '["postgres_password"]' || return 1
    vault_validate_bundle "$vault_candidate_dir/runtime-s3.json" \
        '["access_key_id","secret_access_key"]' || return 1
    vault_validate_bundle "$vault_candidate_dir/bootstrap-s3.json" \
        '["access_key_id","secret_access_key"]' || return 1
    vault_validate_bundle "$vault_candidate_dir/api.json" \
        '["google_client_secret","stripe_secret_key","stripe_webhook_secret"]' || return 1
    vault_validate_bundle "$vault_candidate_dir/control.json" \
        '["openrouter_api_key"]' || return 1
    vault_validate_bundle "$vault_candidate_dir/backup-s3.json" \
        '["access_key_id","heartbeat_url","secret_access_key"]' \
        '["heartbeat_url"]' || return 1
    vault_validate_bundle "$vault_candidate_dir/backup-encryption.json" \
        '["key_id","passphrase"]' || return 1
}

vault_write_scalar() {
    vault_source_json=$1
    vault_source_key=$2
    vault_destination_file=$3
    vault_allow_empty=${4:-false}
    vault_destination_tmp=${vault_destination_file}.tmp
    jq -erj --arg key "$vault_source_key" '.secrets[$key]' \
        "$vault_source_json" > "$vault_destination_tmp"
    if [ "$vault_allow_empty" != true ]; then
        [ -s "$vault_destination_tmp" ] || return 1
    fi
    chown "0:${vault_secrets_gid}" "$vault_destination_tmp"
    chmod 0640 "$vault_destination_tmp"
    mv -f "$vault_destination_tmp" "$vault_destination_file"
}

vault_split_candidate() {
    vault_candidate_dir=$1

    vault_write_scalar "$vault_candidate_dir/foundation.json" postgres_password \
        "$vault_candidate_dir/postgres_password"
    vault_write_scalar "$vault_candidate_dir/runtime-s3.json" access_key_id \
        "$vault_candidate_dir/s3_runtime_access_key"
    vault_write_scalar "$vault_candidate_dir/runtime-s3.json" secret_access_key \
        "$vault_candidate_dir/s3_runtime_secret_key"
    vault_write_scalar "$vault_candidate_dir/bootstrap-s3.json" access_key_id \
        "$vault_candidate_dir/s3_bootstrap_access_key"
    vault_write_scalar "$vault_candidate_dir/bootstrap-s3.json" secret_access_key \
        "$vault_candidate_dir/s3_bootstrap_secret_key"
    vault_write_scalar "$vault_candidate_dir/api.json" google_client_secret \
        "$vault_candidate_dir/google_client_secret"
    vault_write_scalar "$vault_candidate_dir/api.json" stripe_secret_key \
        "$vault_candidate_dir/stripe_secret_key"
    vault_write_scalar "$vault_candidate_dir/api.json" stripe_webhook_secret \
        "$vault_candidate_dir/stripe_webhook_secret"
    vault_write_scalar "$vault_candidate_dir/control.json" openrouter_api_key \
        "$vault_candidate_dir/openrouter_api_key"
    vault_write_scalar "$vault_candidate_dir/backup-s3.json" access_key_id \
        "$vault_candidate_dir/backup_s3_access_key"
    vault_write_scalar "$vault_candidate_dir/backup-s3.json" secret_access_key \
        "$vault_candidate_dir/backup_s3_secret_key"
    vault_write_scalar "$vault_candidate_dir/backup-s3.json" heartbeat_url \
        "$vault_candidate_dir/backup_heartbeat_url" true
    vault_write_scalar "$vault_candidate_dir/backup-encryption.json" key_id \
        "$vault_candidate_dir/backup_encryption_key_id"
    vault_write_scalar "$vault_candidate_dir/backup-encryption.json" passphrase \
        "$vault_candidate_dir/backup_encryption_passphrase"

    vault_manifest_tmp=$vault_candidate_dir/manifest.json.tmp
    jq -n \
        --slurpfile foundation "$vault_candidate_dir/foundation.json" \
        --slurpfile runtime_s3 "$vault_candidate_dir/runtime-s3.json" \
        --slurpfile bootstrap_s3 "$vault_candidate_dir/bootstrap-s3.json" \
        --slurpfile api "$vault_candidate_dir/api.json" \
        --slurpfile control "$vault_candidate_dir/control.json" \
        --slurpfile backup_s3 "$vault_candidate_dir/backup-s3.json" \
        --slurpfile backup_encryption "$vault_candidate_dir/backup-encryption.json" \
        '{
            schema_version: 1,
            bundle_kv_versions: {
                foundation: $foundation[0].kv_version,
                "runtime-s3": $runtime_s3[0].kv_version,
                "bootstrap-s3": $bootstrap_s3[0].kv_version,
                api: $api[0].kv_version,
                control: $control[0].kv_version,
                "backup-s3": $backup_s3[0].kv_version,
                "backup-encryption": $backup_encryption[0].kv_version
            }
        }' > "$vault_manifest_tmp"
    chmod 0600 "$vault_manifest_tmp"
    mv -f "$vault_manifest_tmp" "$vault_candidate_dir/manifest.json"

    rm -f \
        "$vault_candidate_dir/foundation.json" \
        "$vault_candidate_dir/runtime-s3.json" \
        "$vault_candidate_dir/bootstrap-s3.json" \
        "$vault_candidate_dir/api.json" \
        "$vault_candidate_dir/control.json" \
        "$vault_candidate_dir/backup-s3.json" \
        "$vault_candidate_dir/backup-encryption.json"
}

vault_validate_generation() {
    vault_generation_dir=$1
    [ -d "$vault_generation_dir" ] && [ ! -L "$vault_generation_dir" ] \
        || return 1
    for vault_scalar_file in $(vault_required_scalar_files); do
        [ -f "$vault_generation_dir/$vault_scalar_file" ] \
            && [ ! -L "$vault_generation_dir/$vault_scalar_file" ] \
            && [ -r "$vault_generation_dir/$vault_scalar_file" ] \
            || return 1
        if [ "$vault_scalar_file" != backup_heartbeat_url ]; then
            [ -s "$vault_generation_dir/$vault_scalar_file" ] || return 1
        fi
        vault_scalar_metadata=$(stat -c '%u:%g:%a' \
            "$vault_generation_dir/$vault_scalar_file")
        [ "$vault_scalar_metadata" = "0:${vault_secrets_gid}:640" ] \
            || return 1
    done
    [ -f "$vault_generation_dir/manifest.json" ] \
        && [ ! -L "$vault_generation_dir/manifest.json" ] \
        || return 1
    jq -e '
        type == "object"
        and (.schema_version == 1)
        and (.bundle_kv_versions | type == "object")
        and ((.bundle_kv_versions | keys | sort) == [
            "api", "backup-encryption", "backup-s3", "bootstrap-s3",
            "control", "foundation", "runtime-s3"
        ])
        and ([.bundle_kv_versions[] |
            (type == "number") and (. >= 1) and (floor == .)
        ] | all)
    ' "$vault_generation_dir/manifest.json" >/dev/null 2>&1
}

vault_safe_remove_candidate() {
    vault_remove_path=$1
    case "$vault_remove_path" in
        "$secret_state_dir"/.candidate-*)
            [ ! -L "$vault_remove_path" ] || return 1
            rm -rf -- "$vault_remove_path"
            ;;
        *) return 1 ;;
    esac
}

vault_safe_remove_generation() {
    vault_remove_path=$1
    case "$vault_remove_path" in
        "$generations_dir"/*)
            [ "$vault_remove_path" != "$generations_dir" ] || return 1
            [ ! -L "$vault_remove_path" ] || return 1
            rm -rf -- "$vault_remove_path"
            ;;
        *) return 1 ;;
    esac
}

vault_render_generation() (
    set -eu
    vault_timestamp=$(date -u +%Y%m%dT%H%M%SZ)
    vault_candidate_dir=$(mktemp -d "${secret_state_dir}/.candidate-${vault_timestamp}-XXXXXX")
    chown "0:${vault_secrets_gid}" "$vault_candidate_dir"
    chmod 0750 "$vault_candidate_dir"
    vault_pending_candidate=$vault_candidate_dir
    trap '
        vault_render_status=$?
        if [ -n "${vault_pending_candidate:-}" ] && [ -e "$vault_pending_candidate" ]; then
            vault_safe_remove_candidate "$vault_pending_candidate" || true
        fi
        exit "$vault_render_status"
    ' EXIT HUP INT TERM

    deployment_log "rendering a sealed Vault candidate generation" >&2
    if ! compose_vault_renderer "$vault_candidate_dir" \
        run --rm --no-deps vault-renderer >&2; then
        fail "Vault renderer failed; active secrets were not changed"
    fi
    if ! vault_validate_candidate_bundles "$vault_candidate_dir"; then
        fail "Vault renderer produced an invalid candidate bundle; active secrets were not changed"
    fi
    if ! vault_split_candidate "$vault_candidate_dir"; then
        fail "could not split the validated Vault candidate; active secrets were not changed"
    fi
    if ! vault_validate_generation "$vault_candidate_dir"; then
        fail "the split Vault candidate generation is invalid; active secrets were not changed"
    fi

    vault_generation_id=${vault_candidate_dir##*/}
    vault_generation_id=${vault_generation_id#.candidate-}
    vault_generation_dir=$generations_dir/$vault_generation_id
    [ ! -e "$vault_generation_dir" ] \
        || fail "generated Vault generation ID already exists"
    mv "$vault_candidate_dir" "$vault_generation_dir"
    vault_pending_candidate=
    trap - EXIT HUP INT TERM
    printf '%s\n' "$vault_generation_dir"
)

vault_link_target() {
    vault_link_name=$1
    vault_link_path=$secret_state_dir/$vault_link_name
    [ -L "$vault_link_path" ] || return 1
    vault_link_value=$(readlink "$vault_link_path") || return 1
    case "$vault_link_value" in
        generations/*)
            case "${vault_link_value#generations/}" in
                ""|*/*) return 1 ;;
            esac
            [ -d "$secret_state_dir/$vault_link_value" ] || return 1
            printf '%s\n' "$vault_link_value"
            ;;
        *) return 1 ;;
    esac
}

vault_current_generation() {
    vault_current_target=$(vault_link_target current) || return 1
    vault_current_path=$secret_state_dir/$vault_current_target
    vault_validate_generation "$vault_current_path" || return 1
    printf '%s\n' "$vault_current_path"
}

vault_set_link() {
    vault_link_name=$1
    vault_link_value=$2
    case "$vault_link_name" in current|previous) ;; *) return 1 ;; esac
    case "$vault_link_value" in
        generations/*)
            case "${vault_link_value#generations/}" in ""|*/*) return 1 ;; esac
            ;;
        *) return 1 ;;
    esac
    [ -d "$secret_state_dir/$vault_link_value" ] || return 1
    vault_link_tmp=$secret_state_dir/.${vault_link_name}.$$
    rm -f "$vault_link_tmp"
    ln -s "$vault_link_value" "$vault_link_tmp" || return 1
    mv -Tf "$vault_link_tmp" "$secret_state_dir/$vault_link_name"
}

vault_clear_link() {
    vault_link_name=$1
    case "$vault_link_name" in current|previous) ;; *) return 1 ;; esac
    vault_link_path=$secret_state_dir/$vault_link_name
    [ ! -e "$vault_link_path" ] || [ -L "$vault_link_path" ] || return 1
    rm -f "$vault_link_path"
}

vault_activate_generation() {
    vault_new_generation=$1
    vault_validate_generation "$vault_new_generation" || return 1
    case "$vault_new_generation" in
        "$generations_dir"/*) ;;
        *) return 1 ;;
    esac
    vault_new_target=generations/${vault_new_generation##*/}
    if vault_old_current_target=$(vault_link_target current 2>/dev/null); then
        vault_set_link previous "$vault_old_current_target" || return 1
    else
        [ ! -e "$secret_state_dir/current" ] && [ ! -L "$secret_state_dir/current" ] \
            || return 1
        vault_clear_link previous || return 1
    fi
    vault_set_link current "$vault_new_target"
}

vault_restore_links() {
    vault_restore_current=$1
    vault_restore_previous=$2
    [ -n "$vault_restore_current" ] || return 1
    vault_set_link current "$vault_restore_current" || return 1
    if [ -n "$vault_restore_previous" ]; then
        vault_set_link previous "$vault_restore_previous" || return 1
    else
        vault_clear_link previous || return 1
    fi
}

vault_prune_generations() {
    vault_keep_current=$(vault_link_target current 2>/dev/null || true)
    vault_keep_previous=$(vault_link_target previous 2>/dev/null || true)
    for vault_generation_path in "$generations_dir"/*; do
        [ -e "$vault_generation_path" ] || continue
        vault_generation_target=generations/${vault_generation_path##*/}
        case "$vault_generation_target" in
            "$vault_keep_current"|"$vault_keep_previous") ;;
            *) vault_safe_remove_generation "$vault_generation_path" || return 1 ;;
        esac
    done
}

vault_bundle_changed() {
    vault_compare_bundle=$1
    vault_compare_candidate=$2
    vault_compare_active=$3
    for vault_compare_file in $(vault_bundle_scalar_files "$vault_compare_bundle"); do
        cmp -s "$vault_compare_candidate/$vault_compare_file" \
            "$vault_compare_active/$vault_compare_file" || return 0
    done
    return 1
}

vault_changed_bundles() (
    vault_compare_candidate=$1
    vault_compare_active=$2
    vault_changed_list=
    for vault_compare_bundle in $(vault_bundle_names); do
        if vault_bundle_changed "$vault_compare_bundle" \
            "$vault_compare_candidate" "$vault_compare_active"; then
            vault_changed_list="${vault_changed_list}${vault_changed_list:+ }${vault_compare_bundle}"
        fi
    done
    printf '%s\n' "$vault_changed_list"
)

vault_list_has() {
    case " $1 " in
        *" $2 "*) return 0 ;;
        *) return 1 ;;
    esac
}

vault_recreate_and_wait() {
    vault_recreate_services=$*
    [ -n "$vault_recreate_services" ] || return 0
    # Intentional word splitting: callers pass only fixed service names.
    compose up -d --no-deps --force-recreate $vault_recreate_services || return 1
    for vault_recreate_service in $vault_recreate_services; do
        wait_for_service "$vault_recreate_service" || return 1
    done
}

vault_reconcile_regular_consumers() {
    vault_changed_list=$1
    vault_reconcile_mode=${2:-apply}

    if vault_list_has "$vault_changed_list" runtime-s3; then
        vault_recreate_and_wait \
            worker-media worker-analysis worker-render worker-export || return 1
    fi
    if vault_list_has "$vault_changed_list" runtime-s3 \
        || vault_list_has "$vault_changed_list" control; then
        vault_recreate_and_wait worker-control || return 1
    fi
    if vault_list_has "$vault_changed_list" runtime-s3 \
        || vault_list_has "$vault_changed_list" api; then
        vault_recreate_and_wait api || return 1
    fi
    if vault_list_has "$vault_changed_list" backup-s3; then
        if [ "$vault_reconcile_mode" = apply ]; then
            compose run --rm postgres-backup backup-once || return 1
        fi
        vault_recreate_and_wait postgres-backup || return 1
    fi
    # bootstrap-s3 intentionally has no long-running consumer.
}

vault_reconcile_postgres_consumers() {
    vault_reconcile_mode=${1:-apply}
    vault_recreate_and_wait pgbouncer || return 1
    vault_recreate_and_wait \
        worker-media worker-analysis worker-render worker-export || return 1
    vault_recreate_and_wait worker-control || return 1
    vault_recreate_and_wait api || return 1
    if [ "$vault_reconcile_mode" = apply ]; then
        compose run --rm postgres-backup backup-once || return 1
    fi
    vault_recreate_and_wait postgres-backup || return 1
}

vault_reconcile_backup_encryption_consumer() {
    vault_reconcile_mode=${1:-apply}
    if [ "$vault_reconcile_mode" = apply ]; then
        compose run --rm postgres-backup backup-once || return 1
    fi
    vault_recreate_and_wait postgres-backup
}

vault_set_postgres_password() {
    vault_password_file=$1
    [ -f "$vault_password_file" ] && [ -r "$vault_password_file" ] \
        || return 1
    compose exec -T --user postgres postgres \
        /opt/hook2stream/postgres-set-password.sh < "$vault_password_file"
}

vault_preflight_release() {
    vault_active_generation=
    if [ -L "$secret_state_dir/current" ]; then
        vault_active_generation=$(vault_current_generation) \
            || fail "the active Vault generation or current symlink is invalid"
    elif [ -e "$secret_state_dir/current" ]; then
        fail "the Vault current path exists but is not a managed symlink"
    fi

    vault_candidate_generation=$(vault_render_generation)
    if [ -z "$vault_active_generation" ]; then
        vault_activate_generation "$vault_candidate_generation" \
            || fail "could not atomically activate the initial Vault generation"
        vault_prune_generations \
            || fail "initial Vault generation activated, but stale generation cleanup failed"
        deployment_log "initial Vault generation activated"
        return 0
    fi

    vault_preflight_changes=$(vault_changed_bundles \
        "$vault_candidate_generation" "$vault_active_generation")
    if [ -n "$vault_preflight_changes" ]; then
        vault_safe_remove_generation "$vault_candidate_generation" || true
        printf '%s\n' \
            "${deployment_program}: Vault secret drift detected in bundles: ${vault_preflight_changes}" \
            "${deployment_program}: release stopped before workload mutation; run rotate-vault-secrets.sh or the named specialized rotation script" >&2
        return 1
    fi

    vault_safe_remove_generation "$vault_candidate_generation" \
        || fail "Vault values match, but candidate cleanup failed"
    deployment_log "Vault candidate matches the active scalar secrets"
}

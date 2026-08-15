#!/bin/sh
set -eu
set -f

readonly alias_name=hook2stream
readonly system_key=hook2stream-system/storage-protocol.json

fail() { printf '%s\n' "storage init: $*" >&2; exit 1; }

read_secret() {
    path=$1
    [ -f "$path" ] && [ ! -L "$path" ] && [ -r "$path" ] \
        || fail "secret is not a readable regular file: $path"
    value=
    extra=
    exec 8< "$path"
    IFS= read -r value <&8 || [ -n "$value" ] || fail "secret is empty: $path"
    if IFS= read -r extra <&8 || [ -n "$extra" ]; then
        exec 8<&-
        fail "secret must contain exactly one line: $path"
    fi
    exec 8<&-
    [ -n "$value" ] || fail "secret is empty: $path"
    case "$value" in *[[:space:]]*) fail "secret contains whitespace: $path" ;; esac
    printf '%s' "$value"
}

validate_bucket() {
    bucket=$1
    case "$bucket" in
        [a-z0-9]*[a-z0-9]) ;;
        *) fail "invalid bucket name: $bucket" ;;
    esac
    [ "${#bucket}" -ge 3 ] && [ "${#bucket}" -le 63 ] || fail "invalid bucket length"
    case "$bucket" in *[!a-z0-9.-]*|*..*|*.-*|*-.*) fail "invalid bucket name: $bucket" ;; esac
}

mc_command() { mc --config-dir "$MC_CONFIG_DIR" "$@"; }

mc_host_value() {
    endpoint=$1
    access_key=$2
    secret_key=$3
    [ "$endpoint" = http://minio:9000 ] || fail "mc endpoint is not internal"
    for credential in "$access_key" "$secret_key"; do
        case "$credential" in *[!A-Za-z0-9._+-]*) fail "credential contains a character unsafe for MC_HOST" ;; esac
    done
    printf 'http://%s:%s@minio:9000' "$access_key" "$secret_key"
}

assert_identity_exact() {
    access_key=$1
    policy=$2
    identity_label=$3
    identity_json=$MC_CONFIG_DIR/$identity_label-user.json
    identity_compact=$MC_CONFIG_DIR/$identity_label-user.compact.json
    mc_command admin user info --json "$alias_name" "$access_key" > "$identity_json"
    tr -d '[:space:]' < "$identity_json" > "$identity_compact"
    for identity_key in status accessKey policyName userStatus; do
        [ "$(grep -o "\"$identity_key\":" "$identity_compact" | wc -l | tr -d '[:space:]')" -eq 1 ] \
            || fail "$identity_label identity JSON is missing or duplicates $identity_key"
    done
    identity_member_count=$(grep -o '"memberOf":' "$identity_compact" | wc -l | tr -d '[:space:]')
    [ "$identity_member_count" -le 1 ] || fail "$identity_label identity JSON duplicates memberOf"
    if [ "$identity_member_count" -eq 1 ]; then
        grep -F '"memberOf":[]' "$identity_compact" >/dev/null \
            || fail "$identity_label must not inherit a group policy"
    fi
    identity_auth_count=$(grep -o '"authentication":' "$identity_compact" | wc -l | tr -d '[:space:]')
    [ "$identity_auth_count" -le 1 ] || fail "$identity_label identity JSON duplicates authentication"
    identity_expected_fields=$((4 + identity_member_count + identity_auth_count))
    [ "$(grep -o '"[^"]*":' "$identity_compact" | wc -l | tr -d '[:space:]')" -eq "$identity_expected_fields" ] \
        || fail "$identity_label identity JSON has unexpected fields"
    grep -F "\"status\":\"success\"" "$identity_compact" >/dev/null \
        || fail "$identity_label identity status is not success"
    grep -F "\"accessKey\":\"$access_key\"" "$identity_compact" >/dev/null \
        || fail "$identity_label access key differs"
    grep -F "\"policyName\":\"$policy\"" "$identity_compact" >/dev/null \
        || fail "$identity_label must have exactly policy $policy"
    grep -F '"userStatus":"enabled"' "$identity_compact" >/dev/null \
        || fail "$identity_label user is not enabled"
}

reconcile_identity() {
    access_key=$1
    secret_key=$2
    policy=$3
    identity_label=$4

    # Deleting and recreating the current managed identity is deliberate: it
    # atomically clears every direct policy and group membership that a stale
    # or compromised previous deployment may have left behind. The secret is
    # delivered over stdin, never as a process argument.
    if mc_command admin user info "$alias_name" "$access_key" >/dev/null 2>&1; then
        mc_command admin user rm "$alias_name" "$access_key" >/dev/null
    fi
    printf '%s\n%s\n' "$access_key" "$secret_key" \
        | mc_command admin user add "$alias_name" >/dev/null
    mc_command admin policy attach "$alias_name" "$policy" --user "$access_key" >/dev/null
    assert_identity_exact "$access_key" "$policy" "$identity_label"
}

assert_unique_values() {
    values=$*
    for left in $values; do
        count=0
        for right in $values; do [ "$left" != "$right" ] || count=$((count + 1)); done
        [ "$count" -eq 1 ] || fail "root/bootstrap/runtime/backup credential values must all be distinct"
    done
}

read_managed_identity_inventory() {
    inventory_header=
    inventory_bootstrap_line=
    inventory_runtime_line=
    inventory_backup_line=
    inventory_extra=
    exec 7<&0
    IFS= read -r inventory_header <&7 || { exec 7<&-; fail "managed identity inventory is truncated"; }
    IFS= read -r inventory_bootstrap_line <&7 || { exec 7<&-; fail "managed identity inventory is truncated"; }
    IFS= read -r inventory_runtime_line <&7 || { exec 7<&-; fail "managed identity inventory is truncated"; }
    IFS= read -r inventory_backup_line <&7 || { exec 7<&-; fail "managed identity inventory is truncated"; }
    if IFS= read -r inventory_extra <&7 || [ -n "$inventory_extra" ]; then
        exec 7<&-
        fail "managed identity inventory has extra lines"
    fi
    exec 7<&-
    [ "$inventory_header" = HOOK2STREAM_STORAGE_MANAGED_IDENTITIES_V1 ] \
        || fail "managed identity inventory header is invalid"
    case "$inventory_bootstrap_line" in bootstrap=*) ;; *) fail "managed identity bootstrap entry is invalid" ;; esac
    case "$inventory_runtime_line" in runtime=*) ;; *) fail "managed identity runtime entry is invalid" ;; esac
    case "$inventory_backup_line" in backup=*) ;; *) fail "managed identity backup entry is invalid" ;; esac
    previous_bootstrap_access=${inventory_bootstrap_line#bootstrap=}
    previous_runtime_access=${inventory_runtime_line#runtime=}
    previous_backup_access=${inventory_backup_line#backup=}
    previous_values=
    for previous_access in \
        "$previous_bootstrap_access" "$previous_runtime_access" "$previous_backup_access"; do
        case "$previous_access" in
            -) continue ;;
            ''|*[!A-Za-z0-9._+-]*) fail "managed identity inventory contains an unsafe access key" ;;
        esac
        [ "${#previous_access}" -ge 3 ] \
            || fail "managed identity inventory contains a short access key"
        case " $previous_values " in
            *" $previous_access "*) fail "managed identity inventory contains duplicate access keys" ;;
        esac
        previous_values="$previous_values $previous_access"
    done
    unset previous_values previous_access inventory_header inventory_bootstrap_line \
        inventory_runtime_line inventory_backup_line inventory_extra
}

retire_managed_identity() {
    previous_access=$1
    identity_label=$2
    [ "$previous_access" != - ] || return 0
    [ "$previous_access" != "$root_user" ] \
        || fail "$identity_label inventory entry collides with the MinIO root identity"
    for current_access in "$bootstrap_access" "$runtime_access" "$backup_access"; do
        [ "$previous_access" != "$current_access" ] || return 0
    done
    if mc_command admin user info "$alias_name" "$previous_access" >/dev/null 2>&1; then
        mc_command admin user rm "$alias_name" "$previous_access" >/dev/null
    fi
    if mc_command admin user info "$alias_name" "$previous_access" >/dev/null 2>&1; then
        fail "$identity_label retired access key remains active"
    fi
}

publish_marker() {
    bucket=$1
    marker=$2
    target=$alias_name/$bucket/$system_key
    if mc_command stat "$target" >/dev/null 2>&1; then
        mc_command cat "$target" > "$MC_CONFIG_DIR/remote-marker.json"
        cmp -s "$marker" "$MC_CONFIG_DIR/remote-marker.json" \
            || fail "existing storage protocol marker differs in $bucket"
    else
        mc_command cp --attr 'Content-Type=application/json' "$marker" "$target"
        mc_command cat "$target" > "$MC_CONFIG_DIR/remote-marker.json"
        cmp -s "$marker" "$MC_CONFIG_DIR/remote-marker.json" \
            || fail "storage protocol marker verification failed in $bucket"
    fi
}

: "${DEPLOYMENT_ENVIRONMENT:?DEPLOYMENT_ENVIRONMENT is required}"
: "${STORAGE_MODE:?STORAGE_MODE is required}"
: "${STORAGE_PROTOCOL_VERSION:?STORAGE_PROTOCOL_VERSION is required}"
: "${STORAGE_OBJECT_FORMAT:?STORAGE_OBJECT_FORMAT is required}"
: "${MINIO_ENDPOINT:?MINIO_ENDPOINT is required}"
: "${MINIO_REGION:?MINIO_REGION is required}"
: "${MINIO_MEDIA_BUCKET:?MINIO_MEDIA_BUCKET is required}"
: "${MINIO_BACKUP_BUCKET:?MINIO_BACKUP_BUCKET is required}"
: "${MINIO_BACKUP_PREFIX:?MINIO_BACKUP_PREFIX is required}"
: "${MINIO_MEDIA_QUOTA_GIB:?MINIO_MEDIA_QUOTA_GIB is required}"
: "${MINIO_BACKUP_QUOTA_GIB:?MINIO_BACKUP_QUOTA_GIB is required}"
: "${BACKUP_RETENTION_DAYS:?BACKUP_RETENTION_DAYS is required}"
: "${MINIO_ROOT_USER_FILE:?MINIO_ROOT_USER_FILE is required}"
: "${MINIO_ROOT_PASSWORD_FILE:?MINIO_ROOT_PASSWORD_FILE is required}"
: "${S3_BOOTSTRAP_ACCESS_KEY_FILE:?S3_BOOTSTRAP_ACCESS_KEY_FILE is required}"
: "${S3_BOOTSTRAP_SECRET_KEY_FILE:?S3_BOOTSTRAP_SECRET_KEY_FILE is required}"
: "${S3_RUNTIME_ACCESS_KEY_FILE:?S3_RUNTIME_ACCESS_KEY_FILE is required}"
: "${S3_RUNTIME_SECRET_KEY_FILE:?S3_RUNTIME_SECRET_KEY_FILE is required}"
: "${BACKUP_S3_ACCESS_KEY_FILE:?BACKUP_S3_ACCESS_KEY_FILE is required}"
: "${BACKUP_S3_SECRET_KEY_FILE:?BACKUP_S3_SECRET_KEY_FILE is required}"
: "${MANAGED_IDENTITY_INVENTORY_SOURCE:?MANAGED_IDENTITY_INVENTORY_SOURCE is required}"
: "${MC_CONFIG_DIR:=/tmp/mc}"

[ "$STORAGE_MODE" = minio ] || fail "STORAGE_MODE must be minio"
[ "$MANAGED_IDENTITY_INVENTORY_SOURCE" = stdin ] \
    || fail "managed identity inventory must arrive through stdin"
[ "$STORAGE_PROTOCOL_VERSION" = 1 ] || fail "only storage protocol v1 is supported"
[ "$STORAGE_OBJECT_FORMAT" = H2SEv1 ] || fail "only H2SEv1 is supported"
[ "$MINIO_ENDPOINT" = http://minio:9000 ] || fail "MINIO_ENDPOINT must stay on the internal network"
[ "$MINIO_REGION" = us-east-1 ] || fail "MINIO_REGION must be us-east-1"
validate_bucket "$MINIO_MEDIA_BUCKET"
validate_bucket "$MINIO_BACKUP_BUCKET"
[ "$MINIO_MEDIA_BUCKET" != "$MINIO_BACKUP_BUCKET" ] || fail "bucket names must differ"

case "$DEPLOYMENT_ENVIRONMENT" in
    staging)
        expected_media=hook2stream-staging-media
        expected_backup=hook2stream-staging-pg-backups
        expected_prefix=hook2stream/staging/postgres
        expected_media_quota=35
        expected_backup_quota=10
        expected_retention=7
        ;;
    production)
        expected_media=hook2stream-production-media
        expected_backup=hook2stream-production-pg-backups
        expected_prefix=hook2stream/production/postgres
        expected_media_quota=160
        expected_backup_quota=30
        expected_retention=35
        ;;
    *) fail "DEPLOYMENT_ENVIRONMENT must be staging or production" ;;
esac
[ "$MINIO_MEDIA_BUCKET" = "$expected_media" ] || fail "media bucket differs from the exact topology"
[ "$MINIO_BACKUP_BUCKET" = "$expected_backup" ] || fail "backup bucket differs from the exact topology"
[ "$MINIO_BACKUP_PREFIX" = "$expected_prefix" ] || fail "backup prefix differs from the exact topology"
[ "$MINIO_MEDIA_QUOTA_GIB" = "$expected_media_quota" ] || fail "media quota differs from the exact topology"
[ "$MINIO_BACKUP_QUOTA_GIB" = "$expected_backup_quota" ] || fail "backup quota differs from the exact topology"
[ "$BACKUP_RETENTION_DAYS" = "$expected_retention" ] || fail "backup retention differs from the exact topology"

policy_dir=/etc/hook2stream/policies/$DEPLOYMENT_ENVIRONMENT
lifecycle_dir=/etc/hook2stream/lifecycle
marker=/etc/hook2stream/markers/$DEPLOYMENT_ENVIRONMENT-storage-protocol-v1.json
runtime_policy=$policy_dir/runtime-media.json
bootstrap_policy=$policy_dir/bootstrap-media.json
backup_policy=$policy_dir/postgres-backup.json
media_lifecycle=$lifecycle_dir/$DEPLOYMENT_ENVIRONMENT-media.json
backup_lifecycle=$lifecycle_dir/$DEPLOYMENT_ENVIRONMENT-backup.json
for required_file in "$runtime_policy" "$bootstrap_policy" "$backup_policy" \
    "$media_lifecycle" "$backup_lifecycle" "$marker"; do
    [ -f "$required_file" ] && [ ! -L "$required_file" ] && [ -r "$required_file" ] \
        || fail "required immutable config is missing: $required_file"
done

root_user=$(read_secret "$MINIO_ROOT_USER_FILE")
root_password=$(read_secret "$MINIO_ROOT_PASSWORD_FILE")
bootstrap_access=$(read_secret "$S3_BOOTSTRAP_ACCESS_KEY_FILE")
bootstrap_secret=$(read_secret "$S3_BOOTSTRAP_SECRET_KEY_FILE")
runtime_access=$(read_secret "$S3_RUNTIME_ACCESS_KEY_FILE")
runtime_secret=$(read_secret "$S3_RUNTIME_SECRET_KEY_FILE")
backup_access=$(read_secret "$BACKUP_S3_ACCESS_KEY_FILE")
backup_secret=$(read_secret "$BACKUP_S3_SECRET_KEY_FILE")
read_managed_identity_inventory
[ "${#root_user}" -ge 3 ] && [ "${#root_password}" -ge 8 ] || fail "root credential is too short"
for access in "$bootstrap_access" "$runtime_access" "$backup_access"; do
    [ "${#access}" -ge 3 ] || fail "managed access key is too short"
done
for secret in "$bootstrap_secret" "$runtime_secret" "$backup_secret"; do
    [ "${#secret}" -ge 12 ] || fail "managed secret key must contain at least 12 characters"
done
assert_unique_values "$root_user" "$root_password" "$bootstrap_access" "$bootstrap_secret" \
    "$runtime_access" "$runtime_secret" "$backup_access" "$backup_secret"

umask 077
mkdir -p "$MC_CONFIG_DIR"
MC_HOST_hook2stream=$(mc_host_value "$MINIO_ENDPOINT" "$root_user" "$root_password")
export MC_HOST_hook2stream
mc_command ready "$alias_name"
retire_managed_identity "$previous_runtime_access" runtime
retire_managed_identity "$previous_bootstrap_access" bootstrap
retire_managed_identity "$previous_backup_access" backup
mc_command mb --ignore-existing --region "$MINIO_REGION" "$alias_name/$MINIO_MEDIA_BUCKET"
mc_command mb --ignore-existing --region "$MINIO_REGION" "$alias_name/$MINIO_BACKUP_BUCKET"
mc_command anonymous set none "$alias_name/$MINIO_MEDIA_BUCKET"
mc_command anonymous set none "$alias_name/$MINIO_BACKUP_BUCKET"

mc_command version suspend "$alias_name/$MINIO_MEDIA_BUCKET"
mc_command version enable "$alias_name/$MINIO_BACKUP_BUCKET"
mc_command quota set "$alias_name/$MINIO_MEDIA_BUCKET" --size "${MINIO_MEDIA_QUOTA_GIB}GiB"
mc_command quota set "$alias_name/$MINIO_BACKUP_BUCKET" --size "${MINIO_BACKUP_QUOTA_GIB}GiB"
mc_command ilm import "$alias_name/$MINIO_MEDIA_BUCKET" < "$media_lifecycle"
mc_command ilm import "$alias_name/$MINIO_BACKUP_BUCKET" < "$backup_lifecycle"

runtime_policy_name=hook2stream-$DEPLOYMENT_ENVIRONMENT-runtime-media
bootstrap_policy_name=hook2stream-$DEPLOYMENT_ENVIRONMENT-bootstrap-media
backup_policy_name=hook2stream-$DEPLOYMENT_ENVIRONMENT-postgres-backup
mc_command admin policy create "$alias_name" "$runtime_policy_name" "$runtime_policy"
mc_command admin policy create "$alias_name" "$bootstrap_policy_name" "$bootstrap_policy"
mc_command admin policy create "$alias_name" "$backup_policy_name" "$backup_policy"
reconcile_identity "$runtime_access" "$runtime_secret" "$runtime_policy_name" runtime
reconcile_identity "$bootstrap_access" "$bootstrap_secret" "$bootstrap_policy_name" bootstrap
reconcile_identity "$backup_access" "$backup_secret" "$backup_policy_name" backup

publish_marker "$MINIO_MEDIA_BUCKET" "$marker"
publish_marker "$MINIO_BACKUP_BUCKET" "$marker"

# Verification is intentionally separate from the mutation commands. Any
# mismatch prevents the deployment receipt from claiming a successful gate.
mc_command stat "$alias_name/$MINIO_MEDIA_BUCKET" >/dev/null
mc_command stat "$alias_name/$MINIO_BACKUP_BUCKET" >/dev/null
mc_command admin policy info "$alias_name" "$runtime_policy_name" >/dev/null
mc_command admin policy info "$alias_name" "$bootstrap_policy_name" >/dev/null
mc_command admin policy info "$alias_name" "$backup_policy_name" >/dev/null
assert_identity_exact "$runtime_access" "$runtime_policy_name" runtime-final
assert_identity_exact "$bootstrap_access" "$bootstrap_policy_name" bootstrap-final
assert_identity_exact "$backup_access" "$backup_policy_name" backup-final
mc_command quota info "$alias_name/$MINIO_MEDIA_BUCKET" > "$MC_CONFIG_DIR/media-quota.txt"
mc_command quota info "$alias_name/$MINIO_BACKUP_BUCKET" > "$MC_CONFIG_DIR/backup-quota.txt"
grep -Ei "(Quota:|hard quota of)[^0-9]*${MINIO_MEDIA_QUOTA_GIB}[[:space:]]+GiB" "$MC_CONFIG_DIR/media-quota.txt" >/dev/null \
    || fail "media quota verification failed"
grep -Ei "(Quota:|hard quota of)[^0-9]*${MINIO_BACKUP_QUOTA_GIB}[[:space:]]+GiB" "$MC_CONFIG_DIR/backup-quota.txt" >/dev/null \
    || fail "backup quota verification failed"
mc_command version info "$alias_name/$MINIO_MEDIA_BUCKET" > "$MC_CONFIG_DIR/media-version.txt"
mc_command version info "$alias_name/$MINIO_BACKUP_BUCKET" > "$MC_CONFIG_DIR/backup-version.txt"
grep -i 'suspend' "$MC_CONFIG_DIR/media-version.txt" >/dev/null || fail "media versioning is not suspended"
grep -i 'enabled' "$MC_CONFIG_DIR/backup-version.txt" >/dev/null || fail "backup versioning is not enabled"
mc_command ilm export "$alias_name/$MINIO_MEDIA_BUCKET" > "$MC_CONFIG_DIR/media-ilm.json"
mc_command ilm export "$alias_name/$MINIO_BACKUP_BUCKET" > "$MC_CONFIG_DIR/backup-ilm.json"
grep -F "hook2stream-$DEPLOYMENT_ENVIRONMENT-media-abort-multipart-1d" "$MC_CONFIG_DIR/media-ilm.json" >/dev/null \
    || fail "media lifecycle verification failed"
grep -F "hook2stream-$DEPLOYMENT_ENVIRONMENT-staging-object-expiry-1d" "$MC_CONFIG_DIR/media-ilm.json" >/dev/null \
    || fail "stale staging-object lifecycle verification failed"
grep -F "hook2stream-$DEPLOYMENT_ENVIRONMENT-backup-retention-${BACKUP_RETENTION_DAYS}d" "$MC_CONFIG_DIR/backup-ilm.json" >/dev/null \
    || fail "backup lifecycle verification failed"

unset MC_HOST_hook2stream root_user root_password bootstrap_access bootstrap_secret runtime_access runtime_secret backup_access backup_secret \
    previous_bootstrap_access previous_runtime_access previous_backup_access
printf '%s\n' "storage init: topology, policies, quotas, versioning, lifecycle, and protocol markers verified"

#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
deployment_dir=$(CDPATH= cd -- "$script_dir/.." && pwd)
environment_file=${1:-${HOOK2STREAM_ENV_FILE:-$deployment_dir/.env}}

fail() { printf '%s\n' "egress render: $*" >&2; exit 1; }
read_value() {
    render_name=$1
    awk -v name="$render_name" '
        index($0, "=") > 0 {
            candidate = substr($0, 1, index($0, "=") - 1)
            if (candidate == name) value = substr($0, index($0, "=") + 1)
        }
        END { sub(/\r$/, "", value); print value }
    ' "$environment_file"
}

[ -r "$environment_file" ] || fail "environment file is not readable"
duplicate_names=$(awk -F= '
    /^[A-Za-z_][A-Za-z0-9_]*=/ { count[$1]++ }
    END { for (name in count) if (count[name] > 1) print name }
' "$environment_file" | sort)
[ -z "$duplicate_names" ] \
    || fail "environment file contains duplicate assignments: $(printf '%s' "$duplicate_names" | tr '\n' ' ')"
environment=$(read_value DEPLOYMENT_ENVIRONMENT)
case "$environment" in staging|production) ;; *) fail "DEPLOYMENT_ENVIRONMENT must be staging or production" ;; esac

validate_endpoint_pair() {
    endpoint_label=$1
    endpoint_host_name=$2
    endpoint_url_value=$3
    printf '%s\n' "$endpoint_host_name" \
        | grep -Eq '^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$' \
        || fail "$endpoint_label host must be an exact lowercase FQDN without a wildcard or port"
    case "$endpoint_host_name" in *'*'*|.*|*.) fail "$endpoint_label host must not contain a wildcard" ;; esac
    [ "$endpoint_url_value" = "https://$endpoint_host_name" ] \
        || fail "$endpoint_label URL must be exactly https://$endpoint_label host"
}

media_endpoint_host=$(read_value S3_ENDPOINT_HOST)
media_endpoint_url=$(read_value S3_SERVICE_URL)
backup_endpoint_host=$(read_value BACKUP_S3_ENDPOINT_HOST)
backup_endpoint_url=$(read_value BACKUP_S3_ENDPOINT)
validate_endpoint_pair media "$media_endpoint_host" "$media_endpoint_url"
validate_endpoint_pair backup "$backup_endpoint_host" "$backup_endpoint_url"
[ "$media_endpoint_host" = gateway.storjshare.io ] \
    || fail "S3_ENDPOINT_HOST must be gateway.storjshare.io for the Storj MVP"
[ "$backup_endpoint_host" = gateway.storjshare.io ] \
    || fail "BACKUP_S3_ENDPOINT_HOST must be gateway.storjshare.io for the Storj MVP"
[ "$(read_value STORAGE_MODE)" = external ] \
    || fail "staging/production requires STORAGE_MODE=external"
[ "$(read_value STORAGE_PROVISIONING_MODE)" = VerifyOnly ] \
    || fail "staging/production requires STORAGE_PROVISIONING_MODE=VerifyOnly"
[ "$(read_value STORAGE_OBJECT_EXPIRATION_MODE)" = Storj ] \
    || fail "staging/production requires STORAGE_OBJECT_EXPIRATION_MODE=Storj"
[ "$(read_value S3_FORCE_PATH_STYLE)" = true ] \
    || fail "Storj media access requires S3_FORCE_PATH_STYLE=true"
[ "$(read_value BACKUP_S3_FORCE_PATH_STYLE)" = true ] \
    || fail "Storj backup access requires BACKUP_S3_FORCE_PATH_STYLE=true"
[ "$(read_value S3_REGION)" = global ] \
    || fail "Storj media signing region must be global"
[ "$(read_value BACKUP_S3_REGION)" = global ] \
    || fail "Storj backup signing region must be global"
[ "$(read_value S3_MEDIA_BUCKET)" = "hook2stream-com-${environment}-media" ] \
    || fail "S3_MEDIA_BUCKET does not match the fixed ${environment} Storj bucket"
[ "$(read_value BACKUP_S3_BUCKET)" = "hook2stream-com-${environment}-pg-backups" ] \
    || fail "BACKUP_S3_BUCKET does not match the fixed ${environment} Storj bucket"
[ "$(read_value S3_CONFIGURE_BUCKET_LIFECYCLE)" = false ] \
    || fail "Storj does not support PutBucketLifecycle"
[ "$(read_value S3_CONFIGURE_MULTIPART_ABORT_LIFECYCLE)" = false ] \
    || fail "Storj multipart cleanup is owned by the local media janitor"
[ "$(read_value STORAGE_PROTOCOL_VERSION)" = 1 ] \
    || fail "STORAGE_PROTOCOL_VERSION must be exactly 1"
[ "$(read_value STORAGE_CONTRACT_KEY)" = .hook2stream/contracts/storage-v1.json ] \
    || fail "STORAGE_CONTRACT_KEY must be the canonical private marker key"
printf '%s\n' "$(read_value STORAGE_CONTRACT_SHA256)" \
    | grep -Eq '^[0-9a-f]{64}$' \
    || fail "STORAGE_CONTRACT_SHA256 must be a lowercase SHA-256 digest"

expected_relative_dir=./egress/rendered/$environment
[ "$(read_value EGRESS_CONFIG_DIR)" = "$expected_relative_dir" ] \
    || fail "EGRESS_CONFIG_DIR must be exactly $expected_relative_dir"
output_dir=${2:-$deployment_dir/egress/rendered/$environment}
case "$output_dir" in /*) ;; *) fail "output directory must be absolute" ;; esac
[ ! -L "$deployment_dir/egress" ] || fail "egress template directory must not be a symlink"
[ ! -e "$output_dir" ] || [ ! -L "$output_dir" ] \
    || fail "output directory must not be a symlink"
mkdir -p "$output_dir"
chmod 0755 "$output_dir"

temporary_files=
cleanup() {
    for temporary_file in $temporary_files; do
        rm -f -- "$temporary_file"
    done
}
trap cleanup EXIT HUP INT TERM

for config_name in api s3 control backup; do
    template=$deployment_dir/egress/$config_name.conf.in
    [ -f "$template" ] && [ ! -L "$template" ] \
        || fail "$template must be a regular non-symlink template"
    temporary_file=$(mktemp "$output_dir/.${config_name}.conf.XXXXXX")
    temporary_files="$temporary_files $temporary_file"
    sed \
        -e "s/__HOOK2STREAM_MEDIA_S3_ENDPOINT_HOST__/$media_endpoint_host/g" \
        -e "s/__HOOK2STREAM_BACKUP_S3_ENDPOINT_HOST__/$backup_endpoint_host/g" \
        "$template" > "$temporary_file"
    case "$config_name" in
        api)
            expected_allowlist="acl allowed_domains dstdomain $media_endpoint_host accounts.google.com oauth2.googleapis.com openidconnect.googleapis.com api.stripe.com"
            ;;
        control)
            expected_allowlist="acl allowed_domains dstdomain $media_endpoint_host openrouter.ai"
            ;;
        s3)
            expected_allowlist="acl allowed_domains dstdomain $media_endpoint_host"
            ;;
        backup)
            expected_allowlist="acl allowed_domains dstdomain $backup_endpoint_host"
            ;;
    esac
    grep -Fxq "$expected_allowlist" "$temporary_file" \
        || fail "$config_name does not contain its exact role allowlist"
    if grep -Fq '*' "$temporary_file"; then
        fail "$config_name contains a wildcard token"
    fi
    if ! awk '
        $1 == "acl" && $2 == "allowed_domains" && $3 == "dstdomain" {
            for (i = 4; i <= NF; i++) if ($i ~ /^\./) exit 1
        }
    ' "$temporary_file"; then
        fail "$config_name contains a suffix-domain allowlist entry"
    fi
    chmod 0644 "$temporary_file"
    mv -f -- "$temporary_file" "$output_dir/$config_name.conf"
done

temporary_files=
trap - EXIT HUP INT TERM
printf '%s\n' "egress render: exact $environment storage hostname rendered"

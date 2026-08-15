#!/bin/sh
set -eu
set -f

fail() { printf '%s\n' "storage retired identity: $*" >&2; exit 1; }
read_secret() {
    retired_path=$1
    retired_value=
    retired_extra=
    [ -f "$retired_path" ] && [ ! -L "$retired_path" ] && [ -r "$retired_path" ] \
        || fail "retired credential fixture is unreadable"
    exec 8< "$retired_path"
    IFS= read -r retired_value <&8 || [ -n "$retired_value" ] || fail "retired credential is empty"
    if IFS= read -r retired_extra <&8 || [ -n "$retired_extra" ]; then
        exec 8<&-
        fail "retired credential is multiline"
    fi
    exec 8<&-
    case "$retired_value" in
        ''|*[!A-Za-z0-9._+-]*) fail "retired credential is unsafe for MC_HOST" ;;
    esac
    printf '%s' "$retired_value"
}

: "${MINIO_ENDPOINT:?MINIO_ENDPOINT is required}"
: "${MINIO_MEDIA_BUCKET:?MINIO_MEDIA_BUCKET is required}"
: "${MC_CONFIG_DIR:=/tmp/mc-retired-identity}"
[ "$MINIO_ENDPOINT" = http://minio:9000 ] || fail "endpoint is not internal"
retired_access=$(read_secret /retired/runtime_access_key)
retired_secret=$(read_secret /retired/runtime_secret_key)
umask 077
mkdir -p "$MC_CONFIG_DIR"
MC_HOST_retired="http://$retired_access:$retired_secret@minio:9000"
export MC_HOST_retired
if mc --config-dir "$MC_CONFIG_DIR" stat \
    "retired/$MINIO_MEDIA_BUCKET/hook2stream-system/storage-protocol.json" >/dev/null 2>&1; then
    fail "retired runtime identity can still read media"
fi
if mc --config-dir "$MC_CONFIG_DIR" cp /etc/hook2stream/markers/"$DEPLOYMENT_ENVIRONMENT"-storage-protocol-v1.json \
    "retired/$MINIO_MEDIA_BUCKET/policy-probes/retired-identity" >/dev/null 2>&1; then
    fail "retired runtime identity can still write media"
fi
unset MC_HOST_retired retired_access retired_secret
printf '%s\n' "storage retired identity: old access-key ID is denied" >&2

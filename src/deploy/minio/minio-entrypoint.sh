#!/bin/sh
set -eu

fail() {
    printf '%s\n' "minio entrypoint: $*" >&2
    exit 1
}

read_secret() {
    secret_path=$1
    [ -r "$secret_path" ] || fail "required secret file is not readable: $secret_path"
    secret_value=
    secret_extra=
    exec 8< "$secret_path"
    if ! IFS= read -r secret_value <&8; then
        [ -n "$secret_value" ] || fail "required secret file is empty: $secret_path"
    fi
    if IFS= read -r secret_extra <&8 || [ -n "$secret_extra" ]; then
        exec 8<&-
        fail "required secret file must contain exactly one line: $secret_path"
    fi
    exec 8<&-
    [ -n "$secret_value" ] || fail "required secret file is empty: $secret_path"
    carriage_return=$(printf '\r')
    case "$secret_value" in
        *"$carriage_return"*)
            fail "required secret file must contain exactly one line: $secret_path"
            ;;
        [[:space:]]*|*[[:space:]])
            fail "required secret must not start or end with whitespace: $secret_path"
            ;;
    esac
    printf '%s' "$secret_value"
}

[ "${STORAGE_MODE:-}" = "minio" ] \
    || fail "STORAGE_MODE must be exactly 'minio' for the MinIO overlay"
: "${MINIO_ROOT_USER_FILE:?MINIO_ROOT_USER_FILE is required}"
: "${MINIO_ROOT_PASSWORD_FILE:?MINIO_ROOT_PASSWORD_FILE is required}"

export MINIO_ROOT_USER
MINIO_ROOT_USER=$(read_secret "$MINIO_ROOT_USER_FILE")
export MINIO_ROOT_PASSWORD
MINIO_ROOT_PASSWORD=$(read_secret "$MINIO_ROOT_PASSWORD_FILE")

[ "${#MINIO_ROOT_USER}" -ge 3 ] \
    || fail "the root access key must contain at least 3 characters"
[ "${#MINIO_ROOT_PASSWORD}" -ge 8 ] \
    || fail "the root secret key must contain at least 8 characters"

exec /usr/local/bin/minio "$@"

#!/bin/sh
set -eu
set -f

fail() { printf '%s\n' "storage live multipart: $*" >&2; exit 1; }
read_secret() {
    secret_path=$1
    secret_value=
    secret_extra=
    [ -f "$secret_path" ] && [ ! -L "$secret_path" ] && [ -r "$secret_path" ] \
        || fail "secret is unreadable"
    exec 8< "$secret_path"
    IFS= read -r secret_value <&8 || [ -n "$secret_value" ] || fail "secret is empty"
    if IFS= read -r secret_extra <&8 || [ -n "$secret_extra" ]; then
        exec 8<&-
        fail "secret is multiline"
    fi
    exec 8<&-
    case "$secret_value" in ''|*[[:space:]]*) fail "secret is empty or contains whitespace" ;; esac
    printf '%s' "$secret_value"
}
mc_host_value() {
    access_key=$1
    secret_key=$2
    for credential in "$access_key" "$secret_key"; do
        case "$credential" in *[!A-Za-z0-9._+-]*) fail "credential is unsafe for MC_HOST" ;; esac
    done
    printf 'http://%s:%s@minio:9000' "$access_key" "$secret_key"
}

: "${MINIO_ENDPOINT:?MINIO_ENDPOINT is required}"
: "${MINIO_MEDIA_BUCKET:?MINIO_MEDIA_BUCKET is required}"
: "${S3_RUNTIME_ACCESS_KEY_FILE:?S3_RUNTIME_ACCESS_KEY_FILE is required}"
: "${S3_RUNTIME_SECRET_KEY_FILE:?S3_RUNTIME_SECRET_KEY_FILE is required}"
: "${MC_CONFIG_DIR:=/tmp/mc-live-multipart}"
[ "$MINIO_ENDPOINT" = http://minio:9000 ] || fail "endpoint is not internal"
[ -f /fixtures/multipart.bin ] && [ -r /fixtures/multipart.bin ] \
    || fail "multipart fixture is unavailable"

runtime_access=$(read_secret "$S3_RUNTIME_ACCESS_KEY_FILE")
runtime_secret=$(read_secret "$S3_RUNTIME_SECRET_KEY_FILE")
umask 077
mkdir -p "$MC_CONFIG_DIR"
MC_HOST_runtime=$(mc_host_value "$runtime_access" "$runtime_secret")
export MC_HOST_runtime
key=staging/acceptance/incomplete-multipart-$$.bin
target=runtime/$MINIO_MEDIA_BUCKET/$key

# A rate-limited 128 MiB copy necessarily selects multipart upload. SIGKILL
# prevents the client from helpfully cleaning up its upload ID, after which the
# runtime identity must be able to list and abort that exact incomplete upload.
mc --config-dir "$MC_CONFIG_DIR" cp --limit-upload=1MiB \
    /fixtures/multipart.bin "$target" >/tmp/multipart-upload.log 2>&1 &
upload_pid=$!
sleep 10
kill -9 "$upload_pid" >/dev/null 2>&1 || fail "multipart upload exited before interruption"
if wait "$upload_pid" 2>/dev/null; then fail "interrupted multipart upload unexpectedly succeeded"; fi
mc --config-dir "$MC_CONFIG_DIR" ls --incomplete "$target" > /tmp/incomplete-before.txt
grep -F "${key##*/}" /tmp/incomplete-before.txt >/dev/null \
    || fail "interrupted multipart upload was not retained"
mc --config-dir "$MC_CONFIG_DIR" rm --incomplete --force "$target" >/dev/null
mc --config-dir "$MC_CONFIG_DIR" ls --incomplete "$target" > /tmp/incomplete-after.txt
if grep -F "${key##*/}" /tmp/incomplete-after.txt >/dev/null; then
    fail "runtime abort left an incomplete multipart upload"
fi
unset MC_HOST_runtime runtime_access runtime_secret
printf '%s\n' "storage live multipart: incomplete upload listed and aborted" >&2

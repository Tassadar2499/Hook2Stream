#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
temporary_dir=$(mktemp -d)
trap 'rm -rf "$temporary_dir"' EXIT HUP INT TERM

fail_test() {
    printf '%s\n' "storage probe test: $*" >&2
    exit 1
}

mock_bin=$temporary_dir/bin
mkdir -p "$mock_bin"

cat > "$mock_bin/curl" <<'EOF'
#!/bin/sh
set -eu
output=
while [ "$#" -gt 0 ]; do
    case "$1" in
        --output) output=$2; shift 2 ;;
        --write-out) shift 2 ;;
        *) shift ;;
    esac
done
printf '%s' "${PROTOCOL_BODY:-1}" > "$output"
printf '%s' 200
EOF

cat > "$mock_bin/aws" <<'EOF'
#!/bin/sh
set -eu
[ -f "$AWS_CONFIG_FILE" ] \
    && grep -Fx '    addressing_style = path' "$AWS_CONFIG_FILE" >/dev/null \
    || exit 92
[ "$AWS_SHARED_CREDENTIALS_FILE" = /dev/null ] || exit 93
operation=
output_file=
previous=
for argument in "$@"; do
    if [ "$previous" = s3api ]; then operation=$argument; fi
    previous=$argument
    output_file=$argument
done
printf '%s\n' "$operation" >> "$AWS_OPERATION_LOG"
case "$operation" in
    put-object|delete-object) ;;
    head-object) printf '%s\n' 28 ;;
    get-object) printf '%s' storage > "$output_file" ;;
    *) exit 91 ;;
esac
EOF
chmod 0755 "$mock_bin/curl" "$mock_bin/aws"

access_key_file=$temporary_dir/access
secret_key_file=$temporary_dir/secret
printf '%s\n' probe-access-key > "$access_key_file"
printf '%s\n' probe-secret-key > "$secret_key_file"
operation_log=$temporary_dir/operations
: > "$operation_log"

PATH="$mock_bin:$PATH" \
AWS_OPERATION_LOG=$operation_log \
PROTOCOL_BODY=1 \
DEPLOYMENT_ENVIRONMENT=staging \
S3_ENDPOINT=https://h2s-storage-staging.tail1234.ts.net \
S3_REGION=us-east-1 \
S3_BUCKET=hook2stream-staging-media \
STORAGE_PROTOCOL_URL=https://h2s-storage-staging.tail1234.ts.net/.well-known/hook2stream-storage-protocol \
STORAGE_PROTOCOL_VERSION=1 \
S3_ACCESS_KEY_FILE=$access_key_file \
S3_SECRET_KEY_FILE=$secret_key_file \
    sh "$deployment_dir/scripts/storage-probe.sh" >/dev/null

expected_operations='put-object
head-object
get-object
delete-object'
[ "$(cat "$operation_log")" = "$expected_operations" ] \
    || fail_test "S3 operations were not PUT, HEAD, single Range GET, DELETE in order"

: > "$operation_log"
if PATH="$mock_bin:$PATH" \
    AWS_OPERATION_LOG=$operation_log \
    PROTOCOL_BODY=2 \
    DEPLOYMENT_ENVIRONMENT=staging \
    S3_ENDPOINT=https://h2s-storage-staging.tail1234.ts.net \
    S3_REGION=us-east-1 \
    S3_BUCKET=hook2stream-staging-media \
    STORAGE_PROTOCOL_URL=https://h2s-storage-staging.tail1234.ts.net/.well-known/hook2stream-storage-protocol \
    STORAGE_PROTOCOL_VERSION=1 \
    S3_ACCESS_KEY_FILE=$access_key_file \
    S3_SECRET_KEY_FILE=$secret_key_file \
        sh "$deployment_dir/scripts/storage-probe.sh" >/dev/null 2>&1; then
    fail_test "wrong storage protocol body was accepted"
fi
[ ! -s "$operation_log" ] \
    || fail_test "authenticated S3 operations ran before protocol validation"

printf '%s\n%s\n' first second > "$access_key_file"
if PATH="$mock_bin:$PATH" \
    AWS_OPERATION_LOG=$operation_log \
    PROTOCOL_BODY=1 \
    DEPLOYMENT_ENVIRONMENT=staging \
    S3_ENDPOINT=https://h2s-storage-staging.tail1234.ts.net \
    S3_REGION=us-east-1 \
    S3_BUCKET=hook2stream-staging-media \
    STORAGE_PROTOCOL_URL=https://h2s-storage-staging.tail1234.ts.net/.well-known/hook2stream-storage-protocol \
    STORAGE_PROTOCOL_VERSION=1 \
    S3_ACCESS_KEY_FILE=$access_key_file \
    S3_SECRET_KEY_FILE=$secret_key_file \
        sh "$deployment_dir/scripts/storage-probe.sh" >/dev/null 2>&1; then
    fail_test "multi-line access key was accepted"
fi
[ ! -s "$operation_log" ] \
    || fail_test "S3 operations ran with a malformed credential file"
printf '%s\n' probe-access-key > "$access_key_file"

probe_line=$(grep -n '^    current_stage=remote-storage-probe$' "$deployment_dir/scripts/deploy-release.sh" | cut -d: -f1)
backup_line=$(grep -n '^current_stage=pre-migration-backup$' "$deployment_dir/scripts/deploy-release.sh" | cut -d: -f1)
bootstrap_line=$(grep -n '^current_stage=bootstrap$' "$deployment_dir/scripts/deploy-release.sh" | cut -d: -f1)
[ "$probe_line" -lt "$backup_line" ] && [ "$probe_line" -lt "$bootstrap_line" ] \
    || fail_test "storage probe does not run before backup and migrations"

grep -A35 '^  storage-probe:' "$deployment_dir/compose.yaml" \
    | grep -Fq 'HTTPS_PROXY: http://egress-s3:3128' \
    || fail_test "storage probe does not use the role-specific egress proxy"
grep -Fq 'addressing_style = path' "$deployment_dir/scripts/storage-probe.sh" \
    || fail_test "storage probe does not force path-style S3 addressing"

printf '%s\n' \
    "storage probe test: exact protocol v1, path-style S3, and PUT/HEAD/single-Range/DELETE ordering passed"

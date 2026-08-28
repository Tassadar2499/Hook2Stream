#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
strict_probes=$deployment_dir/storj/strict-probes.sh
bootstrap=$deployment_dir/storj/bootstrap-buckets.sh
acceptance=$deployment_dir/storj/live-acceptance.sh
temporary_dir=$(mktemp -d)
trap 'rm -rf -- "$temporary_dir"' EXIT HUP INT TERM

fail_test() {
    printf '%s\n' "Storj strict probe test: $*" >&2
    exit 1
}

for required_file in "$strict_probes" "$bootstrap" "$acceptance"; do
    [ -f "$required_file" ] || fail_test "missing ${required_file}"
    sh -n "$required_file" || fail_test "invalid shell syntax in ${required_file}"
done
for consumer in "$bootstrap" "$acceptance"; do
    grep -Fq '. "$script_dir/strict-probes.sh"' "$consumer" \
        || fail_test "${consumer} does not source the shared strict probe contract"
    grep -Fq 'storj_initialize_operator_runtime' "$consumer" \
        || fail_test "${consumer} does not initialize the trusted operator runtime"
    grep -Fq 'storj_require_private_anonymous_get' "$consumer" \
        || fail_test "${consumer} bypasses the strict anonymous privacy probe"
    grep -Fq 'storj_write_aws_credentials_file' "$consumer" \
        || fail_test "${consumer} does not use private temporary AWS credentials"
    grep -Fq '"$operator_secret_dir/.hook2stream-storj-' "$consumer" \
        || fail_test "${consumer} does not create its workspace inside the encrypted secret directory"
    if grep -Fq '/tmp/hook2stream-storj-' "$consumer"; then
        fail_test "${consumer} copies operator credentials to /tmp"
    fi
    if grep -Eq 'export[[:space:]]+AWS_(ACCESS_KEY_ID|SECRET_ACCESS_KEY)' "$consumer"; then
        fail_test "${consumer} exports plaintext credentials into the shared environment"
    fi
done
grep -Fq 'storj_error_is_missing_bucket "$head_error_code"' "$bootstrap" \
    || fail_test "bootstrap bypasses the exact missing-bucket allowlist"
grep -Fq 'storj_error_is_missing_cors "$cors_error_code"' "$bootstrap" \
    || fail_test "bootstrap bypasses the exact missing-CORS allowlist"

fixture_dir=$temporary_dir/fixtures
mock_bin=$temporary_dir/bin
mkdir -m 700 "$fixture_dir" "$mock_bin"
printf '%s\n' \
    'An error occurred (NoSuchBucket) when calling the HeadBucket operation: The specified bucket does not exist' \
    > "$fixture_dir/head-no-such-bucket.stderr"
printf '%s\n' \
    'An error occurred (AccessDenied) when calling the HeadBucket operation: Access Denied' \
    > "$fixture_dir/head-access-denied.stderr"
printf '%s\n' \
    'An error occurred (500) when calling the HeadBucket operation: Internal Server Error' \
    > "$fixture_dir/head-500.stderr"
printf '%s\n' \
    'An error occurred (NoSuchCORSConfiguration) when calling the GetBucketCors operation: The CORS configuration does not exist' \
    > "$fixture_dir/cors-none.stderr"
printf '%s\n' \
    'An error occurred (AccessDenied) when calling the GetBucketCors operation: Access Denied' \
    > "$fixture_dir/cors-access-denied.stderr"
printf '%s\n' \
    'An error occurred (404) when calling the GetBucketCors operation: Not Found' \
    > "$fixture_dir/cors-404.stderr"
for permission_code in AccessDenied Forbidden 403; do
    printf '%s\n' \
        "An error occurred (${permission_code}) when calling the PutObject operation: Permission denied" \
        > "$fixture_dir/put-${permission_code}.stderr"
done
printf '%s\n' \
    'An error occurred (NoSuchBucket) when calling the PutObject operation: The specified bucket does not exist' \
    > "$fixture_dir/put-no-such-bucket.stderr"
printf '%s\n' \
    'An error occurred (500) when calling the PutObject operation: Internal Server Error' \
    > "$fixture_dir/put-500.stderr"
printf '%s\n' \
    'An error occurred (SlowDown) when calling the PutObject operation: Please reduce your request rate' \
    > "$fixture_dir/put-throttled.stderr"
: > "$fixture_dir/put-network.stderr"

# shellcheck source=../storj/strict-probes.sh
. "$strict_probes"

[ "$(storj_aws_error_code_from_file \
    "$fixture_dir/head-no-such-bucket.stderr" HeadBucket)" = NoSuchBucket ] \
    || fail_test "NoSuchBucket fixture was not parsed exactly"
storj_error_is_missing_bucket NoSuchBucket \
    && storj_error_is_missing_bucket NotFound \
    && storj_error_is_missing_bucket 404 \
    || fail_test "exact missing-bucket allowlist is incomplete"
for rejected_head_code in AccessDenied 403 500 InternalError; do
    if storj_error_is_missing_bucket "$rejected_head_code"; then
        fail_test "unsafe HeadBucket code was accepted: ${rejected_head_code}"
    fi
done
[ "$(storj_aws_error_code_from_file \
    "$fixture_dir/head-access-denied.stderr" HeadBucket)" = AccessDenied ] \
    || fail_test "HeadBucket AccessDenied fixture was not parsed exactly"
[ "$(storj_aws_error_code_from_file \
    "$fixture_dir/head-500.stderr" HeadBucket)" = 500 ] \
    || fail_test "HeadBucket 500 fixture was not parsed exactly"
[ "$(storj_aws_error_code_from_file \
    "$fixture_dir/cors-none.stderr" GetBucketCors)" = NoSuchCORSConfiguration ] \
    || fail_test "NoSuchCORSConfiguration fixture was not parsed exactly"
storj_error_is_missing_cors NoSuchCORSConfiguration \
    || fail_test "exact missing-CORS code was rejected"
for rejected_cors_code in AccessDenied 403 404 500 NoSuchBucket; do
    if storj_error_is_missing_cors "$rejected_cors_code"; then
        fail_test "unsafe GetBucketCors code was accepted: ${rejected_cors_code}"
    fi
done
for permission_code in AccessDenied Forbidden 403; do
    storj_require_permission_denied_error \
        "$fixture_dir/put-${permission_code}.stderr" PutObject \
        || fail_test "exact permission denial was rejected: ${permission_code}"
done
for rejected_permission_fixture in \
    "$fixture_dir/put-no-such-bucket.stderr" \
    "$fixture_dir/put-500.stderr" \
    "$fixture_dir/put-throttled.stderr" \
    "$fixture_dir/put-network.stderr"; do
    if storj_require_permission_denied_error \
        "$rejected_permission_fixture" PutObject; then
        fail_test "ambiguous or non-permission failure proved role isolation"
    fi
done

for required_operation in PutObject HeadObject DeleteObject ListObjectsV2; do
    grep -Fq "$required_operation" "$acceptance" \
        || fail_test "live acceptance does not parse ${required_operation} permission errors"
done
grep -Fq 'storj_require_permission_denied_error' "$acceptance" \
    || fail_test "live acceptance bypasses exact permission-error parsing"

# Code-loading variables are rejected before credentials or provider tools are
# touched. Test them inside an already-running shell to avoid host loader noise.
if (PYTHONPATH=$temporary_dir/injected; export PYTHONPATH; \
    storj_reject_inherited_code_environment) >/dev/null 2>&1; then
    fail_test "inherited PYTHONPATH was accepted"
fi
if (LD_PRELOAD=$temporary_dir/injected.so; export LD_PRELOAD; \
    storj_reject_inherited_code_environment) >/dev/null 2>&1; then
    fail_test "inherited LD_PRELOAD was accepted"
fi
if (AWS_CLI_PLUGIN_PATH=$temporary_dir/plugins; export AWS_CLI_PLUGIN_PATH; \
    storj_reject_inherited_code_environment) >/dev/null 2>&1; then
    fail_test "inherited AWS_CLI_PLUGIN_PATH was accepted"
fi

# An attacker-controlled PATH is discarded. Whether or not the host has AWS
# CLI installed, no executable from this directory may run during validation.
cat > "$mock_bin/aws" <<'MOCK_PATH_TOOL'
#!/bin/sh
: > "${STORJ_PATH_SENTINEL:?}"
exit 97
MOCK_PATH_TOOL
cp "$mock_bin/aws" "$mock_bin/stat"
cp "$mock_bin/aws" "$mock_bin/readlink"
chmod 0755 "$mock_bin/aws" "$mock_bin/stat" "$mock_bin/readlink"
path_sentinel=$temporary_dir/path-tool-ran
(
    PATH=$mock_bin
    STORJ_PATH_SENTINEL=$path_sentinel
    export PATH STORJ_PATH_SENTINEL
    storj_initialize_operator_runtime
) >/dev/null 2>&1 || true
[ ! -e "$path_sentinel" ] \
    || fail_test "trusted runtime executed a tool from the caller-controlled PATH"

# Credential sources are canonical, regular, current-operator-owned 0600 files
# with one hard link in a private root/operator-owned directory.
secret_dir=$temporary_dir/secrets
mkdir -m 700 "$secret_dir"
access_file=$secret_dir/access
secret_file=$secret_dir/secret
printf '%s\n' bootstrap-access > "$access_file"
printf '%s\n' bootstrap-secret > "$secret_file"
chmod 0600 "$access_file" "$secret_file"
STORJ_OPERATOR_UID=$(/usr/bin/id -u)
storj_validate_credential_file "$access_file" 'fixture access' \
    || fail_test "valid operator credential was rejected"

generated_credentials=$temporary_dir/generated-credentials
storj_write_aws_credentials_file \
    "$access_file" "$secret_file" "$generated_credentials" fixture \
    || fail_test "valid credentials were not converted to a private AWS file"
[ "$(/usr/bin/stat -c %a "$generated_credentials")" = 600 ] \
    || fail_test "temporary AWS credentials are not mode 0600"
grep -Fq 'aws_access_key_id = bootstrap-access' "$generated_credentials" \
    && grep -Fq 'aws_secret_access_key = bootstrap-secret' "$generated_credentials" \
    || fail_test "temporary AWS credentials contain unexpected values"

if (cd "$temporary_dir" && \
    storj_validate_credential_file secrets/access relative) >/dev/null 2>&1; then
    fail_test "relative credential path was accepted"
fi
if storj_validate_credential_file \
    "$secret_dir/../secrets/access" noncanonical >/dev/null 2>&1; then
    fail_test "non-canonical credential path was accepted"
fi
ln -s "$access_file" "$secret_dir/access-link"
if storj_validate_credential_file "$secret_dir/access-link" symlink >/dev/null 2>&1; then
    fail_test "symlink credential was accepted"
fi
chmod 0640 "$access_file"
if storj_validate_credential_file "$access_file" mode >/dev/null 2>&1; then
    fail_test "credential mode other than 0600 was accepted"
fi
chmod 0600 "$access_file"
ln "$access_file" "$secret_dir/access-hardlink"
if storj_validate_credential_file "$access_file" hardlink >/dev/null 2>&1; then
    fail_test "multiply-linked credential was accepted"
fi
rm "$secret_dir/access-hardlink"
chmod 0770 "$secret_dir"
if storj_validate_credential_file "$access_file" directory >/dev/null 2>&1; then
    fail_test "credential in a group-writable directory was accepted"
fi
chmod 0700 "$secret_dir"
printf '%s\n%s\n' first second > "$secret_dir/multiline"
chmod 0600 "$secret_dir/multiline"
if storj_read_single_line_secret "$secret_dir/multiline" multiline >/dev/null 2>&1; then
    fail_test "multiline credential was accepted"
fi

# Unit-run the provider and anonymous probes through deliberately untrusted
# binaries. Production entrypoints overwrite these variables with validated
# root-owned canonical paths before any credential is read. env -i must keep
# proxy, CA, Python, AWS-profile, and arbitrary caller variables out.
aws_home=$temporary_dir/aws-home
mkdir -m 700 "$aws_home"
aws_config=$temporary_dir/aws-config
printf '%s\n' '[default]' 'region = global' > "$aws_config"
cat > "$mock_bin/aws-minimal" <<'MOCK_AWS'
#!/bin/sh
set -eu
if /usr/bin/env | /usr/bin/grep -Eq \
    '^(http_proxy|https_proxy|all_proxy|ftp_proxy|no_proxy|HTTP_PROXY|HTTPS_PROXY|ALL_PROXY|FTP_PROXY|NO_PROXY|AWS_CA_BUNDLE|AWS_ENDPOINT_URL|AWS_ENDPOINT_URL_S3|AWS_PROFILE|AWS_DEFAULT_PROFILE|CURL_CA_BUNDLE|REQUESTS_CA_BUNDLE|SSL_CERT_FILE|SSL_CERT_DIR|LD_|PYTHON|BOTO_|MOCK_|STORJ_PATH_SENTINEL)='; then
    exit 71
fi
[ "$PATH" = /usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin ] || exit 72
fixture_parent=${HOME%/*}
[ "$AWS_CONFIG_FILE" = "$fixture_parent/aws-config" ] || exit 73
[ "$AWS_SHARED_CREDENTIALS_FILE" = "$fixture_parent/generated-credentials" ] || exit 74
[ "$AWS_REGION" = global ] && [ "$AWS_DEFAULT_REGION" = global ] || exit 75
[ "$AWS_EC2_METADATA_DISABLED" = true ] || exit 76
case "$*" in *bootstrap-access*|*bootstrap-secret*) exit 77 ;; esac
/usr/bin/grep -Fq 'aws_access_key_id = bootstrap-access' "$AWS_SHARED_CREDENTIALS_FILE" || exit 78
/usr/bin/grep -Fq 'aws_secret_access_key = bootstrap-secret' "$AWS_SHARED_CREDENTIALS_FILE" || exit 79
printf '%s\n' "$*" > "$HOME/aws-invocation"
MOCK_AWS
chmod 0755 "$mock_bin/aws-minimal"

STORJ_ENV_BIN=/usr/bin/env
STORJ_AWS_BIN=$mock_bin/aws-minimal
STORJ_OPERATOR_HOME=$aws_home
STORJ_AWS_CONFIG_FILE=$aws_config
STORJ_S3_ENDPOINT=https://gateway.storjshare.io
STORJ_S3_REGION=global
export \
    http_proxy=http://proxy.invalid:3128 \
    HTTPS_PROXY=http://proxy.invalid:3128 \
    AWS_CA_BUNDLE=$temporary_dir/untrusted-ca \
    AWS_PROFILE=untrusted \
    PYTHONPATH=$temporary_dir/python-injection \
    MOCK_CALLER_VALUE=untrusted
storj_run_aws "$generated_credentials" s3api head-bucket --bucket fixture \
    || fail_test "minimal AWS execution contract failed"
grep -Fq 's3api head-bucket --bucket fixture' "$aws_home/aws-invocation" \
    || fail_test "AWS test double received unexpected arguments"

cat > "$mock_bin/curl-minimal" <<'MOCK_CURL'
#!/bin/sh
set -eu
if /usr/bin/env | /usr/bin/grep -Eq \
    '^(http_proxy|https_proxy|all_proxy|ftp_proxy|no_proxy|HTTP_PROXY|HTTPS_PROXY|ALL_PROXY|FTP_PROXY|NO_PROXY|AWS_|CURL_CA_BUNDLE|REQUESTS_CA_BUNDLE|SSL_CERT_FILE|SSL_CERT_DIR|LD_|PYTHON|BOTO_|MOCK_)='; then
    exit 81
fi
[ "$1" = -q ] || exit 82
saw_empty_proxy=false
saw_no_proxy=false
saw_zero_redirects=false
last=
while [ "$#" -gt 0 ]; do
    case "$1" in
        --proxy)
            shift
            [ "$#" -gt 0 ] && [ -z "$1" ] || exit 83
            saw_empty_proxy=true
            ;;
        --noproxy)
            shift
            [ "$#" -gt 0 ] && [ "$1" = '*' ] || exit 84
            saw_no_proxy=true
            ;;
        --max-redirs)
            shift
            [ "$#" -gt 0 ] && [ "$1" = 0 ] || exit 85
            saw_zero_redirects=true
            ;;
        --location|-L) exit 86 ;;
    esac
    last=$1
    shift
done
[ "$saw_empty_proxy" = true ] \
    && [ "$saw_no_proxy" = true ] \
    && [ "$saw_zero_redirects" = true ] \
    || exit 87
case "$last" in
    *private-403) printf '%s' 403 ;;
    *private-404) printf '%s' 404 ;;
    *redirect-302) printf '%s' 302 ;;
    *no-content-204) printf '%s' 204 ;;
    *network-failure) exit 7 ;;
    *) exit 88 ;;
esac
MOCK_CURL
chmod 0755 "$mock_bin/curl-minimal"
STORJ_CURL_BIN=$mock_bin/curl-minimal
storj_require_private_anonymous_get https://gateway.storjshare.io/private-403 \
    || fail_test "exact anonymous 403 was rejected"
storj_require_private_anonymous_get https://gateway.storjshare.io/private-404 \
    || fail_test "exact anonymous 404 was rejected"
for rejected_url in \
    https://gateway.storjshare.io/redirect-302 \
    https://gateway.storjshare.io/no-content-204 \
    https://gateway.storjshare.io/network-failure; do
    if storj_require_private_anonymous_get "$rejected_url" >/dev/null 2>&1; then
        fail_test "redirect, 204, or network failure proved marker privacy"
    fi
done

printf '%s\n' \
    "Storj strict probe test: canonical tool/credential trust, minimal provider environment, exact AWS errors, and anonymous 403/404 passed"

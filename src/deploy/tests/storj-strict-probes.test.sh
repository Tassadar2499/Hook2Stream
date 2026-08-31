#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
strict_probes=$deployment_dir/storj/strict-probes.sh
bootstrap=$deployment_dir/storj/bootstrap-buckets.sh
acceptance=$deployment_dir/storj/live-acceptance.sh
installer=$deployment_dir/storj/install-compatible-s3-client.sh
client=$deployment_dir/storj/storj-s3-client.py
client_lock=$deployment_dir/storj/boto3-requirements.lock
temporary_dir=$(mktemp -d)
trap 'rm -rf -- "$temporary_dir"' EXIT HUP INT TERM

fail_test() {
    printf '%s\n' "Storj strict probe test: $*" >&2
    exit 1
}

for required_file in "$strict_probes" "$bootstrap" "$acceptance" "$installer"; do
    [ -f "$required_file" ] || fail_test "missing ${required_file}"
    sh -n "$required_file" || fail_test "invalid shell syntax in ${required_file}"
done
[ -f "$client" ] && [ -f "$client_lock" ] \
    || fail_test "pinned Storj S3 client or dependency lock is missing"
/usr/bin/python3 -c \
    'import ast, pathlib, sys; ast.parse(pathlib.Path(sys.argv[1]).read_text())' \
    "$client" || fail_test "pinned Storj S3 client has invalid Python syntax"
grep -Fq 'boto3==1.35.99' "$client_lock" \
    && grep -Fq 'botocore==1.35.99' "$client_lock" \
    && [ "$(grep -c -- '--hash=sha256:' "$client_lock")" -eq 7 ] \
    || fail_test "Storj dependency lock is not exact and fully hashed"
grep -Fq -- '--require-hashes' "$installer" \
    && grep -Fq -- '--only-binary=:all:' "$installer" \
    && grep -Fq -- 'PIP_CONFIG_FILE=/dev/null PIP_NO_INPUT=1' "$installer" \
    && grep -Fq -- '-m pip --isolated install' "$installer" \
    && grep -Fq '/opt/hook2stream-storj-s3-client-v1-boto3-1.35.99-1feba5d7c2f0' "$installer" \
    && grep -Fq '. "$strict_probes"' "$installer" \
    && grep -Fq 'storj_initialize_operator_runtime' "$installer" \
    || fail_test "Storj client installer is not reproducible or path-pinned"
created_install_line=$(grep -n '^created_install=true$' "$installer" | cut -d: -f1)
venv_create_line=$(grep -n ' -m venv "$install_root"' "$installer" | cut -d: -f1)
[ -n "$created_install_line" ] && [ -n "$venv_create_line" ] \
    && [ "$created_install_line" -lt "$venv_create_line" ] \
    || fail_test "partial venv creation is not covered by installer cleanup"
grep -Fq 'storj_require_safe_client_tree' "$strict_probes" \
    && grep -Fq 'unexpected symlink' "$strict_probes" \
    && grep -Fq 'unsafe ownership or mode' "$strict_probes" \
    || fail_test "operator runtime does not validate the entire pinned venv before Python"
if grep -R -Eq 'awscli==|hook2stream-storj-awscli|/bin/aws([[:space:]]|$)' \
    "$deployment_dir/storj"; then
    fail_test "obsolete AWS CLI dependency remains in the Storj operator contract"
fi
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
if grep -Eq '(get|put|delete)-bucket-cors|(Get|Put|Delete)BucketCors' "$bootstrap"; then
    fail_test "bootstrap calls the unsupported Storj bucket CORS API"
fi
grep -Fq 'STORJ_REQUIRED_BOTO3_VERSION=1.35.99' "$strict_probes" \
    && grep -Fq 'STORJ_REQUIRED_BOTOCORE_VERSION=1.35.99' "$strict_probes" \
    && grep -Fq 'storj_require_compatible_s3_client' "$strict_probes" \
    && grep -Fq '"$STORJ_PYTHON_BIN" -I -E -s' "$strict_probes" \
    || fail_test "operator runtime does not pin and isolate the Storj-compatible boto3 client"
client_digest=$(sha256sum "$client" | cut -d' ' -f1)
grep -Fq "STORJ_S3_CLIENT_SHA256=${client_digest}" "$strict_probes" \
    || fail_test "runtime client digest does not match the checked-in source"

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

# The same tree primitive used before executing an existing root install must
# reject writable drift. Use the current test UID only for this isolated helper;
# production always passes UID 0 from storj_require_safe_client_tree.
tree_fixture=$temporary_dir/client-tree
mkdir -m 0755 "$tree_fixture"
printf '%s\n' safe > "$tree_fixture/package.py"
chmod 0644 "$tree_fixture/package.py"
STORJ_FIND_BIN=/usr/bin/find
tree_fixture_uid=$(/usr/bin/id -u)
[ -z "$(storj_first_unsafe_client_tree_entry "$tree_fixture" "$tree_fixture_uid")" ] \
    || fail_test "safe pinned client tree fixture was rejected"
chmod 0666 "$tree_fixture/package.py"
[ "$(storj_first_unsafe_client_tree_entry "$tree_fixture" "$tree_fixture_uid")" = \
    "$tree_fixture/package.py" ] \
    || fail_test "group/other-writable pinned client code was accepted"
chmod 0644 "$tree_fixture/package.py"
wrong_tree_uid=$((tree_fixture_uid + 1))
[ "$(storj_first_unsafe_client_tree_entry "$tree_fixture" "$wrong_tree_uid")" = \
    "$tree_fixture" ] \
    || fail_test "wrong-owner pinned client tree was accepted"

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

client_fixture=$fixture_dir/client-fixture.py
printf '%s\n' '# client fixture' > "$client_fixture"
client_fixture_digest=$(sha256sum "$client_fixture" | cut -d' ' -f1)
cat > "$mock_bin/python-compatible" <<'MOCK_CLIENT_VERSION'
#!/bin/sh
[ "$1" = -I ] && [ "$2" = -E ] && [ "$3" = -s ] \
    && [ "$5" = --self-check ] || exit 90
printf '%s\n' 'hook2stream-storj-s3/1 boto3/1.35.99 botocore/1.35.99 Python/3.12'
MOCK_CLIENT_VERSION
cat > "$mock_bin/python-wrong-boto3" <<'MOCK_CLIENT_VERSION'
#!/bin/sh
printf '%s\n' 'hook2stream-storj-s3/1 boto3/1.36.0 botocore/1.35.99 Python/3.12'
MOCK_CLIENT_VERSION
cat > "$mock_bin/python-wrong-botocore" <<'MOCK_CLIENT_VERSION'
#!/bin/sh
printf '%s\n' 'hook2stream-storj-s3/1 boto3/1.35.99 botocore/1.36.0 Python/3.12'
MOCK_CLIENT_VERSION
cat > "$mock_bin/python-version-multiline" <<'MOCK_CLIENT_VERSION'
#!/bin/sh
printf '%s\n%s\n' \
    'hook2stream-storj-s3/1 boto3/1.35.99 botocore/1.35.99 Python/3.12' \
    'unexpected second line'
MOCK_CLIENT_VERSION
cat > "$mock_bin/python-version-failure" <<'MOCK_CLIENT_VERSION'
#!/bin/sh
exit 95
MOCK_CLIENT_VERSION
chmod 0755 \
    "$mock_bin/python-compatible" \
    "$mock_bin/python-wrong-boto3" \
    "$mock_bin/python-wrong-botocore" \
    "$mock_bin/python-version-multiline" \
    "$mock_bin/python-version-failure"
STORJ_ENV_BIN=/usr/bin/env
STORJ_SHA256SUM_BIN=/usr/bin/sha256sum
STORJ_S3_CLIENT_BIN=$client_fixture
STORJ_S3_CLIENT_SHA256=$client_fixture_digest
STORJ_PYTHON_BIN=$mock_bin/python-compatible
storj_require_compatible_s3_client \
    || fail_test "exact Storj-compatible boto3 client version was rejected"
for rejected_client in \
    python-wrong-boto3 \
    python-wrong-botocore \
    python-version-multiline \
    python-version-failure; do
    STORJ_PYTHON_BIN=$mock_bin/$rejected_client
    if storj_require_compatible_s3_client >/dev/null 2>&1; then
        fail_test "incompatible boto3 client fixture was accepted: ${rejected_client}"
    fi
done
STORJ_PYTHON_BIN=$mock_bin/python-compatible
STORJ_S3_CLIENT_SHA256=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
if storj_require_compatible_s3_client >/dev/null 2>&1; then
    fail_test "modified Storj S3 client source was accepted"
fi

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

# Unit-run the provider and anonymous probes through test doubles. Production
# entrypoints overwrite these variables with validated root-owned canonical
# paths. env -i must keep proxy, CA, Python, profile, and caller values out.
client_home=$temporary_dir/client-home
mkdir -m 700 "$client_home"
aws_config=$temporary_dir/client-config
printf '%s\n' '[default]' 'region = global' > "$aws_config"
cat > "$mock_bin/python-minimal" <<'MOCK_CLIENT'
#!/bin/sh
set -eu
if /usr/bin/env | /usr/bin/grep -Eq '^(http_proxy|https_proxy|all_proxy|ftp_proxy|no_proxy|HTTP_PROXY|HTTPS_PROXY|ALL_PROXY|FTP_PROXY|NO_PROXY|AWS_CA_BUNDLE|AWS_ENDPOINT_URL|AWS_ENDPOINT_URL_S3|AWS_PROFILE|AWS_DEFAULT_PROFILE|CURL_CA_BUNDLE|REQUESTS_CA_BUNDLE|SSL_CERT_FILE|SSL_CERT_DIR|LD_|PYTHON(PATH|HOME|STARTUP|USERBASE|INSPECT|BREAKPOINT)=|BOTO_|MOCK_|STORJ_PATH_SENTINEL)='; then
    exit 71
fi
[ "$PATH" = /usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin ] || exit 72
fixture_parent=${HOME%/*}
[ "$AWS_CONFIG_FILE" = "$fixture_parent/client-config" ] || exit 73
[ "$AWS_SHARED_CREDENTIALS_FILE" = "$fixture_parent/generated-credentials" ] || exit 74
[ "$AWS_REGION" = global ] && [ "$AWS_DEFAULT_REGION" = global ] || exit 75
[ "$AWS_EC2_METADATA_DISABLED" = true ] || exit 76
[ "$PYTHONNOUSERSITE" = 1 ] || exit 80
case "$*" in *bootstrap-access*|*bootstrap-secret*) exit 77 ;; esac
/usr/bin/grep -Fq 'aws_access_key_id = bootstrap-access' "$AWS_SHARED_CREDENTIALS_FILE" || exit 78
/usr/bin/grep -Fq 'aws_secret_access_key = bootstrap-secret' "$AWS_SHARED_CREDENTIALS_FILE" || exit 79
printf '%s\n' "$*" > "$HOME/aws-invocation"
MOCK_CLIENT
chmod 0755 "$mock_bin/python-minimal"

STORJ_ENV_BIN=/usr/bin/env
STORJ_PYTHON_BIN=$mock_bin/python-minimal
STORJ_S3_CLIENT_BIN=$client_fixture
STORJ_OPERATOR_HOME=$client_home
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
storj_run_s3_client "$generated_credentials" s3api head-bucket \
    --bucket hook2stream-com-staging-media \
    || fail_test "minimal pinned S3 client execution contract failed"
grep -Fq -- \
    '-I -E -s' "$client_home/aws-invocation" \
    && grep -Fq \
        's3api head-bucket --bucket hook2stream-com-staging-media' \
        "$client_home/aws-invocation" \
    || fail_test "pinned S3 client test double received unexpected arguments"
if storj_run_s3_client "$generated_credentials" codeartifact login \
    >/dev/null 2>&1; then
    fail_test "non-S3 command reached the pinned provider client"
fi
if storj_run_s3_client "$generated_credentials" s3api put-bucket-cors \
    >/dev/null 2>&1; then
    fail_test "operation outside the fixed S3 allowlist reached the client"
fi

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

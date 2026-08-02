#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
temporary_dir=$(mktemp -d)

cleanup() {
    rm -rf "$temporary_dir"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

fail_test() {
    printf '%s\n' "deployment smoke status test: $*" >&2
    exit 1
}

stub_bin=${temporary_dir}/bin
mkdir -p "$stub_bin"
cat > "${stub_bin}/curl" <<'EOF'
#!/bin/sh
set -eu
printf '%s' "${TEST_HTTP_STATUS:-000}"
[ "${TEST_CURL_FAILURE:-false}" != true ]
EOF
cat > "${stub_bin}/sleep" <<'EOF'
#!/bin/sh
exit 0
EOF
chmod 0700 "${stub_bin}/curl" "${stub_bin}/sleep"

environment_file=${temporary_dir}/deployment.env
: > "$environment_file"
deployment_program=deployment-smoke-status-test
HOOK2STREAM_ENV_FILE=$environment_file
HOOK2STREAM_PUBLIC_SMOKE_TIMEOUT_SECONDS=1
export HOOK2STREAM_ENV_FILE HOOK2STREAM_PUBLIC_SMOKE_TIMEOUT_SECONDS
. "$deployment_dir/scripts/lib/deployment-common.sh"

PATH="${stub_bin}:${PATH}"
export PATH

TEST_HTTP_STATUS=200
export TEST_HTTP_STATUS
wait_for_url https://app.example.invalid/health/ready >/dev/null \
    || fail_test "exact HTTP 200 did not pass"

TEST_HTTP_STATUS=302
export TEST_HTTP_STATUS
if wait_for_url https://app.example.invalid/health/ready >/dev/null 2>&1; then
    fail_test "HTTP redirect passed the rollout gate"
fi

TEST_HTTP_STATUS=000
TEST_CURL_FAILURE=true
export TEST_HTTP_STATUS TEST_CURL_FAILURE
if wait_for_url https://app.example.invalid/health/ready >/dev/null 2>&1; then
    fail_test "transport failure passed the rollout gate"
fi

printf '%s\n' "deployment smoke status test: only exact HTTP 200 passes"

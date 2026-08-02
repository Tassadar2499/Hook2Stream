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
    printf '%s\n' "environment override preflight test: $*" >&2
    exit 1
}

environment_file=${temporary_dir}/deployment.env
cat > "$environment_file" <<'EOF'
API_IMAGE=registry.invalid/hook2stream-api@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
EOF

deployment_program=environment-override-preflight-test
HOOK2STREAM_ENV_FILE=$environment_file
export HOOK2STREAM_ENV_FILE
. "$deployment_dir/scripts/lib/deployment-common.sh"

override_output=${temporary_dir}/override-output
if (API_IMAGE=registry.invalid/hook2stream-api:latest; export API_IMAGE; \
    deployment_reject_compose_environment_overrides) \
    >"$override_output" 2>&1; then
    fail_test "an exported image override bypassed the environment file"
fi
grep -F 'API_IMAGE' "$override_output" >/dev/null \
    || fail_test "the rejected image override was not identified by name"
if grep -F 'registry.invalid/hook2stream-api:latest' "$override_output" >/dev/null; then
    fail_test "the rejected environment value was exposed in diagnostics"
fi

if (COMPOSE_PROFILES=tools; export COMPOSE_PROFILES; \
    deployment_reject_compose_environment_overrides) \
    >"$override_output" 2>&1; then
    fail_test "an exported Compose control variable bypassed preflight"
fi
grep -F 'COMPOSE_PROFILES' "$override_output" >/dev/null \
    || fail_test "the rejected Compose control variable was not identified"

printf '%s\n' "environment override preflight test: exported overrides are rejected"

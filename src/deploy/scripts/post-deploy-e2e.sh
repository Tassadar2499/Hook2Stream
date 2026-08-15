#!/bin/sh
set -eu

fail() { printf '%s\n' "post-deploy E2E: $*" >&2; exit 1; }
[ "$#" -eq 3 ] || fail "usage: post-deploy-e2e.sh staging|production ENV_FILE COMMIT_SHA"
environment=$1; environment_file=$2; commit=$3
: "${HOOK2STREAM_AUTHENTICATED_E2E_HOOK:?HOOK2STREAM_AUTHENTICATED_E2E_HOOK is required}"
case "$environment" in staging|production) ;; *) fail "invalid environment" ;; esac
case "$commit" in *[!0-9a-f]*|'') fail "invalid commit" ;; esac
[ "${#commit}" -eq 40 ] || fail "invalid commit"
[ -r "$environment_file" ] || fail "environment file is not readable"
for tool in curl jq mktemp; do command -v "$tool" >/dev/null 2>&1 || fail "$tool is required"; done
[ -x "$HOOK2STREAM_AUTHENTICATED_E2E_HOOK" ] && [ ! -L "$HOOK2STREAM_AUTHENTICATED_E2E_HOOK" ] \
    || fail "authenticated E2E hook must be an executable non-symlink file"
[ "$(stat -c '%u:%a' "$HOOK2STREAM_AUTHENTICATED_E2E_HOOK")" = "0:500" ] \
    || fail "authenticated E2E hook must be root-owned mode 0500"

origin=$(awk -F= '$1 == "PUBLIC_ORIGIN" {print substr($0,index($0,"=")+1)}' "$environment_file")
case "$environment:$origin" in staging:https://staging.hook2stream.com|production:https://hook2stream.com) ;; *) fail "public origin does not match environment" ;; esac
temporary_dir=$(mktemp -d)
trap 'rm -rf "$temporary_dir"' EXIT HUP INT TERM
chmod 0700 "$temporary_dir"

request() {
    name=$1; path=$2
    status=$(curl --silent --show-error --proto '=https' --tlsv1.2 --connect-timeout 5 --max-time 30 \
        --output "$temporary_dir/$name.body" --dump-header "$temporary_dir/$name.headers" \
        --write-out '%{http_code}' "$origin$path") || fail "$name request failed"
    [ "$status" = 200 ] || fail "$name returned HTTP $status"
}

request root /
request ready /health/ready
request api-ready /health/api-ready
request anonymous-session /api/v1/auth/session
jq -e '
  .authenticated == false and .subject == null and .email == null and
  .displayName == null and .expiresAt == null and .csrfToken == null
' "$temporary_dir/anonymous-session.body" >/dev/null || fail "anonymous session response shape is unsafe"

if [ "$environment" = staging ]; then
    robots=$(awk 'BEGIN{IGNORECASE=1} /^X-Robots-Tag:/ {sub(/^[^:]*:[[:space:]]*/, ""); sub(/\r$/, ""); print; exit}' "$temporary_dir/root.headers")
    [ "$robots" = "noindex, nofollow, noarchive" ] || fail "staging noindex header is missing"
fi

# Health checks are not evidence that encrypted media, workers, rendering and
# billing still work. A separately installed root-owned scenario owns its
# environment-specific OAuth/billing credentials and must emit exactly this
# capability record after exercising the full release gate. Its output is
# captured so credentials or browser traces cannot leak into deploy logs.
expected_gate='HOOK2STREAM_E2E_GATE=oauth,h2se-upload-range,workers-openrouter,preview-render18-zip,stripe-idempotency,egress-deny'
if ! authenticated_gate=$("$HOOK2STREAM_AUTHENTICATED_E2E_HOOK" "$environment" "$environment_file" "$commit" \
    2>"$temporary_dir/authenticated-e2e.stderr"); then
    fail "authenticated encrypted-media/worker/billing scenario failed"
fi
[ "$authenticated_gate" = "$expected_gate" ] \
    || fail "authenticated E2E hook did not attest the complete release gate"
unset authenticated_gate

printf '%s\n' "post-deploy E2E: public, authenticated H2SE, worker/render/export, and billing gates passed for $commit"

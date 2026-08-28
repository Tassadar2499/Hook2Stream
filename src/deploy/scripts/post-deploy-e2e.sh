#!/bin/sh
set -eu

fail() { printf '%s\n' "post-deploy E2E: $*" >&2; exit 1; }
[ "$#" -eq 4 ] \
    || fail "usage: post-deploy-e2e.sh staging|production ENV_FILE COMMIT_SHA OPERATION_ID|rollback-verify|soak-60m"
environment=$1; environment_file=$2; commit=$3; mode=$4
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
child_pid=
cleanup() { rm -rf "$temporary_dir"; }
forward_signal() {
    trap - HUP INT TERM
    if [ -n "$child_pid" ]; then
        kill -TERM "$child_pid" 2>/dev/null || true
        wait "$child_pid" 2>/dev/null || true
    fi
    exit 130
}
trap cleanup EXIT
trap forward_signal HUP INT TERM
chmod 0700 "$temporary_dir"

if [ "$mode" = soak-60m ]; then
    [ "$environment" = staging ] || fail "the sustained soak is staging-only"
    "$HOOK2STREAM_AUTHENTICATED_E2E_HOOK" \
        "$environment" "$environment_file" "$commit" soak-60m \
        >"$temporary_dir/soak.stdout" 2>"$temporary_dir/soak.stderr" &
    child_pid=$!
    if ! wait "$child_pid"; then
        child_pid=
        fail "authenticated render/network soak failed"
    fi
    child_pid=
    [ "$(wc -c < "$temporary_dir/soak.stdout")" -le 8192 ] \
        && [ "$(wc -l < "$temporary_dir/soak.stdout")" -eq 1 ] \
        && [ "$(tail -c 1 "$temporary_dir/soak.stdout" | od -An -tu1 | tr -d ' ')" = 10 ] \
        || fail "authenticated soak output must be one bounded newline-terminated JSON line"
    jq -e '
      (keys | sort) == ["completedRenderCount","cpuThrottled","maxConcurrentRenderJobs","networkChecks","networkFailures","oomKilled","renderActiveSeconds","schema"] and
      .schema == "hook2stream-soak-hook-result-v1" and
      (.completedRenderCount | type == "number" and floor == . and . > 0) and
      (.renderActiveSeconds | type == "number" and floor == . and . >= 3300) and
      .maxConcurrentRenderJobs == 1 and
      (.networkChecks | type == "number" and floor == . and . >= 60) and
      .networkFailures == 0 and .cpuThrottled == false and .oomKilled == false
    ' "$temporary_dir/soak.stdout" >/dev/null || fail "authenticated soak result is invalid"
    cat "$temporary_dir/soak.stdout"
    exit 0
fi
rollback_verification=false
if [ "$mode" = rollback-verify ]; then
    rollback_verification=true
else
case "$mode" in
  *[!0-9a-f]*|'') fail "authenticated E2E operation ID is invalid" ;;
esac
[ "${#mode}" -eq 32 ] || fail "authenticated E2E operation ID is invalid"
fi

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

# Health checks are not encrypted-media or worker evidence. A root-owned
# scenario owns its environment-specific OAuth inputs and emits the exact
# environment capability below. Only staging mutates test billing/final render;
# production stops after upload/OpenRouter/preview. Output is captured so no
# credential or browser trace reaches deploy logs.
if [ "$rollback_verification" = true ]; then
  expected_gate='HOOK2STREAM_ROLLBACK_GATE=oauth,h2se-range,workers-state,preview-export,egress-deny'
else
case "$environment" in
  staging)
    expected_gate='HOOK2STREAM_E2E_GATE=oauth,h2se-upload-range,workers-openrouter,preview-render18-zip,stripe-test-idempotency,egress-deny'
    ;;
  production)
    expected_gate='HOOK2STREAM_E2E_GATE=oauth,h2se-upload-range,workers-openrouter,preview,egress-deny'
    ;;
esac
fi
"$HOOK2STREAM_AUTHENTICATED_E2E_HOOK" \
    "$environment" "$environment_file" "$commit" "$mode" \
    >"$temporary_dir/authenticated-e2e.stdout" \
    2>"$temporary_dir/authenticated-e2e.stderr" &
child_pid=$!
if ! wait "$child_pid"; then
    child_pid=
    fail "authenticated environment release scenario failed"
fi
child_pid=
[ "$(wc -c < "$temporary_dir/authenticated-e2e.stdout")" -le 4096 ] \
    && [ "$(wc -l < "$temporary_dir/authenticated-e2e.stdout")" -eq 1 ] \
    || fail "authenticated environment release scenario output is invalid"
authenticated_gate=$(cat "$temporary_dir/authenticated-e2e.stdout")
[ "$authenticated_gate" = "$expected_gate" ] \
    || fail "authenticated E2E hook did not attest the complete release gate"
unset authenticated_gate

if [ "$rollback_verification" = true ]; then
  printf '%s\n' "post-deploy E2E: bounded rollback evidence reverified for $commit"
elif [ "$environment" = staging ]; then
  printf '%s\n' "post-deploy E2E: staging H2SE, workers, render/export, and Stripe-test gates passed for $commit"
else
  printf '%s\n' "post-deploy E2E: production H2SE upload, workers, OpenRouter, and preview gates passed for $commit"
fi

#!/bin/sh
set -eu

deployment_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
wrapper=$deployment_root/scripts/deploy-forced-command.sh

fail() {
    printf '%s\n' "forced-command deployment-dir contract test: $*" >&2
    exit 1
}

extract_lookup() {
    lookup_block=$1
    lookup_lines=$(printf '%s\n' "$lookup_block" | grep -F 'deployment-common.sh' || true)
    [ -n "$lookup_lines" ] \
        && [ "$(printf '%s\n' "$lookup_lines" | wc -l | tr -d ' ')" -eq 1 ] \
        || fail "target path must contain exactly one deployment-common lookup"
    printf '%s\n' "$lookup_lines" | cut -d "'" -f 2
}

collect_block=$(sed -n '/^collect_actual_images() {/,/^}/p' "$wrapper")
recovery_block=$(sed -n '/^write_recovery_required() {/,/^}/p' "$wrapper")
soak_block=$(sed -n '/^  soak)$/,/^  rollback)$/p' "$wrapper")

collect_lookup=$(extract_lookup "$collect_block")
recovery_lookup=$(extract_lookup "$recovery_block")
soak_lookup=$(extract_lookup "$soak_block")

[ "$collect_lookup" = 'deployment_dir=$1; . "$deployment_dir/scripts/lib/deployment-common.sh"; compose ps -q "$2"' ] \
    || fail "collect_actual_images does not initialize deployment_dir before sourcing the shared library"
[ "$recovery_lookup" = 'deployment_dir=$1; . "$deployment_dir/scripts/lib/deployment-common.sh"; compose ps -q caddy' ] \
    || fail "write_recovery_required does not initialize deployment_dir before its Caddy lookup"
[ "$soak_lookup" = 'deployment_dir=$1; . "$deployment_dir/scripts/lib/deployment-common.sh"; compose ps -q worker-render' ] \
    || fail "soak does not initialize deployment_dir before its worker-render lookup"

if grep -Fq '. "$1/scripts/lib/deployment-common.sh"' "$wrapper"; then
    fail "a child shell still sources deployment-common through positional path expansion"
fi
[ "$(grep -Fc 'deployment_dir=$1; . "$deployment_dir/scripts/lib/deployment-common.sh"; compose ps -q' "$wrapper")" -eq 3 ] \
    || fail "the wrapper must contain exactly the three reviewed deployment_dir-aware lookups"

scratch=$(mktemp -d)
cleanup() {
    rm -rf "$scratch"
}
trap cleanup EXIT
trap 'exit 130' HUP INT TERM

fixture=$scratch/deploy
fixture_environment=$scratch/candidate.env
mkdir -p "$fixture/scripts/lib"
: > "$fixture_environment"
{
    printf '%s\n' ': "${deployment_dir:?deployment_dir must be set before sourcing deployment-common.sh}"'
    printf '%s\n' '[ "$deployment_dir" = "$EXPECTED_DEPLOYMENT_DIR" ] || exit 91'
    printf '%s\n' '[ "$HOOK2STREAM_ENV_FILE" = "$EXPECTED_ENVIRONMENT_FILE" ] || exit 92'
    printf '%s\n' 'compose() {'
    printf '%s\n' '    [ "$#" -eq 3 ] && [ "$1" = ps ] && [ "$2" = -q ] || exit 93'
    printf '%s\n' '    printf "fixture-%s\\n" "$3"'
    printf '%s\n' '}'
} > "$fixture/scripts/lib/deployment-common.sh"

exercise_lookup() {
    lookup=$1
    expected_service=$2
    shift 2
    actual=$(
        EXPECTED_DEPLOYMENT_DIR=$fixture \
        EXPECTED_ENVIRONMENT_FILE=$fixture_environment \
        HOOK2STREAM_ENV_FILE=$fixture_environment \
            sh -c "$lookup" _ "$fixture" "$@"
    ) || fail "$expected_service lookup could not source the deployment-scoped library"
    [ "$actual" = "fixture-$expected_service" ] \
        || fail "$expected_service lookup did not invoke Compose for the expected service"
}

exercise_lookup "$collect_lookup" api api
exercise_lookup "$recovery_lookup" caddy
exercise_lookup "$soak_lookup" worker-render

printf '%s\n' \
    "forced-command deployment-dir contract test: collect, recovery Caddy, and soak worker lookups passed"

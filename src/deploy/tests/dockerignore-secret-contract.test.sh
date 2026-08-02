#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
dockerignore=${deployment_dir}/../../.dockerignore

fail() {
    printf '%s\n' "dockerignore secret contract test: $*" >&2
    exit 1
}

[ -r "$dockerignore" ] || fail "root .dockerignore is missing"
grep -Fx 'src/deploy/secrets' "$dockerignore" >/dev/null \
    || fail "src/deploy/secrets is not excluded from every image build context"
if grep -Eq '^!/?src/deploy/secrets(/|$)' "$dockerignore"; then
    fail "a later Docker ignore exception re-includes deployment secrets"
fi

printf '%s\n' "dockerignore secret contract test: deployment secrets are excluded"

#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
dockerignore=${deployment_dir}/../../.dockerignore
gitignore=${deployment_dir}/../../.gitignore

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
[ -r "$gitignore" ] || fail "root .gitignore is missing"
grep -Fx '/src/deploy/secrets/*' "$gitignore" >/dev/null \
    || fail "extensionless deployment secrets are not excluded from Git"
grep -Fx '!/src/deploy/secrets/README.md' "$gitignore" >/dev/null \
    || fail "the non-secret secrets README is not explicitly retained"

printf '%s\n' "dockerignore secret contract test: deployment secrets are excluded from Docker and Git"

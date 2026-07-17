#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"

cd "$REPO_ROOT"

if [[ -z "${ASPIRE_ALLOW_UNSECURED_TRANSPORT+x}" ]] &&
    ! dotnet dev-certs https --check --trust >/dev/null 2>&1; then
  export ASPIRE_ALLOW_UNSECURED_TRANSPORT=true
fi

exec dotnet run --project src/Hook2Stream.AppHost/Hook2Stream.AppHost.csproj "$@"

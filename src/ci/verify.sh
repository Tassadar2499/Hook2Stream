#!/usr/bin/env bash
set -euo pipefail

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
contract_snapshot="$(mktemp -d)"
trap 'rm -rf "$contract_snapshot"' EXIT

cp "$source_root/Hook2Stream.Api/openapi/Hook2Stream.Api.json" \
  "$contract_snapshot/Hook2Stream.Api.json"
cp "$source_root/web/src/lib/api-schema.d.ts" \
  "$contract_snapshot/api-schema.d.ts"

dotnet test "$source_root/Hook2Stream.slnx" \
  --no-restore \
  -m:1 \
  -nodeReuse:false
npm run check --prefix "$source_root/web"
npm run lint --prefix "$source_root/web"
npm run test:unit --prefix "$source_root/web"
npm run build --prefix "$source_root/web"
"$source_root/ci/generate-contracts.sh"

contracts_changed=false
if ! cmp -s \
  "$contract_snapshot/Hook2Stream.Api.json" \
  "$source_root/Hook2Stream.Api/openapi/Hook2Stream.Api.json"; then
  echo "Generated OpenAPI contract is stale." >&2
  diff -u \
    "$contract_snapshot/Hook2Stream.Api.json" \
    "$source_root/Hook2Stream.Api/openapi/Hook2Stream.Api.json" || true
  contracts_changed=true
fi
if ! cmp -s \
  "$contract_snapshot/api-schema.d.ts" \
  "$source_root/web/src/lib/api-schema.d.ts"; then
  echo "Generated TypeScript API contract is stale." >&2
  diff -u \
    "$contract_snapshot/api-schema.d.ts" \
    "$source_root/web/src/lib/api-schema.d.ts" || true
  contracts_changed=true
fi
if [[ "$contracts_changed" == true ]]; then
  exit 1
fi

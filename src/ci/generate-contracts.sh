#!/usr/bin/env bash
set -euo pipefail

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

ASPNETCORE_ENVIRONMENT=Testing \
DOTNET_ENVIRONMENT=Testing \
Auth__Mode=Local \
Auth__LocalToken=contract-generation-token \
StorageEncryption__Mode=Plaintext \
dotnet build "$source_root/Hook2Stream.Api/Hook2Stream.Api.csproj" \
  --no-restore \
  -m:1 \
  -nodeReuse:false \
  -p:OpenApiGenerateDocumentsOnBuild=true \
  -p:OpenApiDocumentsDirectory="$source_root/Hook2Stream.Api/openapi"

npm run generate:api --prefix "$source_root/web"

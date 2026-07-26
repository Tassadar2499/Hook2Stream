#!/usr/bin/env bash
set -euo pipefail

source_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
repo_root="$(cd "$source_root/.." && pwd)"
runtime_root="$source_root/web/test-results/full-stack/runtime"
services_log="$runtime_root/apphost.log"
e2e_token="${HOOK2STREAM_E2E_AUTH_TOKEN:-hook2stream-e2e-local-auth-token-20260725-fixed}"
apphost_pid=""

mkdir -p "$runtime_root"

cleanup() {
  local exit_code=$?
  trap - EXIT INT TERM
  if [[ -n "$apphost_pid" ]] && kill -0 "$apphost_pid" >/dev/null 2>&1; then
    kill -TERM -- "-$apphost_pid" >/dev/null 2>&1 || true
    for _ in {1..20}; do
      kill -0 "$apphost_pid" >/dev/null 2>&1 || break
      sleep 1
    done
    kill -KILL -- "-$apphost_pid" >/dev/null 2>&1 || true
  fi
  docker ps -a --format \
    'table {{.Names}}\t{{.Image}}\t{{.Status}}\t{{.Ports}}' \
    > "$runtime_root/docker-ps.txt" 2>&1 || true
  exit "$exit_code"
}
trap cleanup EXIT INT TERM

wait_for_url() {
  local name=$1
  local url=$2
  local attempts=${3:-120}
  for ((attempt = 1; attempt <= attempts; attempt++)); do
    if curl --fail --silent --show-error "$url" >/dev/null 2>&1; then
      return 0
    fi
    if [[ -n "$apphost_pid" ]] && ! kill -0 "$apphost_pid" >/dev/null 2>&1; then
      echo "$name stopped before becoming ready. See $services_log." >&2
      return 1
    fi
    sleep 2
  done
  echo "Timed out waiting for $name at $url. See $services_log." >&2
  return 1
}

command -v docker >/dev/null
command -v ffmpeg >/dev/null
command -v ffprobe >/dev/null
command -v unzip >/dev/null

"$source_root/ci/generate-e2e-media.sh" "$runtime_root"

dotnet build "$source_root/Hook2Stream.slnx" \
  -m:1 \
  -nodeReuse:false

env \
  NEXT_PUBLIC_API_BASE_URL=http://127.0.0.1:5100 \
  NEXT_PUBLIC_AUTH_MODE=local \
  NEXT_PUBLIC_LOCAL_AUTH_TOKEN="$e2e_token" \
  npm run build --prefix "$source_root/web"

cd "$repo_root"
setsid env \
  ASPIRE_ALLOW_UNSECURED_TRANSPORT=true \
  ASPNETCORE_ENVIRONMENT=Testing \
  DOTNET_ENVIRONMENT=Testing \
  HOOK2STREAM_E2E=1 \
  HOOK2STREAM_E2E_AUTH_TOKEN="$e2e_token" \
  dotnet run \
    --project "$source_root/Hook2Stream.AppHost/Hook2Stream.AppHost.csproj" \
    --no-build \
    --launch-profile "Hook2Stream.AppHost E2E" \
    > "$services_log" 2>&1 &
apphost_pid=$!

wait_for_url "Hook2Stream API" "http://127.0.0.1:5100/health/ready"
wait_for_url "Hook2Stream web" "http://127.0.0.1:3100/"

env \
  HOOK2STREAM_FULL_STACK=1 \
  HOOK2STREAM_E2E_API_BASE_URL=http://127.0.0.1:5100 \
  HOOK2STREAM_E2E_AUTH_TOKEN="$e2e_token" \
  HOOK2STREAM_E2E_MP3="$runtime_root/fixture-master.mp3" \
  HOOK2STREAM_E2E_WAV="$runtime_root/fixture-master.wav" \
  PLAYWRIGHT_BASE_URL=http://127.0.0.1:3100 \
  npm run test:e2e:full-stack --prefix "$source_root/web"

#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"

cd "$REPO_ROOT"

GRACE_SECONDS="${H2S_STOP_GRACE_SECONDS:-15}"

# Collect AppHost PIDs: both the `dotnet run --project ...AppHost.csproj` wrapper
# (launched by run.sh) and the built AppHost binary it spawns. Signaling the
# AppHost binary with SIGINT lets Aspire/DCP tear down the Dashboard, Worker,
# Api and any other resources gracefully.
mapfile -t PIDS < <(
  {
    pgrep -f "dotnet run --project .*Hook2Stream\.AppHost\.csproj" || true
    pgrep -f "Hook2Stream\.AppHost/bin/[^ ]*/Hook2Stream\.AppHost( |$)" || true
  } | grep -v "^$$\$" | sort -u
)

if [[ "${#PIDS[@]}" -eq 0 || -z "${PIDS[0]:-}" ]]; then
  echo "No running Hook2Stream.AppHost process found."
  exit 0
fi

echo "Stopping Hook2Stream.AppHost (PIDs: ${PIDS[*]})..."
kill -INT "${PIDS[@]}" 2>/dev/null || true

deadline=$(( $(date +%s) + GRACE_SECONDS ))
while (( $(date +%s) < deadline )); do
  alive=0
  for pid in "${PIDS[@]}"; do
    if kill -0 "$pid" 2>/dev/null; then alive=1; fi
  done
  [[ "$alive" -eq 0 ]] && break
  sleep 1
done

still_alive=()
for pid in "${PIDS[@]}"; do
  if kill -0 "$pid" 2>/dev/null; then still_alive+=("$pid"); fi
done

if [[ "${#still_alive[@]}" -gt 0 ]]; then
  echo "Did not exit after ${GRACE_SECONDS}s, sending SIGKILL (PIDs: ${still_alive[*]})..."
  kill -KILL "${still_alive[@]}" 2>/dev/null || true
fi

echo "Stopped."

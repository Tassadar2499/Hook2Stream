#!/usr/bin/env bash
set -euo pipefail

ci_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_root="$(cd "$ci_dir/.." && pwd)"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

sha="$(git -C "$source_root/.." rev-parse HEAD)"
run_id=123456
run_attempt=2
repository=example/Hook2Stream
fragments="$scratch/fragments"
candidate="$scratch/candidate"
mkdir -p "$fragments" "$candidate"

index=0
for key in API_IMAGE WORKER_IMAGE BOOTSTRAPPER_IMAGE WEB_IMAGE POSTGRES_BACKUP_IMAGE CADDY_IMAGE POSTGRES_IMAGE PGBOUNCER_IMAGE EGRESS_PROXY_IMAGE; do
  index=$((index + 1))
  digest="$(printf '%064x' "$index")"
  case "$key" in
    API_IMAGE) image=ghcr.io/example/hook2stream-api ;;
    WORKER_IMAGE) image=ghcr.io/example/hook2stream-worker ;;
    BOOTSTRAPPER_IMAGE) image=ghcr.io/example/hook2stream-bootstrapper ;;
    WEB_IMAGE) image=ghcr.io/example/hook2stream-web ;;
    POSTGRES_BACKUP_IMAGE) image=ghcr.io/example/hook2stream-postgres-backup ;;
    CADDY_IMAGE) image=caddy ;;
    POSTGRES_IMAGE) image=postgres ;;
    PGBOUNCER_IMAGE) image=edoburu/pgbouncer ;;
    EGRESS_PROXY_IMAGE) image=ubuntu/squid ;;
  esac
  printf '%s=%s\n' "$key" "${image}@sha256:${digest}" > "$fragments/${index}.env"
done

node "$ci_dir/release-candidate.mjs" create \
  --output "$candidate" \
  --fragments "$fragments" \
  --deploy-dir "$source_root/deploy" \
  --repository "$repository" \
  --sha "$sha" \
  --run-id "$run_id" \
  --run-attempt "$run_attempt"

node "$ci_dir/release-candidate.mjs" validate \
  --candidate "$candidate" \
  --repository "$repository" \
  --sha "$sha" \
  --run-id "$run_id" \
  --run-attempt "$run_attempt"

receipt="$scratch/staging-receipt.json"
node - "$candidate" "$scratch/remote-result.json" <<'JS'
const fs = require("fs");
const crypto = require("crypto");
const path = require("path");
const [candidate, output] = process.argv.slice(2);
const metadata = JSON.parse(fs.readFileSync(path.join(candidate, "release-metadata.json")));
const digest = (name) => crypto.createHash("sha256").update(fs.readFileSync(path.join(candidate, name))).digest("hex");
fs.writeFileSync(output, JSON.stringify({
  schemaVersion: 1,
  kind: "hook2stream-remote-deploy-result",
  environment: "staging",
  result: "success",
  candidateArtifact: metadata.artifactName,
  commitSha: metadata.commitSha,
  releaseImagesSha256: digest("release-images.env"),
  deployBundleSha256: digest("deploy-bundle.tar.gz"),
  actualImages: metadata.images,
  checks: ["pre-migration-backup", "migration", "smoke", "e2e", "digest-verification"],
}) + "\n");
JS
node "$ci_dir/remote-deploy-result.mjs" validate \
  --candidate "$candidate" \
  --result "$scratch/remote-result.json" \
  --environment staging
printf '%s\n' "{\"schemaVersion\":1,\"kind\":\"hook2stream-remote-rollback-result\",\"environment\":\"staging\",\"result\":\"success\",\"releaseSha\":\"$sha\",\"storageFormat\":\"H2SEv1\",\"minimumRollbackReleaseSha\":\"$sha\",\"checks\":[\"target-recorded-success\",\"storage-format-compatible\",\"application-images-only\",\"infrastructure-unchanged\",\"no-migrations\",\"smoke\",\"e2e\",\"digest-verification\"]}" > "$scratch/rollback-result.json"
node "$ci_dir/remote-deploy-result.mjs" validate-rollback \
  --result "$scratch/rollback-result.json" \
  --environment staging \
  --release-sha "$sha" \
  --storage-format H2SEv1 \
  --minimum-release-sha "$sha"
node "$ci_dir/staging-receipt.mjs" create \
  --candidate "$candidate" \
  --remote-result "$scratch/remote-result.json" \
  --output "$receipt"
node "$ci_dir/staging-receipt.mjs" validate \
  --candidate "$candidate" \
  --receipt "$receipt" \
  --repository "$repository" \
  --sha "$sha" \
  --run-id "$run_id" \
  --run-attempt "$run_attempt"

cp -a "$candidate" "$scratch/tampered"
printf '\nTAMPERED\n' >> "$scratch/tampered/release-images.env"
if node "$ci_dir/release-candidate.mjs" validate --candidate "$scratch/tampered" >/dev/null 2>&1; then
  echo "tampered candidate unexpectedly validated" >&2
  exit 1
fi

cp -a "$candidate" "$scratch/extra-file"
touch "$scratch/extra-file/unexpected"
if node "$ci_dir/release-candidate.mjs" validate --candidate "$scratch/extra-file" >/dev/null 2>&1; then
  echo "candidate with an extra file unexpectedly validated" >&2
  exit 1
fi

cp -a "$candidate" "$scratch/mismatched-receipt-candidate"
python3 - "$scratch/mismatched-receipt-candidate/release-metadata.json" <<'PY'
import json
import sys

path = sys.argv[1]
with open(path, encoding="utf-8") as handle:
    data = json.load(handle)
data["ciRunId"] += 1
with open(path, "w", encoding="utf-8") as handle:
    json.dump(data, handle)
    handle.write("\n")
PY
if node "$ci_dir/staging-receipt.mjs" validate \
  --candidate "$scratch/mismatched-receipt-candidate" \
  --receipt "$receipt" >/dev/null 2>&1; then
  echo "receipt for a different candidate unexpectedly validated" >&2
  exit 1
fi

echo "release candidate and staging receipt contract tests passed"

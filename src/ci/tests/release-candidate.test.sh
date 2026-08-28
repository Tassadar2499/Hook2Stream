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
    CADDY_IMAGE) image=ghcr.io/example/hook2stream-caddy ;;
    POSTGRES_IMAGE) image=ghcr.io/example/hook2stream-postgres ;;
    PGBOUNCER_IMAGE) image=ghcr.io/example/hook2stream-pgbouncer ;;
    EGRESS_PROXY_IMAGE) image=ghcr.io/example/hook2stream-egress-proxy ;;
  esac
  printf '%s=%s\n' "$key" "${image}@sha256:${digest}" > "$fragments/${index}.env"
done

GITHUB_REPOSITORY_OWNER=ambient-owner node "$ci_dir/release-candidate.mjs" create \
  --output "$candidate" \
  --fragments "$fragments" \
  --deploy-dir "$source_root/deploy" \
  --repository "$repository" \
  --sha "$sha" \
  --run-id "$run_id" \
  --run-attempt "$run_attempt"

GITHUB_REPOSITORY_OWNER=ambient-owner node "$ci_dir/release-candidate.mjs" validate \
  --candidate "$candidate" \
  --repository "$repository" \
  --sha "$sha" \
  --run-id "$run_id" \
  --run-attempt "$run_attempt"

for runtime_replacement in \
  'hook2stream-caddy|docker.io/library/caddy' \
  'hook2stream-pgbouncer|docker.io/edoburu/pgbouncer' \
  'hook2stream-egress-proxy|docker.io/ubuntu/squid'; do
  hardened_repository=${runtime_replacement%%|*}
  external_repository=${runtime_replacement#*|}
  mutated="$scratch/external-${hardened_repository}"
  cp -a "$candidate" "$mutated"
  sed -i \
    "s#ghcr.io/example/${hardened_repository}@#${external_repository}@#g" \
    "$mutated/release-images.env" \
    "$mutated/release-metadata.json"
  (
    cd "$mutated"
    sha256sum deploy-bundle.tar.gz release-images.env release-metadata.json > SHA256SUMS
  )
  if node "$ci_dir/release-candidate.mjs" validate \
    --candidate "$mutated" \
    --repository "$repository" \
    --sha "$sha" \
    --run-id "$run_id" \
    --run-attempt "$run_attempt" >/dev/null 2>&1; then
    echo "candidate validator accepted external runtime repository: $external_repository" >&2
    exit 1
  fi
done

cp -a "$candidate" "$scratch/official-postgres-candidate"
sed -i \
  's#ghcr.io/example/hook2stream-postgres@#docker.io/library/postgres@#g' \
  "$scratch/official-postgres-candidate/release-images.env" \
  "$scratch/official-postgres-candidate/release-metadata.json"
(
  cd "$scratch/official-postgres-candidate"
  sha256sum deploy-bundle.tar.gz release-images.env release-metadata.json > SHA256SUMS
)
if node "$ci_dir/release-candidate.mjs" validate \
  --candidate "$scratch/official-postgres-candidate" \
  --repository "$repository" \
  --sha "$sha" \
  --run-id "$run_id" \
  --run-attempt "$run_attempt" >/dev/null 2>&1; then
  echo "candidate validator accepted the unhardened official PostgreSQL image" >&2
  exit 1
fi

bundle_listing="$scratch/bundle-listing.txt"
tar -tzf "$candidate/deploy-bundle.tar.gz" > "$bundle_listing"
for forbidden_path in \
  deploy/Caddyfile.minio \
  deploy/compose.minio.yaml \
  deploy/minio \
  deploy/storage \
  deploy/scripts/validate-deployment.sh \
  deploy/tests/caddy-minio-contract.test.sh \
  deploy/tests/minio-overlay-contract.test.sh \
  deploy/tests/minio-release-integration.test.sh; do
  if grep -Eq "^${forbidden_path}(/|$)" "$bundle_listing"; then
    echo "candidate contains local-only path: $forbidden_path" >&2
    exit 1
  fi
done

cp -a "$candidate" "$scratch/forbidden-bundle"
mkdir -p "$scratch/forbidden-tree/deploy/minio"
printf '%s\n' 'local-only' > "$scratch/forbidden-tree/deploy/minio/Dockerfile"
tar -C "$scratch/forbidden-tree" -czf "$scratch/forbidden-bundle/deploy-bundle.tar.gz" deploy
bundle_sha=$(sha256sum "$scratch/forbidden-bundle/deploy-bundle.tar.gz" | awk '{print $1}')
node - "$scratch/forbidden-bundle/release-metadata.json" "$bundle_sha" <<'JS'
const fs = require("fs");
const [path, bundleSha] = process.argv.slice(2);
const metadata = JSON.parse(fs.readFileSync(path, "utf8"));
metadata.deployBundle.sha256 = bundleSha;
fs.writeFileSync(path, `${JSON.stringify(metadata, null, 2)}\n`);
JS
(
  cd "$scratch/forbidden-bundle"
  sha256sum deploy-bundle.tar.gz release-images.env release-metadata.json > SHA256SUMS
)
if node "$ci_dir/release-candidate.mjs" validate \
  --candidate "$scratch/forbidden-bundle" >/dev/null 2>&1; then
  echo "candidate validator accepted a local-only MinIO bundle" >&2
  exit 1
fi

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
  minimumRollbackReleaseSha: metadata.commitSha,
  releaseImagesSha256: digest("release-images.env"),
  deployBundleSha256: digest("deploy-bundle.tar.gz"),
  actualImages: metadata.images,
  checks: ["pre-migration-backup", "migration", "smoke", "e2e", "digest-verification"],
}) + "\n");
JS
node "$ci_dir/remote-deploy-result.mjs" validate \
  --candidate "$candidate" \
  --result "$scratch/remote-result.json" \
  --environment staging \
  --minimum-release-sha "$sha"
node - "$scratch/remote-result.json" "$scratch/remote-result-provider-evidence.json" <<'JS'
const fs = require("fs");
const [source, output] = process.argv.slice(2);
const result = JSON.parse(fs.readFileSync(source, "utf8"));
result.providerWindow = { provider: "digitalocean" };
fs.writeFileSync(output, `${JSON.stringify(result)}\n`);
JS
if node "$ci_dir/remote-deploy-result.mjs" validate \
  --candidate "$candidate" \
  --result "$scratch/remote-result-provider-evidence.json" \
  --environment staging \
  --minimum-release-sha "$sha" >/dev/null 2>&1; then
  echo "staging remote result with provider lifecycle evidence unexpectedly validated" >&2
  exit 1
fi
node - "$scratch/remote-result.json" "$scratch/remote-soak-result.json" <<'JS'
const fs = require("fs");
const [remotePath, output] = process.argv.slice(2);
const remote = JSON.parse(fs.readFileSync(remotePath, "utf8"));
const started = Math.floor((Date.now() - 3_600_000) / 1000) * 1000;
const timestamp = (milliseconds) => new Date(milliseconds).toISOString().replace(".000Z", "Z");
fs.writeFileSync(output, `${JSON.stringify({
  schemaVersion: 1,
  kind: "hook2stream-remote-soak-result",
  environment: "staging",
  result: "success",
  candidateArtifact: remote.candidateArtifact,
  commitSha: remote.commitSha,
  startedAt: timestamp(started),
  completedAt: timestamp(started + 3_600_000),
  elapsedSeconds: 3600,
  hookResult: {
    schema: "hook2stream-soak-hook-result-v1",
    completedRenderCount: 1,
    renderActiveSeconds: 3300,
    maxConcurrentRenderJobs: 1,
    networkChecks: 60,
    networkFailures: 0,
    cpuThrottled: false,
    oomKilled: false,
  },
  workerRenderInstances: 1,
  workerRenderHealthy: true,
  workerRenderOomKilled: false,
  checks: ["render-network-soak", "elapsed-window", "single-render-worker", "no-oom"],
})}\n`);
JS
node "$ci_dir/staging-receipt.mjs" validate-soak \
  --candidate "$candidate" \
  --remote-result "$scratch/remote-result.json" \
  --soak-result "$scratch/remote-soak-result.json" \
  --minimum-release-sha "$sha"
node - "$scratch/remote-soak-result.json" "$scratch/remote-soak-failed-network.json" <<'JS'
const fs = require("fs");
const [source, output] = process.argv.slice(2);
const result = JSON.parse(fs.readFileSync(source, "utf8"));
result.hookResult.networkFailures = 1;
fs.writeFileSync(output, `${JSON.stringify(result)}\n`);
JS
if node "$ci_dir/staging-receipt.mjs" validate-soak \
  --candidate "$candidate" \
  --remote-result "$scratch/remote-result.json" \
  --soak-result "$scratch/remote-soak-failed-network.json" \
  --minimum-release-sha "$sha" >/dev/null 2>&1; then
  echo "soak receipt with a network failure unexpectedly validated" >&2
  exit 1
fi
node - "$candidate/release-metadata.json" "$scratch/rollback-result.json" "$sha" <<'JS'
const fs = require("fs");
const [metadataPath, output, sha] = process.argv.slice(2);
const { images } = JSON.parse(fs.readFileSync(metadataPath, "utf8"));
const { BOOTSTRAPPER_IMAGE: preservedBootstrapImage, ...actualRunningImages } = images;
fs.writeFileSync(output, `${JSON.stringify({
  schemaVersion: 1,
  kind: "hook2stream-remote-rollback-result",
  environment: "staging",
  result: "success",
  releaseSha: sha,
  storageFormat: "H2SEv1",
  minimumRollbackReleaseSha: sha,
  actualRunningImages,
  preservedBootstrapImage,
  checks: ["target-recorded-success", "storage-format-compatible", "application-images-only", "infrastructure-unchanged", "no-migrations", "smoke", "e2e", "digest-verification"],
})}\n`);
JS
node "$ci_dir/remote-deploy-result.mjs" validate-rollback \
  --result "$scratch/rollback-result.json" \
  --environment staging \
  --release-sha "$sha" \
  --storage-format H2SEv1 \
  --minimum-release-sha "$sha"
node - "$scratch/rollback-result.json" "$scratch/rollback-result-missing-images.json" <<'JS'
const fs = require("fs");
const [source, output] = process.argv.slice(2);
const result = JSON.parse(fs.readFileSync(source, "utf8"));
delete result.actualRunningImages;
fs.writeFileSync(output, `${JSON.stringify(result)}\n`);
JS
if node "$ci_dir/remote-deploy-result.mjs" validate-rollback \
  --result "$scratch/rollback-result-missing-images.json" \
  --environment staging \
  --release-sha "$sha" \
  --storage-format H2SEv1 \
  --minimum-release-sha "$sha" >/dev/null 2>&1; then
  echo "rollback result without running-image evidence unexpectedly validated" >&2
  exit 1
fi
node "$ci_dir/staging-receipt.mjs" create \
  --candidate "$candidate" \
  --remote-result "$scratch/remote-result.json" \
  --soak-result "$scratch/remote-soak-result.json" \
  --output "$receipt" \
  --staging-run-id 200 \
  --staging-run-attempt 1 \
  --policy-sha "$sha" \
  --minimum-release-sha "$sha"
node "$ci_dir/staging-receipt.mjs" validate \
  --candidate "$candidate" \
  --receipt "$receipt" \
  --repository "$repository" \
  --sha "$sha" \
  --run-id "$run_id" \
  --run-attempt "$run_attempt" \
  --staging-run-id 200 \
  --staging-run-attempt 1 \
  --policy-sha "$sha" \
  --minimum-release-sha "$sha"

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
  --receipt "$receipt" \
  --minimum-release-sha "$sha" >/dev/null 2>&1; then
  echo "receipt for a different candidate unexpectedly validated" >&2
  exit 1
fi

echo "release candidate and staging receipt contract tests passed"

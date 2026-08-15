#!/bin/sh
set -eu

storage_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
validator=$storage_dir/scripts/validate-candidate.sh
scratch=$(mktemp -d)
cleanup() { rm -rf "$scratch"; }
trap cleanup EXIT HUP INT TERM
fail() { printf '%s\n' "storage candidate contract: $*" >&2; exit 1; }
if ! command -v jq >/dev/null 2>&1; then
    printf '%s\n' "storage candidate contract: SKIP (host jq unavailable; CI runs this gate)"
    exit 0
fi

candidate=$scratch/candidate
mkdir -p "$candidate"
sha=1111111111111111111111111111111111111111
digest=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
artifact=storage-candidate-$sha-7-2
tar -C "$storage_dir/.." -czf "$candidate/storage-bundle.tar.gz" storage
cat > "$candidate/storage-images.env" <<EOF
STORAGE_RELEASE_VERSION=$sha
MINIO_IMAGE=ghcr.io/example/hook2stream-minio@sha256:$digest
MINIO_MC_IMAGE=docker.io/minio/mc@sha256:$digest
CADDY_IMAGE=docker.io/library/caddy@sha256:$digest
EOF
bundle_sha=$(sha256sum "$candidate/storage-bundle.tar.gz" | awk '{print $1}')
cat > "$candidate/storage-metadata.json" <<EOF
{"schemaVersion":1,"kind":"hook2stream-storage-candidate","protocolVersion":"storage-forced-command-v1","sourceRef":"refs/heads/main","sourceEvent":"push","workflow":".github/workflows/storage-ci.yml","workflowName":"Storage CI","artifactName":"$artifact","repository":"example/Hook2Stream","commitSha":"$sha","ciRunId":7,"ciRunAttempt":2,"createdAt":"2026-08-15T00:00:00Z","images":{"MINIO_IMAGE":"ghcr.io/example/hook2stream-minio@sha256:$digest","MINIO_MC_IMAGE":"docker.io/minio/mc@sha256:$digest","CADDY_IMAGE":"docker.io/library/caddy@sha256:$digest"},"storageBundle":{"file":"storage-bundle.tar.gz","sha256":"$bundle_sha"}}
EOF
(cd "$candidate" && sha256sum storage-bundle.tar.gz storage-images.env storage-metadata.json > SHA256SUMS)
test "$(sh "$validator" "$candidate" "$artifact" example/Hook2Stream)" = "$sha" \
    || fail "valid immutable candidate was rejected (digest validation must preserve metadata repository)"
future_tree=$scratch/future-tree
mkdir -p "$future_tree"
tar -xzf "$candidate/storage-bundle.tar.gz" -C "$future_tree"
jq '.minioRelease = "RELEASE.2026-12-31T00-00-00Z" |
    .minioSourceCommit = "3333333333333333333333333333333333333333"' \
    "$future_tree/storage/storage-release.json" > "$future_tree/storage/storage-release.json.tmp"
mv "$future_tree/storage/storage-release.json.tmp" "$future_tree/storage/storage-release.json"
tar -C "$future_tree" -czf "$candidate/storage-bundle.tar.gz" storage
bundle_sha=$(sha256sum "$candidate/storage-bundle.tar.gz" | awk '{print $1}')
jq --arg sha "$bundle_sha" '.storageBundle.sha256 = $sha' \
    "$candidate/storage-metadata.json" > "$candidate/storage-metadata.json.tmp"
mv "$candidate/storage-metadata.json.tmp" "$candidate/storage-metadata.json"
(cd "$candidate" && sha256sum storage-bundle.tar.gz storage-images.env storage-metadata.json > SHA256SUMS)
test "$(sh "$validator" "$candidate" "$artifact" example/Hook2Stream)" = "$sha" \
    || fail "release-independent host validator rejected a structurally valid forward source pin"
printf '\n#tamper\n' >> "$candidate/storage-images.env"
if sh "$validator" "$candidate" "$artifact" example/Hook2Stream >/dev/null 2>&1; then
    fail "checksum-tampered candidate was accepted"
fi
printf '%s\n' "storage candidate contract: PASS"

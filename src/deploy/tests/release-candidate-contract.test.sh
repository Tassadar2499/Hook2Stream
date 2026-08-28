#!/bin/sh
set -eu

deployment_dir=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
validator=$deployment_dir/scripts/validate-candidate.sh
temporary_dir=$(mktemp -d)
trap 'rm -rf "$temporary_dir"' EXIT HUP INT TERM
fail() { printf '%s\n' "release candidate contract test: $*" >&2; exit 1; }
if ! command -v jq >/dev/null 2>&1; then
    printf '%s\n' "release candidate contract test: skipped (jq unavailable)"
    exit 0
fi

candidate=$temporary_dir/candidate
bundle_root=$temporary_dir/bundle
mkdir -p "$candidate" "$bundle_root/deploy/scripts"
printf '%s\n' '#!/bin/sh' > "$bundle_root/deploy/scripts/deploy-release.sh"
tar -czf "$candidate/deploy-bundle.tar.gz" -C "$bundle_root" deploy
commit=aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
digest=bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
cat > "$candidate/release-images.env" <<EOF
RELEASE_VERSION=$commit
API_IMAGE=ghcr.io/example/hook2stream-api@sha256:$digest
WORKER_IMAGE=ghcr.io/example/hook2stream-worker@sha256:$digest
BOOTSTRAPPER_IMAGE=ghcr.io/example/hook2stream-bootstrapper@sha256:$digest
WEB_IMAGE=ghcr.io/example/hook2stream-web@sha256:$digest
POSTGRES_BACKUP_IMAGE=ghcr.io/example/hook2stream-postgres-backup@sha256:$digest
CADDY_IMAGE=docker.io/library/caddy@sha256:$digest
POSTGRES_IMAGE=docker.io/library/postgres@sha256:$digest
PGBOUNCER_IMAGE=docker.io/edoburu/pgbouncer@sha256:$digest
EGRESS_PROXY_IMAGE=docker.io/ubuntu/squid@sha256:$digest
EOF
bundle_sha=$(sha256sum "$candidate/deploy-bundle.tar.gz" | awk '{print $1}')
cat > "$candidate/release-metadata.json" <<EOF
{"schemaVersion":1,"kind":"hook2stream-release-candidate","protocolVersion":"forced-command-v1","sourceRef":"refs/heads/main","sourceEvent":"push","workflow":".github/workflows/ci.yml","workflowName":"CI","artifactName":"release-candidate-$commit-1-1","repository":"example/hook2stream","commitSha":"$commit","ciRunId":1,"ciRunAttempt":1,"createdAt":"2026-08-14T00:00:00Z","images":{"API_IMAGE":"ghcr.io/example/hook2stream-api@sha256:$digest","WORKER_IMAGE":"ghcr.io/example/hook2stream-worker@sha256:$digest","BOOTSTRAPPER_IMAGE":"ghcr.io/example/hook2stream-bootstrapper@sha256:$digest","WEB_IMAGE":"ghcr.io/example/hook2stream-web@sha256:$digest","POSTGRES_BACKUP_IMAGE":"ghcr.io/example/hook2stream-postgres-backup@sha256:$digest","CADDY_IMAGE":"docker.io/library/caddy@sha256:$digest","POSTGRES_IMAGE":"docker.io/library/postgres@sha256:$digest","PGBOUNCER_IMAGE":"docker.io/edoburu/pgbouncer@sha256:$digest","EGRESS_PROXY_IMAGE":"docker.io/ubuntu/squid@sha256:$digest"},"deployBundle":{"file":"deploy-bundle.tar.gz","sha256":"$bundle_sha"}}
EOF
(cd "$candidate" && sha256sum release-metadata.json release-images.env deploy-bundle.tar.gz > SHA256SUMS)
chmod 0600 "$candidate"/*

HOOK2STREAM_REPOSITORY=example/hook2stream "$validator" "$candidate" >/dev/null \
    || fail "valid candidate was rejected"

cp "$candidate/deploy-bundle.tar.gz" "$temporary_dir/good-bundle.tar.gz"
cp "$candidate/release-metadata.json" "$temporary_dir/good-metadata.json"
refresh_bundle_identity() {
    refreshed_sha=$(sha256sum "$candidate/deploy-bundle.tar.gz" | awk '{print $1}')
    jq --arg sha "$refreshed_sha" '.deployBundle.sha256 = $sha' \
        "$candidate/release-metadata.json" > "$candidate/release-metadata.json.tmp"
    mv "$candidate/release-metadata.json.tmp" "$candidate/release-metadata.json"
    (cd "$candidate" && sha256sum release-metadata.json release-images.env deploy-bundle.tar.gz > SHA256SUMS)
    chmod 0600 "$candidate"/*
}

forbidden_bundle_root=$temporary_dir/forbidden-bundle
mkdir -p "$forbidden_bundle_root/deploy/minio"
printf '%s\n' 'local-only' > "$forbidden_bundle_root/deploy/minio/Dockerfile"
tar -czf "$candidate/deploy-bundle.tar.gz" -C "$forbidden_bundle_root" deploy
refresh_bundle_identity
if HOOK2STREAM_REPOSITORY=example/hook2stream "$validator" "$candidate" >/dev/null 2>&1; then
    fail "candidate containing local-only MinIO content was accepted"
fi

ci_only_bundle_root=$temporary_dir/ci-only-bundle
mkdir -p "$ci_only_bundle_root/deploy/scripts"
printf '%s\n' '#!/bin/sh' > "$ci_only_bundle_root/deploy/scripts/validate-deployment.sh"
tar -czf "$candidate/deploy-bundle.tar.gz" -C "$ci_only_bundle_root" deploy
refresh_bundle_identity
if HOOK2STREAM_REPOSITORY=example/hook2stream "$validator" "$candidate" >/dev/null 2>&1; then
    fail "candidate containing CI-only deployment validator was accepted"
fi
cp "$temporary_dir/good-bundle.tar.gz" "$candidate/deploy-bundle.tar.gz"
cp "$temporary_dir/good-metadata.json" "$candidate/release-metadata.json"
(cd "$candidate" && sha256sum release-metadata.json release-images.env deploy-bundle.tar.gz > SHA256SUMS)
chmod 0600 "$candidate"/*

truncate -s 67108865 "$candidate/deploy-bundle.tar.gz"
refresh_bundle_identity
if HOOK2STREAM_REPOSITORY=example/hook2stream "$validator" "$candidate" >/dev/null 2>&1; then
    fail "compressed deploy bundle larger than 64 MiB was accepted"
fi

cp "$temporary_dir/good-bundle.tar.gz" "$candidate/deploy-bundle.tar.gz"
cp "$temporary_dir/good-metadata.json" "$candidate/release-metadata.json"
truncate -s 268435457 "$bundle_root/deploy/expanded-bomb"
tar -czf "$candidate/deploy-bundle.tar.gz" -C "$bundle_root" deploy
refresh_bundle_identity
if HOOK2STREAM_REPOSITORY=example/hook2stream "$validator" "$candidate" >/dev/null 2>&1; then
    fail "deploy bundle expanding beyond 256 MiB was accepted"
fi

cp "$temporary_dir/good-bundle.tar.gz" "$candidate/deploy-bundle.tar.gz"
cp "$temporary_dir/good-metadata.json" "$candidate/release-metadata.json"
(cd "$candidate" && sha256sum release-metadata.json release-images.env deploy-bundle.tar.gz > SHA256SUMS)
chmod 0600 "$candidate"/*

cp "$candidate/release-images.env" "$temporary_dir/good.env"
printf '%s\n' 'UNEXPECTED_IMAGE=registry.invalid/image@sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb' >> "$candidate/release-images.env"
(cd "$candidate" && sha256sum release-metadata.json release-images.env deploy-bundle.tar.gz > SHA256SUMS)
if HOOK2STREAM_REPOSITORY=example/hook2stream "$validator" "$candidate" >/dev/null 2>&1; then
    fail "unknown image variable was accepted"
fi
mv "$temporary_dir/good.env" "$candidate/release-images.env"
(cd "$candidate" && sha256sum release-metadata.json release-images.env deploy-bundle.tar.gz > SHA256SUMS)

if HOOK2STREAM_REPOSITORY=wrong/repository "$validator" "$candidate" >/dev/null 2>&1; then
    fail "repository mismatch was accepted"
fi

printf '%s\n' "release candidate contract test: passed"

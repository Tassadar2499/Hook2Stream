#!/bin/sh
set -eu

script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_dir/lib/forced-command-trust.sh"

fail() { printf '%s\n' "candidate validation: $*" >&2; exit 1; }
[ "$#" -ge 1 ] && [ "$#" -le 2 ] || fail "usage: validate-candidate.sh CANDIDATE_DIRECTORY [APPROVAL_DIRECTORY]"
candidate_dir=$1
approval_dir=${2:-}
case "$candidate_dir" in /*) ;; *) fail "candidate path must be absolute" ;; esac
[ -d "$candidate_dir" ] && [ ! -L "$candidate_dir" ] || fail "candidate must be a real directory"

for tool in awk find jq sha256sum ssh-keygen stat tar wc; do command -v "$tool" >/dev/null 2>&1 || fail "$tool is required"; done
for file in release-metadata.json release-images.env deploy-bundle.tar.gz SHA256SUMS; do
    path=$candidate_dir/$file
    [ -f "$path" ] && [ ! -L "$path" ] || fail "$file must be a regular non-symlink file"
done
[ "$(wc -c < "$candidate_dir/deploy-bundle.tar.gz")" -le 67108864 ] \
    || fail "deploy bundle exceeds 64 MiB"
[ "$(find "$candidate_dir" -mindepth 1 -maxdepth 1 | wc -l | tr -d ' ')" -eq 4 ] || fail "candidate must contain exactly four files"
[ -z "$(find "$candidate_dir" -maxdepth 1 -type f -perm /022 -print -quit)" ] || fail "candidate files must not be group/world writable"
(cd "$candidate_dir" && sha256sum --strict --check SHA256SUMS) >/dev/null || fail "SHA256SUMS verification failed"
[ "$(wc -l < "$candidate_dir/SHA256SUMS" | tr -d ' ')" -eq 3 ] || fail "SHA256SUMS must contain exactly three records"
for checksummed in release-metadata.json release-images.env deploy-bundle.tar.gz; do
    grep -Eq "^[0-9a-f]{64}  ${checksummed}$" "$candidate_dir/SHA256SUMS" || fail "SHA256SUMS does not exactly cover $checksummed"
done

repository=${HOOK2STREAM_REPOSITORY:?HOOK2STREAM_REPOSITORY is required}
owner=$(printf '%s' "${repository%%/*}" | tr '[:upper:]' '[:lower:]')
jq -e --arg repository "$repository" '
  .schemaVersion == 1 and .kind == "hook2stream-release-candidate" and
  .protocolVersion == "forced-command-v1" and .repository == $repository and
  .sourceRef == "refs/heads/main" and .sourceEvent == "push" and
  .workflow == ".github/workflows/ci.yml" and
  (.commitSha | type == "string" and test("^[0-9a-f]{40}$")) and
  (.ciRunId | type == "number") and (.ciRunAttempt | type == "number") and
  (.deployBundle.file == "deploy-bundle.tar.gz") and
  (.deployBundle.sha256 | type == "string" and test("^[0-9a-f]{64}$"))
' "$candidate_dir/release-metadata.json" >/dev/null || fail "release metadata contract is invalid"
expected_bundle=$(jq -r '.deployBundle.sha256' "$candidate_dir/release-metadata.json")
actual_bundle=$(sha256sum "$candidate_dir/deploy-bundle.tar.gz" | awk '{print $1}')
[ "$expected_bundle" = "$actual_bundle" ] || fail "bundle digest differs from metadata"

image_names='API_IMAGE WORKER_IMAGE BOOTSTRAPPER_IMAGE WEB_IMAGE POSTGRES_BACKUP_IMAGE CADDY_IMAGE POSTGRES_IMAGE PGBOUNCER_IMAGE EGRESS_PROXY_IMAGE'
commit=$(jq -r '.commitSha' "$candidate_dir/release-metadata.json")
artifact=$(jq -r '.artifactName' "$candidate_dir/release-metadata.json")
ci_run_id=$(jq -r '.ciRunId' "$candidate_dir/release-metadata.json")
ci_attempt=$(jq -r '.ciRunAttempt' "$candidate_dir/release-metadata.json")
[ "$artifact" = "release-candidate-${commit}-${ci_run_id}-${ci_attempt}" ] || fail "artifactName is not canonical"
seen_names=
seen_release=false
while IFS= read -r line || [ -n "$line" ]; do
    case "$line" in ''|'#'*) continue ;; esac
    name=${line%%=*}; value=${line#*=}
    [ "$name" != "$line" ] || fail "release-images.env contains a malformed line"
    if [ "$name" = RELEASE_VERSION ]; then
        [ "$seen_release" = false ] || fail "release-images.env repeats RELEASE_VERSION"
        [ "$value" = "$commit" ] || fail "RELEASE_VERSION differs from commitSha"
        seen_release=true; continue
    fi
    case " $image_names " in *" $name "*) ;; *) fail "release-images.env contains unexpected variable $name" ;; esac
    case " $seen_names " in *" $name "*) fail "release-images.env repeats $name" ;; esac
    printf '%s\n' "$value" | grep -Eq '^[^[:space:]@]+@sha256:[0-9a-f]{64}$' || fail "$name is not digest-only"
    metadata_value=$(jq -r --arg name "$name" '.images[$name] // empty' "$candidate_dir/release-metadata.json")
    [ "$metadata_value" = "$value" ] || fail "$name differs from metadata"
    case "$name:$value" in
      API_IMAGE:ghcr.io/$owner/hook2stream-api@sha256:*|WORKER_IMAGE:ghcr.io/$owner/hook2stream-worker@sha256:*|BOOTSTRAPPER_IMAGE:ghcr.io/$owner/hook2stream-bootstrapper@sha256:*|WEB_IMAGE:ghcr.io/$owner/hook2stream-web@sha256:*|POSTGRES_BACKUP_IMAGE:ghcr.io/$owner/hook2stream-postgres-backup@sha256:*|POSTGRES_IMAGE:ghcr.io/$owner/hook2stream-postgres@sha256:*|CADDY_IMAGE:ghcr.io/$owner/hook2stream-caddy@sha256:*|PGBOUNCER_IMAGE:ghcr.io/$owner/hook2stream-pgbouncer@sha256:*|EGRESS_PROXY_IMAGE:ghcr.io/$owner/hook2stream-egress-proxy@sha256:*) ;;
      *) fail "$name repository is outside the allowlist" ;;
    esac
    seen_names="$seen_names $name"
done < "$candidate_dir/release-images.env"
for name in $image_names; do case " $seen_names " in *" $name "*) ;; *) fail "release-images.env is missing $name" ;; esac; done
[ "$seen_release" = true ] || fail "release-images.env is missing RELEASE_VERSION"

tar -tzf "$candidate_dir/deploy-bundle.tar.gz" | while IFS= read -r member; do
    case "$member" in ''|/*|../*|*/../*|*/..|*'//'*) fail "bundle contains an unsafe path" ;; esac
    case "$member" in deploy|deploy/*) ;; *) fail "bundle member is outside deploy/" ;; esac
    case "$member" in
        deploy/Caddyfile.minio|deploy/compose.minio.yaml|deploy/minio|deploy/minio/*|deploy/storage|deploy/storage/*|deploy/scripts/validate-deployment.sh|deploy/tests/caddy-minio-contract.test.sh|deploy/tests/minio-overlay-contract.test.sh|deploy/tests/minio-release-integration.test.sh)
            fail "bundle contains local-only MinIO/storage-plane or CI validation content"
            ;;
    esac
done
if tar -tvzf "$candidate_dir/deploy-bundle.tar.gz" | awk '$1 !~ /^[d-]/ {bad=1} END {exit bad ? 0 : 1}'; then fail "bundle links and special files are forbidden"; fi
tar -tvzf "$candidate_dir/deploy-bundle.tar.gz" \
    | awk '{total += $3} END {exit total <= 268435456 ? 0 : 1}' \
    || fail "expanded deploy bundle exceeds 256 MiB"

if [ -n "$approval_dir" ]; then
    receipt=$approval_dir/staging-receipt.json; signature=$approval_dir/staging-receipt.sig
    for approval_file in "$receipt" "$signature"; do
        [ -f "$approval_file" ] && [ ! -L "$approval_file" ] \
            || fail "signed staging approval is incomplete"
    done
    [ "$(find "$approval_dir" -mindepth 1 -maxdepth 1 | wc -l | tr -d ' ')" -eq 2 ] \
        || fail "approval must contain exactly one receipt and its signature"
    images_sha=$(sha256sum "$candidate_dir/release-images.env" | awk '{print $1}')
    expected_images=$(jq -c '.images' "$candidate_dir/release-metadata.json")
    minimum_release_sha=${MIN_ROLLBACK_RELEASE_SHA:?MIN_ROLLBACK_RELEASE_SHA is required for production approval}
    printf '%s\n' "$minimum_release_sha" | grep -Eq '^[0-9a-f]{40}$' \
        || fail "MIN_ROLLBACK_RELEASE_SHA must be exactly 40 lowercase hex"
    jq -e --arg repository "$repository" --arg commit "$commit" --arg artifact "$artifact" --arg bundle "$actual_bundle" --arg images "$images_sha" --arg minimum "$minimum_release_sha" --argjson expectedImages "$expected_images" '
      def epoch: fromdateiso8601;
      def canonical_time: type == "string" and test("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$") and (fromdateiso8601 >= 0);
      (.soakResult.elapsedSeconds) as $elapsed |
      (keys | sort) == ["candidateArtifact","checks","ciRunAttempt","ciRunId","commitSha","deployedAt","environment","hashes","kind","policySha","remoteResult","repository","result","schemaVersion","soakResult","stagingWorkflowRunAttempt","stagingWorkflowRunId"] and
      .schemaVersion == 1 and .kind == "hook2stream-staging-receipt" and
      .environment == "staging" and .result == "success" and .repository == $repository and
      .commitSha == $commit and .candidateArtifact == $artifact and
      (.stagingWorkflowRunId | type == "number" and . > 0 and floor == .) and
      (.stagingWorkflowRunAttempt | type == "number" and . > 0 and floor == .) and
      (.policySha | type == "string" and test("^[0-9a-f]{40}$")) and
      (.remoteResult | keys | sort) == ["actualImages","candidateArtifact","checks","commitSha","deployBundleSha256","environment","kind","minimumRollbackReleaseSha","releaseImagesSha256","result","schemaVersion"] and
      .hashes.releaseImagesSha256 == $images and .hashes.deployBundleSha256 == $bundle and
      .remoteResult.kind == "hook2stream-remote-deploy-result" and
      .remoteResult.environment == "staging" and .remoteResult.result == "success" and
      .remoteResult.candidateArtifact == $artifact and
      .remoteResult.commitSha == $commit and .remoteResult.minimumRollbackReleaseSha == $minimum and
      .remoteResult.actualImages == $expectedImages and
      .remoteResult.checks == ["pre-migration-backup","migration","smoke","e2e","digest-verification"] and
      (.soakResult | keys | sort) == ["candidateArtifact","checks","commitSha","completedAt","elapsedSeconds","environment","hookResult","kind","result","schemaVersion","startedAt","workerRenderHealthy","workerRenderInstances","workerRenderOomKilled"] and
      .soakResult.schemaVersion == 1 and .soakResult.kind == "hook2stream-remote-soak-result" and
      .soakResult.environment == "staging" and .soakResult.result == "success" and
      .soakResult.candidateArtifact == $artifact and .soakResult.commitSha == $commit and
      (.soakResult.elapsedSeconds | type == "number" and floor == . and . >= 3600 and . <= 3900) and
      (.soakResult.startedAt | canonical_time) and (.soakResult.completedAt | canonical_time) and
      ((.soakResult.completedAt | epoch) - (.soakResult.startedAt | epoch)) == .soakResult.elapsedSeconds and
      (.deployedAt | canonical_time) and (.deployedAt | epoch) >= (.soakResult.completedAt | epoch) and
      (.soakResult.hookResult | keys | sort) == ["completedRenderCount","cpuThrottled","maxConcurrentRenderJobs","networkChecks","networkFailures","oomKilled","renderActiveSeconds","schema"] and
      .soakResult.hookResult.schema == "hook2stream-soak-hook-result-v1" and
      (.soakResult.hookResult.completedRenderCount | type == "number" and floor == . and . > 0) and
      (.soakResult.hookResult.renderActiveSeconds | type == "number" and floor == . and . >= 3300 and . <= $elapsed) and
      .soakResult.hookResult.maxConcurrentRenderJobs == 1 and
      (.soakResult.hookResult.networkChecks | type == "number" and floor == . and . >= 60) and
      .soakResult.hookResult.networkFailures == 0 and
      .soakResult.hookResult.cpuThrottled == false and .soakResult.hookResult.oomKilled == false and
      .soakResult.workerRenderInstances == 1 and .soakResult.workerRenderHealthy == true and
      .soakResult.workerRenderOomKilled == false and
      .soakResult.checks == ["render-network-soak","elapsed-window","single-render-worker","no-oom"] and
      .checks == ["pre-migration-backup","migration","smoke","e2e","digest-verification","render-network-soak"]
    ' "$receipt" >/dev/null || fail "staging receipt does not approve this candidate"
    signers=${HOOK2STREAM_STAGING_SIGNERS:?HOOK2STREAM_STAGING_SIGNERS is required}
    [ "$signers" = /etc/hook2stream/staging-receipt-allowed-signers ] \
        || fail "staging signer path is not canonical"
    [ -f "$signers" ] && [ ! -L "$signers" ] \
        && [ "$(stat -c '%u:%g:%a' "$signers")" = 0:0:600 ] \
        || fail "staging signers must be root:root mode 0600"
    hook2stream_validate_exact_allowed_signer "$signers" hook2stream-staging \
        || fail "staging signers must contain exactly one hook2stream-staging ED25519 key"
    ssh-keygen -Y verify -f "$signers" -I hook2stream-staging -n hook2stream-staging-receipt -s "$signature" < "$receipt" >/dev/null \
        || fail "staging receipt signature is invalid"
fi

printf '%s\n' "candidate validation: passed"

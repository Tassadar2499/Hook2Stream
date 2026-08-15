#!/bin/sh
set -eu

fail() { printf '%s\n' "candidate validation: $*" >&2; exit 1; }
[ "$#" -ge 1 ] && [ "$#" -le 2 ] || fail "usage: validate-candidate.sh CANDIDATE_DIRECTORY [APPROVAL_DIRECTORY]"
candidate_dir=$1
approval_dir=${2:-}
case "$candidate_dir" in /*) ;; *) fail "candidate path must be absolute" ;; esac
[ -d "$candidate_dir" ] && [ ! -L "$candidate_dir" ] || fail "candidate must be a real directory"

for tool in jq sha256sum tar; do command -v "$tool" >/dev/null 2>&1 || fail "$tool is required"; done
for file in release-metadata.json release-images.env deploy-bundle.tar.gz SHA256SUMS; do
    path=$candidate_dir/$file
    [ -f "$path" ] && [ ! -L "$path" ] || fail "$file must be a regular non-symlink file"
done
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
      API_IMAGE:ghcr.io/$owner/hook2stream-api@sha256:*|WORKER_IMAGE:ghcr.io/$owner/hook2stream-worker@sha256:*|BOOTSTRAPPER_IMAGE:ghcr.io/$owner/hook2stream-bootstrapper@sha256:*|WEB_IMAGE:ghcr.io/$owner/hook2stream-web@sha256:*|POSTGRES_BACKUP_IMAGE:ghcr.io/$owner/hook2stream-postgres-backup@sha256:*|CADDY_IMAGE:caddy@sha256:*|CADDY_IMAGE:docker.io/library/caddy@sha256:*|POSTGRES_IMAGE:postgres@sha256:*|POSTGRES_IMAGE:docker.io/library/postgres@sha256:*|PGBOUNCER_IMAGE:edoburu/pgbouncer@sha256:*|PGBOUNCER_IMAGE:docker.io/edoburu/pgbouncer@sha256:*|EGRESS_PROXY_IMAGE:ubuntu/squid@sha256:*|EGRESS_PROXY_IMAGE:docker.io/ubuntu/squid@sha256:*) ;;
      *) fail "$name repository is outside the allowlist" ;;
    esac
    seen_names="$seen_names $name"
done < "$candidate_dir/release-images.env"
for name in $image_names; do case " $seen_names " in *" $name "*) ;; *) fail "release-images.env is missing $name" ;; esac; done
[ "$seen_release" = true ] || fail "release-images.env is missing RELEASE_VERSION"

tar -tzf "$candidate_dir/deploy-bundle.tar.gz" | while IFS= read -r member; do
    case "$member" in ''|/*|../*|*/../*|*/..|*'//'*) fail "bundle contains an unsafe path" ;; esac
    case "$member" in deploy|deploy/*) ;; *) fail "bundle member is outside deploy/" ;; esac
done
if tar -tvzf "$candidate_dir/deploy-bundle.tar.gz" | awk '$1 !~ /^[d-]/ {bad=1} END {exit bad ? 0 : 1}'; then fail "bundle links and special files are forbidden"; fi

if [ -n "$approval_dir" ]; then
    receipt=$approval_dir/staging-receipt.json; signature=$approval_dir/staging-receipt.sig
    [ -f "$receipt" ] && [ ! -L "$receipt" ] && [ -f "$signature" ] && [ ! -L "$signature" ] || fail "signed staging approval is incomplete"
    [ "$(find "$approval_dir" -mindepth 1 -maxdepth 1 | wc -l | tr -d ' ')" -eq 2 ] || fail "approval must contain exactly receipt and signature"
    images_sha=$(sha256sum "$candidate_dir/release-images.env" | awk '{print $1}')
    expected_images=$(jq -c '.images' "$candidate_dir/release-metadata.json")
    jq -e --arg repository "$repository" --arg commit "$commit" --arg artifact "$artifact" --arg bundle "$actual_bundle" --arg images "$images_sha" --argjson expectedImages "$expected_images" '
      .schemaVersion == 1 and .kind == "hook2stream-staging-receipt" and
      .environment == "staging" and .result == "success" and .repository == $repository and
      .commitSha == $commit and .candidateArtifact == $artifact and
      .hashes.releaseImagesSha256 == $images and .hashes.deployBundleSha256 == $bundle and
      .remoteResult.kind == "hook2stream-remote-deploy-result" and
      .remoteResult.commitSha == $commit and .remoteResult.actualImages == $expectedImages and
      .remoteResult.checks == ["pre-migration-backup","migration","smoke","e2e","digest-verification"] and
      .checks == ["pre-migration-backup","migration","smoke","e2e","digest-verification"]
    ' "$receipt" >/dev/null || fail "staging receipt does not approve this candidate"
    signers=${HOOK2STREAM_STAGING_SIGNERS:?HOOK2STREAM_STAGING_SIGNERS is required}
    ssh-keygen -Y verify -f "$signers" -I hook2stream-staging -n hook2stream-staging-receipt -s "$signature" < "$receipt" >/dev/null \
        || fail "staging receipt signature is invalid"
fi

printf '%s\n' "candidate validation: passed"

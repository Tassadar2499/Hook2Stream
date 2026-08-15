#!/bin/sh
set -eu
set -f

[ "$#" -eq 3 ] || { printf '%s\n' "usage: validate-production-approval.sh CANDIDATE_DIR APPROVAL_DIR ALLOWED_SIGNERS" >&2; exit 2; }
candidate=$1
approval=$2
allowed_signers=$3
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
. "$script_dir/lib/storage-common.sh"

[ -d "$approval" ] && [ ! -L "$approval" ] || storage_fail "production approval directory is invalid"
approval_files=$(find "$approval" -mindepth 1 -maxdepth 1 -printf '%f\n' | sort)
[ "$approval_files" = "$(printf '%s\n' storage-staging-receipt.json storage-staging-receipt.sig | sort)" ] \
    || storage_fail "production approval contains missing or unknown files"
receipt=$approval/storage-staging-receipt.json
signature=$approval/storage-staging-receipt.sig
for path in "$receipt" "$signature"; do [ -f "$path" ] && [ ! -L "$path" ] || storage_fail "approval file is unsafe"; done
[ -f "$allowed_signers" ] && [ ! -L "$allowed_signers" ] \
    && [ "$(stat -c '%u:%a' "$allowed_signers")" = 0:600 ] \
    || storage_fail "staging allowed-signers file must be root-owned mode 0600"

metadata=$candidate/storage-metadata.json
repository=$(jq -r .repository "$metadata")
commit=$(jq -r .commitSha "$metadata")
run_id=$(jq -r .ciRunId "$metadata")
run_attempt=$(jq -r .ciRunAttempt "$metadata")
artifact=$(jq -r .artifactName "$metadata")
metadata_sha=$(sha256sum "$metadata" | awk '{print $1}')
images_sha=$(sha256sum "$candidate/storage-images.env" | awk '{print $1}')
bundle_sha=$(sha256sum "$candidate/storage-bundle.tar.gz" | awk '{print $1}')
checksums_sha=$(sha256sum "$candidate/SHA256SUMS" | awk '{print $1}')
minio_image=$(storage_env_value "$candidate/storage-images.env" MINIO_IMAGE)
mc_image=$(storage_env_value "$candidate/storage-images.env" MINIO_MC_IMAGE)
caddy_image=$(storage_env_value "$candidate/storage-images.env" CADDY_IMAGE)

jq -e \
    --arg repository "$repository" --arg commit "$commit" --arg artifact "$artifact" \
    --arg metadataSha "$metadata_sha" --arg imagesSha "$images_sha" \
    --arg bundleSha "$bundle_sha" --arg checksumsSha "$checksums_sha" \
    --arg minio "$minio_image" --arg mc "$mc_image" --arg caddy "$caddy_image" \
    --argjson runId "$run_id" --argjson runAttempt "$run_attempt" '
    def expectedChecks: ["policy-verification","quota-verification","versioning-verification","lifecycle-verification","digest-verification"];
    type == "object" and
    (keys | sort) == (["schemaVersion","kind","environment","result","repository","commitSha","ciRunId","ciRunAttempt","candidateArtifact","deployedAt","checks","hashes","remoteResult"] | sort) and
    .schemaVersion == 1 and .kind == "hook2stream-storage-staging-receipt" and
    .environment == "storage-staging" and .result == "success" and
    .repository == $repository and .commitSha == $commit and
    .ciRunId == $runId and .ciRunAttempt == $runAttempt and .candidateArtifact == $artifact and
    .checks == expectedChecks and
    .hashes == {storageMetadataSha256:$metadataSha,storageImagesSha256:$imagesSha,storageBundleSha256:$bundleSha,checksumsSha256:$checksumsSha} and
    (.remoteResult | keys | sort) == (["schemaVersion","kind","environment","result","candidateArtifact","commitSha","storageImagesSha256","storageBundleSha256","actualImages","checks"] | sort) and
    .remoteResult.schemaVersion == 1 and .remoteResult.kind == "hook2stream-storage-remote-deploy-result" and
    .remoteResult.environment == "storage-staging" and .remoteResult.result == "success" and
    .remoteResult.candidateArtifact == $artifact and .remoteResult.commitSha == $commit and
    .remoteResult.storageImagesSha256 == $imagesSha and .remoteResult.storageBundleSha256 == $bundleSha and
    .remoteResult.actualImages == {MINIO_IMAGE:$minio,MINIO_MC_IMAGE:$mc,CADDY_IMAGE:$caddy} and
    .remoteResult.checks == expectedChecks
' "$receipt" >/dev/null || storage_fail "staging receipt does not bind the exact candidate and verified state"

ssh-keygen -Y verify \
    -f "$allowed_signers" \
    -I hook2stream-storage-staging \
    -n hook2stream-storage-staging-receipt \
    -s "$signature" < "$receipt" >/dev/null \
    || storage_fail "staging receipt signature verification failed"
printf '%s\n' "storage deploy: production approval verified" >&2

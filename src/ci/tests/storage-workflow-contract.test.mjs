#!/usr/bin/env node

import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const read = (path) => readFileSync(join(repoRoot, path), "utf8");
const storageCi = read(".github/workflows/storage-ci.yml");
const promotion = read(".github/workflows/promote-storage-production.yml");
const candidate = read("src/ci/storage-candidate.mjs");
const receipt = read("src/ci/storage-receipt.mjs");
const forcedCommand = read("src/deploy/storage/scripts/storage-forced-command.sh");
const liveAcceptance = read("src/deploy/storage/tests/run-minio-acceptance.sh");

function assert(condition, message) {
  if (!condition) {
    console.error(`storage-workflow-contract: ${message}`);
    process.exit(1);
  }
}

for (const [name, workflow] of [["storage CI", storageCi], ["storage promotion", promotion]]) {
  for (const match of workflow.matchAll(/^\s*(?:-\s*)?uses:\s*[^\s@]+@([^\s#]+)/gm)) {
    assert(/^[0-9a-f]{40}$/.test(match[1]), `${name} contains a non-immutable action reference: ${match[0].trim()}`);
  }
}

assert(storageCi.startsWith("name: Storage CI\n"), "source workflow must keep the identity checked by promotion");
assert(storageCi.includes("bash src/deploy/storage/tests/validate-storage-deployment.sh"),
  "Storage CI must run the runtime storage shell contracts before publishing");
assert(storageCi.includes("node src/ci/tests/storage-minio-security-gate.test.mjs"),
  "Storage CI must exercise the fail-closed MinIO security policy contract offline");
assert(storageCi.includes("branches: [main]") && storageCi.includes("- src/deploy/storage/**") &&
  storageCi.includes("- src/deploy/minio/**") && storageCi.includes("- src/ci/storage-*") &&
  storageCi.includes("- .github/workflows/storage-ci.yml"), "Storage CI must be scoped to main storage changes");
assert(storageCi.includes("file: src/deploy/minio/Dockerfile") &&
  storageCi.includes("ghcr.io/${GITHUB_REPOSITORY_OWNER,,}/hook2stream-minio"),
"Storage CI must publish the source-pinned MinIO image from this repository");
assert(storageCi.includes("Verify exact published MinIO source identity labels") &&
  storageCi.includes('IMAGE_REFERENCE: ${{ steps.image.outputs.repository }}@${{ steps.build.outputs.digest }}') &&
  storageCi.includes('jq -er .minioRelease src/deploy/storage/storage-release.json') &&
  storageCi.includes('jq -er .minioSourceCommit src/deploy/storage/storage-release.json') &&
  storageCi.includes('com.hook2stream.minio.source-release') &&
  storageCi.includes('com.hook2stream.minio.source-commit'),
"Storage CI must verify custom source labels on the exact published MinIO digest");
assert(storageCi.includes("sbom: true") && storageCi.includes("provenance: mode=max"),
  "published MinIO must include SBOM and maximum provenance");
assert(storageCi.includes("image: ${{ steps.image.outputs.repository }}@${{ steps.build.outputs.digest }}") &&
  storageCi.includes("image: ${{ steps.image.outputs.reference }}") &&
  (storageCi.match(/severity-cutoff: high/g) ?? []).length === 2,
"published and runtime storage digests must both block High/Critical findings");
assert(storageCi.includes("source: caddy:2.11.4-alpine") &&
  storageCi.includes("source: minio/mc:RELEASE.2025-07-21T05-28-08Z"),
"Caddy and minio/mc reviewed runtime inputs must be resolved and scanned");
assert(storageCi.includes("storage-acceptance:\n    needs: [publish-minio, runtime-images]") &&
  storageCi.includes("MINIO_IMAGE=\"$(read_digest_fragment MINIO_IMAGE storage-acceptance-digests/minio.env)\"") &&
  storageCi.includes("MINIO_MC_IMAGE=\"$(read_digest_fragment MINIO_MC_IMAGE storage-acceptance-digests/minio-mc.env)\"") &&
  storageCi.includes("CADDY_IMAGE=\"$(read_digest_fragment CADDY_IMAGE storage-acceptance-digests/caddy.env)\"") &&
  storageCi.includes("export MINIO_IMAGE MINIO_MC_IMAGE CADDY_IMAGE") &&
  storageCi.includes("dotnet-version: 10.0.302") &&
  storageCi.includes("bash src/deploy/storage/tests/run-minio-acceptance.sh") &&
  liveAcceptance.includes("S3ObjectStorageMinioTests.H2se_round_trips_ranges_and_never_persists_plaintext_in_real_minio") &&
  storageCi.includes("storage-candidate:\n    needs: storage-acceptance"),
"the candidate must be blocked on live H2SE acceptance of the exact scanned MinIO, mc, and Caddy digests");
assert(storageCi.includes("pattern: storage-digest-*-${{ github.sha }}-${{ github.run_id }}-${{ github.run_attempt }}") &&
  storageCi.includes("storage-digest-minio-${{ github.sha }}-${{ github.run_id }}-${{ github.run_attempt }}") &&
  storageCi.includes("storage-digest-${{ matrix.name }}-${{ github.sha }}-${{ github.run_id }}-${{ github.run_attempt }}"),
"all digest fragments must be scoped to one run attempt");
assert((storageCi.match(/node src\/ci\/storage-minio-security-gate\.mjs/g) ?? []).length === 2 &&
  (storageCi.match(/--release-manifest src\/deploy\/storage\/storage-release\.json/g) ?? []).length === 2 &&
  (storageCi.match(/--dockerfile src\/deploy\/minio\/Dockerfile/g) ?? []).length === 2 &&
  (storageCi.match(/--policy src\/deploy\/storage\/minio-security-policy\.json/g) ?? []).length === 2,
"the current fail-closed MinIO source policy must run before publish and again before candidate assembly");
const sourceGatePositions = [...storageCi.matchAll(/node src\/ci\/storage-minio-security-gate\.mjs/g)]
  .map((match) => match.index);
assert(sourceGatePositions[0] < storageCi.indexOf("  publish-minio:") &&
  sourceGatePositions[1] > storageCi.indexOf("  storage-candidate:") &&
  sourceGatePositions[1] < storageCi.indexOf("node src/ci/storage-candidate.mjs create"),
"the MinIO source policy gates must precede both image publication and candidate creation");
assert(storageCi.includes('artifact_name="storage-candidate-${GITHUB_SHA}-${GITHUB_RUN_ID}-${GITHUB_RUN_ATTEMPT}"') &&
  storageCi.includes("--storage-dir src/deploy/storage") && storageCi.includes("retention-days: 90"),
"the immutable 90-day candidate must pass the MinIO security gate and contain only the dedicated storage bundle");

assert(storageCi.includes("environment: storage-staging") &&
  storageCi.includes("group: hook2stream-storage-staging") && storageCi.includes("cancel-in-progress: false"),
"storage staging must have its own non-cancelling deployment lock");
assert(promotion.includes("environment: storage-production") &&
  promotion.includes("group: hook2stream-storage-production") && promotion.includes("cancel-in-progress: false"),
"storage production must have its own non-cancelling deployment lock and approval environment");
assert(storageCi.includes("tag:hook2stream-storage-ci-staging") &&
  promotion.includes("tag:hook2stream-storage-ci-production"),
"storage environments must use distinct ephemeral Tailscale tags");
assert(storageCi.includes("secrets.STORAGE_DEPLOY_SSH_PRIVATE_KEY") &&
  storageCi.includes("secrets.STORAGE_DEPLOY_SSH_KNOWN_HOSTS") &&
  promotion.includes("secrets.STORAGE_DEPLOY_SSH_PRIVATE_KEY") &&
  promotion.includes("secrets.STORAGE_DEPLOY_SSH_KNOWN_HOSTS"),
"both storage environments must use environment-scoped SSH material");
for (const workflow of [storageCi, promotion]) {
  assert(workflow.includes("StrictHostKeyChecking=yes") && workflow.includes("IdentitiesOnly=yes"),
    "every storage SSH deployment must pin the host key and identity");
  assert(workflow.includes('if ($2 != "ssh-ed25519") bad=1') &&
    workflow.includes('"ssh-ed25519 "*'),
  "every storage deployment must enforce ED25519 host and client keys");
  assert(workflow.includes('"deploy-storage $CANDIDATE_NAME"') &&
    workflow.includes("HOOK2STREAM_STORAGE_REMOTE_RECEIPT="),
  "every storage deployment must use the narrow forced-command wire protocol");
  assert(workflow.includes('wc -l < "$RUNNER_TEMP/storage-deploy-output.log"'),
    "the storage wire protocol must reject any extra remote stdout");
}

assert(promotion.includes('test "$(jq -r .name <<<"$run_json")" = "Storage CI"') &&
  promotion.includes('test "$(jq -r .path <<<"$run_json")" = ".github/workflows/storage-ci.yml"') &&
  promotion.includes('test "$(jq -r .event <<<"$run_json")" = push') &&
  promotion.includes('test "$(jq -r .head_branch <<<"$run_json")" = main') &&
  promotion.includes('test "$(jq -r .conclusion <<<"$run_json")" = success'),
"production promotion must bind a successful main push of the exact Storage CI workflow");
assert((promotion.match(/if: github\.ref == 'refs\/heads\/main'/g) ?? []).length === 2,
  "both storage promotion jobs must reject workflow_dispatch from any non-main ref");
assert(!promotion.includes("docker/build-push-action") && !/\bdocker\s+build\b/.test(promotion),
  "storage production promotion must never rebuild images");
assert(promotion.includes("--signer-workflow \"$GITHUB_REPOSITORY/.github/workflows/storage-ci.yml\"") &&
  promotion.includes("--deny-self-hosted-runners"),
"production must verify GitHub attestations from the exact hosted-runner storage workflow");
assert((promotion.match(/ssh-keygen -Y verify/g) ?? []).length === 2 &&
  promotion.includes("-I hook2stream-storage-staging") &&
  promotion.includes("-n hook2stream-storage-staging-receipt"),
"the staging receipt signature must be verified before and after the production approval boundary");
assert(promotion.includes("promotion/approval/storage-staging-receipt.json") &&
  promotion.includes("storage-promotion-payload/approval/storage-staging-receipt.sig"),
"the signed staging approval must stay in the documented approval/ payload location");

const productionJobPosition = promotion.indexOf("  deploy-storage-production:");
assert(productionJobPosition > 0, "storage production deployment job is missing");
const beforeApproval = promotion.slice(0, productionJobPosition);
const afterApproval = promotion.slice(productionJobPosition);
for (const [boundary, workflowSection] of [
  ["before", beforeApproval],
  ["after", afterApproval],
]) {
  const sourceReceiptValidation = workflowSection.indexOf("node src/ci/storage-receipt.mjs validate");
  const trustedMainCheckout = workflowSection.indexOf("Checkout current protected-main MinIO security policy");
  const currentPolicyGate = workflowSection.indexOf("node current-main-security/src/ci/storage-minio-security-gate.mjs");
  assert(workflowSection.includes("ref: refs/heads/main") &&
    workflowSection.includes("path: current-main-security") &&
    workflowSection.includes("node current-main-security/src/ci/storage-minio-security-gate.mjs") &&
    workflowSection.includes("--release-manifest current-security-review/storage-release.json") &&
    workflowSection.includes("--dockerfile src/deploy/minio/Dockerfile") &&
    workflowSection.includes("--policy current-main-security/src/deploy/storage/minio-security-policy.json"),
  `production must apply the current protected-main policy ${boundary} Environment approval`);
  assert(sourceReceiptValidation >= 0 && sourceReceiptValidation < trustedMainCheckout &&
    trustedMainCheckout < currentPolicyGate,
  `the pristine protected-main security checkout must occur after source validation and immediately guard ${boundary}-approval policy use`);
  assert(workflowSection.includes("packages: read") &&
    workflowSection.includes("docker/login-action@abd2ef45e78c5afb21d64d4ca52ee8550d9572c7") &&
    (workflowSection.match(/anchore\/scan-action@e1165082ffb1fe366ebaf02d8526e7c4989ea9d2/g) ?? []).length === 3 &&
    (workflowSection.match(/severity-cutoff: high/g) ?? []).length === 3 &&
    workflowSection.includes("steps.current_security.outputs.minio_image") &&
    workflowSection.includes("steps.current_security.outputs.minio_mc_image") &&
    workflowSection.includes("steps.current_security.outputs.caddy_image"),
  `production must rescan all three exact digests with current data ${boundary} Environment approval`);
}
assert(!promotion.includes("node src/ci/storage-minio-security-gate.mjs"),
  "production must not use the source-run security policy implementation");

assert(candidate.includes('const IMAGE_KEYS = ["MINIO_IMAGE", "MINIO_MC_IMAGE", "CADDY_IMAGE"]') &&
  candidate.includes("storage bundle entry is outside storage/") &&
  candidate.includes("unsafe storage bundle path") &&
  candidate.includes("repository is outside the storage allowlist") &&
  candidate.includes("storageRelease.storageFormatVersion !== 1") &&
  candidate.includes('storageRelease.objectFormat !== "H2SEv1"') &&
  candidate.includes('storageRelease.minioRelease !== "RELEASE.2025-10-15T17-29-55Z"') &&
  candidate.includes('storageRelease.minioSourceCommit !== "9e49d5e7a648f00e26f2246f4dc28e6b07f8c84a"'),
"candidate validation must reject unknown images, repositories, tags, and archive traversal");
for (const check of [
  "policy-verification",
  "quota-verification",
  "versioning-verification",
  "lifecycle-verification",
  "digest-verification",
]) {
  assert(receipt.includes(`\"${check}\"`), `remote storage receipt is missing ${check}`);
}

const firstDockerMutation = forcedCommand.indexOf('"$release_dir/storage/scripts/deploy-storage.sh"');
assert(firstDockerMutation > 0, "storage forced command must call the reviewed runtime deployment entrypoint");
for (const gate of [
  '"$script_dir/validate-candidate.sh"',
  '"$script_dir/validate-production-approval.sh"',
  "storage protocol downgrade is forbidden",
  "MinIO on-disk format downgrade is forbidden",
  "candidate_minio_security_sequence=$(storage_validate_minio_security_policy",
  "storage_validate_minio_security_transition",
  '"$candidate_minio_security_sequence" "$candidate_minio_release" "$candidate_minio_source_commit"',
  '"$minimum_minio_security_sequence" "$floor_minio_release" "$floor_minio_source_commit"',
  "object format change is forbidden",
]) {
  const gatePosition = forcedCommand.indexOf(gate);
  assert(gatePosition >= 0 && gatePosition < firstDockerMutation,
    `${gate} must fail closed before the first Docker mutation`);
}
assert(forcedCommand.includes("HOOK2STREAM_STORAGE_REMOTE_RECEIPT=") &&
  forcedCommand.indexOf("HOOK2STREAM_STORAGE_REMOTE_RECEIPT=") > firstDockerMutation,
"the forced command must emit the agreed receipt only after runtime mutation and verification");

console.log("storage workflow contracts passed");

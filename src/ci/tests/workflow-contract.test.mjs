#!/usr/bin/env node

import { existsSync, readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const read = (path) => readFileSync(join(repoRoot, path), "utf8");
const ci = read(".github/workflows/ci.yml");
const staging = read(".github/workflows/stage-candidate.yml");
const promotion = read(".github/workflows/promote-production.yml");
const rollback = read(".github/workflows/rollback.yml");
const candidateBuilder = read("src/ci/release-candidate.mjs");
const stagingSecretJob = staging.slice(staging.indexOf("  deploy-staging:"));
const productionSecretJob = promotion.slice(promotion.indexOf("  deploy-production:"));

function assert(condition, message) {
  if (!condition) {
    console.error(`workflow-contract: ${message}`);
    process.exit(1);
  }
}

function yamlNamedListItem(text, indentation, name) {
  const marker = `${" ".repeat(indentation)}- name: ${name}\n`;
  const start = text.indexOf(marker);
  assert(start >= 0, `missing YAML list item: ${name}`);
  const lines = text.slice(start).split("\n");
  const block = [lines[0]];
  for (const line of lines.slice(1)) {
    const leadingSpaces = line.match(/^ */)[0].length;
    if (line.trim() !== "" && leadingSpaces <= indentation) {
      break;
    }
    block.push(line);
  }
  return block.join("\n");
}

function canonicalYamlChildKeys(block, indentation, context) {
  const directChildren = block.split("\n").filter((line) =>
    line.trim() !== "" && line.match(/^ */)[0].length === indentation);
  const keys = directChildren.map((line) => {
    const match = line.match(new RegExp(`^ {${indentation}}([a-z][a-z0-9-]*):(?: .*)?$`));
    assert(match !== null, `${context} contains a non-canonical direct child: ${line.trim()}`);
    return match[1];
  });
  return keys.sort();
}

for (const [name, workflow] of [["ci", ci], ["staging", staging], ["promotion", promotion], ["rollback", rollback]]) {
  for (const match of workflow.matchAll(/^\s*uses:\s*[^\s@]+@([^\s#]+)/gm)) {
    assert(/^[0-9a-f]{40}$/.test(match[1]), `${name} workflow contains a non-immutable action reference: ${match[0].trim()}`);
  }
}

for (const [name, workflow] of [["promotion", promotion]]) {
  assert(workflow.includes('case "$(ssh-keygen -y -f "$RUNNER_TEMP/hook2stream-ssh/id_ed25519")" in') &&
    workflow.includes('"ssh-ed25519 "*') &&
    workflow.includes('$1 != host || $2 != "ssh-ed25519" || NF != 3') &&
    workflow.includes("records == 1"),
  `${name} workflow must reject non-ED25519 deploy identities and host keys`);
}

const attemptSpecificDigestName = "release-digest-${{ matrix.name }}-${{ github.sha }}-${{ github.run_id }}-${{ github.run_attempt }}";
assert(ci.split(attemptSpecificDigestName).length - 1 === 2, "both digest-fragment uploads must include run ID and run attempt");
assert(ci.includes("pattern: release-digest-*-${{ github.sha }}-${{ github.run_id }}-${{ github.run_attempt }}"),
  "digest-fragment download must be scoped to the current run attempt");
assert(ci.includes("Build pinned quarantined MinIO acceptance dependency") && ci.includes("src/ci/start-minio-acceptance.sh"),
  "MinIO must remain an isolated local/CI acceptance dependency");
assert(!ci.includes("deploy-staging:") && !ci.includes("environment: staging") && !ci.includes("STAGING_RECEIPT_SIGNING_KEY"),
  "main CI must stop after publishing the immutable candidate and must not deploy staging");
const publishSection = ci.slice(ci.indexOf("  publish:"), ci.indexOf("  runtime-images:"));
assert(!/name:\s*minio\b/.test(publishSection) && !publishSection.includes("MINIO_IMAGE"),
  "application publish must not publish or record a deployable MinIO image");
assert(/^\s*-\s+name:\s*postgres\s*$/m.test(publishSection) && publishSection.includes("src/deploy/postgres/Dockerfile") &&
  publishSection.includes("deploy_var: POSTGRES_IMAGE"),
  "application publish must build and record the hardened PostgreSQL image");
const containerSection = ci.slice(ci.indexOf("  containers:"), ci.indexOf("  publish:"));
assert(/^\s*-\s+name:\s*postgres\s*$/m.test(containerSection) && containerSection.includes("src/deploy/postgres/Dockerfile") &&
  containerSection.includes("Verify hardened PostgreSQL privilege drop"),
  "container checks must build, scan, and verify the hardened PostgreSQL image");
const expectedContainerDeployability = new Map([
  ["api", true],
  ["worker", true],
  ["bootstrapper", true],
  ["web", true],
  ["postgres-backup", true],
  ["postgres", true],
  ["minio", false],
]);
const containerMatrixStart = containerSection.indexOf("      matrix:\n        include:\n");
const containerMatrixEnd = containerSection.indexOf("    steps:\n", containerMatrixStart);
assert(containerMatrixStart >= 0 && containerMatrixEnd > containerMatrixStart,
  "container matrix boundaries are missing or malformed");
const containerMatrixSection = containerSection.slice(containerMatrixStart, containerMatrixEnd);
const containerMatrixRows = containerMatrixSection.split("\n").filter((line) =>
  line.match(/^ */)[0].length === 10 && line.trimStart().startsWith("-"));
assert(containerMatrixRows.length === expectedContainerDeployability.size,
  "container matrix must contain exactly seven list items");
const containerMatrixNames = containerMatrixRows.map((line) => {
  const match = line.match(/^ {10}- name: ([a-z0-9-]+)$/);
  assert(match !== null, `container matrix contains a non-canonical row: ${line.trim()}`);
  return match[1];
}).sort();
assert(JSON.stringify(containerMatrixNames) ===
  JSON.stringify([...expectedContainerDeployability.keys()].sort()),
  "container matrix must contain exactly the seven reviewed entries");
for (const [name, deployable] of expectedContainerDeployability) {
  const entry = yamlNamedListItem(containerSection, 10, name);
  assert((entry.match(/^\s{12}deployable: (?:true|false)$/gm) ?? []).length === 1 &&
    entry.includes(`            deployable: ${deployable}`),
  `${name} must declare deployable: ${deployable} exactly once`);
}
assert((containerSection.match(/^\s{12}deployable: false$/gm) ?? []).length === 1 &&
  (containerSection.match(/^\s{12}deployable: true$/gm) ?? []).length === 6,
  "only MinIO may be non-deployable in the container matrix");

const deployableScan = yamlNamedListItem(containerSection, 6,
  "Scan deployable container for high and critical vulnerabilities");
assert(deployableScan.includes("        if: matrix.deployable == true") &&
  deployableScan.includes("        uses: anchore/scan-action@e1165082ffb1fe366ebaf02d8526e7c4989ea9d2") &&
  deployableScan.includes("          image: hook2stream-${{ matrix.name }}:ci") &&
  deployableScan.includes("          fail-build: true") &&
  deployableScan.includes("          severity-cutoff: high") &&
  deployableScan.includes("          only-fixed: false") &&
  deployableScan.includes("          output-format: table") &&
  !deployableScan.includes("continue-on-error"),
  "every deployable container must retain the blocking High/Critical vulnerability gate");

const minioInventory = yamlNamedListItem(containerSection, 6,
  "Inventory quarantined local/CI MinIO vulnerabilities");
assert(minioInventory.includes("        if: matrix.name == 'minio' && matrix.deployable == false") &&
  minioInventory.includes("        uses: anchore/scan-action@e1165082ffb1fe366ebaf02d8526e7c4989ea9d2") &&
  minioInventory.includes("          image: hook2stream-${{ matrix.name }}:ci") &&
  minioInventory.includes("          fail-build: false") &&
  minioInventory.includes("          severity-cutoff: high") &&
  minioInventory.includes("          only-fixed: false") &&
  minioInventory.includes("          output-format: table") &&
  !minioInventory.includes("continue-on-error"),
  "archived MinIO must retain a complete non-blocking local/CI vulnerability inventory");

const minioPolicy = yamlNamedListItem(containerSection, 6, "Record MinIO quarantine policy");
assert(minioPolicy.includes("        if: matrix.name == 'minio' && matrix.deployable == false") &&
  minioPolicy.includes("This archived upstream image is never published, bundled, staged, or deployed"),
  "the MinIO inventory must visibly record its non-deployable quarantine policy");
assert((containerSection.match(/fail-build: false/g) ?? []).length === 1 &&
  !containerSection.includes("continue-on-error:"),
  "no container scan other than the MinIO inventory may bypass the vulnerability gate");
const expectedScanInputs = ["fail-build", "image", "only-fixed", "output-format", "severity-cutoff"];
for (const [name, block] of [["deployable", deployableScan], ["MinIO inventory", minioInventory]]) {
  const inputKeys = canonicalYamlChildKeys(block, 10, `${name} scan`);
  assert(JSON.stringify(inputKeys) === JSON.stringify(expectedScanInputs),
    `${name} scan must use exactly the reviewed Anchore inputs`);
}
const runtimeSection = ci.slice(ci.indexOf("  runtime-images:"), ci.indexOf("  release-candidate:"));
assert(!/^\s*-\s+name:\s*postgres\s*$/m.test(runtimeSection) && !runtimeSection.includes("POSTGRES_IMAGE"),
  "official PostgreSQL must not be recorded as a reviewed runtime image");
assert(!ci.includes("postgres:17.10"), "CI must not retain PostgreSQL 17.10");
for (const removedPath of [
  ".github/workflows/storage-ci.yml",
  ".github/workflows/promote-storage-production.yml",
  "src/ci/storage-candidate.mjs",
  "src/ci/storage-receipt.mjs",
  "src/ci/storage-minio-security-gate.mjs",
]) {
  assert(!existsSync(join(repoRoot, removedPath)), `remote storage-plane artifact still exists: ${removedPath}`);
}
assert(!existsSync(join(repoRoot, "src/ci/provider-teardown-receipt.mjs")) &&
  !existsSync(join(repoRoot, "src/ci/cloudzy-teardown-receipt.mjs")),
"permanent staging must not retain a provider teardown validator");
for (const excludedPath of ["Caddyfile.minio", "compose.minio.yaml", "minio", "storage"]) {
  assert(candidateBuilder.includes(`:(exclude)${excludedPath}`),
    `release candidate must exclude local/remote storage path: ${excludedPath}`);
}
assert(promotion.includes("remote-deploy-result.mjs validate \\") && !promotion.includes("remote-deploy-result.mjs --candidate"),
  "production must invoke the explicit remote deploy-result validate subcommand");
assert(staging.includes("source_ci_run_id:") && staging.includes('SOURCE_CI_RUN_ID: ${{ inputs.source_ci_run_id }}'),
  "staging must accept an explicit source CI run ID");
assert((staging.match(/ref: \$\{\{ github\.workflow_sha \}\}/g) ?? []).length === 2 &&
  staging.includes('test "$GITHUB_SHA" = "$POLICY_SHA"') &&
  (staging.match(/\.commit\.sha <<<"\$main_json"/g) ?? []).length >= 3 &&
  (staging.match(/test "\$\(jq -r \.commit\.sha <<<"\$main_json"\)" = "\$POLICY_SHA"/g) ?? []).length >= 3 &&
  !staging.includes("ref: ${{ steps.source.outputs.sha }}") &&
  !staging.includes("ref: ${{ needs.verify-source.outputs.source-sha }}"),
  "staging policy and secret-bearing jobs must execute only the current dispatch workflow commit");
assert(!stagingSecretJob.includes("release-candidate/deploy/") &&
  !stagingSecretJob.includes(". release-candidate/") &&
  !stagingSecretJob.includes("source release-candidate/"),
  "the selected staging candidate must remain data and never become runner policy code");
assert(staging.includes('test "$GITHUB_REF" = refs/heads/main') && staging.includes("--jq .protected") &&
  staging.includes(".github/workflows/ci.yml") && staging.includes("source CI commit is no longer an ancestor"),
  "staging must fail closed unless the successful source run belongs to protected main");
assert(staging.includes('gh run download "$SOURCE_CI_RUN_ID" --name "$CANDIDATE_NAME"') &&
  staging.includes('--signer-workflow "$GITHUB_REPOSITORY/.github/workflows/ci.yml"') &&
  staging.includes('--source-digest "${{ steps.source.outputs.sha }}"') &&
  staging.includes("--deny-self-hosted-runners"),
  "staging must download and attest the exact candidate from the selected CI run");
assert(staging.includes("environment: staging") && staging.includes("group: hook2stream-staging") &&
  staging.includes("cancel-in-progress: false") && staging.includes("tags: tag:hook2stream-ci-staging") &&
  staging.includes('"deploy $CANDIDATE_NAME"'),
  "staging must serialize forced-command deploys through the staging Environment and Tailscale ACL tag");
assert(staging.includes('DEPLOY_HOST: ${{ secrets.DEPLOY_HOST }}') && staging.includes('ping: ${{ secrets.DEPLOY_HOST }}') &&
  staging.includes("^h2s-app-staging\\.[a-z0-9-]+\\.ts\\.net$") &&
  staging.includes('$1 != host || $2 != "ssh-ed25519" || NF != 3') &&
  staging.includes("records == 1") && staging.includes("StrictHostKeyChecking=yes"),
  "permanent staging must trust only the exact MagicDNS host through one pinned ED25519 host key");
assert(!staging.includes("@cert-authority") && !staging.includes("ssh-keyscan") &&
  !staging.includes("hook2stream_validate_staging_host_certificate") &&
  !staging.includes("ssh-ed25519-cert-v01@openssh.com"),
  "permanent staging must not retain ephemeral host-certificate trust");
assert(staging.includes('case "$(ssh-keygen -y -f "$RUNNER_TEMP/hook2stream-ssh/id_ed25519")" in') &&
  staging.includes('"ssh-ed25519 "*'),
  "staging must reject a non-ED25519 deploy identity");
assert(staging.includes("staging_receipt_signing_fingerprint") &&
  staging.includes("hook2stream_validate_distinct_ed25519_fingerprints") &&
  staging.includes("staging receipt signer must differ from deploy and host keys") &&
  staging.includes('"$RUNNER_TEMP/hook2stream-ssh/id_ed25519"') &&
  staging.includes('"$RUNNER_TEMP/hook2stream-ssh/staging-host-key.pub"'),
  "the staging receipt authority must be independent from deploy and host identities");
assert(staging.includes("Validate staging receipt authority before deployment") &&
  staging.includes('STAGING_RECEIPT_ALLOWED_SIGNERS: ${{ vars.STAGING_RECEIPT_ALLOWED_SIGNERS }}') &&
  staging.includes("hook2stream_validate_exact_allowed_signer") &&
  staging.includes('test "$staging_receipt_signing_fingerprint" = "$STAGING_RECEIPT_SIGNING_FINGERPRINT"') &&
  staging.includes("ssh-keygen -Y verify") &&
  staging.indexOf("Validate staging receipt authority before deployment") <
    staging.indexOf("Connect ephemeral staging runner to Tailscale"),
  "staging must fail early on signer mismatch, bind the private signer to its exact public authority, and self-verify the receipt");
assert(stagingSecretJob.includes('MIN_ROLLBACK_RELEASE_SHA: ${{ vars.MIN_ROLLBACK_RELEASE_SHA }}') &&
  !staging.slice(0, staging.indexOf("  deploy-staging:")).includes("MIN_ROLLBACK_RELEASE_SHA:") &&
  (staging.match(/--minimum-release-sha "\$MIN_ROLLBACK_RELEASE_SHA"/g) ?? []).length === 3 &&
  staging.includes('compare/${MIN_ROLLBACK_RELEASE_SHA}...${SOURCE_SHA}'),
  "staging must bind the Environment rollback floor to a floor-capable candidate, host result, soak, and signed receipt");
assert(staging.includes("Run trusted 60-minute render and network soak") &&
  staging.includes('"soak $CANDIDATE_NAME"') && staging.includes("HOOK2STREAM_REMOTE_SOAK_RECEIPT=") &&
  staging.includes("staging-receipt.mjs validate-soak") && staging.includes("--soak-result remote-soak-result.json") &&
  staging.indexOf('"deploy $CANDIDATE_NAME"') < staging.indexOf('"soak $CANDIDATE_NAME"') &&
  staging.indexOf('"soak $CANDIDATE_NAME"') < staging.indexOf("Create successful staging receipt") &&
  !staging.includes("for minute in $(seq 1 60)"),
  "the signed staging receipt must require the separate trusted sustained render/network soak");
const stagingDeployMutation = staging.slice(
  staging.indexOf("Deploy candidate through forced SSH and run host smoke/E2E"),
  staging.indexOf("Run trusted 60-minute render and network soak"),
);
assert(stagingDeployMutation.includes('test "$(jq -r .commit.sha <<<"$main_json")" = "$POLICY_SHA"') &&
  stagingDeployMutation.indexOf('test "$(jq -r .commit.sha <<<"$main_json")" = "$POLICY_SHA"') <
    stagingDeployMutation.indexOf('"deploy $CANDIDATE_NAME"') &&
  stagingDeployMutation.includes("remote-deploy-result.mjs validate \\") &&
  staging.indexOf("remote-deploy-result.mjs validate \\") < staging.indexOf('"soak $CANDIDATE_NAME"'),
  "staging must revalidate live main immediately before deploy and reject a malformed host/floor receipt before the soak");
const stagingSignStep = staging.slice(staging.indexOf("Sign staging receipt for production host verification"));
assert(stagingSignStep.includes('test "$(jq -r .commit.sha <<<"$main_json")" = "$POLICY_SHA"') &&
  stagingSignStep.indexOf('test "$(jq -r .commit.sha <<<"$main_json")" = "$POLICY_SHA"') <
    stagingSignStep.indexOf("ssh-keygen -Y sign"),
  "staging must enforce the release freeze immediately before signing its production authority receipt");
assert(staging.includes("name: staging-receipt-${{ github.run_id }}-${{ github.run_attempt }}") && staging.includes("subject-path:") &&
  staging.includes("staging-receipt.json") && staging.includes("staging-receipt.sig") &&
  staging.includes('--policy-sha "$POLICY_SHA"'),
  "staging must bind the current workflow policy SHA into its attested signed receipt");
assert(!staging.includes('| tee "$RUNNER_TEMP/deploy-output.log"'),
  "staging must not mirror potentially sensitive forced-command output into CI logs");
assert(staging.includes('> "$RUNNER_TEMP/deploy-output.log" 2>&1') && staging.includes("umask 077"),
  "staging must keep forced-command output in a private runner-local file");
assert(staging.includes('> "$RUNNER_TEMP/soak-output.log" 2>&1') &&
  staging.includes('wc -l < "$RUNNER_TEMP/soak-output.log"') &&
  staging.includes('wc -c < "$RUNNER_TEMP/soak-output.log"') &&
  !staging.includes('| tee "$RUNNER_TEMP/soak-output.log"'),
  "staging must keep trusted soak hook output and diagnostics out of Actions logs");

assert(promotion.includes("source_staging_run_id:") &&
  promotion.includes('SOURCE_STAGING_RUN_ID: ${{ inputs.source_staging_run_id }}') &&
  !promotion.includes("provider_teardown_receipt_b64:") &&
  !promotion.includes("provider_teardown_signature_b64:") &&
  !promotion.includes("inputs.source_ci_run_id"),
  "production promotion must be selected by staging workflow run ID, not directly by CI run ID");
assert((promotion.match(/ref: \$\{\{ github\.workflow_sha \}\}/g) ?? []).length === 2 &&
  promotion.includes('test "$GITHUB_SHA" = "$POLICY_SHA"') &&
  (promotion.match(/test "\$\(jq -r \.commit\.sha <<<"\$main_json"\)" = "\$POLICY_SHA"/g) ?? []).length >= 2 &&
  !promotion.includes("ref: ${{ needs.verify-source.outputs.source-sha }}") &&
  !promotion.includes("ref: ${{ steps.source.outputs.sha }}"),
  "production policy and secret-bearing jobs must execute only the current dispatch workflow commit");
assert(promotion.indexOf("Revalidate live protected-main policy after production Environment boundary") <
  promotion.lastIndexOf("actions/checkout@"),
  "production must revoke stale workflow policy immediately after approval before any third-party action executes");
assert(!productionSecretJob.includes("promotion-payload/candidate/deploy/") &&
  !productionSecretJob.includes(". promotion-payload/candidate/") &&
  !productionSecretJob.includes("source promotion-payload/candidate/"),
  "the selected production candidate must remain data and never become runner policy code");
assert(promotion.includes('test "$(jq -r .name <<<"$run_json")" = "Stage candidate"') &&
  promotion.includes(".github/workflows/stage-candidate.yml") &&
  promotion.includes('receipt_name="staging-receipt-${SOURCE_STAGING_RUN_ID}-${{ steps.staging.outputs.attempt }}"') &&
  promotion.includes('gh run download "$SOURCE_STAGING_RUN_ID" --name "$receipt_name"') &&
  promotion.includes("$'staging-receipt.json\\nstaging-receipt.sig'") &&
  promotion.includes('--signer-workflow "$GITHUB_REPOSITORY/.github/workflows/stage-candidate.yml"'),
  "production must fetch and attest the receipt from the exact successful staging workflow run");
assert(!promotion.includes("provider-teardown-receipt.mjs") &&
  !promotion.includes("PROVIDER_LIFECYCLE_ALLOWED_SIGNERS") &&
  !promotion.includes("provider-destroy-receipt.json") &&
  !promotion.includes("provider-destroy-receipt.sig"),
  "production must not require provider teardown evidence for permanent staging");
assert((promotion.match(/hook2stream_validate_exact_allowed_signer/g) ?? []).length === 2 &&
  (promotion.match(/"\$RUNNER_TEMP\/staging-receipt-allowed-signers" hook2stream-staging/g) ?? []).length >= 2 &&
  (promotion.match(/\. src\/deploy\/scripts\/lib\/forced-command-trust\.sh/g) ?? []).length >= 2,
  "production must accept exactly one staging ED25519 receipt authority before and after approval");
assert(promotion.includes('--staging-run-id "$SOURCE_STAGING_RUN_ID"') &&
  promotion.includes('--staging-run-attempt "${{ steps.staging.outputs.attempt }}"') &&
  promotion.includes('staging-run-id: ${{ inputs.source_staging_run_id }}') &&
  promotion.includes('staging-run-attempt: ${{ steps.staging.outputs.attempt }}'),
  "production must bind the staging receipt to the exact staging workflow run attempt");
assert(promotion.includes('test "$staging_sha" = "$POLICY_SHA"') &&
  promotion.includes('test "$(jq -r .head_sha <<<"$staging_run_json")" = "$POLICY_SHA"') &&
  promotion.includes('.policySha == $policySha') &&
  (promotion.match(/--policy-sha "\$POLICY_SHA"/g) ?? []).length === 2 &&
  (promotion.match(/test "\$GITHUB_SHA" = "\$POLICY_SHA"/g) ?? []).length >= 3,
  "production must bind and revalidate the exact current workflow policy before and after approval");
assert(promotion.indexOf("actions/checkout@") < promotion.indexOf("Download signed receipt from exact staging run") &&
  (promotion.match(/actions\/checkout@/g) ?? []).length === 2,
  "production must checkout before downloading receipt and must not clean the verified payload afterward");
assert(promotion.includes('ci_run_id="$(jq -r') && promotion.includes(".ciRunId") &&
  promotion.includes('gh run download "$SOURCE_CI_RUN_ID" --name "$CANDIDATE_NAME"') &&
  promotion.includes('--signer-workflow "$GITHUB_REPOSITORY/.github/workflows/ci.yml"'),
  "production must resolve the source CI identity from the receipt and download its exact attested candidate");
assert(promotion.includes("Source CI run:") && promotion.includes("source-ci-run-id:") &&
  promotion.includes("--run-id \"$SOURCE_CI_RUN_ID\"") && promotion.includes("without rebuild"),
  "production must expose and revalidate the receipt-bound source CI run without rebuilding");
assert(promotion.includes('test "$GITHUB_REF" = refs/heads/main') && promotion.includes("--jq .protected") &&
  promotion.includes("group: hook2stream-production") && promotion.includes("cancel-in-progress: false") &&
  promotion.includes("^h2s-app-production\\.[a-z0-9-]+\\.ts\\.net$"),
  "production promotion must reject non-main dispatches and serialize without cancellation");
assert(!promotion.includes('| tee "$RUNNER_TEMP/deploy-output.log"'),
  "production must not mirror potentially sensitive forced-command output into CI logs");
assert(promotion.includes('> "$RUNNER_TEMP/deploy-output.log" 2>&1') && promotion.includes("umask 077"),
  "production must keep forced-command output in a private runner-local file");
const productionDeployMutation = promotion.slice(promotion.indexOf("Promote exact staging-tested candidate without rebuild"));
assert(productionDeployMutation.includes('test "$(jq -r .commit.sha <<<"$main_json")" = "$POLICY_SHA"') &&
  productionDeployMutation.includes('compare/${MIN_ROLLBACK_RELEASE_SHA}...${SOURCE_SHA}') &&
  productionDeployMutation.indexOf('test "$(jq -r .commit.sha <<<"$main_json")" = "$POLICY_SHA"') <
    productionDeployMutation.indexOf('"deploy $CANDIDATE_NAME"') &&
  productionDeployMutation.includes('--minimum-release-sha "$MIN_ROLLBACK_RELEASE_SHA"'),
  "production must revalidate live main and the exact signed rollback floor immediately before SSH mutation");
assert(rollback.includes("if: github.ref == 'refs/heads/main'"),
  "rollback must reject non-main workflow dispatches before environment secrets");
assert(rollback.includes('ref: ${{ github.workflow_sha }}') &&
  rollback.includes('test "$GITHUB_SHA" = "$POLICY_SHA"') &&
  (rollback.match(/test "\$\(jq -r \.commit\.sha <<<"\$main_json"\)" = "\$POLICY_SHA"/g) ?? []).length >= 3,
  "rollback must execute only the current live protected-main workflow policy before every secret boundary");
assert(rollback.includes('DEPLOYMENT_ENVIRONMENT: ${{ inputs.environment }}') &&
  rollback.includes("^h2s-app-staging\\.[a-z0-9-]+\\.ts\\.net$") &&
  rollback.includes("^h2s-app-production\\.[a-z0-9-]+\\.ts\\.net$") &&
  rollback.indexOf("Validate stable environment MagicDNS identity") < rollback.indexOf("Connect ephemeral runner to Tailscale"),
  "rollback must reject public IPs and wrong-environment hosts before Tailscale access");
assert(rollback.includes('case "$(ssh-keygen -y -f "$RUNNER_TEMP/hook2stream-ssh/id_ed25519")" in') &&
  rollback.includes('"ssh-ed25519 "*') && !rollback.includes('$1 != "@cert-authority"') &&
  rollback.includes('$1 != host || $2 != "ssh-ed25519" || NF != 3') &&
  (rollback.match(/records == 1/g) ?? []).length === 2,
  "rollback must use exact pinned ED25519 host-key trust for staging and production");
assert(!rollback.includes('| tee "$RUNNER_TEMP/rollback-output.log"') &&
  rollback.includes('> "$RUNNER_TEMP/rollback-output.log" 2>&1') && rollback.includes("umask 077"),
  "rollback must keep forced-command output in a private runner-local file");
assert(rollback.includes("required_storage_format:") && rollback.includes("MIN_ROLLBACK_RELEASE_SHA: ${{ vars.MIN_ROLLBACK_RELEASE_SHA }}"),
  "rollback must require the H2SE capability and an environment rollback-floor identity");
assert((rollback.match(/compare\/\$\{MIN_ROLLBACK_RELEASE_SHA\}\.\.\.\$\{RELEASE_SHA\}/g) ?? []).length === 2 &&
  (rollback.match(/identical\|ahead/g) ?? []).length >= 2 &&
  rollback.includes("rollback target predates or diverges from the configured H2SE floor"),
  "rollback must reject an older or diverged target before access and immediately before host mutation");
assert(rollback.includes('"rollback $RELEASE_SHA $REQUIRED_STORAGE_FORMAT"'),
  "rollback forced command must pass the required storage format capability");
assert(rollback.includes("HOOK2STREAM_ROLLBACK_RECEIPT=") && rollback.includes("remote-deploy-result.mjs validate-rollback \\") ,
  "rollback must parse and validate the host rollback receipt");

const rollbackMutation = rollback.slice(rollback.indexOf("Roll back to a recorded H2SE-compatible release"));
assert(rollbackMutation.includes('test "$(jq -r .commit.sha <<<"$main_json")" = "$POLICY_SHA"') &&
  rollbackMutation.includes('compare/${MIN_ROLLBACK_RELEASE_SHA}...${RELEASE_SHA}') &&
  rollbackMutation.indexOf('test "$(jq -r .commit.sha <<<"$main_json")" = "$POLICY_SHA"') <
    rollbackMutation.indexOf('"rollback $RELEASE_SHA $REQUIRED_STORAGE_FORMAT"'),
  "rollback must revalidate live main immediately before the forced-command mutation");

console.log("workflow contracts passed");

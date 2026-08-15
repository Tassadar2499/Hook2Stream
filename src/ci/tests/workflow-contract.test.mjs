#!/usr/bin/env node

import { readFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../../..");
const read = (path) => readFileSync(join(repoRoot, path), "utf8");
const ci = read(".github/workflows/ci.yml");
const promotion = read(".github/workflows/promote-production.yml");
const rollback = read(".github/workflows/rollback.yml");

function assert(condition, message) {
  if (!condition) {
    console.error(`workflow-contract: ${message}`);
    process.exit(1);
  }
}

for (const [name, workflow] of [["ci", ci], ["promotion", promotion], ["rollback", rollback]]) {
  for (const match of workflow.matchAll(/^\s*uses:\s*[^\s@]+@([^\s#]+)/gm)) {
    assert(/^[0-9a-f]{40}$/.test(match[1]), `${name} workflow contains a non-immutable action reference: ${match[0].trim()}`);
  }
}

const attemptSpecificDigestName = "release-digest-${{ matrix.name }}-${{ github.sha }}-${{ github.run_id }}-${{ github.run_attempt }}";
assert(ci.split(attemptSpecificDigestName).length - 1 === 2, "both digest-fragment uploads must include run ID and run attempt");
assert(ci.includes("pattern: release-digest-*-${{ github.sha }}-${{ github.run_id }}-${{ github.run_attempt }}"),
  "digest-fragment download must be scoped to the current run attempt");
assert(promotion.includes("remote-deploy-result.mjs validate \\") && !promotion.includes("remote-deploy-result.mjs --candidate"),
  "production must invoke the explicit remote deploy-result validate subcommand");
assert(rollback.includes("required_storage_format:") && rollback.includes("MIN_ROLLBACK_RELEASE_SHA: ${{ vars.MIN_ROLLBACK_RELEASE_SHA }}"),
  "rollback must require the H2SE capability and an environment rollback-floor identity");
assert(rollback.includes('"rollback $RELEASE_SHA $REQUIRED_STORAGE_FORMAT"'),
  "rollback forced command must pass the required storage format capability");
assert(rollback.includes("HOOK2STREAM_ROLLBACK_RECEIPT=") && rollback.includes("remote-deploy-result.mjs validate-rollback \\") ,
  "rollback must parse and validate the host rollback receipt");

console.log("workflow contracts passed");

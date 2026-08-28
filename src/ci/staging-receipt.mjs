#!/usr/bin/env node

import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { basename, join, resolve } from "node:path";

const SHA_RE = /^[0-9a-f]{40}$/;
const DEPLOY_CHECKS = ["pre-migration-backup", "migration", "smoke", "e2e", "digest-verification"];
const STAGING_CHECKS = [...DEPLOY_CHECKS, "render-network-soak"];
const SOAK_CHECKS = ["render-network-soak", "elapsed-window", "single-render-worker", "no-oom"];

function sameImages(left, right) {
  const keys = Object.keys(right ?? {}).sort();
  return left && right && Object.keys(left).sort().join("\0") === keys.join("\0") && keys.every((key) => left[key] === right[key]);
}

function fail(message) {
  console.error(`staging-receipt: ${message}`);
  process.exit(1);
}

function usage() {
  console.error(`Usage:
  staging-receipt.mjs create --candidate DIR --remote-result FILE --soak-result FILE --output FILE --staging-run-id ID --staging-run-attempt N --policy-sha SHA --minimum-release-sha SHA
  staging-receipt.mjs validate-soak --candidate DIR --remote-result FILE --soak-result FILE --minimum-release-sha SHA
  staging-receipt.mjs validate --candidate DIR --receipt FILE --minimum-release-sha SHA [--repository OWNER/REPO] [--sha SHA] [--run-id ID] [--run-attempt N] [--staging-run-id ID] [--staging-run-attempt N] [--policy-sha SHA]`);
  process.exit(2);
}

function parseArgs(values) {
  const result = {};
  for (let i = 0; i < values.length; i += 2) {
    if (!values[i]?.startsWith("--") || values[i + 1] === undefined || values[i + 1].startsWith("--")) usage();
    result[values[i].slice(2)] = values[i + 1];
  }
  return result;
}

function required(args, key) {
  if (!args[key]) fail(`missing --${key}`);
  return args[key];
}

function readJson(path, label) {
  try {
    return JSON.parse(readFileSync(path, "utf8"));
  } catch (error) {
    fail(`${label} is invalid JSON: ${error.message}`);
  }
}

function sha256(path) {
  return createHash("sha256").update(readFileSync(path)).digest("hex");
}

function canonicalPositiveInteger(value) {
  return Number.isSafeInteger(value) && value > 0;
}

function canonicalNonNegativeInteger(value) {
  return Number.isSafeInteger(value) && value >= 0;
}

function exactKeys(value, expected) {
  return value && typeof value === "object" && !Array.isArray(value) &&
    JSON.stringify(Object.keys(value).sort()) === JSON.stringify([...expected].sort());
}

function canonicalTimestamp(value, label) {
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$/.test(value)) {
    fail(`${label} must be a canonical UTC timestamp with whole seconds`);
  }
  const parsed = Date.parse(value);
  if (!Number.isFinite(parsed)) fail(`${label} is invalid`);
  return parsed;
}

function candidateIdentity(candidateDir) {
  const directory = resolve(candidateDir);
  const metadata = readJson(join(directory, "release-metadata.json"), "release metadata");
  return {
    metadata,
    hashes: {
      releaseMetadataSha256: sha256(join(directory, "release-metadata.json")),
      releaseImagesSha256: sha256(join(directory, "release-images.env")),
      deployBundleSha256: sha256(join(directory, "deploy-bundle.tar.gz")),
      checksumsSha256: sha256(join(directory, "SHA256SUMS")),
    },
  };
}

function validateRemoteResult(metadata, hashes, remoteResult, minimumReleaseSha) {
  if (!SHA_RE.test(minimumReleaseSha ?? "")) fail("minimum release SHA must be exactly 40 lowercase hexadecimal characters");
  if (!exactKeys(remoteResult, [
    "schemaVersion", "kind", "environment", "result", "candidateArtifact", "commitSha",
    "releaseImagesSha256", "deployBundleSha256", "actualImages", "minimumRollbackReleaseSha",
    "checks",
  ]) ||
      remoteResult.schemaVersion !== 1 || remoteResult.kind !== "hook2stream-remote-deploy-result" ||
      remoteResult.environment !== "staging" || remoteResult.result !== "success" ||
      remoteResult.candidateArtifact !== metadata.artifactName || remoteResult.commitSha !== metadata.commitSha ||
      remoteResult.minimumRollbackReleaseSha !== minimumReleaseSha ||
      remoteResult.releaseImagesSha256 !== hashes.releaseImagesSha256 ||
      remoteResult.deployBundleSha256 !== hashes.deployBundleSha256 ||
      !sameImages(remoteResult.actualImages, metadata.images) ||
      JSON.stringify(remoteResult.checks) !== JSON.stringify(DEPLOY_CHECKS)) {
    fail("remote deployment result does not bind the verified staging candidate");
  }
  return remoteResult;
}

function validateSoakResult(metadata, remoteResult, soakResult) {
  const hook = soakResult?.hookResult;
  if (!exactKeys(soakResult, [
    "schemaVersion", "kind", "environment", "result", "candidateArtifact", "commitSha",
    "startedAt", "completedAt", "elapsedSeconds", "hookResult",
    "workerRenderInstances", "workerRenderHealthy", "workerRenderOomKilled", "checks",
  ]) ||
      soakResult.schemaVersion !== 1 || soakResult.kind !== "hook2stream-remote-soak-result" ||
      soakResult.environment !== "staging" || soakResult.result !== "success" ||
      soakResult.candidateArtifact !== metadata.artifactName || soakResult.commitSha !== metadata.commitSha ||
      !canonicalNonNegativeInteger(soakResult.elapsedSeconds) ||
      soakResult.elapsedSeconds < 3600 || soakResult.elapsedSeconds > 3900 ||
      soakResult.workerRenderInstances !== 1 || soakResult.workerRenderHealthy !== true ||
      soakResult.workerRenderOomKilled !== false ||
      JSON.stringify(soakResult.checks) !== JSON.stringify(SOAK_CHECKS)) {
    fail("remote soak result does not bind the candidate and single healthy render worker");
  }
  if (!exactKeys(hook, [
    "schema", "completedRenderCount", "renderActiveSeconds", "maxConcurrentRenderJobs",
    "networkChecks", "networkFailures", "cpuThrottled", "oomKilled",
  ]) || hook.schema !== "hook2stream-soak-hook-result-v1" ||
      !canonicalPositiveInteger(hook.completedRenderCount) ||
      !canonicalNonNegativeInteger(hook.renderActiveSeconds) || hook.renderActiveSeconds < 3300 ||
      hook.renderActiveSeconds > soakResult.elapsedSeconds || hook.maxConcurrentRenderJobs !== 1 ||
      !canonicalNonNegativeInteger(hook.networkChecks) || hook.networkChecks < 60 ||
      hook.networkFailures !== 0 || hook.cpuThrottled !== false || hook.oomKilled !== false) {
    fail("remote soak hook result does not prove sustained rendering and failure-free network checks");
  }
  const startedAt = canonicalTimestamp(soakResult.startedAt, "soak startedAt");
  const completedAt = canonicalTimestamp(soakResult.completedAt, "soak completedAt");
  if (completedAt - startedAt !== soakResult.elapsedSeconds * 1000 ||
      completedAt > Date.now() + 60_000) {
    fail("remote soak timestamps are invalid");
  }
  return soakResult;
}

function validateSoakFiles(args) {
  const { metadata, hashes } = candidateIdentity(required(args, "candidate"));
  const remoteResult = validateRemoteResult(
    metadata,
    hashes,
    readJson(resolve(required(args, "remote-result")), "remote deployment result"),
    required(args, "minimum-release-sha"),
  );
  return validateSoakResult(
    metadata,
    remoteResult,
    readJson(resolve(required(args, "soak-result")), "remote soak result"),
  );
}

function validateReceipt(args) {
  const { metadata, hashes } = candidateIdentity(required(args, "candidate"));
  const receipt = readJson(resolve(required(args, "receipt")), "staging receipt");
  if (!exactKeys(receipt, [
    "schemaVersion", "kind", "environment", "result", "repository", "commitSha", "ciRunId",
    "ciRunAttempt", "candidateArtifact", "stagingWorkflowRunId", "stagingWorkflowRunAttempt",
    "deployedAt", "policySha", "checks", "hashes", "remoteResult", "soakResult",
  ]) || receipt.schemaVersion !== 1 || receipt.kind !== "hook2stream-staging-receipt") {
    fail("unsupported staging receipt schema");
  }
  if (receipt.environment !== "staging" || receipt.result !== "success") fail("receipt is not a successful staging deployment");
  if (receipt.repository !== metadata.repository || receipt.commitSha !== metadata.commitSha ||
      receipt.ciRunId !== metadata.ciRunId || receipt.ciRunAttempt !== metadata.ciRunAttempt ||
      receipt.candidateArtifact !== metadata.artifactName) {
    fail("receipt release identity does not match the candidate");
  }
  if (JSON.stringify(receipt.hashes) !== JSON.stringify(hashes)) fail("receipt hashes do not match the candidate");
  if (JSON.stringify(receipt.checks) !== JSON.stringify(STAGING_CHECKS)) fail("receipt does not contain the required successful checks");
  if (!canonicalPositiveInteger(receipt.stagingWorkflowRunId) ||
      !canonicalPositiveInteger(receipt.stagingWorkflowRunAttempt)) {
    fail("receipt does not bind a canonical staging workflow run");
  }
  if (!SHA_RE.test(receipt.policySha ?? "")) fail("receipt does not bind a canonical trusted workflow policy SHA");
  validateRemoteResult(metadata, hashes, receipt.remoteResult, required(args, "minimum-release-sha"));
  validateSoakResult(metadata, receipt.remoteResult, receipt.soakResult);
  const deployedAt = canonicalTimestamp(receipt.deployedAt, "receipt deployedAt");
  if (deployedAt < Date.parse(receipt.soakResult.completedAt) ||
      deployedAt > Date.now() + 60_000) fail("receipt deployedAt is invalid or predates the soak");
  if (args.repository && receipt.repository !== args.repository) fail("receipt repository does not match --repository");
  if (args.sha && (!SHA_RE.test(args.sha) || receipt.commitSha !== args.sha)) fail("receipt commit does not match --sha");
  if (args["run-id"] && String(receipt.ciRunId) !== args["run-id"]) fail("receipt run does not match --run-id");
  if (args["run-attempt"] && String(receipt.ciRunAttempt) !== args["run-attempt"]) fail("receipt attempt does not match --run-attempt");
  if (args["staging-run-id"] && String(receipt.stagingWorkflowRunId) !== args["staging-run-id"]) {
    fail("receipt staging workflow run does not match --staging-run-id");
  }
  if (args["staging-run-attempt"] && String(receipt.stagingWorkflowRunAttempt) !== args["staging-run-attempt"]) {
    fail("receipt staging workflow attempt does not match --staging-run-attempt");
  }
  if (args["policy-sha"] && (!SHA_RE.test(args["policy-sha"]) || receipt.policySha !== args["policy-sha"])) {
    fail("receipt policy does not match --policy-sha");
  }
  return receipt;
}

function createReceipt(args) {
  const candidate = required(args, "candidate");
  const output = resolve(required(args, "output"));
  const { metadata, hashes } = candidateIdentity(candidate);
  const remoteResult = validateRemoteResult(
    metadata,
    hashes,
    readJson(resolve(required(args, "remote-result")), "remote deployment result"),
    required(args, "minimum-release-sha"),
  );
  const soakResult = validateSoakResult(
    metadata,
    remoteResult,
    readJson(resolve(required(args, "soak-result")), "remote soak result"),
  );
  const stagingWorkflowRunId = Number(required(args, "staging-run-id"));
  const stagingWorkflowRunAttempt = Number(required(args, "staging-run-attempt"));
  if (!canonicalPositiveInteger(stagingWorkflowRunId) || !canonicalPositiveInteger(stagingWorkflowRunAttempt)) {
    fail("staging workflow identity must use canonical positive integers");
  }
  const policySha = required(args, "policy-sha");
  if (!SHA_RE.test(policySha)) fail("policy SHA must be exactly 40 lowercase hexadecimal characters");
  const receipt = {
    schemaVersion: 1,
    kind: "hook2stream-staging-receipt",
    environment: "staging",
    result: "success",
    repository: metadata.repository,
    commitSha: metadata.commitSha,
    ciRunId: metadata.ciRunId,
    ciRunAttempt: metadata.ciRunAttempt,
    candidateArtifact: metadata.artifactName,
    stagingWorkflowRunId,
    stagingWorkflowRunAttempt,
    policySha,
    deployedAt: new Date(Math.floor(Date.now() / 1000) * 1000).toISOString().replace(".000Z", "Z"),
    checks: STAGING_CHECKS,
    hashes,
    remoteResult,
    soakResult,
  };
  writeFileSync(output, `${JSON.stringify(receipt, null, 2)}\n`, { mode: 0o644 });
  validateReceipt({ candidate, receipt: output, "minimum-release-sha": required(args, "minimum-release-sha") });
  console.log(`created ${basename(output)}`);
}

const [command, ...rest] = process.argv.slice(2);
const args = parseArgs(rest);
if (command === "create") createReceipt(args);
else if (command === "validate-soak") validateSoakFiles(args);
else if (command === "validate") validateReceipt(args);
else usage();
